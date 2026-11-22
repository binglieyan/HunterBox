using HunterBox.Application.Models;

namespace HunterBox.Tests;

/// <summary>模型文本缓存的并发、跨游戏隔离与淘汰策略由单元测试覆盖。</summary>
public class ModelTextCacheTests
{
    private const string Wilds = "mhws";
    private const string Rise = "mhr";

    /// <summary>签名一致才命中；签名变化视为未命中。</summary>
    [Fact]
    public void TryGet_SignatureMismatch_Misses()
    {
        ModelTextCache cache = new(8);
        cache.Store(Wilds, [(1, "sig-a", "文本", "")]);

        Assert.True(cache.TryGet(1, "sig-a", Wilds, out string text));
        Assert.Equal("文本", text);
        Assert.False(cache.TryGet(1, "sig-b", Wilds, out _));
    }

    /// <summary>不同游戏的同 ID 条目各自保留，互不覆盖也互不命中。</summary>
    [Fact]
    public void Store_DifferentGames_KeepsBothEntries()
    {
        ModelTextCache cache = new(8);
        cache.Store(Wilds, [(1, "wilds-sig", "荒野文本", "")]);
        cache.Store(Rise, [(1, "rise-sig", "崛起文本", "")]);

        Assert.True(cache.TryGet(1, "wilds-sig", Wilds, out string wilds));
        Assert.Equal("荒野文本", wilds);
        Assert.True(cache.TryGet(1, "rise-sig", Rise, out string rise));
        Assert.Equal("崛起文本", rise);
        Assert.False(cache.TryGet(1, "rise-sig", Wilds, out _));
    }

    /// <summary>空游戏 ID 的查询视为未命中（旧调用契约收紧）。</summary>
    [Fact]
    public void TryGet_EmptyGameId_Misses()
    {
        ModelTextCache cache = new(8);
        cache.Store(Wilds, [(1, "sig", "文本", "")]);

        Assert.False(cache.TryGet(1, "sig", string.Empty, out _));
    }

    /// <summary>同批「超长 + 正常」混排时，未截断条目不能被写成 null。</summary>
    [Fact]
    public void Store_MixedLongAndShortTexts_KeepsAllTexts()
    {
        ModelTextCache cache = new(8);
        string longText = new('x', ModelTextPrefetchService.MaxTextLength + 100);

        cache.Store(Wilds,
        [
            (1, "sig-1", "前置正常", ""),
            (2, "sig-2", longText, ""),
            (3, "sig-3", "后续正常", ""),
        ]);

        Assert.True(cache.TryGet(1, "sig-1", Wilds, out string first));
        Assert.Equal("前置正常", first);
        Assert.True(cache.TryGet(2, "sig-2", Wilds, out string second));
        Assert.True(second.Length <= ModelTextPrefetchService.MaxTextLength);
        Assert.EndsWith("…（已截断）", second, StringComparison.Ordinal);
        Assert.True(cache.TryGet(3, "sig-3", Wilds, out string third));
        Assert.Equal("后续正常", third);
    }

    /// <summary>单条文本写入缓存时收紧到上限，持久化文件不会因个别超长描述无限膨胀。</summary>
    [Fact]
    public void Store_LongText_IsClampedToLimit()
    {
        ModelTextCache cache = new(8);
        cache.Store(Wilds, [(1, "sig", new string('x', ModelTextPrefetchService.MaxTextLength + 100), "")]);

        Assert.True(cache.TryGet(1, "sig", Wilds, out string text));
        Assert.Equal(ModelTextPrefetchService.MaxTextLength, text.Length);
        Assert.EndsWith("…（已截断）", text, StringComparison.Ordinal);
    }

    /// <summary>装载旧缓存时同样收紧，下一次保存不会把超长文本原样写回。</summary>
    [Fact]
    public void Load_LongText_IsClampedToLimit()
    {
        ModelTextCache cache = new(8);
        cache.Load(
            [new ModelTextCacheEntry(1, "sig", new string('y', ModelTextPrefetchService.MaxTextLength + 1), "", Wilds)]);

        Assert.True(cache.TryGet(1, "sig", Wilds, out string text));
        Assert.Equal(ModelTextPrefetchService.MaxTextLength, text.Length);
    }

    /// <summary>GameId 仅大小写不同仍命中，查询与裁剪语义一致。</summary>
    [Fact]
    public void TryGet_GameIdCaseInsensitive_Hits()
    {
        ModelTextCache cache = new(8);
        cache.Store(Wilds, [(1, "sig", "文本", "")]);

        Assert.True(cache.TryGet(1, "sig", Wilds.ToUpperInvariant(), out _));

        cache.Prune(Wilds.ToUpperInvariant(), new HashSet<int>());
        Assert.False(cache.TryGet(1, "sig", Wilds, out _));
    }

