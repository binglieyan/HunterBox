using HunterBox.Domain.Models;

namespace HunterBox.Application.Models;

/// <summary>
/// 模型编辑器行的纯逻辑（候选过滤/强制加回当前模型/目标完整身份拼装/关键词搜索/自撞校验），
/// 便于单测与复用。
/// </summary>
/// <remarks>
/// 候选只取同类型；「仅显示未使用的ID」过滤掉被其它 MOD 占用的完整 ID，
/// 但当前模型无论是否被过滤/是否在名称表里都强制加回并选中。
/// <c>usedIds</c> 由宿主合并「其它 MOD 占用的 ID」与「本 MOD 其它模型行的当前 ID」，
/// 因此不会把自身文件当成可选目标。
/// 提交给改写服务的目标必须是完整身份路径形式（护甲保留 ch0X 性别、武器保留 itTT/wpTT），
/// 不能只提交数字段。
/// </remarks>
public static class ModelEditorPlanner
{
    /// <summary>构建某一行可见的目标候选。</summary>
    /// <param name="current">当前模型身份。</param>
    /// <param name="currentName">当前模型的显示名（不在名称表时作为候选名）。</param>
    /// <param name="currentHasChain">当前模型是否带 <c>.chain</c>。</param>
    /// <param name="all">该类型的全部候选（来自 <see cref="IModelCatalog"/>）。</param>
    /// <param name="usedIds">其它 MOD 已占用的完整模型 ID。</param>
    /// <param name="filterUnused">是否只显示未使用的 ID。</param>
    /// <param name="rememberedCode">切换过滤前用户已选的目标 Code；<see langword="null"/> 表示未选。</param>
    /// <returns>可见候选（当前模型必定在列表里，且用户已选目标尽量保留）。</returns>
    public static IReadOnlyList<ModelChoice> BuildCandidates(
        ModelIdentity current,
        string currentName,
        bool currentHasChain,
        IReadOnlyList<ModelChoice> all,
        IReadOnlySet<string> usedIds,
        bool filterUnused,
        string? rememberedCode = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(usedIds);

        string currentCode = current.Code;
        // 候选必须与当前行同类同游戏，否则 BuildTargetForm 会抛异常；
        // Identity 为 null 的名称表候选沿用当前行身份，仍然兼容。
        List<ModelChoice> visible = filterUnused
            ? [.. all.Where(c => IsCompatible(c, current)
                && (CodeEquals(c.Code, currentCode) || !usedIds.Contains(c.Code)))]
            : [.. all.Where(c => IsCompatible(c, current))];

        if (rememberedCode is not null)
        {
            ModelChoice? remembered = all.FirstOrDefault(
                c => CodeEquals(c.Code, rememberedCode) && IsCompatible(c, current));
            if (remembered is not null && !visible.Any(c => CodeEquals(c.Code, rememberedCode)))
            {
                // 切换过滤时保留用户已选目标（B 优于 「重置回 Origin」）
                visible.Add(remembered);
            }
        }

        if (!visible.Any(c => CodeEquals(c.Code, currentCode)))
        {
            visible.Insert(0, new ModelChoice(currentCode, currentName, currentHasChain, current));
        }

        return visible;
    }

    /// <summary>按关键词模糊过滤候选：每个空格分隔的词都必须命中显示名或完整 ID（不区分大小写）。</summary>
    /// <remarks>
    /// 空格用全角/半角均可（按 Unicode 空白分割），词的顺序无所谓：<c>刺花 001</c> 与 <c>001 刺花</c> 等价。
    /// 关键词为空时原样返回传入列表（不复制），供候选很多的编辑器每次打开面板时零分配。
    /// </remarks>
    /// <param name="choices">候选列表（通常已按「仅显示未使用的ID」过滤）。</param>
    /// <param name="query">搜索文本；<see langword="null"/> 或空白时返回全部候选。</param>
    /// <returns>命中的候选（保持原顺序）；没有命中时返回空列表。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="choices"/> 为 <see langword="null"/>。</exception>
    public static IReadOnlyList<ModelChoice> Search(IReadOnlyList<ModelChoice> choices, string? query)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (string.IsNullOrWhiteSpace(query))
        {
            return choices;
        }

