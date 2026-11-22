using HunterBox.Application.Mods;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>MOD 列表顺序协调器：归一、移动与回滚（A-06）。</summary>
public class ModListCoordinatorTests
{
    private const string GameId = "mhws";

    [Fact]
    public void NormalizeIndexes_ContinuousIndexes_ReturnsFalseAndKeepsOrder()
    {
        using TempDir temp = TempDir.Create();
        (ModListCoordinator coordinator, ModRepository store) = Create(temp);
        Add(store, 1, 1);
        Add(store, 2, 2);
        Add(store, 3, 3);

        bool changed = coordinator.NormalizeIndexes(GameId);

        Assert.False(changed);
        Assert.Equal(new[] { 1, 2, 3 }, coordinator.GetOrder(GameId));
    }

    /// <summary>调用方已取好的快照参与归一，不再重复读取仓储。</summary>
    [Fact]
    public void NormalizeIndexes_WithSnapshot_ReordersUsingProvidedRecords()
    {
        using TempDir temp = TempDir.Create();
        (ModListCoordinator coordinator, ModRepository store) = Create(temp);
        Add(store, 1, 1);
        Add(store, 2, 4);
        Add(store, 3, 9);
        IReadOnlyList<ModRecord> snapshot = store.ModsFor(GameId);

        bool changed = coordinator.NormalizeIndexes(GameId, snapshot);

        Assert.True(changed);
        Assert.Equal(new[] { 1, 2, 3 }, store.ModsFor(GameId).Select(record => record.Index));
    }

    [Fact]
    public void NormalizeIndexes_GappedIndexes_ReordersToOneToN()
    {
        using TempDir temp = TempDir.Create();
        (ModListCoordinator coordinator, ModRepository store) = Create(temp);
        Add(store, 1, 1);
        Add(store, 2, 4);
        Add(store, 3, 9);

        bool changed = coordinator.NormalizeIndexes(GameId);

        Assert.True(changed);
        Assert.Equal(new[] { 1, 2, 3 }, coordinator.GetOrder(GameId));
        Assert.Equal(new[] { 1, 2, 3 }, store.ModsFor(GameId).Select(record => record.Index));
    }

    [Fact]
    public void Move_ToEarlierPosition_ReturnsOriginalOrderAndReorders()
    {
        using TempDir temp = TempDir.Create();
        (ModListCoordinator coordinator, ModRepository store) = Create(temp);
        Add(store, 1, 1);
        Add(store, 2, 2);
        Add(store, 3, 3);

        IReadOnlyList<int>? original = coordinator.Move(GameId, modId: 3, targetIndex: 0);

        Assert.Equal(new[] { 1, 2, 3 }, original);
        Assert.Equal(new[] { 3, 1, 2 }, coordinator.GetOrder(GameId));
    }

    [Fact]
    public void Move_ToLaterPosition_ReturnsOriginalOrderAndReorders()
    {
        using TempDir temp = TempDir.Create();
        (ModListCoordinator coordinator, ModRepository store) = Create(temp);
        Add(store, 1, 1);
        Add(store, 2, 2);
        Add(store, 3, 3);

        IReadOnlyList<int>? original = coordinator.Move(GameId, modId: 1, targetIndex: 2);

        Assert.Equal(new[] { 1, 2, 3 }, original);
        Assert.Equal(new[] { 2, 3, 1 }, coordinator.GetOrder(GameId));
    }

    [Fact]
    public void Move_SameOrInvalidPosition_ReturnsNullAndKeepsOrder()
    {
        using TempDir temp = TempDir.Create();
        (ModListCoordinator coordinator, ModRepository store) = Create(temp);
        Add(store, 1, 1);
        Add(store, 2, 2);

        Assert.Null(coordinator.Move(GameId, modId: 1, targetIndex: 0));
        Assert.Null(coordinator.Move(GameId, modId: 99, targetIndex: 1));
        Assert.Null(coordinator.Move(GameId, modId: 1, targetIndex: 2));
        Assert.Null(coordinator.Move(GameId, modId: 1, targetIndex: -1));

        Assert.Equal(new[] { 1, 2 }, coordinator.GetOrder(GameId));
    }

    [Fact]
    public void Restore_AfterMove_AppliesOriginalOrder()
    {
        using TempDir temp = TempDir.Create();
        (ModListCoordinator coordinator, ModRepository store) = Create(temp);
        Add(store, 1, 1);
        Add(store, 2, 2);
        Add(store, 3, 3);
        IReadOnlyList<int>? original = coordinator.Move(GameId, modId: 3, targetIndex: 0);

        coordinator.Restore(GameId, original!);

        Assert.Equal(new[] { 1, 2, 3 }, coordinator.GetOrder(GameId));
    }

    [Fact]
    public void GetOrder_IgnoresOtherGames()
    {
        using TempDir temp = TempDir.Create();
        (ModListCoordinator coordinator, ModRepository store) = Create(temp);
        Add(store, 1, 1);
        ModRecord other = new() { Id = 2, GameId = "mhw", Index = 1 };
        store.Add(other);

        Assert.Equal(new[] { 1 }, coordinator.GetOrder(GameId));
    }

    private static (ModListCoordinator Coordinator, ModRepository Store) Create(TempDir temp)
    {
        (BoxPaths _, ModRepository store) = TestBox.OpenRepository(temp);
        return (new ModListCoordinator(store), store);
    }

    private static void Add(ModRepository store, int id, int index) =>
        store.Add(new ModRecord { Id = id, GameId = GameId, Index = index });
}
