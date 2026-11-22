using System.Globalization;

namespace HunterBox.Domain.Layout;

/// <summary>
/// HunterBox自身的磁盘布局（见 <c>docs/SPEC.md</c> 第 2 节）。
/// 只负责计算路径与做路径安全校验，不创建任何目录、不读写文件。
/// </summary>
/// <remarks>
/// 所有对外暴露的路径都是绝对路径；<see cref="ResolveInside"/> 与
/// <see cref="NormalizeRelative"/> 共同保证外部传入的相对路径无法逃出目标目录。
/// </remarks>
public sealed class BoxPaths
{
    /// <summary>Windows 保留设备名（含扩展名形式）；作为路径段会写到设备而不是文件。</summary>
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Windows 非法文件名字符；<see cref="Path.GetInvalidFileNameChars"/> 每次调用都会新建数组。</summary>
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>用HunterBox根目录（通常是程序所在目录）初始化布局。</summary>
    /// <param name="root">HunterBox根目录，会规范化为绝对路径。</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> 为 <see langword="null"/>、空或空白。</exception>
    public BoxPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>HunterBox根目录（绝对路径）。</summary>
    public string Root { get; }

    /// <summary>MOD 存放根目录：<c>&lt;Root&gt;/Mods</c>。</summary>
    public string ModsDirectory => Path.Combine(Root, "Mods");

    /// <summary>被覆盖的游戏原文件备份根目录：<c>&lt;Root&gt;/Mods/.backup</c>（其下按游戏分目录）。</summary>
    public string BackupRootDirectory => Path.Combine(ModsDirectory, ".backup");

    /// <summary>解压等临时文件目录：<c>&lt;Root&gt;/Temp</c>。</summary>
    public string TempDirectory => Path.Combine(Root, "Temp");

    /// <summary>
    /// 安装包归档目录：<c>&lt;Root&gt;/Download</c>（安装选项：复制/移动安装包到这里）。
    /// </summary>
    public string DownloadDirectory => Path.Combine(Root, "Download");

    /// <summary>
    /// 安装/重装的盒内暂存根目录：<c>&lt;Root&gt;/Mods/.staging</c>。
    /// </summary>
    /// <remarks>
    /// 与 <c>Mods</c> 同卷：先把内容完整写进 <c>.staging/&lt;guid&gt;/files</c>，
    /// 成功后再用目录改名换入 <c>Mods/&lt;id&gt;/files</c>，失败时只删暂存目录、不留半成品。
    /// </remarks>
    public string StagingDirectory => Path.Combine(ModsDirectory, ".staging");

    /// <summary>全局设置文件：<c>&lt;Root&gt;/hunterbox.ini</c>。</summary>
    public string SettingsFile => Path.Combine(Root, "hunterbox.ini");

    /// <summary>某个 MOD的记录目录。</summary>
    /// <param name="id">MOD 持久 ID。</param>
    /// <returns><c>&lt;Root&gt;/Mods/&lt;id&gt;</c>。</returns>
    public string ModDirectory(int id) =>
        Path.Combine(ModsDirectory, id.ToString(CultureInfo.InvariantCulture));

    /// <summary>某个 MOD的文件本体目录。</summary>
    /// <param name="id">MOD 持久 ID。</param>
    /// <returns><c>&lt;Root&gt;/Mods/&lt;id&gt;/files</c>。</returns>
    public string FilesDirectory(int id) => Path.Combine(ModDirectory(id), "files");

    /// <summary>某个游戏的原文件备份目录：<c>&lt;Root&gt;/Mods/.backup/&lt;gameId&gt;</c>。</summary>
    /// <param name="gameId">游戏稳定 ID（如 <c>mhws</c>），会作为安全目录名校验。</param>
    /// <returns>该游戏的备份根目录绝对路径。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空或包含路径字符。</exception>
    public string BackupDirectory(string gameId) =>
        Path.Combine(BackupRootDirectory, NormalizeGameId(gameId));

