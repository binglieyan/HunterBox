namespace HunterBox.Domain.Games;

/// <summary>内置游戏清单：稳定 ID、Steam AppId、pak 配置与显示名的唯一来源。</summary>
public static class GameCatalog
{
    /// <summary>
    /// 怪物猎人荒野：pak 走 <c>pak_mods</c>。静态下界兜底 15（当前本体 patch 已到 015）；
    /// 实际编号以扫描游戏根目录 + <c>pak_mods</c> 为准，后续更新/DLC 不需要改这个值。
    /// </summary>
    public static GameProfile Wilds { get; } = new(
        "mhws",
        "怪物猎人荒野",
        "re_chunk_000.pak.sub_000.pak.patch_",
        15,
        2246340,
        "MonsterHunterWilds.exe",
        "MonsterHunterWilds",
        "monsterhunterwilds");

    /// <summary>
    /// 怪物猎人崛起：pak 走 <c>pak_mods</c>。静态下界兜底 1（Steam 本体+曙光当前只提供 patch_001）；
    /// 实际编号仍以扫描结果为准。
    /// </summary>
    public static GameProfile Rise { get; } = new(
        "mhr",
        "怪物猎人崛起",
        "re_chunk_000.pak.patch_",
        1,
        1446780,
        "MonsterHunterRise.exe",
        "MonsterHunterRise",
        "monsterhunterrise");

    /// <summary>怪物猎人世界：不使用 <c>pak_mods</c>，pak 当普通文件处理；下界填 0（不参与编号）。</summary>
    public static GameProfile World { get; } = new(
        "mhw",
        "怪物猎人世界",
        null,
        0,
        582010,
        "MonsterHunterWorld.exe",
        "Monster Hunter World",
        "monsterhunterworld");

    /// <summary>界面展示顺序。</summary>
    public static IReadOnlyList<GameProfile> All { get; } = [Wilds, Rise, World];

    /// <summary>按稳定 ID 查找游戏配置（大小写不敏感）。</summary>
    /// <param name="id">稳定 ID（如 <c>mhws</c>）。</param>
    /// <returns>游戏配置；未知 ID 返回 <see langword="null"/>。</returns>
    public static GameProfile? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return All.FirstOrDefault(game => string.Equals(game.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按 Steam AppId 查找稳定游戏 ID（旧数据迁移/配置兼容用）。</summary>
    /// <param name="appId">Steam AppId。</param>
    /// <returns>稳定 ID；未知 AppId 返回 <see langword="null"/>。</returns>
    public static string? FindIdByAppId(int appId) =>
        appId <= 0 ? null : All.FirstOrDefault(game => game.SteamAppId == appId)?.Id;
}
