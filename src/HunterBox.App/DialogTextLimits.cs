namespace HunterBox.App;

/// <summary>对话框/提示文本的截断行数上限：集中定义，避免各入口使用不一致的字面量。</summary>
internal static class DialogTextLimits
{
    /// <summary>警告/提示最多展示的行数（其余写日志）。</summary>
    public const int WarningLines = 8;

    /// <summary>普通明细（文件、失败项、迁移项）最多展示的行数。</summary>
    public const int DetailLines = 10;

    /// <summary>冲突列表最多展示的行数。</summary>
    public const int ConflictLines = 12;

    /// <summary>
    /// 按条截断并追加「其余 N 条见日志」提示；未截断时原样连接。
    /// </summary>
    /// <remarks>
    /// 调用方必须在展示前把全部条目写入日志，提示里的「见日志」才成立。
    /// 泛型参数让带前缀的列表只格式化展示部分，避免先物化全部字符串。
    /// </remarks>
    /// <typeparam name="T">条目类型。</typeparam>
    /// <param name="lines">要展示的条目。</param>
    /// <param name="limit">最多展示条数，必须大于 0。</param>
    /// <param name="separator">条目分隔符。</param>
    /// <param name="unit">单位（条/个）。</param>
    /// <param name="logFile">日志文件路径（必填，提示中的「见日志」依赖它）。</param>
    /// <param name="format">展示格式化；为 <see langword="null"/> 时对条目调用 <c>ToString()</c>。</param>
    /// <returns>截断后的文本。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> 或 <paramref name="logFile"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> 不大于 0。</exception>
    public static string JoinTruncated<T>(
        IReadOnlyList<T> lines,
        int limit,
        string separator,
        string unit,
        string logFile,
        Func<T, string>? format = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(logFile);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        string Shown(T line) => format is null ? line?.ToString() ?? string.Empty : format(line);

        if (lines.Count <= limit)
        {
            return string.Join(separator, lines.Select(Shown));
        }

        int hidden = lines.Count - limit;
        return string.Join(separator, lines.Take(limit).Select(Shown))
            + $"{separator}…（其余 {hidden} {unit}见日志：{logFile}）";
    }
}
