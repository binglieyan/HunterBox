using HunterBox.Application.Activation;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>批量启用/禁用/重新复制的进度与取消语义。</summary>
public class ModBatchActivatorTests
{
    private const string LooseA = "natives/stm/art/model/character/ch03/a.mod3";
    private const string LooseB = "natives/stm/art/model/character/ch03/b.mod3";
    private const string LooseC = "natives/stm/art/model/character/ch03/c.mod3";

    private sealed record Box(
        BoxPaths Paths,
        string GameRoot,
        ModRepository Store,
        ModActivationService Activation,
        ModBatchActivator Batch);

    private static Box Create(TempDir temp)
    {
        TestBox<ModRepository> box = TestBox.Create(temp);
        ModActivationService activation = new(box.Paths, box.Store, box.GameRoot, box.Game, new FileContentFingerprint(), new ActivationOptions { FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance });
        return new Box(box.Paths, box.GameRoot, box.Store, activation, new ModBatchActivator(activation));
    }

    private static ModRecord AddMod(Box box, int id, string file)
    {
        ModRecord mod = new()
        {
            Id = id,
            Index = id,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [file],
        };
        string source = Path.Combine(box.Paths.FilesDirectory(id), file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, $"content-{id}");
        box.Store.Add(mod);
        box.Store.Save();
        return mod;
    }

    private static string GameFile(Box box, string relative) =>
        Path.Combine(box.GameRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private static ModRecord AddPakMod(Box box, int id, string pakName, string content)
    {
        ModRecord mod = new()
        {
            Id = id,
            Index = id,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.PakFiles,
            RelativeFiles = [pakName],
        };
        string source = Path.Combine(box.Paths.FilesDirectory(id), pakName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, content);
        box.Store.Add(mod);
        box.Store.Save();
        return mod;
    }

    private static ModRecord[] AddThree(Box box)
    {
        ModRecord a = AddMod(box, 1, LooseA);
        ModRecord b = AddMod(box, 2, LooseB);
        ModRecord c = AddMod(box, 3, LooseC);
        return [a, b, c];
    }

    [Fact]
    public void Disable_CanceledAfterFirst_FirstDisabledRestUntouched()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord[] mods = AddThree(box);
        foreach (ModRecord mod in mods)
        {
            box.Activation.Enable(mod, PakNamingMode.PatchNumbered);
        }

        using CancellationTokenSource cts = new();
        List<(int Done, int Total)> reports = [];
        IProgress<(int Done, int Total)> progress = new SynchronousProgress<(int Done, int Total)>(value =>
        {
            reports.Add(value);
            if (value.Done == 1)
            {
                cts.Cancel();
            }
        });

        BatchActivationResult result = box.Batch.Disable(
            mods, PakNamingMode.PatchNumbered, cts.Token, progress);

        Assert.True(result.Canceled);
        Assert.Equal(1, result.Processed);
        Assert.Equal(3, result.Total);
        Assert.Empty(result.Failures);
        Assert.Equal([(1, 3)], reports);
        Assert.False(mods[0].Enabled);
        Assert.True(mods[1].Enabled);
        Assert.True(mods[2].Enabled);
        Assert.False(File.Exists(GameFile(box, LooseA)));
        Assert.True(File.Exists(GameFile(box, LooseB)));
        Assert.True(File.Exists(GameFile(box, LooseC)));

        // 已处理项落盘、未处理项保持；取消不得被当成成功。
        ModRepository reloaded = ModRepository.Load(box.Paths);
        Assert.Equal([false, true, true], reloaded.Mods.OrderBy(m => m.Index).Select(m => m.Enabled));
    }

    [Fact]
    public void Disable_RemainingSourceMissing_CollectsWarnings()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord target = AddPakMod(box, 1, "target.pak", "target-content");
        ModRecord other = AddPakMod(box, 2, "other.pak", "other-content");
        box.Activation.Enable(target, PakNamingMode.PatchNumbered);
        box.Activation.Enable(other, PakNamingMode.PatchNumbered);

        // 模拟杀软隔离/用户删除：剩余启用 MOD 的源文件缺失，禁用目标时无法证明编号归属。
        Directory.Delete(box.Paths.FilesDirectory(other.Id), recursive: true);

        BatchActivationResult result = box.Batch.Disable([target], PakNamingMode.PatchNumbered);

        Assert.Equal(1, result.Processed);
        Assert.Empty(result.Failures);
        Assert.Contains(result.Warnings, warning => warning.Contains("保守保留") || warning.Contains("源文件缺失"));
    }

    [Fact]
    public void Enable_CanceledAfterFirst_OnlyFirstEnabled()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord[] mods = AddThree(box);

        using CancellationTokenSource cts = new();
        IProgress<(int Done, int Total)> progress = new SynchronousProgress<(int Done, int Total)>(value =>
        {
            if (value.Done == 1)
            {
                cts.Cancel();
            }
        });

        BatchActivationResult result = box.Batch.Enable(
            mods, PakNamingMode.PatchNumbered, cts.Token, progress);

        Assert.True(result.Canceled);
        Assert.Equal(1, result.Processed);
        Assert.True(mods[0].Enabled);
        Assert.False(mods[1].Enabled);
        Assert.True(File.Exists(GameFile(box, LooseA)));
        Assert.False(File.Exists(GameFile(box, LooseB)));
    }

    [Fact]
    public void Enable_SingleFailureDoesNotStopOtherItems()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord[] mods = AddThree(box);

        // 第二个 MOD的源文件缺失：应逐条失败但不影响第一、第三个。
        File.Delete(Path.Combine(box.Paths.FilesDirectory(mods[1].Id),
            LooseB.Replace('/', Path.DirectorySeparatorChar)));

        BatchActivationResult result = box.Batch.Enable(mods, PakNamingMode.PatchNumbered);

        Assert.False(result.Canceled);
        Assert.Equal(2, result.Processed);
        Assert.Equal(3, result.Total);
        string failure = Assert.Single(result.Failures);
        Assert.Contains(mods[1].DisplayName, failure);
        Assert.True(mods[0].Enabled);
        Assert.False(mods[1].Enabled);
        Assert.True(mods[2].Enabled);
    }

    [Fact]
    public void Recopy_StopsAtFirstFailure()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord[] mods = AddThree(box);
        foreach (ModRecord mod in mods)
        {
            box.Activation.Enable(mod, PakNamingMode.PatchNumbered);
        }

        File.Delete(Path.Combine(box.Paths.FilesDirectory(mods[0].Id),
            LooseA.Replace('/', Path.DirectorySeparatorChar)));

        BatchActivationResult result = box.Batch.Recopy(mods, PakNamingMode.PatchNumbered);

        Assert.False(result.Canceled);
        Assert.Equal(0, result.Processed);
        Assert.Single(result.Failures);
    }

    [Fact]
    public void Enable_PreCanceledToken_DoesNothing()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord[] mods = AddThree(box);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        BatchActivationResult result = box.Batch.Enable(mods, PakNamingMode.PatchNumbered, cts.Token);

        Assert.True(result.Canceled);
        Assert.Equal(0, result.Processed);
        Assert.All(mods, mod => Assert.False(mod.Enabled));
        Assert.False(File.Exists(GameFile(box, LooseA)));
    }

    /// <summary>同步 IProgress：测试里需要在回调中直接取消令牌（<see cref="Progress{T}"/> 会异步投递）。</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
