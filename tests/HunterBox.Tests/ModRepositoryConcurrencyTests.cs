using System.Text.Json;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>
/// 并发与存储线程契约（backlog 并发专项）：仓储成员并发安全、列表快照可跨线程枚举、记录是活引用、
/// 并发 Save + Add/Remove/Find/ModsFor/Reorder 不丢记录、不写坏 JSON、不残留 .tmp。
/// </summary>
/// <remarks>
/// 这些用例固化 <see cref="IModStore"/> 的线程契约（见接口 remarks），不是性能基准：
/// 断言只看「终态正确 + 索引始终可解析 + 无临时文件 + 无数据丢失」，避免依赖线程调度顺序。
/// </remarks>
public class ModRepositoryConcurrencyTests
{
    private const string GameId = "mhws";
    private const string OtherGameId = "mhw";

    private static string IndexPath(BoxPaths paths) => Path.Combine(paths.ModsDirectory, "index.json");

    private static string[] TempFiles(BoxPaths paths) =>
        [.. Directory.GetFiles(paths.ModsDirectory, "*.tmp")];

    private static string[] BadFiles(BoxPaths paths) =>
        [.. Directory.GetFiles(paths.ModsDirectory, "index.json.bad-*")];

    private static ModRecord Record(int id, string? gameId = GameId) => new()
    {
        Id = id,
        Index = 0,
        GameId = gameId,
        Kind = ModKind.LooseFiles,
        RelativeFiles = [$"natives/a{id}.tex"],
    };

    private static void AddRecords(ModRepository repo, IEnumerable<int> ids)
    {
        foreach (int id in ids)
        {
            repo.Add(Record(id));
        }
    }

