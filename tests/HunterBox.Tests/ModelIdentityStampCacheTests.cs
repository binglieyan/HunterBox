using HunterBox.Application.Common;
using HunterBox.Application.Models;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

/// <summary>身份内容戳会话缓存只在 TTL 内、记录未变且未被精确失效时复用。</summary>
public class ModelIdentityStampCacheTests
{
    private const string Wilds = "mhws";
    private const string Rise = "mhr";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>同一记录实例在 TTL 内重复查询只计算一次，第二次打开编辑器不产生全库 stat。</summary>
    [Fact]
    public void GetOrCompute_SameInstanceWithinTtl_ComputesOnce()
    {
        MutableClock clock = new();
        ModelIdentityStampCache cache = new(clock, Ttl);
        ModRecord mod = Create(1);
        int calls = 0;

        Assert.Equal("stamp-1", cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal("stamp-1", cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls));
        Assert.Equal(1, calls);
    }

    /// <summary>TTL 到期后重新计算，会话内外部替换 pak 能被感知。</summary>
    [Fact]
    public void GetOrCompute_TtlExpired_Recomputes()
    {
        MutableClock clock = new();
        ModelIdentityStampCache cache = new(clock, Ttl);
        ModRecord mod = Create(1);
        int calls = 0;

        cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);
        clock.Advance(Ttl + ModelIdentityStampCache.MaxJitter + TimeSpan.FromSeconds(1));
        string second = cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);

        Assert.Equal("stamp-2", second);
        Assert.Equal(2, calls);
    }

    /// <summary>精确失效只让该 MOD 重算，其它 MOD 的缓存保留。</summary>
    [Fact]
    public void Invalidate_SingleMod_RecomputesOnlyThatMod()
    {
        ModelIdentityStampCache cache = new();
        ModRecord first = Create(1);
        ModRecord second = Create(2);
        int calls = 0;

        cache.GetOrCompute(Wilds, first, () => "a-" + ++calls);
        cache.GetOrCompute(Wilds, second, () => "b-" + ++calls);
        cache.Invalidate(1);

        cache.GetOrCompute(Wilds, first, () => "a2-" + ++calls);
        cache.GetOrCompute(Wilds, second, () => "b2-" + ++calls);

        Assert.Equal(3, calls);
    }

    /// <summary>空失效列表不清理任何条目。</summary>
    [Fact]
    public void Invalidate_EmptyList_KeepsCache()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);
        int calls = 0;

        cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);
        cache.Invalidate(Wilds, []);
        cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);

        Assert.Equal(1, calls);
    }

    /// <summary>不可读结果缓存 30 秒退避，期间不重复计算/写日志，到期后立即重试。</summary>
    [Fact]
    public void GetOrCompute_UnreadableStamp_BacksOff()
    {
        MutableClock clock = new();
        ModelIdentityStampCache cache = new(clock);
        ModRecord mod = Create(1);
        int calls = 0;

        string first = cache.GetOrCompute(Wilds, mod, () => ModelTextPrefetchService.UnreadableStampPrefix + ++calls);
        clock.Advance(ModelIdentityStampCache.UnreadableBackoff - TimeSpan.FromSeconds(1));
        string backedOff = cache.GetOrCompute(Wilds, mod, () => ModelTextPrefetchService.UnreadableStampPrefix + ++calls);

        Assert.Equal(first, backedOff);
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        string retried = cache.GetOrCompute(Wilds, mod, () => ModelTextPrefetchService.UnreadableStampPrefix + ++calls);

        Assert.NotEqual(first, retried);
        Assert.Equal(2, calls);
    }

    /// <summary>计算期间发生精确失效时不缓存旧戳，下一次查询重新计算。</summary>
    [Fact]
    public void GetOrCompute_InvalidatedDuringCompute_DoesNotCacheStaleStamp()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);
        int calls = 0;

        cache.GetOrCompute(Wilds, mod, () =>
        {
            cache.Invalidate(mod.Id);
            return "stamp-" + ++calls;
        });
        string second = cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);

        Assert.Equal("stamp-2", second);
        Assert.Equal(2, calls);
    }

    /// <summary>记录被替换（同 ID 新实例）时重新计算。</summary>
    [Fact]
    public void GetOrCompute_ReplacedRecord_Recomputes()
    {
        ModelIdentityStampCache cache = new();
        ModRecord first = Create(1);
        ModRecord second = Create(1);
        int calls = 0;

        cache.GetOrCompute(Wilds, first, () => "stamp-" + ++calls);
        string result = cache.GetOrCompute(Wilds, second, () => "stamp-" + ++calls);

        Assert.Equal("stamp-2", result);
        Assert.Equal(2, calls);
    }

    /// <summary>不同游戏互不影响；按游戏裁剪只删除当前游戏的条目。</summary>
    [Fact]
    public void Prune_RemovesOnlyCurrentGameEntries()
    {
        ModelIdentityStampCache cache = new();
        ModRecord wilds = Create(1);
        ModRecord rise = Create(2);
        int calls = 0;

        cache.GetOrCompute(Wilds, wilds, () => "w-" + ++calls);
        cache.GetOrCompute(Rise, rise, () => "r-" + ++calls);

        cache.Prune(Wilds, new HashSet<int>());
        cache.GetOrCompute(Wilds, wilds, () => "w2-" + ++calls);
        cache.GetOrCompute(Rise, rise, () => "r2-" + ++calls);

        Assert.Equal(3, calls);
    }

    /// <summary>
    /// 命中不续期且抖动落在 3–5 分钟；若改回滑动续期，本用例第二次命中后会继续命中而失败。
    /// </summary>
    [Fact]
    public void GetOrCompute_HitsDoNotExtendTtl()
    {
        MutableClock clock = new();
        ModelIdentityStampCache cache = new(clock, Ttl);
        ModRecord mod = Create(1);
        int calls = 0;

        cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);
        // Ttl + MinJitter - 1：仍在任何条目的有效期内（抖动下界）。
        clock.Advance(Ttl + ModelIdentityStampCache.MinJitter - TimeSpan.FromSeconds(1));
        Assert.Equal("stamp-1", cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls));

        // 再越过抖动上界：非滑动必须过期；若命中续期，新窗口至少 Ttl + MinJitter，仍会命中。
        clock.Advance(ModelIdentityStampCache.MaxJitter - ModelIdentityStampCache.MinJitter + TimeSpan.FromSeconds(2));
        string third = cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);

        Assert.Equal("stamp-2", third);
        Assert.Equal(2, calls);
    }

    /// <summary>不可读结果替换旧可读戳；退避期内返回不可读哨兵，不返回旧明细。</summary>
    [Fact]
    public void GetOrCompute_UnreadableAfterCached_ReplacesOldEntry()
    {
        MutableClock clock = new();
        ModelIdentityStampCache cache = new(clock);
        ModRecord mod = Create(1);
        int calls = 0;

        cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);
        string unreadable = cache.GetOrCompute(
            Wilds,
            mod,
            () => ModelTextPrefetchService.UnreadableStampPrefix + ++calls,
            forceRefresh: true);
        string backedOff = cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);

        Assert.True(ModelTextPrefetchService.IsUnreadableStamp(unreadable));
        Assert.True(ModelTextPrefetchService.IsUnreadableStamp(backedOff));
        Assert.Equal(2, calls);

        clock.Advance(ModelIdentityStampCache.UnreadableBackoff + TimeSpan.FromSeconds(1));
        string recomputed = cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);

        Assert.Equal("stamp-3", recomputed);
        Assert.Equal(3, calls);
    }

    /// <summary>计算期间被不可读结果驱逐时，在途计算不会把旧可读戳写回。</summary>
    [Fact]
    public void GetOrCompute_EvictionDuringCompute_DoesNotWriteBack()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);
        int calls = 1;

        cache.GetOrCompute(Wilds, mod, () => "stamp-" + calls);
        string outer = cache.GetOrCompute(
            Wilds,
            mod,
            () =>
            {
                // 模拟另一路径在本计算期间把该 MOD 标成不可读并递增版本。
                _ = cache.GetOrCompute(
                    Wilds,
                    mod,
                    () => ModelTextPrefetchService.UnreadableStampPrefix + "other",
                    forceRefresh: true);
                return "stamp-" + ++calls;
            },
            forceRefresh: true);
        string next = cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);

        Assert.Equal("stamp-2", outer);
        Assert.True(ModelTextPrefetchService.IsUnreadableStamp(next));
        Assert.Equal(2, calls);
    }

    /// <summary>目标 MOD 强制重算，TTL 内也感知外部替换。</summary>
    [Fact]
    public void GetOrCompute_ForceRefresh_RecomputesEvenWithinTtl()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);
        int calls = 0;

        cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);
        string second = cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls, forceRefresh: true);

        Assert.Equal("stamp-2", second);
        Assert.Equal(2, calls);
    }

    /// <summary>裁剪没有实际删除条目时不递增版本，不会作废并发计算中的戳。</summary>
    [Fact]
    public void Prune_NoEntriesRemoved_DoesNotInvalidateInFlightCompute()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);
        int calls = 0;

        cache.GetOrCompute(Wilds, mod, () =>
        {
            cache.Prune(Wilds, new HashSet<int> { mod.Id });
            return "stamp-" + ++calls;
        });
        cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);

        Assert.Equal(1, calls);
    }

    /// <summary>过期条目仍可读取旧戳并标记过期，供调用方按预算轮换刷新。</summary>
    [Fact]
    public void TryGetCached_Expired_ReturnsStampAndFlag()
    {
        MutableClock clock = new();
        ModelIdentityStampCache cache = new(clock, Ttl);
        ModRecord mod = Create(1);

        Assert.False(cache.TryGetCached(Wilds, mod, out _, out _));
        cache.GetOrCompute(Wilds, mod, () => "stamp-1");
        Assert.True(cache.TryGetCached(Wilds, mod, out string stamp, out bool expired));
        Assert.Equal("stamp-1", stamp);
        Assert.False(expired);

        clock.Advance(Ttl + ModelIdentityStampCache.MaxJitter + TimeSpan.FromSeconds(1));
        Assert.True(cache.TryGetCached(Wilds, mod, out string stale, out bool staleExpired));
        Assert.Equal("stamp-1", stale);
        Assert.True(staleExpired);
    }

    /// <summary>同一键并发计算时，先结束的计算不能提前清理版本键，否则另一个计算会写回失效前的旧戳。</summary>
    [Fact]
    public void GetOrCompute_ConcurrentSameKey_InvalidationPreventsStaleWriteBack()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);
        using ManualResetEventSlim firstStarted = new();
        using ManualResetEventSlim releaseFirst = new();

        Task<string> first = Task.Run(() => cache.GetOrCompute(Wilds, mod, () =>
        {
            firstStarted.Set();
            Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10)), "等待释放第一个计算超时");
            return "stale";
        }));

        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(10)), "第一个计算未启动");

        // 第二个同键计算在失效之后结束，先触发 finally；旧实现会在此时删掉版本键。
        string second = cache.GetOrCompute(Wilds, mod, () =>
        {
            cache.Invalidate(mod.Id);
            return "fresh";
        });
        Assert.Equal("fresh", second);

        releaseFirst.Set();
        Assert.Equal("stale", first.GetAwaiter().GetResult());

        int thirdCalls = 0;
        string third = cache.GetOrCompute(Wilds, mod, () => "third-" + ++thirdCalls);

        Assert.Equal("third-1", third);
        Assert.Equal(1, thirdCalls);
    }

    /// <summary>失效后不残留版本键（内部不变量，避免长会话无界增长）。</summary>
    [Fact]
    public void Invalidate_RemovesVersionKeys()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);

        cache.GetOrCompute(Wilds, mod, () => "stamp-1");
        cache.Invalidate(mod.Id);

        Assert.Equal(0, VersionKeyCount(cache));
    }

    /// <summary>真实裁剪已缓存条目时同步清理版本键（不可读结果会留下版本键）。</summary>
    [Fact]
    public void Prune_CachedUnreadableEntry_RemovesEntryAndVersionKey()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);

        cache.GetOrCompute(Wilds, mod, () => ModelTextPrefetchService.UnreadableStampPrefix + "x");
        Assert.Equal(1, VersionKeyCount(cache));

        cache.Prune(Wilds, new HashSet<int>());

        Assert.False(cache.TryGetCached(Wilds, mod, out _, out _));
        Assert.Equal(0, VersionKeyCount(cache));
    }

    /// <summary>MOD 删除时计算仍在途，完成后不能把戳写回已删除条目。</summary>
    [Fact]
    public void Prune_DeletedModWithInFlightCompute_DoesNotWriteBack()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();

        Task<string> compute = Task.Run(() => cache.GetOrCompute(Wilds, mod, () =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)), "等待释放计算超时");
            return "stale";
        }));

        Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "计算未启动");
        cache.Prune(Wilds, new HashSet<int>());
        release.Set();
        Assert.Equal("stale", compute.GetAwaiter().GetResult());

        Assert.False(cache.TryGetCached(Wilds, mod, out _, out _));
        Assert.Equal("fresh", cache.GetOrCompute(Wilds, mod, () => "fresh"));
    }

    /// <summary>MOD 删除时在途计算返回不可读戳，也不能写回已删除条目。</summary>
    [Fact]
    public void Prune_DeletedModWithInFlightUnreadable_DoesNotWriteBack()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();

        Task<string> compute = Task.Run(() => cache.GetOrCompute(Wilds, mod, () =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)), "等待释放计算超时");
            return ModelTextPrefetchService.UnreadableStampPrefix + "stale";
        }));

        Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "计算未启动");
        cache.Prune(Wilds, new HashSet<int>());
        release.Set();
        _ = compute.GetAwaiter().GetResult();

        Assert.False(cache.TryGetCached(Wilds, mod, out _, out _));
        int calls = 0;
        string next = cache.GetOrCompute(Wilds, mod, () => "fresh-" + ++calls);
        Assert.Equal("fresh-1", next);
    }

    /// <summary>删除后立即重装同 ID，新计算在旧计算结束前完成也必须写入，旧结果不能覆盖。</summary>
    [Fact]
    public void Prune_ThenReaddedSameId_NewComputeWritesAfterOldCompletes()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);
        using ManualResetEventSlim oldStarted = new();
        using ManualResetEventSlim releaseOld = new();

        Task<string> old = Task.Run(() => cache.GetOrCompute(Wilds, mod, () =>
        {
            oldStarted.Set();
            Assert.True(releaseOld.Wait(TimeSpan.FromSeconds(10)), "等待释放旧计算超时");
            return "old";
        }));

        Assert.True(oldStarted.Wait(TimeSpan.FromSeconds(10)), "旧计算未启动");
        cache.Prune(Wilds, new HashSet<int>());

        // 删除后立即重装同 ID：新旧计算并发，新计算先完成并写入。
        Assert.Equal("fresh", cache.GetOrCompute(Wilds, mod, () => "fresh"));
        releaseOld.Set();
        Assert.Equal("old", old.GetAwaiter().GetResult());

        Assert.True(cache.TryGetCached(Wilds, mod, out string stamp, out _));
        Assert.Equal("fresh", stamp);
    }

    /// <summary>不可读过期条目同样走预算核心，退避期内不重复计算。</summary>
    [Fact]
    public void UnreadableExpiredEntries_RefreshThroughBudgetCore()
    {
        MutableClock clock = new();
        ModelIdentityStampCache cache = new(clock, Ttl);
        ModRecord[] mods = [Create(1), Create(2), Create(3)];
        foreach (ModRecord mod in mods)
        {
            _ = cache.GetOrCompute(Wilds, mod, () => ModelTextPrefetchService.UnreadableStampPrefix + mod.Id);
        }

        clock.Advance(ModelIdentityStampCache.DefaultTtl + ModelIdentityStampCache.MaxJitter + TimeSpan.FromSeconds(1));

        int computeCalls = 0;
        ModelStampRefresh.RefreshWithinBudget(
            mods,
            cursor: 0,
            budgetMilliseconds: 20,
            elapsedMilliseconds: () => 0,
            refresh: mod => _ = cache.GetOrCompute(
                Wilds,
                mod,
                () => ModelTextPrefetchService.UnreadableStampPrefix + ++computeCalls,
                forceRefresh: true),
            CancellationToken.None,
            warnBatch: null,
            out _);

        Assert.Equal(3, computeCalls);

        ModelStampRefresh.RefreshWithinBudget(
            mods,
            cursor: 0,
            budgetMilliseconds: 20,
            elapsedMilliseconds: () => 0,
            refresh: mod => _ = cache.GetOrCompute(
                Wilds,
                mod,
                () => ModelTextPrefetchService.UnreadableStampPrefix + ++computeCalls,
                forceRefresh: true),
            CancellationToken.None,
            warnBatch: null,
            out _);

        Assert.Equal(3, computeCalls);
    }

    /// <summary>读取私有版本键表数量（只用于断言内部不变量）。</summary>
    private static int VersionKeyCount(ModelIdentityStampCache cache)
    {
        object? field = typeof(ModelIdentityStampCache)
            .GetField("_modVersions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cache);
        return ((System.Collections.ICollection)field!).Count;
    }

    /// <summary>目标 MOD 的强制刷新也受不可读退避约束，退避期内不重复扫描。</summary>
    [Fact]
    public void GetOrCompute_ForceRefreshWithinUnreadableBackoff_DoesNotRecompute()
    {
        MutableClock clock = new();
        ModelIdentityStampCache cache = new(clock);
        ModRecord mod = Create(1);
        int calls = 0;

        string first = cache.GetOrCompute(
            Wilds,
            mod,
            () => ModelTextPrefetchService.UnreadableStampPrefix + ++calls);
        string second = cache.GetOrCompute(
            Wilds,
            mod,
            () => ModelTextPrefetchService.UnreadableStampPrefix + ++calls,
            forceRefresh: true);

        Assert.Equal(first, second);
        Assert.Equal(1, calls);

        clock.Advance(ModelIdentityStampCache.UnreadableBackoff + TimeSpan.FromSeconds(1));
        _ = cache.GetOrCompute(
            Wilds,
            mod,
            () => ModelTextPrefetchService.UnreadableStampPrefix + ++calls,
            forceRefresh: true);
        Assert.Equal(2, calls);
    }

    /// <summary>失效只作废目标 MOD，不丢弃其它 MOD 的在途计算结果。</summary>
    [Fact]
    public void Invalidate_OtherMod_DoesNotDiscardInFlightCompute()
    {
        ModelIdentityStampCache cache = new();
        ModRecord mod = Create(1);
        int calls = 0;

        cache.GetOrCompute(Wilds, mod, () =>
        {
            cache.Invalidate(99);
            return "stamp-" + ++calls;
        });
        string next = cache.GetOrCompute(Wilds, mod, () => "stamp-" + ++calls);

        Assert.Equal("stamp-1", next);
        Assert.Equal(1, calls);
    }

    private static ModRecord Create(int id) => new()
    {
        Id = id,
        Index = id,
        GameId = TestGames.Wilds.Id,
        Kind = ModKind.LooseFiles,
        RelativeFiles = ["natives/stm/a.mod3"],
        Metadata = new ModMetadata(),
    };
}
