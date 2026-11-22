using HunterBox.Domain.Games;

namespace HunterBox.Application.Models;

/// <summary>游戏稳定 ID 的小写规范化缓存：两个模型缓存共用同一实现。</summary>
/// <remarks>
/// 只在调用方锁内使用，本身不加锁；正常只有寥寥几个游戏 ID，超限说明调用方在传随机值。
/// </remarks>
internal sealed class GameIdNormalizationCache
{
    private const int MaxEntries = 64;
    private readonly Dictionary<string, string> _normalized = new(StringComparer.Ordinal);

    /// <summary>规范化游戏 ID：命中缓存时零分配返回。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <returns>小写游戏 ID。</returns>
    public string Normalize(string gameId)
    {
        if (_normalized.TryGetValue(gameId, out string? normalized))
        {
            return normalized;
        }

        if (_normalized.Count >= MaxEntries)
        {
            _normalized.Clear();
        }

        normalized = GameProfile.NormalizeId(gameId);
        _normalized[gameId] = normalized;
        return normalized;
    }
}
