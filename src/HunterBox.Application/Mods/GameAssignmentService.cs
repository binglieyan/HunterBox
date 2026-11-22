using HunterBox.Domain.Mods;

namespace HunterBox.Application.Mods;

/// <summary>
/// 把「未归属游戏」的 MOD 记录归到指定游戏（A-11：归属规则从窗口下沉到用例层）。
/// </summary>
public sealed class GameAssignmentService
{
    private readonly IModStore _store;

    /// <summary>装配归属服务。</summary>
    /// <param name="store">MOD 记录存储。</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> 为 <see langword="null"/>。</exception>
    public GameAssignmentService(IModStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>把一组 MOD 记录归到指定游戏（仓储负责分配该游戏内的排序号）。</summary>
    /// <param name="modIds">要归属的 MOD ID；重复 ID 会被仓储按已归属拒绝。</param>
    /// <param name="gameId">目标游戏稳定 ID。</param>
    /// <returns>实际处理的记录数。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="modIds"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="InvalidOperationException">某条记录不存在或已归属其它游戏。</exception>
    public int Assign(IEnumerable<int> modIds, string gameId)
    {
        ArgumentNullException.ThrowIfNull(modIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        int assigned = 0;
        foreach (int id in modIds)
        {
            _store.AssignGame(id, gameId);
            assigned++;
        }

        return assigned;
    }
}
