using HunterBox.Application.Activation;
using HunterBox.Application.Paks;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Paks;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>P-01：批量启用/禁用共享内容指纹缓存，同一 pak 不再被每个 MOD 重算一次。</summary>
public class PakFingerprintCacheTests
{
    /// <summary>记录每次真正进入底层整文件哈希的路径（缓存命中不会记）。</summary>
    private sealed class CountingFingerprint : IContentFingerprint
    {
        private readonly IContentFingerprint _inner = new FileContentFingerprint();

        public List<string> Reads { get; } = [];

        public string GetFingerprint(string fullPath)
        {
            Reads.Add(Path.GetFullPath(fullPath));
            return _inner.GetFingerprint(fullPath);
        }
    }

    private sealed record Box(
        BoxPaths Paths,
        string GameRoot,
        ModRepository Store,
        ModActivationService Activation,
        ModBatchActivator Batch);

    private static Box Create(TempDir temp, IContentFingerprint fingerprint)
    {
        TestBox<ModRepository> box = TestBox.Create(temp);
        ModActivationService activation = new(box.Paths, box.Store, box.GameRoot, box.Game, fingerprint, new ActivationOptions { FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance });
        return new Box(box.Paths, box.GameRoot, box.Store, activation, new ModBatchActivator(activation));
    }

    private static ModRecord AddPakMod(Box box, int id, string content)
    {
        string pakName = $"mod{id}.pak";
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
        return mod;
    }

    [Fact]
    public void GetFingerprint_UnchangedFile_HitsInnerImplementationOnce()
    {
        using TempDir temp = TempDir.Create();
        string file = temp.File("a.pak", "same-content");
        CountingFingerprint inner = new();
        PakFingerprintCache cache = new(inner, SystemFileSystem.Instance);

        string first = cache.GetFingerprint(file);
        string second = cache.GetFingerprint(file);

        Assert.Equal(first, second);
        Assert.Single(inner.Reads);
    }

    [Fact]
    public void GetFingerprint_FileRewritten_RecomputesInsteadOfReturningStaleValue()
    {
        using TempDir temp = TempDir.Create();
        string file = temp.File("a.pak", "content-A");
        CountingFingerprint inner = new();
        PakFingerprintCache cache = new(inner, SystemFileSystem.Instance);

        string before = cache.GetFingerprint(file);

        // 同长度改写 + 显式改修改时间：必须靠「长度 + 最后写入时间」判定失效，不能只靠长度。
        File.WriteAllText(file, "content-B");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        string after = cache.GetFingerprint(file);

        Assert.NotEqual(before, after);
        Assert.Equal(2, inner.Reads.Count);
    }

    [Fact]
    public void GetFingerprint_MissingFile_KeepsInnerExceptionSemantics()
    {
        using TempDir temp = TempDir.Create();
        PakFingerprintCache cache = new(new FileContentFingerprint(), SystemFileSystem.Instance);

        Assert.Throws<FileNotFoundException>(
            () => cache.GetFingerprint(Path.Combine(temp.Path, "missing.pak")));
    }

    [Fact]
    public void Invalidate_ForcesRecomputeOnNextAccess()
    {
        using TempDir temp = TempDir.Create();
        string file = temp.File("a.pak", "content-A");
        CountingFingerprint inner = new();
        PakFingerprintCache cache = new(inner, SystemFileSystem.Instance);

        _ = cache.GetFingerprint(file);
        cache.Invalidate(file);
        _ = cache.GetFingerprint(file);

        Assert.Equal(2, inner.Reads.Count);
    }

    /// <summary>Remember 也受容量上限约束，超限只淘汰最久未用项而不是整表清空。</summary>
    [Fact]
    public void Remember_ExceedingCapacity_KeepsEntryCountWithinLimit()
    {
        using TempDir temp = TempDir.Create();
        CountingFingerprint inner = new();
        PakFingerprintCache cache = new(inner, SystemFileSystem.Instance, maxEntries: 4);

        for (int i = 0; i < 6; i++)
        {
            cache.Remember(temp.File($"p{i}.pak", $"content-{i}"), "known");
        }

        Assert.Equal(4, cache.EntryCount);
    }

    /// <summary>命中会刷新最近使用顺序，淘汰时只移除最久未用的条目。</summary>
    [Fact]
    public void GetFingerprint_HitRefreshesRecency_EvictsLeastRecentlyUsed()
    {
        using TempDir temp = TempDir.Create();
        string first = temp.File("a.pak", "aaa");
        string second = temp.File("b.pak", "bbb");
        string third = temp.File("c.pak", "ccc");
        CountingFingerprint inner = new();
        PakFingerprintCache cache = new(inner, SystemFileSystem.Instance, maxEntries: 2);
        cache.Remember(first, "known-a");
        cache.Remember(second, "known-b");

        // 访问 first 使之成为最近使用项，再写入 third 应淘汰 second。
        _ = cache.GetFingerprint(first);
        cache.Remember(third, "known-c");

        int reads = inner.Reads.Count;
        _ = cache.GetFingerprint(first);
        Assert.Equal(reads, inner.Reads.Count);

        _ = cache.GetFingerprint(second);
        Assert.Equal(reads + 1, inner.Reads.Count);
    }

