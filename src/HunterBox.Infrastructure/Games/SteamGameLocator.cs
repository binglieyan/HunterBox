using System.Text.RegularExpressions;

namespace HunterBox.Infrastructure.Games;

/// <summary>
/// 自动定位游戏安装目录的输入：注册表探测结果 + 可选的 VDF 内容覆盖（测试用）。
/// </summary>
/// <remarks>
/// 注册表读取放在 UI/组合根层（App），本记录只带原始值；<see cref="LibraryFoldersVdf"/> /
/// <see cref="ConfigVdf"/> 为 <see langword="null"/> 时由定位器按 <see cref="SteamRoot"/> 从磁盘读取。
/// </remarks>
public sealed record SteamLocateRequest
{
    /// <summary>Steam AppId。</summary>
    public int AppId { get; init; }

    /// <summary>游戏主程序文件名（用于校验目录）。</summary>
    public string ExecutableName { get; init; } = "";

    /// <summary>没有 appmanifest 时的兜底安装目录名。</summary>
    public string DefaultFolderName { get; init; } = "";

    /// <summary>Steam 根目录（HKCU <c>SteamPath</c> / HKLM <c>InstallPath</c>）；未知时为 <see langword="null"/>。</summary>
    public string? SteamRoot { get; init; }

    /// <summary>HKCU <c>Software\Valve\Steam\Apps\&lt;appid&gt;\Installed</c>的值；未知时为 <see langword="null"/>。</summary>
    public bool? AppInstalled { get; init; }

    /// <summary>HKLM Uninstall 里的 <c>InstallLocation</c> 兜底值。</summary>
    public string? UninstallLocation { get; init; }

    /// <summary><c>libraryfolders.vdf</c> 内容覆盖；<see langword="null"/> 时从 <see cref="SteamRoot"/> 读取。</summary>
    public string? LibraryFoldersVdf { get; init; }

    /// <summary><c>config.vdf</c> 内容覆盖；<see langword="null"/> 时从 <see cref="SteamRoot"/> 读取。</summary>
    public string? ConfigVdf { get; init; }
}

