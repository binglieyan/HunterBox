using HunterBox.Domain.Mods;

namespace HunterBox.Application.Install;

/// <summary>包内的一个可选变体（例如「Shop Tweaks」里每个子目录各是一套安装）。</summary>
/// <param name="Name">显示名（子目录名或 modinfo 里的名字）。</param>
/// <param name="Root">该变体在包内的根目录（正斜杠、小写、无尾斜杠）。</param>
/// <param name="Metadata">从该目录内 <c>modinfo.ini</c> 读到的元数据。</param>
public sealed record PackageVariant(string Name, string Root, ModMetadata Metadata);

/// <summary>
/// 识别「多目录变体包」：根下多个子目录各自是一套安装。
/// </summary>
/// <remarks>
/// 识别面（.5）：
/// <list type="number">
/// <item>子目录里带 <c>modinfo.ini</c>（最可靠，名称/元数据直接可见）；</item>
/// <item>没有 modinfo 时，子目录自身能被 <see cref="PackageInspector"/> 识别成一种 MOD
/// （例如每个子目录各带 <c>natives/</c> 或 <c>*.pak</c>）。</item>
/// </list>
/// 普通包（根下同时有 <c>natives</c> 与 <c>reframework</c>）不会被误判成变体：
/// 已知内容根目录不参与候选。
/// </remarks>
public static class PackageVariantScanner
{

    /// <summary>扫描变体。</summary>
    /// <param name="files">包内全部文件（相对路径）。</param>
    /// <param name="readFile">按相对路径读取文本（用于读 modinfo.ini）。</param>
    /// <param name="gameId">当前游戏稳定 ID（用于识别无 modinfo的子目录结构；可空）。</param>
    /// <returns>至少两个变体时返回列表，否则返回空。</returns>
    public static IReadOnlyList<PackageVariant> Scan(
        IReadOnlyList<string> files,
        Func<string, string?> readFile,
        string? gameId = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(readFile);

        List<PackageVariant> byModInfo = ScanModInfoRoots(files, readFile);
        if (byModInfo.Count >= 2)
        {
            return byModInfo;
        }

        List<PackageVariant> byStructure = ScanStructureRoots(files, readFile, gameId);
        return byStructure.Count >= 2 ? byStructure : [];
    }

    /// <summary>收集「直接包含 modinfo.ini的目录」作为变体。</summary>
    private static List<PackageVariant> ScanModInfoRoots(
        IReadOnlyList<string> files,
        Func<string, string?> readFile)
    {
        List<string> roots = [];
        foreach (string file in files)
        {
            if (!file.EndsWith("/" + PackageInspector.ModInfoFileName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(file, PackageInspector.ModInfoFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string root = file.Contains('/') ? file[..file.LastIndexOf('/')] : string.Empty;
            if (root.Length > 0 && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(root);
            }
        }

        return [.. roots.Select(root => CreateVariant(root, readFile))];
    }

    /// <summary>没有 modinfo 时，按「子目录各自能被识别成 MOD」识别变体。</summary>
    private static List<PackageVariant> ScanStructureRoots(
        IReadOnlyList<string> files,
        Func<string, string?> readFile,
        string? gameId)
    {
        // 一次遍历按首段分组，避免为每个顶层目录重新过滤整个文件表（P-16）。
        Dictionary<string, List<string>> byRoot = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            int separator = file.IndexOf('/');
            if (separator <= 0)
            {
                continue;
            }

            string root = file[..separator];
            if (PackageInspector.IsKnownContentDirectoryName(root))
            {
                continue;
            }

            if (!byRoot.TryGetValue(root, out List<string>? subFiles))
            {
                subFiles = [];
                byRoot[root] = subFiles;
            }

            subFiles.Add(file[(separator + 1)..]);
        }

        List<PackageVariant> candidates = [];
        foreach ((string root, List<string> subFiles) in byRoot)
        {
            if (PackageInspector.Inspect(subFiles, gameId).Kind == ModKind.Unknown)
            {
                continue;
            }

            candidates.Add(CreateVariant(root, readFile));
        }

        return candidates;
    }

    private static PackageVariant CreateVariant(string root, Func<string, string?> readFile)
    {
        ModMetadata metadata = ModInfoParser.Parse(readFile(root + "/" + PackageInspector.ModInfoFileName));
        string name = string.IsNullOrWhiteSpace(metadata.Name) ? root : metadata.Name!;
        return new PackageVariant(name, root, metadata);
    }
}