    /// <summary>批量禁用：pak_mods 里的每个 pak 在整个批次里最多只做一次整文件哈希（修复前是 N 次）。</summary>
    [Fact]
    public void SharedProcessCache_SecondOperation_DoesNotRereadPakMods()
    {
        using TempDir temp = TempDir.Create();
        CountingFingerprint counting = new();
        Box box = Create(temp, counting);
        ModRecord mod = AddPakMod(box, 1, "content-of-mod-1");
        PakFingerprintCache shared = new(counting, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        ActivationOptions options = new()
        {
            FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            PakFingerprintCache = shared,
        };
        ModActivationService first = new(box.Paths, box.Store, box.GameRoot, TestGames.Wilds, counting, options);
        first.Enable(mod, PakNamingMode.PatchNumbered);
        int readsAfterFirst = counting.Reads.Count;

        ModActivationService second = new(box.Paths, box.Store, box.GameRoot, TestGames.Wilds, counting, options);
        second.Disable(mod, PakNamingMode.PatchNumbered);

        Assert.Equal(readsAfterFirst, counting.Reads.Count);
    }

    [Fact]
    public void BatchDisable_HashesEveryPakAtMostOnceAcrossBatch()
    {
        using TempDir temp = TempDir.Create();
        CountingFingerprint unrelatedCounter = new();
        Box setup = Create(temp, unrelatedCounter);
        ModRecord[] mods =
        [
            AddPakMod(setup, 1, "content-of-mod-1"),
            AddPakMod(setup, 2, "content-of-mod-2"),
            AddPakMod(setup, 3, "content-of-mod-3"),
        ];

        // 先用独立服务把 3 个 MOD 都启用（写入 pak_mods 三个不同 pak），再换计数指纹做整批禁用。
        foreach (ModRecord mod in mods)
        {
            setup.Activation.Enable(mod, PakNamingMode.PatchNumbered);
        }

        // 复用同一个存储（记录对象即 setup 中的那些），只把指纹实现换成计数包装。
        CountingFingerprint counting = new();
        ModActivationService batchActivation = new(setup.Paths, setup.Store, setup.GameRoot, TestGames.Wilds, counting, new ActivationOptions { FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance });
        ModBatchActivator batch = new(batchActivation);
        BatchActivationResult result = batch.Disable(mods, PakNamingMode.PatchNumbered);

        Assert.Empty(result.Failures);
        Assert.Equal(3, result.Processed);
        Assert.False(Directory.Exists(setup.GameRoot) && Directory.EnumerateFiles(
            Path.Combine(setup.GameRoot, "pak_mods"), "*.pak").Any(), "禁用后 pak_mods 里不应残留这 3 个 pak");

        // 核心语义断言：同一路径在整个批次只被整文件哈希一次（修复前每个 MOD 都重算剩余 pak_mods 全部 pak）。
        IReadOnlyList<string> duplicateReads = [.. counting.Reads
            .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ×{group.Count()}")];
        Assert.True(
            duplicateReads.Count == 0,
            "批量禁用中以下文件被重复整文件哈希：" + string.Join("、", duplicateReads));
        Assert.NotEmpty(counting.Reads);
    }

    /// <summary>批量启用：同一 MOD 源 pak / 已部署 pak 在整批里最多被哈希一次。</summary>
    [Fact]
    public void BatchEnable_HashesEveryPakAtMostOnceAcrossBatch()
    {
        using TempDir temp = TempDir.Create();
        CountingFingerprint counting = new();
        Box box = Create(temp, counting);
        ModRecord[] mods =
        [
            AddPakMod(box, 1, "content-of-mod-1"),
            AddPakMod(box, 2, "content-of-mod-2"),
            AddPakMod(box, 3, "content-of-mod-3"),
        ];

        BatchActivationResult result = box.Batch.Enable(mods, PakNamingMode.PatchNumbered);

        Assert.Empty(result.Failures);
        Assert.Equal(3, result.Processed);
        Assert.All(mods, mod => Assert.True(mod.Enabled));

        IReadOnlyList<string> duplicateReads = [.. counting.Reads
            .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ×{group.Count()}")];
        Assert.True(
            duplicateReads.Count == 0,
            "批量启用中以下文件被重复整文件哈希：" + string.Join("、", duplicateReads));
    }
}
