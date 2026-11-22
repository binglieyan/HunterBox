using HunterBox.Domain.Layout;

namespace HunterBox.Application.Common;

/// <summary>
/// 空目录清理的统一实现：自底向上删空目录，不清空非空目录；
/// 默认保留清理上界自身，<c>includeStopAt</c> 为 true 时才允许删除上界。
/// </summary>
/// <remarks>
/// 枚举/删除失败时按调用方的异常策略处理：批量清理方法吞掉 IO/权限异常（不中断主流程），
/// 向上清理方法让异常冒泡（由调用方决定告警或保守停止）。路径包含判断统一走
/// <see cref="BoxPaths.IsSameOrInside"/>。
/// </remarks>
internal static class DirectoryPruner
{
    /// <summary>删除目录树下的全部空目录（保留 <paramref name="root"/> 自身）；失败不影响主流程。</summary>
    /// <param name="root">根目录。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    internal static void DeleteEmptyDirectories(string root, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (!fileSystem.DirectoryExists(root))
        {
            return;
        }

        try
        {
            // 一次遍历拿到目录与直接子项数，自底向上删除；不再逐个 IsDirectoryEmpty 重新枚举。
            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string path, int entryCount) in SafeFileTree.EnumerateDirectoriesWithEntryCount(fileSystem, root))
            {
                counts[path] = entryCount;
            }

            foreach (string directory in counts.Keys.OrderByDescending(Depth))
            {
                if (counts[directory] != 0)
                {
                    continue;
                }

                fileSystem.DeleteEmptyDirectory(directory);
                string? parent = Path.GetDirectoryName(directory);
                if (parent is not null && counts.TryGetValue(parent, out int parentCount) && parentCount > 0)
                {
                    counts[parent] = parentCount - 1;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理空目录失败不影响主流程；残留空目录由后续操作忽略。
        }
    }

    /// <summary>目录深度（分隔符数），用于自底向上删除。</summary>
    private static int Depth(string path) =>
        path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);

    /// <summary>从 <paramref name="directory"/> 向上删除空目录，到 <paramref name="stopAt"/> 为止。</summary>
    /// <param name="directory">起始目录；可为 <see langword="null"/>。</param>
    /// <param name="stopAt">清理上界（游戏根目录或备份根目录）。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="includeStopAt">为 <see langword="true"/> 时允许删除上界自身（仅用于隔离目录等自建根）。</param>
    /// <exception cref="IOException">枚举或删除失败。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限。</exception>
    internal static void PruneEmptyDirectoriesUpward(
        string? directory,
        string stopAt,
        IFileSystem fileSystem,
        bool includeStopAt = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stopAt);
        ArgumentNullException.ThrowIfNull(fileSystem);

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        while (!string.IsNullOrEmpty(directory))
        {
            string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (!BoxPaths.IsSameOrInside(current, root))
            {
                // 起始目录在上界之外时立即停止，绝不越界向上删除用户目录。
                return;
            }

            bool atRoot = string.Equals(current, root, StringComparison.OrdinalIgnoreCase);
            if ((atRoot && !includeStopAt)
                || !fileSystem.DirectoryExists(current)
                || !fileSystem.IsDirectoryEmpty(current))
            {
                return;
            }

            fileSystem.DeleteEmptyDirectory(current);
            if (atRoot)
            {
                return;
            }

            directory = Path.GetDirectoryName(current);
        }
    }
}
