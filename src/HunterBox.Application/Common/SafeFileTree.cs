namespace HunterBox.Application.Common;

/// <summary>
/// 目录树安全枚举：递归时只跳过真正的链接（符号链接 / junction），
/// 防止备份、安装复制与还原跟随链接目标或在链接环上无限枚举。
/// </summary>
/// <remarks>
/// <para>
/// OneDrive 占位文件等非链接重解析点按普通文件处理，读取时触发下载，避免安装/迁移把占位文件误判为链接而漏拷；
/// 无法确认是否为链接时仍按 fail-closed 抛出，绝不把不确定条目当普通文件。
/// </para>
/// <para>
/// 使用 <see cref="IFileSystem.EnumerateFileSystemEntries"/> 逐层展开而不是
/// <see cref="SearchOption.AllDirectories"/>：后者会跟随目录链接进入目标目录。
/// 每个条目的属性读取失败时按 fail-closed 处理——直接抛出带上下文的
/// <see cref="IOException"/>，绝不把无法确认的条目当成普通文件复制。
/// </para>
/// <para>链接本身也不复制：备份/安装结果只包含真实文件，与「链接只删链接本身」的删除策略一致。</para>
/// </remarks>
public static class SafeFileTree
{
    /// <summary>枚举目录下的全部真实文件（递归），只跳过真正的链接。</summary>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="directory">根目录；必须存在。</param>
    /// <returns>文件绝对路径数组（顺序不保证）。</returns>
    /// <exception cref="IOException">枚举或读取属性失败（包括无法确认是否为链接）。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限枚举目录。</exception>
    public static string[] EnumerateFiles(IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string root = Path.GetFullPath(directory);
        FileAttributes rootAttributes = ReadAttributes(fileSystem.GetAttributes, root);
        if (IsLink(fileSystem, root, rootAttributes))
        {
            // 根目录本身是链接时拒绝枚举，绝不访问链接目标。
            throw new IOException($"目录本身是符号链接/junction，已拒绝枚举以避免复制链接目标：{root}");
        }

        return EnumerateFiles(
            root,
            fileSystem.EnumerateFileSystemEntries,
            fileSystem.GetAttributes,
            (entry, attributes) => IsLink(fileSystem, entry, attributes));
    }

