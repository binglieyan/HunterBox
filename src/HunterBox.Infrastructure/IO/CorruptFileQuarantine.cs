using System.Globalization;
using HunterBox.Application.Common;

namespace HunterBox.Infrastructure.IO;

/// <summary>
/// 损坏文件隔离：统一生成不覆盖已有备份的 <c>.bad-时间戳</c> 路径、改名与保留份数清理。
/// </summary>
/// <remarks>
/// 三个缓存/索引存储共用本辅助，避免各自维护一份改名逻辑；清理删除一律走
/// <see cref="IFileSystem.TryDeleteFile"/>，失败只记录警告，不影响主流程。
/// </remarks>
internal static class CorruptFileQuarantine
{
    /// <summary>默认保留份数上限：只保留最近 3 份 <c>.bad-*</c>，避免长期占用磁盘。</summary>
    public const int DefaultMaxCopies = 3;

    /// <summary>生成不覆盖已有备份的 <c>.bad-时间戳[-序号]</c> 路径。</summary>
    /// <param name="path">原始文件路径。</param>
    /// <param name="fileSystem">文件系统端口（存在性检查）。</param>
    /// <returns>尚未被占用的备份路径。</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空。</exception>
    private static string NextBadPath(string path, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string basePath = $"{path}.bad-{stamp}";
        string badPath = basePath;
        int suffix = 1;
        while (fileSystem.FileExists(badPath))
        {
            badPath = $"{basePath}-{suffix.ToString(CultureInfo.InvariantCulture)}";
            suffix++;
        }

        return badPath;
    }

