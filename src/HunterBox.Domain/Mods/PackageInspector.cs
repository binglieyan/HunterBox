namespace HunterBox.Domain.Mods;

/// <summary>
/// 按 <c>docs/SPEC.md</c> 第 3 节判定一个包属于哪种 MOD。
/// 纯函数：只吃相对路径列表，不接触文件系统。
/// </summary>
/// <remarks>
/// <para>
/// 判定只用于展示/后续策略提示，<b>不推断安装前缀</b>：MOD 包内自带的
/// <c>nativePC</c>、<c>natives</c>、<c>reframework</c> 等目录就是最终落点，
/// 安装时按包内相对路径原样复制到游戏根目录。旧版本自动补
/// <c>nativePC</c>/<c>reframework</c> 前缀的做法已移除。
/// </para>
/// <para>判定顺序：FOMOD → pak → 已知内容根（散装）→ 仅 Lua → 仅 DLL → 未知。</para>
/// </remarks>
public static class PackageInspector
{
    /// <summary>MOD 元数据文件名（<c>modinfo.ini</c>，根级与子目录都可能出现）。</summary>
    public const string ModInfoFileName = "modinfo.ini";
    private const string RootFomodConfig = "moduleconfig.xml";
    private const string StandardFomodConfig = "fomod/moduleconfig.xml";

    /// <summary>
    /// 内容根/已知 MOD 目录名：安装时不能把这些单层目录当成「压缩包外壳」剥掉。
    /// </summary>
    private static readonly string[] KnownContentDirectoryNames =
    [
        "natives", "reframework", "nativepc", "pl", "wp", "plugins", "autorun",
        "weapon", "player", "art", "gamedesign",
    ];

    /// <summary>判定包种类。</summary>
    /// <param name="relativePaths">包内相对路径，允许任意分隔符与大小写。</param>
    /// <param name="gameId">当前游戏稳定 ID；保留参数是为了调用方兼容，判定本身不按游戏改前缀。</param>
    /// <returns>识别结论；<see cref="PackageInspection.TargetDirectory"/> 恒为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePaths"/> 为 <see langword="null"/>。</exception>
    public static PackageInspection Inspect(IEnumerable<string> relativePaths, string? gameId = null)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        List<string> paths = relativePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Normalize)
            .ToList();

        bool hasModInfo = paths.Exists(p =>
            p.Equals(ModInfoFileName, StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/" + ModInfoFileName, StringComparison.OrdinalIgnoreCase));

        if (paths.Count == 0)
        {
            return new PackageInspection(ModKind.Unknown, null, hasModInfo, ["安装包内没有文件"]);
        }

        if (paths.Exists(IsFomodConfigNormalized))
        {
            return new PackageInspection(ModKind.Fomod, null, hasModInfo, []);
        }

        if (paths.Exists(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
        {
            return new PackageInspection(ModKind.PakFiles, null, hasModInfo, []);
        }

        if (HasKnownContentRoot(paths))
        {
            return new PackageInspection(ModKind.LooseFiles, null, hasModInfo, []);
        }

        if (paths.Exists(p => p.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
        {
            return new PackageInspection(ModKind.RefScripts, null, hasModInfo, []);
        }

        if (paths.Exists(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            return new PackageInspection(ModKind.RefPlugins, null, hasModInfo, []);
        }

        return new PackageInspection(ModKind.Unknown, null, hasModInfo, ["无法识别的 MOD 格式"]);
    }

    /// <summary>把路径统一成「小写、正斜杠、无前导 <c>./</c>」的形式。</summary>
    /// <param name="path">原始路径。</param>
    /// <returns>规范化后的路径。</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空或空白。</exception>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.ToLowerInvariant();
    }

    /// <summary>判断目录名是否本身就是 MOD 内容根/已知结构目录（压缩包外壳剥壳时应保留）。</summary>
    /// <param name="directoryName">目录名。</param>
    /// <returns>是已知结构目录时返回 <see langword="true"/>。</returns>
    public static bool IsKnownContentDirectoryName(string directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return false;
        }

        return KnownContentDirectoryNames.Contains(directoryName.Trim(), StringComparer.OrdinalIgnoreCase)
            || IsGameFolderName(directoryName);
    }

    /// <summary>包内任意层级出现已知内容根目录，即视为散装 MOD（兼容多套一层包装目录的包）。</summary>
    private static bool HasKnownContentRoot(List<string> paths) =>
        paths.Exists(p => p.Split('/').Any(segment => IsKnownContentDirectoryName(segment)));

    /// <summary>判断包内路径是否是 FOMOD 配置文件（根级或 <c>fomod/</c> 目录下的 moduleconfig.xml）。</summary>
    /// <param name="relativePath">包内相对路径（允许任意分隔符）。</param>
    /// <returns>是配置文件时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> 为空或空白。</exception>
    public static bool IsFomodConfig(string relativePath) => IsFomodConfigNormalized(Normalize(relativePath));

    /// <summary>
    /// 判断已规范化的包内路径是否是 FOMOD 配置文件；调用方已执行 <see cref="Normalize"/> 时用它避免重复规范化。
    /// </summary>
    /// <param name="normalizedPath">已规范化的相对路径（小写、正斜杠；大小写不敏感比较）。</param>
    /// <returns>是配置文件时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="normalizedPath"/> 为空或空白。</exception>
    public static bool IsFomodConfigNormalized(string normalizedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPath);
        return normalizedPath.Equals(RootFomodConfig, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Equals(StandardFomodConfig, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>FOMOD 配置在包内的候选位置（先标准 <c>fomod/</c>，再根目录）。</summary>
    public static IReadOnlyList<string> FomodConfigCandidatePaths { get; } = [StandardFomodConfig, RootFomodConfig];

    private static bool IsGameFolderName(string name) =>
        name.Equals("MonsterHunterWilds", StringComparison.OrdinalIgnoreCase)
        || name.Equals("MonsterHunterRise", StringComparison.OrdinalIgnoreCase)
        || name.Equals("MonsterHunterWorld", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Monster Hunter World", StringComparison.OrdinalIgnoreCase);
}