    /// <summary>批量查询一次返回全部结果，未命中的位置为 null；结果写入调用方缓冲。</summary>
    [Fact]
    public void TryGetMany_ReturnsHitTextsAndNullForMisses()
    {
        ModelTextCache cache = new(8);
        cache.Store(Wilds, [(1, "sig-a", "文本一", ""), (2, "sig-b", "文本二", "")]);
        string?[] results = new string?[4];

        cache.TryGetMany(
        [
            (1, "sig-a", Wilds),
            (1, "sig-b", Wilds),
            (2, "sig-b", Wilds),
            (3, "sig-c", Wilds),
        ], results);

        Assert.Equal("文本一", results[0]);
        Assert.Null(results[1]);
        Assert.Equal("文本二", results[2]);
        Assert.Null(results[3]);
    }

    /// <summary>空游戏 ID 在批量查询里同样视为未命中；缓冲长度不一致直接拒绝。</summary>
    [Fact]
    public void TryGetMany_EmptyGameIdMissesAndLengthMismatchThrows()
    {
        ModelTextCache cache = new(8);
        cache.Store(Wilds, [(1, "sig", "文本", "")]);
        string?[] results = new string?[1];

        cache.TryGetMany([(1, "sig", string.Empty)], results);
        Assert.Null(results[0]);
        Assert.Throws<ArgumentException>(() => cache.TryGetMany([], new string?[1]));
    }

    /// <summary>失效项只丢弃指定游戏的条目，其它游戏缓存不受影响。</summary>
    [Fact]
    public void DropInvalidated_OnlyDropsTargetGame()
    {
        ModelTextCache cache = new(8);
        cache.Store(Wilds, [(1, "wilds-sig", "荒野文本", "")]);
        cache.Store(Rise, [(1, "rise-sig", "崛起文本", "")]);

        cache.DropInvalidated([1], Wilds);

        Assert.False(cache.TryGet(1, "wilds-sig", Wilds, out _));
        Assert.True(cache.TryGet(1, "rise-sig", Rise, out _));
    }

    /// <summary>裁剪只删除当前游戏里已经不存在的 MOD，其它游戏缓存保留。</summary>
    [Fact]
    public void Prune_RemovesOnlyDeadEntriesOfCurrentGame()
    {
        ModelTextCache cache = new(8);
        cache.Store(Wilds, [(1, "sig-1", "一", ""), (2, "sig-2", "二", "")]);
        cache.Store(Rise, [(3, "sig-3", "三", "")]);

        cache.Prune(Wilds, new HashSet<int> { 2 });

        Assert.False(cache.TryGet(1, "sig-1", Wilds, out _));
        Assert.True(cache.TryGet(2, "sig-2", Wilds, out _));
        Assert.True(cache.TryGet(3, "sig-3", Rise, out _));
    }

    /// <summary>写盘快照包含签名、文本、pak 戳与所属游戏。</summary>
    [Fact]
    public void SnapshotWithVersion_IncludesStampAndGame()
    {
        ModelTextCache cache = new(8);
        cache.Store(Wilds, [(1, "sig", "文本", "pak|1|2")]);

        ModelTextCacheEntry entry = Assert.Single(cache.SnapshotWithVersion().Entries);

        Assert.Equal(1, entry.ModId);
        Assert.Equal("sig", entry.Signature);
        Assert.Equal("文本", entry.Text);
        Assert.Equal("pak|1|2", entry.PakStamp);
        Assert.Equal(Wilds, entry.GameId);
    }

    /// <summary>LRU 淘汰后快照里不残留被淘汰的 ID 与辅助数据。</summary>
    [Fact]
    public void Store_OverCapacity_EvictsOldestEntryCompletely()
    {
        ModelTextCache cache = new(1);
        (int Id, string Signature, string Text, string PakStamp)[] first =
        [
            .. Enumerable.Range(1, 3000).Select(i => (i, "sig-" + i.ToString(), "文本" + i.ToString(), "stamp-" + i.ToString())),
        ];
        cache.Store(Wilds, first);

        // 容量上限 5000：第一批 3000 条后再写入 2001 条，最老的 1 条被淘汰。
        (int Id, string Signature, string Text, string PakStamp)[] second =
        [
            .. Enumerable.Range(3001, 2001).Select(i => (i, "sig-" + i.ToString(), "文本" + i.ToString(), "stamp-" + i.ToString())),
        ];
        cache.Store(Wilds, second);

        List<int> ids = [.. cache.SnapshotWithVersion().Entries.Select(entry => entry.ModId)];

        Assert.Equal(5000, ids.Count);
        Assert.DoesNotContain(1, ids);
        Assert.Contains(2, ids);
        Assert.False(cache.TryGetPakStamp(1, Wilds, out _));
    }

