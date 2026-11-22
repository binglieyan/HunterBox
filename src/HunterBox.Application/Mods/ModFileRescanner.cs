using HunterBox.Application.Common;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.Application.Mods;

/// <summary>
/// 启动时重扫 MOD的 <c>files/</c> 目录，修正记录里固化的文件清单。
/// </summary>
/// <remarks>
/// 用户可能手工往 <c>Mods/&lt;id&gt;/files</c> 里加/删文件；旧列表只在安装时固化 <see cref="ModRecord.RelativeFiles"/>，
/// 不重扫会导致启用时复制失败或文件清单悬空。本类只做清单修正，不改 Kind/TargetDirectory。
/// </remarks>
public static class ModFileRescanner
{
    /// <summary>重扫指定 MOD的文件清单。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="mods">要检查的 MOD 记录。</param>
    /// <param name="fileSystem">文件系统端口（枚举与空目录清理）。</param>
    /// <returns>至少一条记录被修正时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static bool Rescan(BoxPaths paths, IEnumerable<ModRecord> mods, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(fileSystem);

        bool changed = false;
        foreach (ModRecord mod in mods)
        {
            string filesDir = paths.FilesDirectory(mod.Id);
            if (!fileSystem.DirectoryExists(filesDir))
            {
                continue;
            }

            // 一次遍历同时得到文件清单与目录树，空目录清理复用同一份数据，不再递归枚举两遍。
            TreeScan scan = ScanTree(filesDir, fileSystem);
            DeleteEmptyDirectories(scan, fileSystem);

            List<string> actual = [.. scan.Files
                .Select(file => Path.GetRelativePath(filesDir, file).Replace('\\', '/'))
                .Where(relative => !string.Equals(relative, "screenshot.png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)];

            if (actual.Count == mod.RelativeFiles.Count
                && actual.SequenceEqual(mod.RelativeFiles, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            mod.RelativeFiles = actual;
            changed = true;
        }

        return changed;
    }

    /// <summary>目录树扫描结果：文件、子目录与每个目录的直接条目数（用于判断空目录）。</summary>
    /// <param name="Files">全部文件绝对路径。</param>
    /// <param name="Directories">全部子目录绝对路径（不含根）。</param>
    /// <param name="EntryCounts">目录 → 直接条目数。</param>
    private sealed record TreeScan(
        List<string> Files,
        List<string> Directories,
        Dictionary<string, int> EntryCounts);

    /// <summary>单次遍历目录树，收集文件/目录与直接条目数。</summary>
    /// <param name="root">根目录。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <returns>扫描结果。</returns>
    private static TreeScan ScanTree(string root, IFileSystem fileSystem)
    {
        List<string> files = [];
        List<string> directories = [];
        Dictionary<string, int> entryCounts = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string[] entries = fileSystem.EnumerateFileSystemEntries(current);
            entryCounts[current] = entries.Length;
            foreach (string entry in entries)
            {
                if (fileSystem.DirectoryExists(entry))
                {
                    directories.Add(entry);
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        return new TreeScan(files, directories, entryCounts);
    }

    /// <summary>按扫描结果自底向上删除空目录（保留根）；单个目录失败不影响其余清理。</summary>
    /// <param name="scan">目录树扫描结果。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    private static void DeleteEmptyDirectories(TreeScan scan, IFileSystem fileSystem)
    {
        // 按路径长度倒序近似「先子后父」：删除子目录后把父目录的直接条目数减一，
        // 父目录因此变空时会在后续迭代里一并删除，不再逐个调用 IsDirectoryEmpty。
        scan.Directories.Sort((left, right) => right.Length.CompareTo(left.Length));
        foreach (string directory in scan.Directories)
        {
            if (scan.EntryCounts.GetValueOrDefault(directory) != 0)
            {
                continue;
            }

            try
            {
                fileSystem.DeleteEmptyDirectory(directory);
                string? parent = Path.GetDirectoryName(directory);
                if (parent is not null
                    && scan.EntryCounts.TryGetValue(parent, out int count)
                    && count > 0)
                {
                    scan.EntryCounts[parent] = count - 1;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个目录删除失败（并发写入/占用）保留，继续清理其它目录。
            }
        }
    }
}