    /// <summary>某游戏里某相对路径对应的游戏原文件备份位置。</summary>
    /// <param name="gameId">游戏稳定 ID，用于隔离不同游戏的备份。</param>
    /// <param name="relativePath">相对游戏根目录的路径，允许任意分隔符与大小写。</param>
    /// <returns><c>&lt;BackupDirectory(gameId)&gt;/&lt;规范化相对路径&gt;</c>。</returns>
    /// <exception cref="ArgumentException">游戏 ID 非法，或路径为空、绝对路径或包含 <c>..</c>。</exception>
    public string BackupFile(string gameId, string relativePath)
    {
        string normalized = NormalizeRelative(relativePath);
        return Path.Combine(BackupDirectory(gameId), normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>校验游戏 ID 可安全用作单级目录名。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <returns>去掉首尾空白后的游戏 ID。</returns>
    /// <exception cref="ArgumentException">为空、含路径分隔符/盘符/通配符，或是 <c>.</c> / <c>..</c>。</exception>
    public static string NormalizeGameId(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        string trimmed = gameId.Trim();
        if (trimmed is "." or ".."
            || trimmed.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            throw new ArgumentException($"游戏 ID 不能包含路径字符: {gameId}", nameof(gameId));
        }

        return trimmed;
    }

    /// <summary>把相对路径拼到目标根目录，并阻止其逃出该目录。</summary>
    /// <param name="root">目标根目录（游戏目录、临时目录等）。</param>
    /// <param name="relativePath">相对路径，允许任意分隔符与大小写。</param>
    /// <returns>规范化后的绝对路径。</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="root"/> 为空，或 <paramref name="relativePath"/> 非法 / 解析后越界。
    /// </exception>
    public static string ResolveInside(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string normalized = NormalizeRelative(relativePath);
        string rootFull = Path.GetFullPath(root);
        return ResolveInsideNormalized(rootFull, normalized);
    }

    /// <summary>判断路径是否等于根目录或位于其内部（不抛异常；大小写不敏感）。</summary>
    /// <param name="candidate">候选绝对路径。</param>
    /// <param name="root">根目录绝对路径。</param>
    /// <returns>等于根或在其内部时返回 <see langword="true"/>。</returns>
    /// <remarks>统一存档备份、还原与清理里的包含判断，避免多份逐字相同的私有实现。</remarks>
    public static bool IsSameOrInside(string candidate, string root)
    {
        string full = Path.GetFullPath(candidate);
        string rootFull = Path.GetFullPath(root);
        string prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        return string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把已规范化的相对路径拼到已规范化的根目录，并阻止其逃出该目录。
    /// </summary>
    /// <remarks>
    /// 供批量解压等热路径复用「根目录只规范化一次」：调用方必须先用 <see cref="Path.GetFullPath(string)"/>
    /// 规范化 <paramref name="normalizedRoot"/>，并用 <see cref="NormalizeRelative"/> 规范化
    /// <paramref name="normalizedRelativePath"/>，本方法不再重复这两步（P-08）。
    /// </remarks>
    /// <param name="normalizedRoot">已规范化的绝对根目录。</param>
    /// <param name="normalizedRelativePath">已规范化的相对路径（正斜杠）。</param>
    /// <returns>规范化后的绝对路径。</returns>
    /// <exception cref="ArgumentException">
    /// 任一参数为空，<paramref name="normalizedRoot"/> 不是绝对路径，或解析后越界。
    /// </exception>
    public static string ResolveInsideNormalized(string normalizedRoot, string normalizedRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRelativePath);

        string full = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        bool inside = full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase);
        if (!inside)
        {
            throw new ArgumentException($"路径逃出目标目录: {normalizedRelativePath}", nameof(normalizedRelativePath));
        }

        return full;
    }

    /// <summary>把相对路径统一成「正斜杠、无前导 <c>./</c>、无空段」的形式。</summary>
    /// <param name="relativePath">原始相对路径。</param>
    /// <returns>规范化后的相对路径（保留大小写）。</returns>
    /// <exception cref="ArgumentException">路径为空、绝对路径、含盘符或含 <c>..</c>。</exception>
    public static string NormalizeRelative(string relativePath) =>
        string.Join('/', NormalizeRelativeSegments(relativePath));

    /// <summary>规范化相对路径并返回逐段结果（调用方需要分段时避免再次 <c>Split</c>）。</summary>
    /// <param name="relativePath">原始相对路径。</param>
    /// <returns>规范化后的路径段（非空、不含 <c>..</c>）。</returns>
    /// <exception cref="ArgumentException">路径为空、绝对路径、含盘符或含 <c>..</c>。</exception>
    public static string[] NormalizeRelativeSegments(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string normalized = relativePath.Replace('\\', '/');
        // 前导 "./" 是合法写法：先剥掉，再检查其余段末尾的点/空格。
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        // 必须在 Trim 之前检查：Windows 会静默去掉路径段末尾的空格/点，
        // 从而把 "CON "、"a." 这类段名变成另一个文件或设备。只检查最后一段，避免整串 Split。
        int lastSlash = normalized.LastIndexOf('/');
        ReadOnlySpan<char> lastSegment = normalized.AsSpan(lastSlash < 0 ? 0 : lastSlash + 1);
        if (!lastSegment.IsEmpty && (lastSegment[^1] == ' ' || lastSegment[^1] == '.'))
        {
            throw new ArgumentException($"路径段不能以点或空格结尾: {relativePath}", nameof(relativePath));
        }

        normalized = normalized.Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            throw new ArgumentException($"必须是相对路径: {relativePath}", nameof(relativePath));
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException($"路径为空: {relativePath}", nameof(relativePath));
        }

        foreach (string segment in segments)
        {
            if (segment == "..")
            {
                throw new ArgumentException($"路径不能包含 ..: {relativePath}", nameof(relativePath));
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw new ArgumentException($"路径段不能以点或空格结尾: {relativePath}", nameof(relativePath));
            }

            if (segment.IndexOfAny(InvalidFileNameChars) >= 0)
            {
                throw new ArgumentException($"路径含 Windows 非法字符: {relativePath}", nameof(relativePath));
            }

            // 设备名取第一个点之前的部分（"CON.txt" 也是设备）；用切片代替 Split('.') 分配数组。
            int dot = segment.IndexOf('.');
            string stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
            if (ReservedWindowsNames.Contains(stem))
            {
                throw new ArgumentException($"路径含 Windows 保留设备名: {relativePath}", nameof(relativePath));
            }
        }

        return segments;
    }
}
