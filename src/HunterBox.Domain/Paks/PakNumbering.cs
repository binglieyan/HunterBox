using System.Globalization;

namespace HunterBox.Domain.Paks;

/// <summary>
/// 一次编号分配所需的既有状态与策略。
/// </summary>
/// <remarks>
/// <para>
/// 两处调用方共用同一套编号规则，只在这里参数化差异：
/// <see cref="PakPlanner"/> 是纯规划（只有游戏根目录文件名，从 <c>max+1</c> 顺延、不复用内容）；
/// <c>PakMountService</c> 要延续 <c>pak_mods</c> 里已部署的编号（跳过已占用编号、按内容复用原文件名）。
/// </para>
/// </remarks>
public sealed record PakNumberingOptions
{
    /// <summary>编号前缀，例如 <c>re_chunk_000.pak.patch_</c>。</summary>
    public required string PatchPrefix { get; init; }

    /// <summary>第一个候选编号；分配时从这里开始取最小空闲编号。</summary>
    public required int FirstCandidateNumber { get; init; }

    /// <summary>已经被占用的编号（<c>pak_mods</c> 里已部署的 patch）；分配会跳过这些编号。</summary>
    public IReadOnlySet<int> OccupiedNumbers { get; init; } = NoOccupiedNumbers;

    /// <summary>内容指纹 → 已部署的编号文件名；配合 <see cref="ReuseExistingContent"/> 沿用原编号。</summary>
    public IReadOnlyDictionary<string, string> ExistingNamesByContent { get; init; } = NoExistingNames;

    /// <summary>是否按内容复用 <see cref="ExistingNamesByContent"/> 里的已有文件名（默认不复用）。</summary>
    public bool ReuseExistingContent { get; init; }

    private static IReadOnlySet<int> NoOccupiedNumbers { get; } = new HashSet<int>();

    private static IReadOnlyDictionary<string, string> NoExistingNames { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// pak 编号的格式化、解析与分配：<see cref="PakPlanner"/>（纯规划）与 <c>PakMountService</c>（<c>pak_mods</c> 实际部署）
/// 共用这一份规则，避免两套编号语义漂移。
/// </summary>
/// <remarks>
/// 分配规则（唯一实现）：按顺序为每个源文件取内容指纹；命中「按内容复用」时沿用已有文件名；
/// 同一批内相同内容的源共用同一个编号；其余从候选编号起取最小未被占用的编号。
/// </remarks>
public static class PakNumbering
{
    /// <summary>把 patch 编号格式化成 pak 文件名（<c>&lt;前缀&gt;NNN.pak</c>，编号至少补足三位）。</summary>
    /// <param name="prefix">编号前缀，例如 <c>re_chunk_000.pak.patch_</c>。</param>
    /// <param name="number">patch 编号；支持到 <see cref="MaxParsableNumber"/>。</param>
    /// <returns>目标文件名（不含路径）。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> 为负或超过可解析上界。</exception>
    public static string FormatPatchName(string prefix, int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, MaxParsableNumber);
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{number:000}.pak");
    }

    /// <summary>可格式化成 3–5 位并被 <see cref="TryParsePatchNumber"/> 还原的最大编号。</summary>
    public const int MaxParsableNumber = 99_999;

    /// <summary>从 <c>&lt;前缀&gt;NNN.pak</c>（3–5 位数字）里取出 NNN。</summary>
    /// <param name="fileName">候选文件名（可含路径）。</param>
    /// <param name="prefix">编号前缀。</param>
    /// <param name="number">解析出的编号；失败时为 0。</param>
    /// <returns>文件名符合「前缀 + 三到五位数字 + .pak」时返回 <see langword="true"/>。</returns>
    public static bool TryParsePatchNumber(string fileName, string prefix, out int number)
    {
        number = 0;
        if (string.IsNullOrEmpty(fileName) || !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rest = fileName[prefix.Length..];
        if (!rest.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string digits = rest[..^4];
        if (digits.Length is < 3 or > 5 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        number = int.Parse(digits, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>编号起点：已有 patch 号与下界兜底取较大者，再 +1。</summary>
    /// <param name="patchNumberFloor">编号下界兜底；已有 patch 号更高时以已有值为准。</param>
    /// <param name="existingFileNames">已有文件名（通常来自游戏根目录）。</param>
    /// <param name="patchPrefix">编号前缀。</param>
    /// <returns>下一个可用的 patch 编号。</returns>
    public static int NextPatchNumber(int patchNumberFloor, IEnumerable<string> existingFileNames, string patchPrefix)
    {
        ArgumentNullException.ThrowIfNull(existingFileNames);

        int max = patchNumberFloor;
        foreach (string existing in existingFileNames)
        {
            if (TryParsePatchNumber(existing, patchPrefix, out int number) && number > max)
            {
                max = number;
            }
        }

        return max + 1;
    }

    /// <summary>按策略为一组 pak 源文件分配编号文件名。</summary>
    /// <param name="sources">按启用顺序排列的源文件；同一份内容可能对应多条分配。</param>
    /// <param name="options">编号策略与既有状态。</param>
    /// <param name="contentOf">按文件绝对路径取内容指纹。</param>
    /// <returns>与 <paramref name="sources"/> 一一对应的分配结果。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<PakAssignment> AssignPatchNumbers(
        IReadOnlyList<PakSource> sources,
        PakNumberingOptions options,
        Func<string, string> contentOf)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contentOf);

        HashSet<int> occupied = [.. options.OccupiedNumbers];
        Dictionary<string, PakAssignment> byContent = new(StringComparer.Ordinal);
        List<PakAssignment> assignments = new(sources.Count);
        int next = options.FirstCandidateNumber;

        foreach (PakSource source in sources)
        {
            string content = contentOf(source.FullPath);

            if (options.ReuseExistingContent
                && options.ExistingNamesByContent.TryGetValue(content, out string? existing))
            {
                assignments.Add(new PakAssignment(existing, source));
                continue;
            }

            if (byContent.TryGetValue(content, out PakAssignment? shared))
            {
                assignments.Add(shared with { Source = source });
                continue;
            }

            while (occupied.Contains(next))
            {
                next++;
            }

            if (next > MaxParsableNumber)
            {
                // 编号用尽属于可读的业务失败，不能把 FormatPatchName 的参数异常直接抛给用户（C-10）。
                throw new InvalidOperationException(
                    $"pak 编号已用尽（当前最大 {MaxParsableNumber}）：请删除 pak_mods 中不再使用的 pak 后重试。");
            }

            PakAssignment created = new(FormatPatchName(options.PatchPrefix, next), source);
            occupied.Add(next);
            next++;
            assignments.Add(created);
            byContent[content] = created;
        }

        return assignments;
    }
}