    /// <summary>持久文件里 GameId 为 null 的条目必须被过滤，不能抛 NullReferenceException。</summary>
    [Fact]
    public void Load_EntryWithNullGameId_SkipsItAndKeepsValidEntries()
    {
        ModelTextCache cache = new(8);

        cache.Load(
        [
            new ModelTextCacheEntry(1, "sig-1", "坏条目", "", null!),
            new ModelTextCacheEntry(2, "sig-2", "好条目", "", Wilds),
        ]);

        ModelTextCacheEntry entry = Assert.Single(cache.SnapshotWithVersion().Entries);
        Assert.Equal(2, entry.ModId);
        Assert.True(cache.TryGet(2, "sig-2", Wilds, out string text));
        Assert.Equal("好条目", text);
    }

    /// <summary>写入侧同样归一 null/超长字段，任何入口都不会把脏数据带进缓存。</summary>
    [Fact]
    public void Store_NullOrOverlongFields_AreNormalized()
    {
        ModelTextCache cache = new(8);

        cache.Store(
            Wilds,
            [
                (1, null!, null!, null!),
                (2, new string('s', ModelTextPrefetchService.MaxStampLength * 4), "文本2", "stamp"),
            ]);

        Assert.True(cache.TryGet(1, string.Empty, Wilds, out string text));
        Assert.Equal(string.Empty, text);
        Assert.False(cache.TryGetPakStamp(1, Wilds, out _));
        Assert.True(cache.TryGetPakStamp(2, Wilds, out string stamp));
        Assert.Equal("stamp", stamp);
        Assert.False(cache.TryGet(2, new string('s', ModelTextPrefetchService.MaxStampLength * 4), Wilds, out _));
    }

    /// <summary>任意 store 返回 Signature/PakStamp 为 null 时，加载侧归一后查询不抛 NRE。</summary>
    [Fact]
    public void Load_NullSignatureAndPakStamp_IsNormalized()
    {
        ModelTextCache cache = new(8);

        cache.Load([new ModelTextCacheEntry(1, null!, "文本", null!, Wilds)]);

        Assert.False(cache.TryGetPakStamp(1, Wilds, out _));
        Assert.True(cache.TryGet(1, string.Empty, Wilds, out string text));
        Assert.Equal("文本", text);
    }

    /// <summary>单条非法 GameId 只丢弃该条目并告警，不能让整份缓存装载失败。</summary>
    [Fact]
    public void Load_InvalidGameId_SkipsOnlyThatEntryAndWarns()
    {
        ModelTextCache cache = new(8);
        List<string> warnings = [];

        cache.Load(
        [
            new ModelTextCacheEntry(1, "sig-1", "坏条目", "", "bad/id"),
            new ModelTextCacheEntry(2, "sig-2", "好条目", "", Wilds),
        ], warnings.Add);

        Assert.Single(warnings);
        Assert.Contains("1", warnings[0], StringComparison.Ordinal);
        Assert.True(cache.TryGet(2, "sig-2", Wilds, out string text));
        Assert.Equal("好条目", text);
    }

    /// <summary>内容完全相同的重复写入不提升版本，避免无变化写盘。</summary>
    [Fact]
    public void Store_SameContentTwice_DoesNotBumpVersion()
    {
        ModelTextCache cache = new(8);
        (int Id, string Signature, string Text, string PakStamp)[] entries = [(1, "sig", "文本", "stamp")];
        cache.Store(Wilds, entries);
        long version = cache.SnapshotWithVersion().Version;

        cache.Store(Wilds, entries);

        Assert.Equal(version, cache.SnapshotWithVersion().Version);
        cache.Store(Wilds, [(1, "sig", "新文本", "stamp")]);
        Assert.True(cache.SnapshotWithVersion().Version > version);
    }

    /// <summary>UI 线程与后台预热并发读写不抛异常，且不会破坏跨游戏隔离。</summary>
    [Fact]
    public void ConcurrentStoreReadPrune_DoesNotThrowAndKeepsGamesSeparated()
    {
        ModelTextCache cache = new(32);

        Parallel.For(0, 2000, i =>
        {
            int id = i % 16;
            string game = i % 2 == 0 ? Wilds : Rise;
            cache.Store(game, [(id, "sig-" + i.ToString(), "文本" + i.ToString(), i % 3 == 0 ? "stamp" : "")]);
            cache.TryGet(id, "sig-" + i.ToString(), game, out _);
            cache.TryGetPakStamp(id, game, out _);
            if (i % 7 == 0)
            {
                cache.DropInvalidated([id], game);
            }

            if (i % 11 == 0)
            {
                _ = cache.SnapshotWithVersion();
            }

            if (i % 13 == 0)
            {
                cache.Prune(game, new HashSet<int> { id });
            }

            if (i % 17 == 0)
            {
                cache.Remove(id);
            }
        });

        foreach (ModelTextCacheEntry entry in cache.SnapshotWithVersion().Entries)
        {
            Assert.True(entry.GameId is Wilds or Rise, "游戏标记被破坏：" + entry.GameId);
        }
    }
}
