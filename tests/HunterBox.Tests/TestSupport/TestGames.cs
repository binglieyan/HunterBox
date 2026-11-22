using HunterBox.Domain.Games;

namespace HunterBox.Tests;

/// <summary>
/// A-08：测试共享的游戏配置样本。各测试类原先各自 <c>new GameProfile(...)</c>，
/// 这里集中一份，避免同一游戏的 Id/前缀/AppId 在多处漂移。
/// </summary>
internal static class TestGames
{
    /// <summary>荒野的 pak 补丁名前缀（静态下界 15；实际编号以扫描为准）。</summary>
    public const string WildsPatchPrefix = "re_chunk_000.pak.sub_000.pak.patch_";

    /// <summary>怪物猎人荒野：与生产清单同一份配置。</summary>
    public static GameProfile Wilds => GameCatalog.Wilds;

    /// <summary>怪物猎人世界：与生产清单同一份配置。</summary>
    public static GameProfile World => GameCatalog.World;

    /// <summary>怪物猎人崛起：与生产清单同一份配置。</summary>
    public static GameProfile Rise => GameCatalog.Rise;
}