    /// <summary>断言索引文件可解析、SchemaVersion/NextId/记录条数符合预期且 ID 不重复。</summary>
    private static void AssertIndexIsValid(BoxPaths paths, int expectedModCount, int expectedNextId)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(IndexPath(paths)));
        JsonElement root = document.RootElement;
        Assert.Equal(ModRepository.CurrentSchemaVersion, root.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(expectedNextId, root.GetProperty("NextId").GetInt32());
        JsonElement mods = root.GetProperty("Mods");
        Assert.Equal(expectedModCount, mods.GetArrayLength());

        int[] ids = [.. mods.EnumerateArray().Select(m => m.GetProperty("Id").GetInt32())];
        Assert.Equal(expectedModCount, ids.Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentAddRemoveFindReorderAndSave_KeepEveryRecordAndWriteValidIndex()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repo) = TestBox.OpenRepository(temp);

        int[] seedIds = [.. Enumerable.Range(1, 40)];
        AddRecords(repo, seedIds);
        repo.Save(); // 先落一版，后续并发 Save 才有可替换的索引与 .bak。

        int[] removedIds = [.. seedIds.Where(id => id % 2 == 0)];   // 20 个：交给两个删除线程。
        int[] survivingIds = [.. seedIds.Where(id => id % 2 != 0)];
        int[] addedIds = [.. Enumerable.Range(41, 40)];             // 40 个：交给四个新增线程。

        List<Task> tasks = [];

        // 4 个写线程：每人新增 10 个互不重叠的 ID。
        for (int worker = 0; worker < 4; worker++)
        {
            int start = 41 + (worker * 10);
            tasks.Add(Task.Run(() =>
            {
                for (int id = start; id < start + 10; id++)
                {
                    repo.Add(Record(id));
                }
            }));
        }

        // 2 个删除线程：各删 10 个不重叠的种子 ID（任务开始前已加入，删除必然成功）。
        for (int worker = 0; worker < 2; worker++)
        {
            int[] ids = [.. removedIds.Skip(worker * 10).Take(10)];
            tasks.Add(Task.Run(() =>
            {
                foreach (int id in ids)
                {
                    Assert.True(repo.Remove(id));
                }
            }));
        }

        // 3 个保存线程：并发 Save 必须由仓储串行化。
        for (int worker = 0; worker < 3; worker++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    repo.Save();
                }
            }));
        }

        // 4 个读线程：Find / Mods / ModsFor / NextId / NextIndex / Reorder 与写线程并发。
        for (int worker = 0; worker < 4; worker++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 30; i++)
                {
                    int probeId = seedIds[i % seedIds.Length];
                    ModRecord? found = repo.Find(probeId);
                    if (found is not null)
                    {
                        Assert.Equal(probeId, found.Id);
                    }

                    IReadOnlyList<ModRecord> snapshot = repo.Mods;
                    Assert.Equal(snapshot.Count, snapshot.Select(m => m.Id).Distinct().Count());
                    Assert.True(repo.NextId() > 0);
                    Assert.True(repo.NextIndex() > 0);

                    IReadOnlyList<ModRecord> gameMods = repo.ModsFor(GameId);
                    Assert.All(gameMods, m => Assert.True(m.BelongsTo(GameId)));

                    // 传完整快照重排：重排只改 Index，不增删记录。
                    repo.ReorderForGame(GameId, gameMods.Select(m => m.Id));
                }
            }));
        }

        await Task.WhenAll(tasks);

        repo.Save();
        Assert.Empty(TempFiles(paths));
        Assert.Empty(BadFiles(paths));

        int[] expectedIds = [.. survivingIds.Concat(addedIds).Order()];
        ModRecord[] inMemory = [.. repo.Mods];
        Assert.Equal(60, inMemory.Length);
        Assert.Equal(expectedIds, inMemory.Select(m => m.Id).Order());
        Assert.Equal(expectedIds, repo.ModsFor(GameId).Select(m => m.Id).Order());
        Assert.Empty(repo.UnassignedMods);

        // 删除不回退高水位；失败/删除都不复用 ID。
        Assert.Equal(81, repo.NextId());

        // 每条记录的字段都与加入时一致：并发不会丢字段、串记录。
        Assert.All(inMemory, m => Assert.Equal($"natives/a{m.Id}.tex", Assert.Single(m.RelativeFiles)));

        // 终态索引可解析且与内存一致；.bak 是某次中间保存的前一版——并发窗口内可能还包含尚未被删除的
        // 种子记录，因此条数区间是 [0, 80]，但必须是完整可解析的 JSON（不能是半截文件）。
        AssertIndexIsValid(paths, expectedModCount: 60, expectedNextId: 81);
        using (JsonDocument backup = JsonDocument.Parse(File.ReadAllText(repo.BackupPath)))
        {
            Assert.InRange(backup.RootElement.GetProperty("Mods").GetArrayLength(), 0, 80);
        }

        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Null(reloaded.LoadWarning);
        Assert.Equal(60, reloaded.Mods.Count);
        Assert.Equal(expectedIds, reloaded.Mods.Select(m => m.Id).Order());
        Assert.Equal(81, reloaded.NextId());
    }

    [Fact]
    public async Task ModsAndModsForSnapshots_RemainStableWhileMembershipChanges()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repo) = TestBox.OpenRepository(temp);
        repo.Add(Record(1));
        repo.Add(Record(2));

        using Barrier start = new(2);
        Task writer = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 300; i++)
            {
                repo.Add(Record(100 + i));
                Assert.True(repo.Remove(100 + i));
            }
        });

        Task reader = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 500; i++)
            {
                IReadOnlyList<ModRecord> all = repo.Mods;
                int count = all.Count;
                int enumerated = 0;
                foreach (ModRecord record in all)
                {
                    enumerated++;
                    Assert.True(record.Id > 0);
                }

                // 列表在发出后是稳定快照：并发增删不会改动它（回归会抛「集合已修改」或条数不符）。
                Assert.Equal(count, enumerated);

                IReadOnlyList<ModRecord> gameMods = repo.ModsFor(GameId);
                Assert.All(gameMods, m => Assert.True(m.BelongsTo(GameId)));
            }
        });

        await Task.WhenAll(writer, reader);

        repo.Save();
        Assert.Equal([1, 2], repo.Mods.Select(m => m.Id));
        Assert.Empty(TempFiles(paths));
        AssertIndexIsValid(paths, expectedModCount: 2, expectedNextId: 400);
    }

    [Fact]
    public void NextIdThenAdd_AcrossThreads_RejectsDuplicateIdInsteadOfSilentlyReusingIt()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repo) = TestBox.OpenRepository(temp);

        const int ThreadCount = 4;
        int[] ids = new int[ThreadCount];
        Exception?[] failures = new Exception?[ThreadCount];
        using Barrier allReadHighWater = new(ThreadCount);

        Thread[] threads = new Thread[ThreadCount];
        for (int i = 0; i < ThreadCount; i++)
        {
            int slot = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    // 所有线程先各取一次高水位，期间没有人 Add（Barrier 保证）。
                    ids[slot] = repo.NextId();
                    allReadHighWater.SignalAndWait(TimeSpan.FromSeconds(10));
                    try
                    {
                        repo.Add(Record(ids[slot]));
                    }
                    catch (InvalidOperationException ex)
                    {
                        failures[slot] = ex;
                    }
                }
                catch (Exception ex)
                {
                    // Barrier 超时等异常也记录下来，交给断言给出明确失败原因。
                    failures[slot] = ex;
                }
            });
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        // NextId 只是读高水位：并发的“取号 → Add”不是原子对，所有线程可能拿到同一个 ID。
        Assert.Single(ids.Distinct());
        // 实现必须拒绝重复 ID（恰好一个成功、其余 InvalidOperationException），绝不静默复用。
        Assert.Equal(1, failures.Count(f => f is null));
        Assert.All(failures.Where(f => f is not null), f => Assert.IsType<InvalidOperationException>(f));

        Assert.Single(repo.Mods);
        Assert.Equal(ids[0] + 1, repo.NextId());

        repo.Save();
        AssertIndexIsValid(paths, expectedModCount: 1, expectedNextId: ids[0] + 1);
        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Equal(ids[0], Assert.Single(reloaded.Mods).Id);
    }

    [Fact]
    public async Task ConcurrentReorderAndSave_KeepPerGameIndexesContiguousAndOtherGameUntouched()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repo) = TestBox.OpenRepository(temp);

        int[] gameIds = [.. Enumerable.Range(1, 30)];
        int[] otherIds = [.. Enumerable.Range(101, 5)];
        AddRecords(repo, gameIds);
        foreach (int id in otherIds)
        {
            repo.Add(Record(id, OtherGameId));
        }

        repo.Save();

        List<Task> tasks = [];
        for (int worker = 0; worker < 3; worker++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    repo.ReorderForGame(GameId, repo.ModsFor(GameId).Select(m => m.Id));
                    repo.Save();
                }
            }));
        }

        await Task.WhenAll(tasks);

        repo.Save();
        Assert.Empty(TempFiles(paths));

        // 完整快照重排后，该游戏 Index 连续为 1..N，另一游戏完全不受影响。
        Assert.Equal(30, repo.ModsFor(GameId).Count);
        Assert.Equal([.. Enumerable.Range(1, 30)], repo.ModsFor(GameId).Select(m => m.Index));
        Assert.Equal(otherIds, repo.ModsFor(OtherGameId).Select(m => m.Id));
        Assert.Equal([1, 2, 3, 4, 5], repo.ModsFor(OtherGameId).Select(m => m.Index));
        Assert.Equal(35, repo.Mods.Count);

        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Equal([.. Enumerable.Range(1, 30)], reloaded.ModsFor(GameId).Select(m => m.Index));
        Assert.Equal(otherIds, reloaded.ModsFor(OtherGameId).Select(m => m.Id));
    }

    [Fact]
    public void ListsAreSnapshotsButElementsAreLiveRecords_AssignGameAndReorderExposeLiveReferences()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repo) = TestBox.OpenRepository(temp);

        ModRecord first = Record(1);
        ModRecord unassigned = Record(2, gameId: null);
        repo.Add(first);
        repo.Add(unassigned);

        // Add 会把分配的 Index 写回传入实例：实例本身被放进仓储，不是复制。
        Assert.Equal(1, first.Index);
        Assert.Same(first, repo.Find(1));

        // Mods/ModsFor 返回列表快照，但元素就是仓储内的活实例。
        IReadOnlyList<ModRecord> snapshot = repo.Mods;
        Assert.Same(first, Assert.Single(repo.ModsFor(GameId)));
        Assert.Same(first, snapshot.Single(m => m.Id == 1));

        // 列表快照不随后续增删更新。
        Assert.True(repo.Remove(1));
        Assert.Equal(2, snapshot.Count);
        Assert.Null(repo.Find(1));

        // AssignGame 用新实例替换旧实例：旧引用变孤儿，返回/Find 拿到活的新实例。
        ModRecord stale = repo.Find(2)!;
        ModRecord assigned = repo.AssignGame(2, GameId);
        Assert.NotSame(stale, assigned);
        Assert.Same(assigned, repo.Find(2));
        Assert.Same(assigned, Assert.Single(repo.ModsFor(GameId)));

        // ReorderForGame 就地改活记录的 Index：此前拿到的引用随之变化。
        repo.Add(Record(3));
        ModRecord third = repo.Find(3)!;
        ModRecord assignedRecord = repo.Find(2)!;
        repo.ReorderForGame(GameId, [3, 2]);
        Assert.Equal(1, third.Index);
        Assert.Equal(2, assignedRecord.Index);
        Assert.Same(third, repo.Find(3));

        // 单写线程下就地改活记录 + Save 即落盘（契约允许的唯一用法）。
        assignedRecord.Metadata = new ModMetadata { Name = "改名后的记录" };
        repo.Save();

        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Equal("改名后的记录", reloaded.Find(2)!.Metadata.Name);
        Assert.Equal([3, 2], reloaded.ModsFor(GameId).Select(m => m.Id));
    }
}