        string[] tokens = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return choices;
        }

        List<ModelChoice> matched = [];
        foreach (ModelChoice choice in choices)
        {
            if (MatchesAllTokens(choice, tokens))
            {
                matched.Add(choice);
            }
        }

        return matched;
    }

    /// <summary>
    /// 按行记住目标时的稳定键：PakFile 与完整身份路径形式组合；同一模型出现在多个 pak 时不会互相覆盖。
    /// </summary>
    /// <param name="pakFile">pak 文件名（散装行为 <see langword="null"/>）。</param>
    /// <param name="identityForm">完整身份路径形式。</param>
    /// <returns>行级稳定键。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identityForm"/> 为 <see langword="null"/>。</exception>
    public static string SelectionKey(string? pakFile, string identityForm)
    {
        ArgumentNullException.ThrowIfNull(identityForm);
        return (pakFile ?? string.Empty) + "\n" + identityForm;
    }

    /// <summary>搜索框按回车时是否应选取第一个命中项；空/全空白查询返回 false，避免误改目标。</summary>
    /// <param name="query">搜索文本。</param>
    /// <returns>存在有效关键词时返回 <see langword="true"/>。</returns>
    public static bool CanChooseFirstOnEnter(string? query) => !string.IsNullOrWhiteSpace(query);

    /// <summary>把候选目标换成当前行对应的完整身份路径形式（保留游戏/性别/itTT）。</summary>
    /// <param name="current">当前模型身份。</param>
    /// <param name="choice">用户选中的候选。</param>
    /// <returns>完整身份路径形式（<c>ch03/012/003</c>、<c>ch05/003/0001</c>、<c>it10/03/0000</c>、<c>rise:wp/GreatSword/001</c>）。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">候选类别/游戏与当前行不一致。</exception>
    public static string BuildTargetForm(ModelIdentity current, ModelChoice choice)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(choice);

        ModelIdentity target = choice.Identity ?? current;
        if (target.Kind != current.Kind)
        {
            throw new ArgumentException(
                $"候选类别与当前模型不一致: {current.Kind} -> {target.Kind}");
        }

        if (target.Game != current.Game)
        {
            throw new ArgumentException(
                $"候选游戏与当前模型不一致: {current.Game} -> {target.Game}");
        }

        // 保留当前行的游戏/性别/武器类型，只换完整模型 ID（护甲 fid_sid、武器 AA/BBBB 或世界 fullId）
        ModelIdentity result = new(current.Kind, current.Type, target.FirstId, target.SecondId)
        {
            Game = current.Game,
        };
        return result.PathForm;
    }

    /// <summary>判断候选是否就是当前模型（完整 ID 相同；性别/武器类型由行本身保证）。</summary>
    /// <param name="current">当前模型身份。</param>
    /// <param name="choice">候选。</param>
    /// <returns>是当前模型时返回 <see langword="true"/>。</returns>
    public static bool IsCurrent(ModelIdentity current, ModelChoice choice)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(choice);
        return CodeEquals(choice.Code, current.Code);
    }

    /// <summary>
    /// 校验一行选择的目标是否会与同一 MOD 的其它模型行自撞。
    /// </summary>
    /// <remarks>
    /// 按完整身份路径形式比较：护甲同 <c>fid_sid</c> 的男/女款路径不同，
    /// 不应因为 <see cref="ModelChoice.Code"/> 相同就误报「覆盖自身文件」。
    /// </remarks>
    /// <param name="ownRows">当前 MOD 检测到的全部模型身份（含当前行）。</param>
    /// <param name="current">当前编辑的模型身份。</param>
    /// <param name="choice">该行选中的目标候选。</param>
    /// <returns>冲突说明；没有冲突时返回 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static string? DescribeSelfCollision(
        IEnumerable<ModelIdentity> ownRows,
        ModelIdentity current,
        ModelChoice choice)
    {
        ArgumentNullException.ThrowIfNull(ownRows);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(choice);
        string targetForm = BuildTargetForm(current, choice);
        foreach (ModelIdentity row in ownRows)
        {
            if (!string.Equals(row.PathForm, current.PathForm, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.PathForm, targetForm, StringComparison.OrdinalIgnoreCase))
            {
                return $"目标 {choice.Code} 与同一 MOD 的另一个模型 ID 相同：改写会覆盖自身文件，请换一个目标。";
            }
        }

        return null;
    }

    /// <summary>候选是否与当前行同类同游戏；<see cref="ModelChoice.Identity"/> 为空时沿用当前行身份，视为兼容。</summary>
    /// <param name="choice">候选。</param>
    /// <param name="current">当前行身份。</param>
    /// <returns>兼容时返回 <see langword="true"/>。</returns>
    private static bool IsCompatible(ModelChoice choice, ModelIdentity current) =>
        choice.Identity is null
        || (choice.Identity.Kind == current.Kind && choice.Identity.Game == current.Game);

    /// <summary>每个词都要命中名字或完整 ID；任一未命中即排除该候选。</summary>
    /// <param name="choice">候选。</param>
    /// <param name="tokens">已按空白切分的关键词。</param>
    /// <returns>全部命中时返回 <see langword="true"/>。</returns>
    private static bool MatchesAllTokens(ModelChoice choice, string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (!choice.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
                && !choice.Code.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CodeEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
