using HunterBox.Domain.Mods;

namespace HunterBox.Application.Mods;

/// <summary>多游戏隔离的公共校验：所有破坏性/写盘用例在操作前必须确认 MOD 属于当前游戏。</summary>
internal static class GameScope
{
    /// <summary>跨游戏操作被拒绝时的统一提示文案。</summary>
    public const string WrongGameMessage = "MOD 不属于当前游戏，请切换游戏后操作";

    /// <summary>确认记录属于当前游戏，否则抛出可诊断的 <see cref="InvalidOperationException"/>。</summary>
    /// <param name="mod">待操作的 MOD 记录。</param>
    /// <param name="gameId">当前游戏稳定 ID。</param>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="InvalidOperationException">记录未归属或属于其它游戏。</exception>
    public static void EnsureBelongsTo(ModRecord mod, string gameId)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        if (!mod.BelongsTo(gameId))
        {
            throw new InvalidOperationException(WrongGameMessage);
        }
    }
}
