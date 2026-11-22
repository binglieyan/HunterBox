using HunterBox.Application.Models;
using HunterBox.Domain.Games;

namespace HunterBox.Tests;

/// <summary>模型身份缓存按游戏隔离、内容戳语义与容量策略由单元测试覆盖。</summary>
public class ModelIdentityCacheTests
{
    private const string Wilds = "mhws";
    private const string Rise = "mhr";

    /// <summary>不同游戏的同 ID 明细互不覆盖；游戏 ID 大小写/空白由统一规范化实现处理。</summary>
    [Fact]
    public void StoreDetails_DifferentGames_KeepsBothAndNormalizesGameId()
    {
        ModelIdentityCache cache = new(detailsCapacity: 8, usedIdCapacity: 8);
        cache.StoreDetails(" MHWS ", 1, "stamp-a", []);
        cache.StoreDetails(Rise, 1, "stamp-b", []);

        ModelIdentitySnapshot snapshot = cache.SnapshotAll();
        Assert.True(snapshot.Details.TryGetValue((Wilds, 1), out ModelDetailsCacheEntry? wilds));
        Assert.Equal("stamp-a", wilds.Stamp);
        Assert.True(snapshot.Details.TryGetValue((Rise, 1), out ModelDetailsCacheEntry? rise));
        Assert.Equal("stamp-b", rise.Stamp);
        Assert.Equal("mhws", GameProfile.NormalizeId(" MHWS "));
    }

    /// <summary>已用身份条目带回写入戳，调用方按当前戳比较，戳不一致视为未命中。</summary>
    [Fact]
    public void SnapshotUsedIds_KeepsStampForCallerComparison()
    {
        ModelIdentityCache cache = new(detailsCapacity: 8, usedIdCapacity: 8);
        cache.StoreUsedIds(Wilds, 1, "old-stamp", ["012_003"]);

        Assert.True(cache.SnapshotAll().UsedIds.TryGetValue((Wilds, 1), out ModelUsedIdsCacheEntry? entry));
        Assert.Equal("old-stamp", entry.Stamp);
        Assert.Equal(["012_003"], entry.UsedIds);
    }

    /// <summary>预热发现戳变化时按游戏移除明细与已用身份，不影响其它游戏。</summary>
    [Fact]
    public void Remove_GameScoped_RemovesOnlyThatGame()
    {
        ModelIdentityCache cache = new(detailsCapacity: 8, usedIdCapacity: 8);
        cache.StoreUsedIds(Wilds, 1, "wilds", ["a"]);
        cache.StoreUsedIds(Rise, 1, "rise", ["b"]);

        cache.Remove(Wilds, [1]);

        ModelIdentitySnapshot snapshot = cache.SnapshotAll();
        Assert.False(snapshot.UsedIds.ContainsKey((Wilds, 1)));
        Assert.True(snapshot.UsedIds.ContainsKey((Rise, 1)));
    }

    /// <summary>裁剪只删除当前游戏的死亡条目，其它游戏条目保留。</summary>
    [Fact]
    public void Prune_KeepsOtherGames()
    {
        ModelIdentityCache cache = new(detailsCapacity: 8, usedIdCapacity: 8);
        cache.StoreUsedIds(Wilds, 1, "sig-1", ["a"]);
        cache.StoreUsedIds(Wilds, 2, "sig-2", ["b"]);
        cache.StoreUsedIds(Rise, 3, "sig-3", ["c"]);

        cache.Prune(Wilds, new HashSet<int> { 2 });

        ModelIdentitySnapshot snapshot = cache.SnapshotAll();
        Assert.False(snapshot.UsedIds.ContainsKey((Wilds, 1)));
        Assert.True(snapshot.UsedIds.ContainsKey((Wilds, 2)));
        Assert.True(snapshot.UsedIds.ContainsKey((Rise, 3)));
    }

    /// <summary>按 MOD 数上调容量后，大库条目不会被初始容量淘汰。</summary>
    [Fact]
    public void EnsureUsedIdsCapacity_KeepsLargeLibraryEntries()
    {
        ModelIdentityCache cache = new(detailsCapacity: 8, usedIdCapacity: 8);
        cache.EnsureUsedIdsCapacity(3000);

        for (int id = 1; id <= 3000; id++)
        {
            cache.StoreUsedIds(Wilds, id, "sig", []);
        }

        Assert.Equal(3000, cache.SnapshotAll().UsedIds.Count);
    }

    /// <summary>粗粒度失效移除该 MOD 在全部游戏下的两条缓存。</summary>
    [Fact]
    public void Remove_AllGames_RemovesDetailsAndUsedIds()
    {
        ModelIdentityCache cache = new(detailsCapacity: 8, usedIdCapacity: 8);
        cache.StoreDetails(Wilds, 7, "sig", []);
        cache.StoreUsedIds(Rise, 7, "sig", []);

        cache.Remove(7);

        ModelIdentitySnapshot snapshot = cache.SnapshotAll();
        Assert.False(snapshot.Details.ContainsKey((Wilds, 7)));
        Assert.False(snapshot.UsedIds.ContainsKey((Rise, 7)));
    }

    /// <summary>一次快照同时带回两份字典，且都是与内部缓存解耦的副本。</summary>
    [Fact]
    public void SnapshotAll_ReturnsIndependentCopies()
    {
        ModelIdentityCache cache = new(detailsCapacity: 8, usedIdCapacity: 8);
        cache.StoreDetails(Wilds, 1, "sig", []);
        cache.StoreUsedIds(Wilds, 1, "sig", ["012_003"]);

        ModelIdentitySnapshot first = cache.SnapshotAll();
        cache.Remove(Wilds, [1]);
        ModelIdentitySnapshot second = cache.SnapshotAll();

        Assert.True(first.Details.ContainsKey((Wilds, 1)));
        Assert.False(second.Details.ContainsKey((Wilds, 1)));
        Assert.Single(first.UsedIds);
        Assert.Empty(second.UsedIds);
    }
}
