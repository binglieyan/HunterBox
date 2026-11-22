using HunterBox.Domain.Mods;

namespace HunterBox.Application.Mods;

/// <summary>
/// MOD 列表顺序协调器：把「当前游戏的顺序读取、序号归一与移动」从界面层抽出（A-06）。
/// </summary>
/// <remarks>
/// <para>
/// 界面只负责选择目标位置与提示文案；顺序计算、边界判断与仓储重排都在这里完成，便于单元测试。
/// 与仓储一致：顺序以 <see cref="ModRecord.Index"/> 为唯一真相，<see cref="IModStore.ModsFor"/>
/// 返回按序号升序的快照。
/// </para>
/// <para>不负责落盘：调用方在需要持久化时调用 <see cref="IModStore.Save"/>，并按需回滚。</para>
/// </remarks>
public sealed class ModListCoordinator
{
    private readonly IModStore _store;

    /// <summary>装配顺序协调器。</summary>
    /// <param name="store">MOD 记录存储。</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> 为 <see langword="null"/>。</exception>
    public ModListCoordinator(IModStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>取指定游戏当前的 MOD ID 顺序（按序号升序）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <returns>该游戏的 MOD ID 列表；没有记录时为空。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public IReadOnlyList<int> GetOrder(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        return [.. _store.ModsFor(gameId).Select(record => record.Id)];
    }

    /// <summary>把指定游戏的序号归一为 1..N（原位连续时不改记录）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <returns>确实重排了记录时返回 <see langword="true"/>；已连续时返回 <see langword="false"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public bool NormalizeIndexes(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        return NormalizeIndexes(gameId, _store.ModsFor(gameId));
    }

    /// <summary>用调用方已取好的记录快照把指定游戏的序号归一为 1..N，避免重复读取仓储。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="mods">该游戏的记录快照（按当前列表顺序）。</param>
    /// <returns>确实重排了记录时返回 <see langword="true"/>；已连续时返回 <see langword="false"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mods"/> 为 <see langword="null"/>。</exception>
    public bool NormalizeIndexes(string gameId, IReadOnlyList<ModRecord> mods)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(mods);

        for (int index = 0; index < mods.Count; index++)
        {
            if (mods[index].Index != index + 1)
            {
                _store.ReorderForGame(gameId, mods.Select(record => record.Id));
                return true;
            }
        }

        return false;
    }

    /// <summary>把指定 MOD 移到目标下标（0 起始）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="modId">要移动的 MOD ID。</param>
    /// <param name="targetIndex">目标下标；越界或与当前位置相同视为未移动。</param>
    /// <returns>
    /// 移动成功时返回移动前的顺序（保存失败时可用 <see cref="Restore"/> 回滚）；
    /// 未找到、越界或位置未变时返回 <see langword="null"/>。
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public IReadOnlyList<int>? Move(string gameId, int modId, int targetIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        List<int> order = [.. _store.ModsFor(gameId).Select(record => record.Id)];
        int from = order.IndexOf(modId);
        if (from < 0 || targetIndex < 0 || targetIndex >= order.Count || from == targetIndex)
        {
            return null;
        }

        IReadOnlyList<int> original = [.. order];
        order.RemoveAt(from);
        order.Insert(targetIndex, modId);
        _store.ReorderForGame(gameId, order);
        return original;
    }

    /// <summary>把顺序恢复为给定列表（保存失败回滚用）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="order">要恢复的 MOD ID 顺序。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="order"/> 为 <see langword="null"/>。</exception>
    public void Restore(string gameId, IReadOnlyList<int> order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(order);
        _store.ReorderForGame(gameId, order);
    }
}
