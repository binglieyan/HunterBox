using HunterBox.Domain.Layout;
using HunterBox.Domain.Paks;

namespace HunterBox.Domain.Games;

/// <summary>一款受支持游戏的静态配置。</summary>
/// <param name="Id">稳定标识（例如 <c>mhws</c>），用于配置持久化。</param>
/// <param name="DisplayName">界面显示名。</param>
/// <param name="PakPatchPrefix">
/// pak 编号前缀；为 <see langword="null"/> 表示该游戏不使用 REFramework的
/// <c>pak_mods</c> 目录（pak 按普通文件处理）。
/// </param>
/// <param name="PatchNumberFloor">
/// 编号起点下界：实际起点 = max(该下界, 已有最大 patch 号) + 1；下界始终生效。
/// 该值不随游戏更新/DLC 维护；不使用 pak_mods 的游戏填 0。
/// </param>
/// <param name="SteamAppId">Steam AppId，用于自动定位游戏目录；0 表示不支持自动定位。</param>
/// <param name="ExecutableName">主程序文件名，用于确认目录正确。</param>
/// <param name="DefaultFolderName">没有 appmanifest 时的兜底目录名。</param>
/// <param name="NexusGamePath">NexusMods 站点上的游戏路径段（如 <c>monsterhunterwilds</c>）。</param>
public sealed record GameProfile(
    string Id,
    string DisplayName,
    string? PakPatchPrefix,
    int PatchNumberFloor,
    int SteamAppId = 0,
    string ExecutableName = "",
    string DefaultFolderName = "",
    string NexusGamePath = "")
{
    /// <summary>是否使用 <c>pak_mods</c> 目录（荒野 / 崛起为 <see langword="true"/>）。</summary>
    public bool UsesPakModsFolder => !string.IsNullOrEmpty(PakPatchPrefix);

    /// <summary>是否配置了用于校验目录的主程序文件名。</summary>
    public bool HasExecutableName => !string.IsNullOrWhiteSpace(ExecutableName);

    /// <summary>规范化游戏稳定 ID：去首尾空白并统一小写，作为跨层缓存键的唯一实现。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <returns>可安全用作缓存键的小写 ID。</returns>
    /// <exception cref="ArgumentException">为空或包含路径字符。</exception>
    public static string NormalizeId(string gameId) => BoxPaths.NormalizeGameId(gameId).ToLowerInvariant();

    /// <summary>按当前命名策略生成本次放置用的 pak 配置。</summary>
    /// <param name="mode">命名策略。</param>
    /// <returns>pak 放置配置。</returns>
    /// <exception cref="InvalidOperationException">该游戏不使用 <c>pak_mods</c>。</exception>
    public PakProfile CreatePakProfile(PakNamingMode mode)
    {
        if (!UsesPakModsFolder)
        {
            throw new InvalidOperationException($"{DisplayName} 不使用 pak_mods 目录");
        }

        return new PakProfile(PakPatchPrefix!, PatchNumberFloor, mode);
    }
}
