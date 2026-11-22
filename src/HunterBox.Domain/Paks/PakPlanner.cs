namespace HunterBox.Domain.Paks;

/// <summary>pak 型 MOD 里一个待放置的 pak 文件。</summary>
/// <param name="ModId">所属 MOD的持久 ID。</param>
/// <param name="ModName">用于冲突提示的可读名字。</param>
/// <param name="FileName">MOD 内原始文件名（如 <c>cool.pak</c>）。</param>
/// <param name="FullPath">MOD 目录里的绝对路径，用于计算内容指纹。</param>
public sealed record PakSource(int ModId, string ModName, string FileName, string FullPath);

/// <summary>pak的命名策略。</summary>
public enum PakNamingMode
{
    /// <summary>沿用 patch 编号（设置里的「自动修正PAK」开启时）。</summary>
    PatchNumbered,

    /// <summary>保留原始文件名；同名冲突需要上层拒绝启用。</summary>
    KeepOriginalName,
}

/// <summary>一次 pak 放置所需的全部外部条件。</summary>
public sealed record PakPlacementOptions
{
    /// <summary>命名策略。</summary>
    public required PakNamingMode Mode { get; init; }

    /// <summary>编号前缀，例如 <c>re_chunk_000.pak.patch_</c>。</summary>
    public required string PatchPrefix { get; init; }

    /// <summary>编号下界兜底：仅当没有任何已有 patch 文件时使用。</summary>
    public int PatchNumberFloor { get; init; } = 1;

    /// <summary>游戏根目录里已有的文件名，用于决定编号起点。</summary>
    public IReadOnlyList<string> ExistingGameRootFileNames { get; init; } = [];
}

/// <summary>一个 pak 源文件最终落成的文件名。</summary>
/// <param name="FinalName">目标文件名（不含路径）。</param>
/// <param name="Source">对应的源文件。</param>
public sealed record PakAssignment(string FinalName, PakSource Source);

/// <summary>同名且内容不同时的冲突记录。</summary>
/// <param name="FileName">冲突的文件名。</param>
/// <param name="Incoming">被拒绝的一方。</param>
/// <param name="Owner">已占用该文件名的一方。</param>
public sealed record PakConflict(string FileName, PakSource Incoming, PakSource Owner);

/// <summary>pak 放置结果。</summary>
/// <param name="Assignments">源文件到目标文件名的分配；同一份内容可能对应多条。</param>
/// <param name="Conflicts">冲突列表；非空时不允许启用。</param>
public sealed record PakPlan(IReadOnlyList<PakAssignment> Assignments, IReadOnlyList<PakConflict> Conflicts)
{
    /// <summary>是否允许启用（没有任何冲突）。</summary>
    public bool CanEnable => Conflicts.Count == 0;
}

/// <summary>内容指纹（实现里通常是哈希），用于「内容相同只放一份」。</summary>
public interface IContentFingerprint
{
    /// <summary>计算文件内容指纹。</summary>
    /// <param name="fullPath">文件绝对路径。</param>
    /// <returns>可比较的指纹字符串。</returns>
    string GetFingerprint(string fullPath);
}

/// <summary>
/// 决定每个 pak 在 <c>pak_mods</c> 里的文件名（<c>docs/SPEC.md</c> 4.2）。
/// 纯函数：不读写文件，内容比对通过 <see cref="IContentFingerprint"/> 注入。
/// </summary>
public static class PakPlanner
{
    /// <summary>计算 pak 放置计划。</summary>
    /// <param name="enabledInOrder">按 MOD 列表顺序排列的、已启用 MOD的 pak 源文件。</param>
    /// <param name="options">命名策略与编号条件。</param>
    /// <param name="fingerprint">内容指纹实现。</param>
    /// <returns>放置计划；<see cref="PakPlan.CanEnable"/> 为 <see langword="false"/> 时应拒绝启用。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static PakPlan Plan(
        IEnumerable<PakSource> enabledInOrder,
        PakPlacementOptions options,
        IContentFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(enabledInOrder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fingerprint);

        PakSource[] sources = [.. enabledInOrder];
        return options.Mode == PakNamingMode.PatchNumbered
            ? new PakPlan(PlanNumbered(sources, options, fingerprint), [])
            : PlanKeepOriginalName(sources, fingerprint);
    }

    /// <summary>
    /// 编号模式：编号规则统一走 <see cref="PakNumbering.AssignPatchNumbers"/>；
    /// 纯规划器没有 <c>pak_mods</c> 状态，因此从「已有编号 max+1」顺延、不按内容复用已有文件。
    /// </summary>
    private static IReadOnlyList<PakAssignment> PlanNumbered(
        IReadOnlyList<PakSource> sources,
        PakPlacementOptions options,
        IContentFingerprint fingerprint)
    {
        PakNumberingOptions numbering = new()
        {
            PatchPrefix = options.PatchPrefix,
            FirstCandidateNumber = NextPatchNumber(options),
            ReuseExistingContent = false,
        };
        return PakNumbering.AssignPatchNumbers(sources, numbering, fingerprint.GetFingerprint);
    }

    /// <summary>保留原名模式：同名同内容共用一条分配，同名不同内容记冲突。</summary>
    private static PakPlan PlanKeepOriginalName(IReadOnlyList<PakSource> sources, IContentFingerprint fingerprint)
    {
        List<PakAssignment> assignments = [];
        List<PakConflict> conflicts = [];
        Dictionary<string, PakAssignment> byName = new(StringComparer.OrdinalIgnoreCase);

        foreach (PakSource source in sources)
        {
            string content = fingerprint.GetFingerprint(source.FullPath);

            if (byName.TryGetValue(source.FileName, out PakAssignment? owner))
            {
                if (fingerprint.GetFingerprint(owner.Source.FullPath) == content)
                {
                    assignments.Add(owner with { Source = source });
                }
                else
                {
                    conflicts.Add(new PakConflict(source.FileName, source, owner.Source));
                }

                continue;
            }

            PakAssignment kept = new(source.FileName, source);
            assignments.Add(kept);
            byName[kept.FinalName] = kept;
        }

        return new PakPlan(assignments, conflicts);
    }

    /// <summary>编号起点：已有 patch 号与下界取较大者，再 +1。</summary>
    /// <param name="options">命名条件。</param>
    /// <returns>下一个可用的 patch 编号。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <see langword="null"/>。</exception>
    internal static int NextPatchNumber(PakPlacementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return PakNumbering.NextPatchNumber(options.PatchNumberFloor, options.ExistingGameRootFileNames, options.PatchPrefix);
    }

    /// <summary>从 <c>&lt;前缀&gt;NNN.pak</c> 里取出 NNN。</summary>
    /// <param name="fileName">候选文件名（可含路径）。</param>
    /// <param name="prefix">编号前缀。</param>
    /// <param name="number">解析出的编号；失败时为 0。</param>
    /// <returns>文件名符合「前缀 + 三到五位数字 + .pak」时返回 <see langword="true"/>。</returns>
    public static bool TryParsePatchNumber(string fileName, string prefix, out int number) =>
        PakNumbering.TryParsePatchNumber(fileName, prefix, out number);
}