    /// <summary>把文件改名成 <c>.bad-*</c> 备份；失败时返回 <see langword="null"/>，不抛异常。</summary>
    /// <param name="path">原始文件路径。</param>
    /// <param name="fileSystem">文件系统端口（改名统一走端口）。</param>
    /// <param name="error">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>备份路径；改名失败（被占用、目录只读等）时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    public static string? TryMoveAside(string path, IFileSystem fileSystem, out Exception? error)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        string badPath = NextBadPath(path, fileSystem);
        try
        {
            fileSystem.MoveFile(path, badPath);
            error = null;
            return badPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex;
            return null;
        }
    }

    /// <summary>
    /// 只保留最近 <see cref="DefaultMaxCopies"/> 份 <c>.bad-*</c>：按文件名内的时间戳排序，
    /// <paramref name="keep"/> 指定的文件一定保留；删除失败只记录警告。
    /// </summary>
    /// <param name="path">原始文件路径（用于推导备份文件名与目录）。</param>
    /// <param name="fileSystem">文件系统端口（枚举与删除）。</param>
    /// <param name="keep">必须保留的备份路径；为 <see langword="null"/> 时按时间戳保留最新几份。</param>
    /// <param name="warn">清理失败的告警回调。</param>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 或 <paramref name="warn"/> 为 <see langword="null"/>。</exception>
    public static void Prune(
        string path,
        IFileSystem fileSystem,
        string? keep,
        Action<string> warn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(warn);

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !fileSystem.DirectoryExists(directory))
        {
            return;
        }

        string prefix = Path.GetFileName(path) + ".bad-";
        List<(long Stamp, int Sequence, string Path)> copies = [];
        try
        {
            // 按文件名模式枚举，交给文件系统过滤，避免把整个目录的条目都物化。
            foreach (string file in fileSystem.EnumerateFiles(directory, prefix + "*"))
            {
                string name = Path.GetFileName(file);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                (long stamp, int sequence) = ParseStamp(name[prefix.Length..]);
                copies.Add((stamp, sequence, file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn($"枚举损坏文件备份失败：{ex.Message}");
            return;
        }

        PruneCopies(copies, fileSystem, keep, warn);
    }

    /// <summary>
    /// 按时间戳保留最新 <see cref="DefaultMaxCopies"/> 份；<paramref name="keep"/> 指定的文件一定保留。
    /// <see cref="Prune"/> 与 <see cref="PruneMany"/> 共用这一份保留策略。
    /// </summary>
    /// <param name="copies">候选备份（会就地排序）。</param>
    /// <param name="fileSystem">文件系统端口（删除）。</param>
    /// <param name="keep">必须保留的备份路径；为 <see langword="null"/> 时按时间戳保留最新几份。</param>
    /// <param name="warn">清理失败的告警回调。</param>
    private static void PruneCopies(
        List<(long Stamp, int Sequence, string Path)> copies,
        IFileSystem fileSystem,
        string? keep,
        Action<string> warn)
    {
        copies.Sort((left, right) =>
        {
            int byStamp = right.Stamp.CompareTo(left.Stamp);
            return byStamp != 0 ? byStamp : right.Sequence.CompareTo(left.Sequence);
        });

        // keep 已被改名到磁盘时（即使枚举遗漏）也计入一个保留名额，保证总数不超过上限。
        int budget = keep is not null ? DefaultMaxCopies - 1 : DefaultMaxCopies;
        foreach ((_, _, string file) in copies)
        {
            if (IsSamePath(file, keep))
            {
                continue;
            }

            if (budget > 0)
            {
                budget--;
                continue;
            }

            if (!fileSystem.TryDeleteFile(file))
            {
                warn($"清理损坏文件备份失败：{file}");
            }
        }
    }

    /// <summary>
    /// 一次枚举目录，为同一目录下的多个缓存文件修剪 <c>.bad-*</c> 备份（启动装载共用）。
    /// </summary>
    /// <param name="paths">原始文件路径（可来自同一目录的不同缓存文件）。</param>
    /// <param name="fileSystem">文件系统端口（枚举与删除）。</param>
    /// <param name="warn">清理失败的告警回调。</param>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/>、<paramref name="fileSystem"/> 或 <paramref name="warn"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">任一 <paramref name="paths"/> 为空。</exception>
    public static void PruneMany(IReadOnlyList<string> paths, IFileSystem fileSystem, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(warn);

        // 同一目录的多个缓存文件共用一次枚举；不同目录分别处理。
        Dictionary<string, List<string>> prefixesByDirectory = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            if (!prefixesByDirectory.TryGetValue(directory, out List<string>? prefixes))
            {
                prefixes = [];
                prefixesByDirectory[directory] = prefixes;
            }

            prefixes.Add(Path.GetFileName(path) + ".bad-");
        }

        foreach ((string directory, List<string> prefixes) in prefixesByDirectory)
        {
            if (!fileSystem.DirectoryExists(directory))
            {
                continue;
            }

            List<(long Stamp, int Sequence, string Path, string Prefix)> copies = [];
            try
            {
                // 所有备份名都包含 ".bad-" 段（后接时间戳），单次模式枚举即可覆盖同目录的全部缓存文件。
                foreach (string file in fileSystem.EnumerateFiles(directory, "*.bad-*"))
                {
                    string name = Path.GetFileName(file);
                    string? prefix = prefixes.Find(
                        candidate => name.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
                    if (prefix is null)
                    {
                        continue;
                    }

                    (long stamp, int sequence) = ParseStamp(name[prefix.Length..]);
                    copies.Add((stamp, sequence, file, prefix));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn($"枚举损坏文件备份失败：{ex.Message}");
                continue;
            }

            foreach (IGrouping<string, (long Stamp, int Sequence, string Path, string Prefix)> group
                in copies.GroupBy(copy => copy.Prefix, StringComparer.OrdinalIgnoreCase))
            {
                List<(long Stamp, int Sequence, string Path)> copiesOfPrefix =
                    [.. group.Select(copy => (copy.Stamp, copy.Sequence, copy.Path))];
                PruneCopies(copiesOfPrefix, fileSystem, keep: null, warn);
            }
        }
    }

    /// <summary>解析备份名里的时间戳（<c>yyyyMMddHHmmss</c>）与可选序号。</summary>
    /// <param name="tail">文件名中 <c>.bad-</c> 之后的部分。</param>
    /// <returns>时间戳数值与序号；无法解析时都为 0。</returns>
    private static (long Stamp, int Sequence) ParseStamp(string tail)
    {
        if (tail.Length < 14
            || !long.TryParse(tail.AsSpan(0, 14), NumberStyles.None, CultureInfo.InvariantCulture, out long stamp))
        {
            return (0, 0);
        }

        int sequence = 0;
        if (tail.Length > 15
            && tail[14] == '-'
            && int.TryParse(tail.AsSpan(15), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            sequence = parsed;
        }

        return (stamp, sequence);
    }

    /// <summary>比较两个路径是否指向同一文件（忽略大小写）。</summary>
    /// <param name="left">路径。</param>
    /// <param name="right">路径；为 <see langword="null"/> 时返回 <see langword="false"/>。</param>
    /// <returns>指向同一路径时返回 <see langword="true"/>。</returns>
    private static bool IsSamePath(string left, string? right) =>
        right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