/// <summary>在 Steam 库目录里定位某个游戏的安装目录。</summary>
public static partial class SteamGameLocator
{
    /// <summary>在多个库目录中查找游戏。</summary>
    /// <param name="libraryRoots">Steam 库根目录（含 <c>steamapps</c>的那一级）。</param>
    /// <param name="appId">Steam AppId。</param>
    /// <param name="fallbackFolderName">没有 appmanifest 时按这个目录名兜底。</param>
    /// <param name="executableName">用于确认目录确实是对的（例如 <c>MonsterHunterWilds.exe</c>）。</param>
    /// <returns>找到的安装目录；找不到返回 <see langword="null"/>。</returns>
    internal static string? Find(
        IEnumerable<string> libraryRoots,
        int appId,
        string fallbackFolderName,
        string executableName)
    {
        ArgumentNullException.ThrowIfNull(libraryRoots);

        foreach (string root in libraryRoots)
        {
            string? found = FindInLibrary(root, appId, fallbackFolderName, executableName);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 完整定位流程（Steam 安装目录探测）：
    /// HKCU Apps Installed 前置判断 → Steam 标准目录 → <c>libraryfolders.vdf</c> 各库（含 apps 映射）
    /// → <c>config.vdf</c> 已安装兜底 → HKLM Uninstall 兜底。
    /// 主程序名非空时每一步都校验主程序存在；为空时退化为「目录存在」判断。
    /// </summary>
    /// <param name="request">注册表探测结果。</param>
    /// <returns>游戏根目录；找不到返回 <see langword="null"/>。</returns>
    public static string? Locate(SteamLocateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AppId <= 0)
        {
            return null;
        }

        bool? installed = request.AppInstalled;
        string? steamRoot = NormalizeDirectory(request.SteamRoot);
        string? libraryVdf = request.LibraryFoldersVdf;
        string? configVdf = request.ConfigVdf;
        bool configChecked = false;
        bool? configInstalled = null;
        if (steamRoot is not null)
        {
            if (libraryVdf is null)
            {
                libraryVdf = TryReadText(System.IO.Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"));
            }

            if (configVdf is null)
            {
                configVdf = TryReadText(System.IO.Path.Combine(steamRoot, "config", "config.vdf"));
            }

            installed ??= ReadConfigInstalled();
        }

        // HKCU Apps\<appid>\Installed 明确为 0 时不再扫描 Steam 库，只走 Uninstall 兜底。
        if (steamRoot is not null && installed != false)
        {
            string? found = LocateInLibraries(libraryVdf, steamRoot, request, ignoreAppFilter: false);
            if (found is not null)
            {
                return found;
            }

            // 新版 libraryfolders.vdf的 apps 映射可能缺失但仍安装着：按 config.vdf的已安装标记全库重扫一次。
            if (ReadConfigInstalled() == true)
            {
                found = LocateInLibraries(libraryVdf, steamRoot, request, ignoreAppFilter: true);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        string? uninstall = NormalizeDirectory(request.UninstallLocation);
        return uninstall is not null && IsGameDirectory(uninstall, request.ExecutableName) ? uninstall : null;

        // 两处「是否安装」判断共用同一次 config.vdf 解析（P-24）。
        bool? ReadConfigInstalled()
        {
            if (!configChecked)
            {
                configInstalled = SteamConfigIndex.IsAppInstalled(configVdf, request.AppId);
                configChecked = true;
            }

            return configInstalled;
        }
    }

    /// <summary>读取 <c>appmanifest_&lt;appid&gt;.acf</c> 里的 <c>installdir</c>。</summary>
    /// <param name="steamAppsDirectory">库目录下的 <c>steamapps</c>。</param>
    /// <param name="appId">Steam AppId。</param>
    /// <returns>安装目录名；文件不存在或解析失败返回 <see langword="null"/>。</returns>
    public static string? ReadInstallDir(string steamAppsDirectory, int appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamAppsDirectory);

        string manifest = System.IO.Path.Combine(steamAppsDirectory, $"appmanifest_{appId}.acf");
        string text;
        try
        {
            if (!File.Exists(manifest))
            {
                return null;
            }

            text = File.ReadAllText(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        Match match = InstallDirLine().Match(text);
        return match.Success ? match.Groups[1].Value.Replace(@"\\", @"\") : null;
    }

    /// <summary>判断目录是否包含指定主程序（主程序名为空时只判断目录存在）。</summary>
    /// <param name="directory">候选游戏根目录。</param>
    /// <param name="executableName">主程序文件名。</param>
    /// <returns>是有效游戏目录时返回 <see langword="true"/>。</returns>
    public static bool IsGameDirectory(string? directory, string executableName) =>
        !string.IsNullOrWhiteSpace(directory)
        && Directory.Exists(directory)
        && (string.IsNullOrWhiteSpace(executableName)
            || File.Exists(System.IO.Path.Combine(directory, executableName)));

    private static string? LocateInLibraries(
        string? libraryFoldersVdf,
        string steamRoot,
        SteamLocateRequest request,
        bool ignoreAppFilter)
    {
        List<SteamLibraryEntry> libraries = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        libraries.Add(new SteamLibraryEntry(steamRoot, new HashSet<int> { request.AppId }));
        seen.Add(steamRoot);
        if (!string.IsNullOrWhiteSpace(libraryFoldersVdf))
        {
            foreach (SteamLibraryEntry entry in SteamLibraryIndex.ParseEntries(libraryFoldersVdf))
            {
                if (seen.Add(entry.Path))
                {
                    libraries.Add(entry);
                }
            }
        }

        foreach (SteamLibraryEntry library in libraries)
        {
            if (!Directory.Exists(library.Path))
            {
                continue;
            }

            if (!ignoreAppFilter && library.AppIds.Count > 0 && !library.AppIds.Contains(request.AppId))
            {
                continue;
            }

            string? found = FindInLibrary(
                library.Path,
                request.AppId,
                request.DefaultFolderName,
                request.ExecutableName);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? FindInLibrary(
        string libraryRoot,
        int appId,
        string fallbackFolderName,
        string executableName)
    {
        string steamApps = System.IO.Path.Combine(libraryRoot, "steamapps");
        List<string> folderNames = [];
        string? installDir = ReadInstallDir(steamApps, appId);
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            folderNames.Add(installDir);
        }

        if (!string.IsNullOrWhiteSpace(fallbackFolderName)
            && !folderNames.Contains(fallbackFolderName, StringComparer.OrdinalIgnoreCase))
        {
            folderNames.Add(fallbackFolderName);
        }

        foreach (string folderName in folderNames)
        {
            string candidate = System.IO.Path.Combine(steamApps, "common", folderName);
            if (IsGameDirectory(candidate, executableName))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>把路径统一成反斜杠形式；可选要求目录存在（App 与 Infrastructure 共用同一实现）。</summary>
    /// <param name="path">原始路径。</param>
    /// <param name="requireExists">为 <see langword="true"/> 时目录不存在返回 <see langword="null"/>。</param>
    /// <returns>规范化后的路径；空路径或（要求存在但不存在）时返回 <see langword="null"/>。</returns>
    public static string? NormalizeDirectory(string? path, bool requireExists = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string trimmed = path.Trim().Replace('/', '\\');
        return !requireExists || Directory.Exists(trimmed) ? trimmed : null;
    }

    [GeneratedRegex("\"installdir\"\\s+\"(.+?)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirLine();
}
