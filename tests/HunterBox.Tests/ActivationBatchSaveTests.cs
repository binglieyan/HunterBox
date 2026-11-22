using HunterBox.Application.Activation;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>P-07：批量启用/禁用只在批量结束统一落盘一次；单项路径仍逐项保存。</summary>
public class ActivationBatchSaveTests
{
    private const string LooseA = "natives/stm/art/model/character/ch03/a.mod3";
    private const string LooseB = "natives/stm/art/model/character/ch03/b.mod3";
    private const string LooseC = "natives/stm/art/model/character/ch03/c.mod3";

    private sealed record Box(
        BoxPaths Paths,
        string GameRoot,
        CountingStore Store,
        ModActivationService Activation,
        ModBatchActivator Batch);

    private static Box Create(TempDir temp)
    {
        TestBox<CountingStore> box = TestBox.Create(temp, paths => new CountingStore(ModRepository.Load(paths)));
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
        return mod;
    }

    [Fact]
    public void BatchEnable_SavesIndexOnce_AndPersistsEveryProcessedItem()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord[] mods =
        [
            AddMod(box, 1, LooseA),
            AddMod(box, 2, LooseB),
            AddMod(box, 3, LooseC),
        ];
        int before = box.Store.SaveCount;

        BatchActivationResult result = box.Batch.Enable(mods, PakNamingMode.PatchNumbered);

        Assert.False(result.Canceled);
        Assert.Empty(result.Failures);
        Assert.Equal(3, result.Processed);
        Assert.Equal(before + 1, box.Store.SaveCount);
        ModRepository reloaded = ModRepository.Load(box.Paths);
        Assert.Equal([true, true, true], reloaded.Mods.OrderBy(m => m.Index).Select(m => m.Enabled));
    }

    [Fact]
    public void BatchDisable_SavesIndexOnce_AndPersistsEveryProcessedItem()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord[] mods =
        [
            AddMod(box, 1, LooseA),
            AddMod(box, 2, LooseB),
            AddMod(box, 3, LooseC),
        ];
        box.Batch.Enable(mods, PakNamingMode.PatchNumbered);
        int before = box.Store.SaveCount;

        BatchActivationResult result = box.Batch.Disable(mods, PakNamingMode.PatchNumbered);

        Assert.False(result.Canceled);
        Assert.Empty(result.Failures);
        Assert.Equal(3, result.Processed);
        Assert.Equal(before + 1, box.Store.SaveCount);
        ModRepository reloaded = ModRepository.Load(box.Paths);
        Assert.Equal([false, false, false], reloaded.Mods.OrderBy(m => m.Index).Select(m => m.Enabled));
    }

    [Fact]
    public void BatchCanceled_SavesProcessedItemsExactlyOnce()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord[] mods =
        [
            AddMod(box, 1, LooseA),
            AddMod(box, 2, LooseB),
        ];
        int before = box.Store.SaveCount;

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
        Assert.Equal(before + 1, box.Store.SaveCount);
        ModRepository reloaded = ModRepository.Load(box.Paths);
        Assert.Equal([true, false], reloaded.Mods.OrderBy(m => m.Index).Select(m => m.Enabled));
    }

    [Fact]
    public void BatchPreCanceled_SavesNothing()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord[] mods =
        [
            AddMod(box, 1, LooseA),
            AddMod(box, 2, LooseB),
        ];
        int before = box.Store.SaveCount;
        using CancellationTokenSource cts = new();
        cts.Cancel();

        BatchActivationResult result = box.Batch.Enable(mods, PakNamingMode.PatchNumbered, cts.Token);

        Assert.True(result.Canceled);
        Assert.Equal(0, result.Processed);
        Assert.Equal(before, box.Store.SaveCount);
    }

    [Fact]
    public void SingleItemPath_StillSavesImmediately()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord mod = AddMod(box, 1, LooseA);
        int before = box.Store.SaveCount;

        box.Activation.Enable(mod, PakNamingMode.PatchNumbered);

        Assert.Equal(before + 1, box.Store.SaveCount);
        Assert.True(Assert.Single(ModRepository.Load(box.Paths).Mods).Enabled);
    }

    [Fact]
    public void BatchFinalSaveFailure_ThrowsAndLeavesScopeClean()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord first = AddMod(box, 1, LooseA);
        ModRecord second = AddMod(box, 2, LooseB);

        box.Store.SaveFailure = new IOException("模拟批量结束落盘失败");
        Assert.Throws<IOException>(() => box.Batch.Enable([first, second], PakNamingMode.PatchNumbered));

        // 文件已铺好、内存状态已启用（磁盘索引尚未更新）：如实抛出，不能当作成功。
        Assert.True(first.Enabled);
        Assert.True(second.Enabled);
        Assert.True(File.Exists(Path.Combine(box.GameRoot, LooseA.Replace('/', Path.DirectorySeparatorChar))));

        // 作用域必须已释放：后续单项操作仍能正常落盘。
        box.Store.SaveFailure = null;
        int before = box.Store.SaveCount;
        ModRecord third = AddMod(box, 3, LooseC);
        box.Activation.Enable(third, PakNamingMode.PatchNumbered);

        Assert.Equal(before + 1, box.Store.SaveCount);
        Assert.True(Assert.Single(ModRepository.Load(box.Paths).Mods, m => m.Id == third.Id).Enabled);
    }

    /// <summary>同步 IProgress：测试里需要在回调中直接取消令牌。</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>统计 Save 次数的存储包装（写失败可注入）。</summary>
    private sealed class CountingStore(ModRepository inner) : IModStore
    {
        public int SaveCount { get; private set; }

        public Exception? SaveFailure { get; set; }

        public IReadOnlyList<ModRecord> Mods => inner.Mods;

        public int NextId() => inner.NextId();

        public ModRecord? Find(int id) => inner.Find(id);

        public void Add(ModRecord record) => inner.Add(record);

        public bool Remove(int id) => inner.Remove(id);

        public ModRecord AssignGame(int id, string gameId) => inner.AssignGame(id, gameId);

        public void ReorderForGame(string gameId, IEnumerable<int> idsInOrder) =>
            inner.ReorderForGame(gameId, idsInOrder);

        public void Save()
        {
            if (SaveFailure is not null)
            {
                throw SaveFailure;
            }

            SaveCount++;
            inner.Save();
        }
    }
}
