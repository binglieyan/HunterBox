using System.Globalization;

namespace HunterBox.Infrastructure.Games;

/// <summary>解析 Steam的 <c>config.vdf</c>，读取其中记录的「已安装 AppId」。</summary>
/// <remarks>
/// <c>config.vdf</c>的结构是
/// <c>InstallConfigStore / Software / Valve / Steam / apps / &lt;appid&gt;</c>；
/// 这里只关心 appid 与可选的 <c>installed</c> 标记，用于在 HKCU
/// <c>Software\Valve\Steam\Apps\&lt;appid&gt;\Installed</c> 缺失时兜底判断。
/// </remarks>
public static class SteamConfigIndex
{
    /// <summary>判断 <c>config.vdf</c> 是否记录某个 AppId 已安装。</summary>
    /// <param name="configVdf">config.vdf的内容。</param>
    /// <param name="appId">Steam AppId。</param>
    /// <returns>
    /// 已安装返回 <see langword="true"/>；内容可解析但未记录该 AppId 返回 <see langword="false"/>；
    /// 内容为空或损坏（无法判断）返回 <see langword="null"/>。
    /// </returns>
    public static bool? IsAppInstalled(string? configVdf, int appId)
    {
        if (appId <= 0)
        {
            return false;
        }

        SteamVdfNode? root = SteamVdfParser.Parse(configVdf);
        if (root is null)
        {
            return null;
        }

        SteamVdfNode? apps = FindAppsNode(root);
        if (apps is null)
        {
            return null;
        }

        SteamVdfNode? node = apps.Child(appId.ToString(CultureInfo.InvariantCulture));
        if (node is null)
        {
            return false;
        }

        string? installed = node.Child("installed")?.Value;
        return installed is null
            || installed.Equals("1", StringComparison.OrdinalIgnoreCase)
            || installed.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static SteamVdfNode? FindAppsNode(SteamVdfNode? root) =>
        root?.Child("InstallConfigStore")
            ?.Child("Software")
            ?.Child("Valve")
            ?.Child("Steam")
            ?.Child("apps");
}
