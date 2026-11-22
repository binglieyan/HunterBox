using HunterBox.Domain.Games;

namespace HunterBox.Domain.Models;

/// <summary>支持模型改写的游戏。</summary>
public enum ModelGame
{
    /// <summary>无法判断（没有模型/游戏相关路径）。</summary>
    Unknown = 0,

    /// <summary>怪物猎人：荒野（<c>natives/stm/art/model</c> 等）。</summary>
    Wilds = 1,

    /// <summary>怪物猎人：崛起（<c>natives/stm/player|weapon</c> 等）。</summary>
    Rise = 2,

    /// <summary>怪物猎人：世界（<c>nativepc/pl|wp</c>）。</summary>
    World = 3,
}

/// <summary>游戏稳定 ID 与显示名（与宿主配置里的游戏标识一致）。</summary>
public static class ModelGameInfo
{
    /// <summary>取游戏的稳定 ID（仅测试/诊断使用；生产代码直接使用游戏配置里的稳定 ID）。</summary>
    /// <param name="game">游戏。</param>
    /// <returns><c>mhws</c> / <c>mhr</c> / <c>mhw</c>；未知游戏返回空串。</returns>
    public static string GetId(ModelGame game) => game switch
    {
        ModelGame.Wilds => GameCatalog.Wilds.Id,
        ModelGame.Rise => GameCatalog.Rise.Id,
        ModelGame.World => GameCatalog.World.Id,
        _ => string.Empty,
    };

    /// <summary>按稳定 ID 解析游戏。</summary>
    /// <param name="id">稳定 ID（大小写不敏感）。</param>
    /// <param name="game">解析结果。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParseId(string? id, out ModelGame game)
    {
        game = ModelGame.Unknown;
        GameProfile? profile = GameCatalog.Find(id);
        if (profile is null)
        {
            return false;
        }

        game = profile.Id.Equals(GameCatalog.Wilds.Id, StringComparison.OrdinalIgnoreCase)
            ? ModelGame.Wilds
            : profile.Id.Equals(GameCatalog.Rise.Id, StringComparison.OrdinalIgnoreCase)
                ? ModelGame.Rise
                : ModelGame.World;
        return true;
    }

    /// <summary>取游戏的界面显示名。</summary>
    /// <param name="game">游戏。</param>
    /// <returns>如「怪物猎人：荒野」；未知游戏返回空串。</returns>
    public static string GetDisplayName(ModelGame game) => game switch
    {
        ModelGame.Wilds => GameCatalog.Wilds.DisplayName,
        ModelGame.Rise => GameCatalog.Rise.DisplayName,
        ModelGame.World => GameCatalog.World.DisplayName,
        _ => string.Empty,
    };
}
