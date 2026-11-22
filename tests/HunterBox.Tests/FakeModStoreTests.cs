using HunterBox.Application.Mods;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

/// <summary>测试替身的排序号分配必须与真实仓储一致（未归属记录用全局序号）。</summary>
public class FakeModStoreTests
{
    [Fact]
    public void Add_UnassignedRecord_UsesGlobalIndex()
    {
        FakeModStore store = new();
        store.Add(new ModRecord { Id = 1, GameId = "mhws" });
        store.Add(new ModRecord { Id = 2, GameId = "mhw" });
        store.Add(new ModRecord { Id = 3, GameId = string.Empty });

        Assert.Equal(1, store.Find(1)!.Index);
        Assert.Equal(1, store.Find(2)!.Index);
        // 两个游戏的最大序号都是 1，未归属记录取全局最大 + 1，而不是从 1 重新开始。
        Assert.Equal(2, store.Find(3)!.Index);
    }

    /// <summary>归属规则下沉到应用服务后，仍由仓储分配目标游戏的排序号。</summary>
    [Fact]
    public void GameAssignment_AssignUnassigned_UpdatesGameAndIndex()
    {
        FakeModStore store = new();
        store.Add(new ModRecord { Id = 1, GameId = "mhws" });
        store.Add(new ModRecord { Id = 2, GameId = string.Empty });

        int assigned = new GameAssignmentService(store).Assign([2], "mhws");

        Assert.Equal(1, assigned);
        Assert.Equal("mhws", store.Find(2)!.GameId);
        Assert.Equal(2, store.Find(2)!.Index);
    }
}
