namespace HunterBox.Tests;

/// <summary>需要源码树布局的用例：找不到 <c>HunterBox.slnx</c> 时在发现阶段跳过。</summary>
internal sealed class SourceTreeFactAttribute : FactAttribute
{
    public SourceTreeFactAttribute()
    {
        if (SourceTreeLocator.TryFind() is null)
        {
            Skip = "找不到 HunterBox.slnx，跳过依赖源码树布局的用例。";
        }
    }
}

/// <summary>从测试输出目录向上定位仓库根（源码树布局）；门禁测试共用。</summary>
internal static class SourceTreeLocator
{
    /// <summary>
    /// 按需枚举时跳过的目录：从 <c>tools/gate-pruned-directories.txt</c> 读取，
    /// 与构建期行尾脚本共用同一份边界。
    /// </summary>
    private static readonly Lazy<HashSet<string>> PrunedNames = new(() => LoadPrunedNames(RootOrThrow()));

    /// <summary>仓库内全部参与门禁的文件（排除构建产物）；整个测试进程只枚举一次。</summary>
    private static readonly Lazy<string[]> RepositoryFiles = new(
        () => [.. EnumerateFilesPruned(RootOrThrow())]);

    /// <summary>尝试定位仓库根。</summary>
    /// <returns>仓库根目录；找不到时返回 <see langword="null"/>。</returns>
    public static string? TryFind()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HunterBox.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }

    /// <summary>定位仓库根，找不到时抛出。</summary>
    /// <returns>仓库根目录。</returns>
    /// <exception cref="DirectoryNotFoundException">找不到 <c>HunterBox.slnx</c>。</exception>
    public static string RootOrThrow() =>
        TryFind() ?? throw new DirectoryNotFoundException("找不到 HunterBox.slnx 所在仓库根目录");

    /// <summary>枚举目录下的 C# 文件（递归，带剪枝），跳过共享剪枝目录与链接。</summary>
    /// <remarks>从仓库级一次性枚举里过滤，不再为每个门禁重复遍历目录树：只服务 .cs，删除未使用参数。</remarks>
    /// <param name="directory">根目录。</param>
    /// <returns>文件绝对路径（顺序不保证）。</returns>
    public static string[] EnumerateSourceFiles(string directory) => EnumerateGateFiles(directory, ".cs");

    /// <summary>枚举目录下的 C# 与 XAML 文件，供行尾门禁共用。</summary>
    /// <param name="directory">根目录。</param>
    /// <returns>文件绝对路径（顺序不保证）。</returns>
    public static string[] EnumerateSourceAndXamlFiles(string directory) =>
        EnumerateGateFiles(directory, ".cs", ".xaml");

    /// <summary>判断目录名是否在剪枝集合内（构建产物、版本库与本地数据）。</summary>
    /// <param name="name">目录名（不含路径）。</param>
    /// <returns>应跳过时返回 <see langword="true"/>。</returns>
    internal static bool IsPrunedDirectory(string name) => PrunedNames.Value.Contains(name);

    /// <summary>剪枝清单相对仓库根的路径：脚本与测试共用同一份定义。</summary>
    internal const string PrunedListRelativePath = "tools/gate-pruned-directories.txt";

    /// <summary>
    /// 判断路径是否为真正的链接（符号链接/junction）；OneDrive 占位文件等非链接重解析点返回
    /// <see langword="false"/>，直接复用生产端口实现，避免测试再维护第三份判定。
    /// </summary>
    /// <param name="path">文件或目录路径。</param>
    /// <returns>是链接时返回 <see langword="true"/>。</returns>
    internal static bool IsReparseLink(string path) =>
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance.IsReparseLink(
            path,
            File.GetAttributes(path));

    /// <summary>按扩展名从仓库级缓存枚举里过滤目录下的门禁文件。</summary>
    /// <param name="directory">根目录。</param>
    /// <param name="extensions">允许的扩展名（含点，忽略大小写）。</param>
    /// <returns>文件绝对路径（顺序不保证）。</returns>
    private static string[] EnumerateGateFiles(string directory, params string[] extensions)
    {
        // 仓库级枚举已剪掉共享清单目录与链接，这里无需再过滤构建产物。
        string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return
        [
            .. EnumerateRepositoryFiles()
                .Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(file => extensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    /// <summary>枚举仓库内（首次调用时缓存的）全部参与门禁的文件；已排除剪枝目录与链接。</summary>
    /// <returns>文件绝对路径（只读快照）。</returns>
    public static IReadOnlyList<string> EnumerateRepositoryFiles() => RepositoryFiles.Value;

    /// <summary>读取共享剪枝清单；缺失或为空直接失败，避免门禁静默扩大扫描范围。</summary>
    /// <param name="root">仓库根目录。</param>
    /// <returns>目录名集合（忽略大小写）。</returns>
    /// <exception cref="InvalidOperationException">清单缺失或为空。</exception>
    internal static HashSet<string> LoadPrunedNames(string root)
    {
        string path = Path.Combine(
            root,
            PrunedListRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"找不到门禁剪枝目录清单：{path}");
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                _ = names.Add(trimmed);
            }
        }

        if (names.Count == 0)
        {
            throw new InvalidOperationException($"门禁剪枝目录清单为空，不能静默通过：{path}");
        }

        return names;
    }

    /// <summary>
    /// 递归枚举文件：遍历时跳过链接、共享剪枝清单中的目录与构建产物，避免进入链接目标或重复遍历。
    /// </summary>
    /// <param name="directory">根目录。</param>
    /// <returns>全部文件的绝对路径。</returns>
    internal static IEnumerable<string> EnumerateFilesPruned(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (IsReparseLink(file))
            {
                continue;
            }

            yield return file;
        }

        foreach (string subdirectory in Directory.EnumerateDirectories(directory))
        {
            if (IsReparseLink(subdirectory))
            {
                continue;
            }

            if (IsPrunedDirectory(Path.GetFileName(subdirectory)))
            {
                continue;
            }

            foreach (string file in EnumerateFilesPruned(subdirectory))
            {
                yield return file;
            }
        }
    }
}