    /// <summary>可注入枚举与属性读取的测试入口：用同一遍历逻辑模拟带链接的目录树。</summary>
    /// <param name="rootFullPath">根目录绝对路径。</param>
    /// <param name="enumerateEntries">返回某目录的直接子条目。</param>
    /// <param name="getAttributes">读取条目属性。</param>
    /// <param name="isLink">判断条目是否为链接；为 <see langword="null"/> 时按 ReparsePoint 位处理（测试入口默认）。</param>
    /// <returns>真实文件绝对路径数组（顺序不保证）。</returns>
    internal static string[] EnumerateFiles(
        string rootFullPath,
        Func<string, string[]> enumerateEntries,
        Func<string, FileAttributes> getAttributes,
        Func<string, FileAttributes, bool>? isLink = null)
    {
        ArgumentNullException.ThrowIfNull(enumerateEntries);
        ArgumentNullException.ThrowIfNull(getAttributes);
        isLink ??= static (_, attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

        List<string> files = [];
        Stack<string> pending = new();
        pending.Push(rootFullPath);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string entry in enumerateEntries(current))
            {
                FileAttributes attributes = ReadAttributes(getAttributes, entry);
                if (isLink(entry, attributes))
                {
                    // 不复制链接，也不进入链接目标：链接目标可能在HunterBox/游戏目录之外。
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        return [.. files];
    }

    /// <summary>单次遍历同时返回真实文件清单、是否含链接与链接条目数。</summary>
    /// <remarks>
    /// 第三个返回值 <c>ReparsePointCount</c> 目前仅旧备份迁移分支使用：
    /// <c>LegacyMigrationService</c> 用它生成「旧备份目录包含 N 个符号链接/junction」的告警文案，不要删除。
    /// </remarks>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="directory">根目录；必须存在。</param>
    /// <returns>真实文件绝对路径（跳过链接）、是否发现链接（根目录自身为链接时抛出）与链接条目数。</returns>
    /// <exception cref="IOException">根目录是链接或枚举/读取属性失败。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限枚举目录。</exception>
    public static (string[] Files, bool ContainsReparsePoint, int ReparsePointCount) EnumerateFilesAndCheckLinks(
        IFileSystem fileSystem,
        string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string root = Path.GetFullPath(directory);
        FileAttributes rootAttributes = ReadAttributes(fileSystem.GetAttributes, root);
        if (IsLink(fileSystem, root, rootAttributes))
        {
            throw new IOException($"目录本身是符号链接/junction，已拒绝枚举以避免复制链接目标：{root}");
        }

        bool containsLink = false;
        int linkCount = 0;
        List<string> files = [];
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string entry in fileSystem.EnumerateFileSystemEntries(current))
            {
                FileAttributes attributes = ReadAttributes(fileSystem.GetAttributes, entry);
                if (IsLink(fileSystem, entry, attributes))
                {
                    containsLink = true;
                    linkCount++;
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        return ([.. files], containsLink, linkCount);
    }

    /// <summary>检查目录树（含根目录自身）是否存在真正的链接；不跟随链接，OneDrive 占位文件不算链接。</summary>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="directory">根目录；必须存在。</param>
    /// <returns>存在链接时返回 <see langword="true"/>。</returns>
    /// <exception cref="IOException">枚举或读取属性失败（包括无法确认是否为链接）。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限枚举目录。</exception>
    public static bool ContainsReparsePoint(IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return ContainsReparsePoint(
            Path.GetFullPath(directory),
            fileSystem.EnumerateFileSystemEntries,
            fileSystem.GetAttributes,
            (entry, attributes) => IsLink(fileSystem, entry, attributes));
    }

    /// <summary>可注入枚举与属性读取的测试入口：用同一判定逻辑模拟带链接的目录树。</summary>
    /// <param name="rootFullPath">根目录绝对路径（含根自身检查）。</param>
    /// <param name="enumerateEntries">返回某目录的直接子条目。</param>
    /// <param name="getAttributes">读取条目属性。</param>
    /// <param name="isLink">判断条目是否为链接；为 <see langword="null"/> 时按 ReparsePoint 位处理（测试入口默认）。</param>
    /// <returns>存在链接时返回 <see langword="true"/>。</returns>
    internal static bool ContainsReparsePoint(
        string rootFullPath,
        Func<string, string[]> enumerateEntries,
        Func<string, FileAttributes> getAttributes,
        Func<string, FileAttributes, bool>? isLink = null)
    {
        ArgumentNullException.ThrowIfNull(enumerateEntries);
        ArgumentNullException.ThrowIfNull(getAttributes);
        isLink ??= static (_, attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

        Stack<(string Path, FileAttributes Attributes)> pending = new();
        pending.Push((rootFullPath, ReadAttributes(getAttributes, rootFullPath)));
        while (pending.Count > 0)
        {
            (string current, FileAttributes currentAttributes) = pending.Pop();
            if (isLink(current, currentAttributes))
            {
                return true;
            }

            foreach (string entry in enumerateEntries(current))
            {
                FileAttributes attributes = ReadAttributes(getAttributes, entry);
                if (isLink(entry, attributes))
                {
                    return true;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((entry, attributes));
                }
            }
        }

        return false;
    }

    /// <summary>单次遍历返回所有真实目录及其直接子项数（链接跳过；根目录自身不在结果里）。</summary>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="directory">根目录；必须存在。</param>
    /// <returns>目录绝对路径与直接子项数（含链接条目，用于判断目录是否为空）。</returns>
    /// <exception cref="IOException">枚举或读取属性失败。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限。</exception>
    internal static IReadOnlyList<(string Path, int EntryCount)> EnumerateDirectoriesWithEntryCount(
        IFileSystem fileSystem,
        string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string root = Path.GetFullPath(directory);
        FileAttributes rootAttributes = ReadAttributes(fileSystem.GetAttributes, root);
        if (IsLink(fileSystem, root, rootAttributes))
        {
            throw new IOException($"目录本身是符号链接/junction，已拒绝枚举以避免进入链接目标：{root}");
        }

        List<(string Path, int EntryCount)> directories = [];
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string[] entries = fileSystem.EnumerateFileSystemEntries(current);
            if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                // 子项数包含链接条目：与旧实现 IsDirectoryEmpty 的语义保持一致。
                directories.Add((current, entries.Length));
            }

            foreach (string entry in entries)
            {
                FileAttributes attributes = ReadAttributes(fileSystem.GetAttributes, entry);
                if (IsLink(fileSystem, entry, attributes))
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }

        return directories;
    }

    /// <summary>判断单个文件/目录是否为真正的链接（符号链接/junction）；读取属性失败按 fail-closed 抛出。</summary>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="path">条目路径。</param>
    /// <returns>是链接时返回 <see langword="true"/>；OneDrive 占位文件等非链接重解析点返回 <see langword="false"/>。</returns>
    /// <exception cref="IOException">读取属性失败或无法确认是否为链接。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限。</exception>
    public static bool IsReparseLink(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileAttributes attributes = ReadAttributes(fileSystem.GetAttributes, path);
        return IsLink(fileSystem, path, attributes);
    }

    /// <summary>先按 ReparsePoint 位快速排除普通条目，再通过端口确认是否为真正的链接。</summary>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="path">条目路径。</param>
    /// <param name="attributes">已读取的属性。</param>
    /// <returns>真正的链接返回 <see langword="true"/>。</returns>
    /// <exception cref="IOException">无法确认是否为链接（fail-closed）。</exception>
    private static bool IsLink(IFileSystem fileSystem, string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            return fileSystem.IsReparseLink(path, attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法确认重解析点是否为链接，已停止扫描以避免跟随链接：{path}", ex);
        }
    }

    /// <summary>读取条目属性；失败时按 fail-closed 转成带路径的 <see cref="IOException"/>。</summary>
    private static FileAttributes ReadAttributes(Func<string, FileAttributes> getAttributes, string entry)
    {
        try
        {
            return getAttributes(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法读取文件属性，已停止扫描以避免跟随符号链接：{entry}", ex);
        }
    }
}
