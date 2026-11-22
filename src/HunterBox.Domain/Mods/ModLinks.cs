namespace HunterBox.Domain.Mods;

/// <summary>把 MOD 元数据转成可打开的链接/路径。</summary>
public static class ModLinks
{
    private const string NexusGamePath = "monsterhunterwilds";

    /// <summary>按 NexusMods 数字 ID 生成 MOD 页面地址。</summary>
    /// <param name="nexusId">NexusMods ID；为 <see langword="null"/> 或非正数时返回 <see langword="null"/>。</param>
    /// <param name="nexusGamePath">Nexus的怪物猎人行路径，默认荒野。</param>
    /// <returns>可打开的 https 地址；无法生成时为 <see langword="null"/>。</returns>
    public static string? NexusUrl(int? nexusId, string nexusGamePath = NexusGamePath) =>
        nexusId is > 0
            ? $"https://www.nexusmods.com/{nexusGamePath}/mods/{nexusId.Value}"
            : null;

    /// <summary>判断字符串是否是可安全打开的 http(s) 地址。</summary>
    /// <param name="url">待检查的地址。</param>
    /// <returns>是 http/https 绝对地址时返回 <see langword="true"/>。</returns>
    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
