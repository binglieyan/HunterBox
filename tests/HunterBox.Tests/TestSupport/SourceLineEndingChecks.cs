namespace HunterBox.Tests;

/// <summary>
/// 源码行尾检查：C# 与 XAML 的行尾门禁共用同一份逐行判定，避免两套逻辑漂移。
/// </summary>
internal static class SourceLineEndingChecks
{
    /// <summary>一个文件的行尾问题集合（门禁与注入用例共用同一判定）。</summary>
    /// <param name="Mixed">是否存在非 CRLF 行尾。</param>
    /// <param name="TrailingBlank">是否以空行结尾。</param>
    /// <param name="ConsecutiveBlank">正文是否存在连续两个空行。</param>
    internal readonly record struct LineEndingIssues(
        bool Mixed,
        bool TrailingBlank,
        bool ConsecutiveBlank);

    /// <summary>
    /// 读取文件并返回三项行尾问题；门禁与临时文件注入用例走同一入口。
    /// </summary>
    /// <remarks>
    /// 一次读取后做多次线性扫描，耗时与文件大小线性相关、为数十毫秒量级，
    /// 相比测试套件整体开销不是瓶颈，不为单趟合并引入更复杂的字节状态机；仓库文件数会增长，注释不写固定快照。
    /// </remarks>
    /// <param name="path">文件路径。</param>
    /// <returns>行尾问题集合。</returns>
    internal static LineEndingIssues Inspect(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new LineEndingIssues(
            FindFirstNonCrlfLineEnding(bytes) >= 0,
            HasTrailingBlankLine(bytes),
            HasConsecutiveBlankLines(bytes));
    }

    /// <summary>从给定文件集合中筛出扩展名匹配且不是纯 CRLF 的文件。</summary>
    /// <param name="files">待检查的文件集合（通常是仓库级枚举或临时目录枚举）。</param>
    /// <param name="extensions">声明的扩展名（含点，忽略大小写）；为空时返回空集合，调用方必须先守卫清单非空。</param>
    /// <returns>违规文件路径；全部合规时为空。</returns>
    public static List<string> FindNonCrlfFiles(
        IEnumerable<string> files,
        IReadOnlyList<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(extensions);
        List<string> offenders = [];
        foreach (string file in files)
        {
            if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Inspect(file).Mixed)
            {
                offenders.Add(file);
            }
        }

        return offenders;
    }

    /// <summary>查找第一处不是 CRLF 的行尾字节偏移。</summary>
    /// <remarks>
    /// 单独出现的 LF 或 CR 都算违规：纯 LF 文件与「LF 文件末尾追加 CRLF」的混合行尾都会被命中，
    /// 不再只看最后一个字节。
    /// </remarks>
    /// <param name="bytes">文件字节。</param>
    /// <returns>第一处违规的偏移；全部为 CRLF 时返回 -1。</returns>
    public static int FindFirstNonCrlfLineEnding(ReadOnlySpan<byte> bytes)
    {
        for (int index = 0; index < bytes.Length; index++)
        {
            bool loneLf = bytes[index] == (byte)'\n' && (index == 0 || bytes[index - 1] != (byte)'\r');
            bool loneCr = bytes[index] == (byte)'\r' && (index + 1 >= bytes.Length || bytes[index + 1] != (byte)'\n');
            if (loneLf || loneCr)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>判断文件是否以换行结尾（空文件视为缺少）。</summary>
    /// <param name="bytes">文件字节。</param>
    /// <returns>以 LF 或 CR 结尾时返回 <see langword="true"/>。</returns>
    public static bool EndsWithNewline(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 0 && bytes[^1] is (byte)'\n' or (byte)'\r';

    /// <summary>判断文件末尾是否多出一个空行（CRLF + CRLF 或 LF + LF）。</summary>
    /// <param name="bytes">文件字节。</param>
    /// <returns>存在尾随空行时返回 <see langword="true"/>。</returns>
    public static bool HasTrailingBlankLine(ReadOnlySpan<byte> bytes)
    {
        bool crlfBlank = bytes.Length >= 4
            && bytes[^1] == (byte)'\n' && bytes[^2] == (byte)'\r'
            && bytes[^3] == (byte)'\n' && bytes[^4] == (byte)'\r';
        bool lfBlank = bytes.Length >= 2 && bytes[^1] == (byte)'\n' && bytes[^2] == (byte)'\n';
        return crlfBlank || lfBlank;
    }

    /// <summary>判断正文是否存在连续两个空行（连续三个 CRLF）。</summary>
    /// <param name="bytes">文件字节。</param>
    /// <returns>存在连续空行时返回 <see langword="true"/>。</returns>
    public static bool HasConsecutiveBlankLines(ReadOnlySpan<byte> bytes)
    {
        for (int index = 0; index + 5 < bytes.Length; index++)
        {
            if (bytes[index] == (byte)'\r' && bytes[index + 1] == (byte)'\n'
                && bytes[index + 2] == (byte)'\r' && bytes[index + 3] == (byte)'\n'
                && bytes[index + 4] == (byte)'\r' && bytes[index + 5] == (byte)'\n')
            {
                return true;
            }
        }

        return false;
    }
}
