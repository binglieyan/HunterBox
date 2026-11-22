using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>索引耐久性（C-13）：并发保存串行化、GUID 临时名、损坏索引降级不阻断启动。</summary>
public class ModRepositoryDurabilityTests
{
    private static string IndexPath(BoxPaths paths) => Path.Combine(paths.ModsDirectory, "index.json");

    private static string[] TempFiles(BoxPaths paths) =>
        [.. Directory.GetFiles(paths.ModsDirectory, "*.tmp")];

    private static ModRecord Record(int id) => new()
    {
        Id = id,
        Index = 0,
        GameId = "mhws",
        Kind = ModKind.LooseFiles,
        RelativeFiles = ["natives/a.tex"],
    };

    [Fact]
    public void Load_CorruptIndexWhenRenameFails_ReturnsEmptyWithWarningInsteadOfThrowing()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        const string corrupt = "{ broken json";
        File.WriteAllText(IndexPath(paths), corrupt);

        // FileShare.ReadWrite 允许读、拒绝改名/删除：模拟「索引损坏且被占用」。
        using FileStream locked = new(
            IndexPath(paths),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        ModRepository repo = ModRepository.Load(paths);

        // 不能抛到启动层；原文件保持原样，等待用户恢复。
        Assert.Empty(repo.Mods);
        Assert.NotNull(repo.LoadWarning);
        Assert.Contains(IndexPath(paths), repo.LoadWarning);
        Assert.True(File.Exists(IndexPath(paths)), "改名失败时不能删除损坏原件");
        Assert.Equal(corrupt, File.ReadAllText(IndexPath(paths)));
    }

    [Fact]
    public void Save_AfterCorruptLoadCouldNotBackup_RefusesOverwriteThenRecovers()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        const string corrupt = "{ broken json";
        File.WriteAllText(IndexPath(paths), corrupt);
        FileStream locked = new(
            IndexPath(paths),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        ModRepository repo = ModRepository.Load(paths);
        Assert.True(File.Exists(IndexPath(paths)));

        // 损坏原件还没备份成功：拒绝覆盖（可诊断 IOException），不能丢用户数据。
        Assert.ThrowsAny<IOException>(() => repo.Save());
        Assert.Equal(corrupt, File.ReadAllText(IndexPath(paths)));

        // 解除占用后保存会自动补做 .bad 备份，再原子写新索引。
        locked.Dispose();
        repo.Add(Record(1));
        repo.Save();

        string bad = Assert.Single(Directory.GetFiles(paths.ModsDirectory, "index.json.bad-*"));
        Assert.Equal(corrupt, File.ReadAllText(bad));
        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Null(reloaded.LoadWarning);
        Assert.Single(reloaded.Mods);
    }

    [Fact]
    public void ConcurrentSaves_AreSerializedAndLeaveNoTempFiles()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repo) = TestBox.OpenRepository(temp);
        repo.Add(Record(1));

        Parallel.For(0, 32, _ => repo.Save());

        Assert.Empty(TempFiles(paths));
        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Null(reloaded.LoadWarning);
        Assert.Single(reloaded.Mods);
        Assert.Equal(1, reloaded.Mods[0].Id);
    }

    [Fact]
    public async Task ConcurrentSavesWithAdds_KeepIndexConsistent()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repo) = TestBox.OpenRepository(temp);

        Task saving = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                repo.Save();
            }
        });

        for (int id = 1; id <= 20; id++)
        {
            repo.Add(Record(id));
        }

        repo.Save();
        await saving;
        repo.Save();

        Assert.Empty(TempFiles(paths));
        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Null(reloaded.LoadWarning);
        Assert.Equal(20, reloaded.Mods.Count);
        Assert.Equal(Enumerable.Range(1, 20), reloaded.Mods.Select(m => m.Id));
    }
}
