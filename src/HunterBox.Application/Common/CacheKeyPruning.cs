namespace HunterBox.Application.Common;

/// <summary>缓存裁剪的共享键谓词：按游戏隔离并剔除已不存在的 MOD。</summary>
internal static class CacheKeyPruning
{
    /// <summary>键是否属于当前游戏且已不在存活集合中。</summary>
    /// <param name="key">缓存键（游戏稳定 ID + MOD ID）。</param>
    /// <param name="game">规范化后的游戏 ID。</param>
    /// <param name="aliveIds">存活 MOD ID。</param>
    /// <returns>应删除时返回 <see langword="true"/>。</returns>
    public static bool IsDead(
        (string GameId, int ModId) key,
        string game,
        IReadOnlySet<int> aliveIds) =>
        string.Equals(key.GameId, game, StringComparison.Ordinal) && !aliveIds.Contains(key.ModId);
}
