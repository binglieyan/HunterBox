namespace HunterBox.Domain.Mods;

/// <summary>FOMOD 依赖求值。</summary>
/// <remarks>
/// 文件依赖用「当前包内文件集」判断；标记依赖用「当前勾选项产生的标记」判断；
/// 游戏/管理器版本依赖在解析阶段已被丢弃（保守视为满足，避免隐藏安装步骤）。
/// </remarks>
public static class FomodDependencyEvaluator
{
    /// <summary>判断依赖是否满足（文件清单已按 <see cref="NormalizeFiles"/> 规范化，供内部热路径复用）。</summary>
    /// <param name="dependency">依赖定义。</param>
    /// <param name="normalizedFiles">已规范化的包内文件集合。</param>
    /// <param name="flags">当前已设置的标记（大小写不敏感）。</param>
    /// <returns>满足时返回 <see langword="true"/>。</returns>
    internal static bool IsSatisfiedNormalized(
        FomodDependency? dependency,
        HashSet<string> normalizedFiles,
        IReadOnlyDictionary<string, string> flags) =>
        dependency is null || dependency.IsEmpty || Evaluate(dependency, normalizedFiles, flags);

    /// <summary>把包内文件清单规范化成「小写、正斜杠」的集合。</summary>
    /// <param name="files">相对路径集合。</param>
    /// <returns>规范化集合。</returns>
    public static HashSet<string> NormalizeFiles(IEnumerable<string> files)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            try
            {
                set.Add(PackageInspector.Normalize(file));
            }
            catch (ArgumentException)
            {
                // 非法的包内路径不参与依赖匹配。
            }
        }

        return set;
    }

    private static bool Evaluate(
        FomodDependency dependency,
        HashSet<string> files,
        IReadOnlyDictionary<string, string> flags)
    {
        List<bool> results = [];
        foreach (FomodFileDependency fileDependency in dependency.Files)
        {
            results.Add(IsFileSatisfied(fileDependency, files));
        }

        foreach (FomodFlagDependency flagDependency in dependency.Flags)
        {
            results.Add(IsFlagSatisfied(flagDependency, flags));
        }

        foreach (FomodDependency nested in dependency.Nested)
        {
            results.Add(nested.IsEmpty || Evaluate(nested, files, flags));
        }

        if (results.Count == 0)
        {
            return true;
        }

        return dependency.Operator == FomodDependencyOperator.Or
            ? results.Exists(r => r)
            : results.TrueForAll(r => r);
    }

    private static bool IsFileSatisfied(FomodFileDependency dependency, HashSet<string> files)
    {
        if (string.IsNullOrWhiteSpace(dependency.File))
        {
            return true;
        }

        string normalized;
        try
        {
            normalized = PackageInspector.Normalize(dependency.File);
        }
        catch (ArgumentException)
        {
            return true;
        }

        bool present = files.Contains(normalized);
        // Active 要求存在；Inactive/Missing 都按「不存在」处理。
        return dependency.State == FomodFileState.Active ? present : !present;
    }

    private static bool IsFlagSatisfied(
        FomodFlagDependency dependency,
        IReadOnlyDictionary<string, string> flags)
    {
        if (string.IsNullOrWhiteSpace(dependency.Flag))
        {
            return true;
        }

        if (!flags.TryGetValue(dependency.Flag, out string? actual))
        {
            return false;
        }

        return string.IsNullOrEmpty(dependency.Value)
            || string.Equals(actual, dependency.Value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>FOMOD 里的一个可选插件条目（带步骤/分组定位，供界面与求值使用）。</summary>
public sealed class FomodSelectionEntry
{
    internal FomodSelectionEntry(
        FomodInstallStep step,
        int stepIndex,
        FomodGroup group,
        int groupIndex,
        FomodPlugin plugin)
    {
        Step = step;
        StepIndex = stepIndex;
        Group = group;
        GroupIndex = groupIndex;
        Plugin = plugin;
    }

    /// <summary>所属安装步骤。</summary>
    public FomodInstallStep Step { get; }

    /// <summary>步骤序号（从 0 开始）。</summary>
    public int StepIndex { get; }

    /// <summary>所属分组。</summary>
    public FomodGroup Group { get; }

    /// <summary>分组序号（从 0 开始）。</summary>
    public int GroupIndex { get; }

    /// <summary>插件本身。</summary>
    public FomodPlugin Plugin { get; }

    /// <summary>插件是否必需（不能在界面上取消）。</summary>
    public bool IsRequired => Plugin.Type == FomodPluginType.Required;

    /// <summary>所属分组是否全部必选。</summary>
    public bool IsSelectAllGroup => Group.Type == FomodGroupType.SelectAll;
}

/// <summary>
/// FOMOD 选择状态：默认值、用户勾选、标记传播、步骤可见性与最终文件解析。
/// </summary>
/// <remarks>
/// <para>默认值：Required 与 SelectAll 强制选中并锁定；Recommended 默认选中；
/// SelectExactlyOne 在没有其它默认值时选中第一个可用项；其余默认不选。</para>
/// <para>不可用项（<see cref="FomodPluginType.NotUsable"/>）不默认选中、不可勾选，也不参与分组校验与安装；
/// 条件可用项（<see cref="FomodPluginType.CouldBeUsable"/>）按可选处理：默认不选、可手动勾选。</para>
/// <para>步骤可见性依赖在每次勾选后重新计算（flag/file 依赖，不动点迭代）；被隐藏步骤的勾选项不参与安装
/// （只遍历可见步骤），其标记也不生效。条件安装用最终标记集合求值。</para>
/// </remarks>
public sealed class FomodSelectionState
{
    private readonly FomodConfig _config;
    private readonly HashSet<string> _files;
    private readonly List<FomodSelectionEntry> _entries = [];
    private readonly HashSet<FomodSelectionEntry> _selected = [];
    private readonly Dictionary<string, string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];
    private bool _convergenceWarningAdded;

    private FomodSelectionState(FomodConfig config, IReadOnlyCollection<string> availableFiles)
    {
        _config = config;
        _files = FomodDependencyEvaluator.NormalizeFiles(availableFiles);
        BuildEntries();
        ApplyDefaults();
    }

    /// <summary>创建选择状态并套用默认值。</summary>
    /// <param name="config">FOMOD 配置。</param>
    /// <param name="availableFiles">包内全部文件（用于文件依赖与条件求值）。</param>
    /// <returns>选择状态。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 <see langword="null"/>。</exception>
    public static FomodSelectionState Create(FomodConfig config, IReadOnlyCollection<string> availableFiles)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(availableFiles);
        return new FomodSelectionState(config, availableFiles);
    }

    /// <summary>全部可选插件（按步骤/分组顺序）。</summary>
    public IReadOnlyList<FomodSelectionEntry> Entries => _entries;

    /// <summary>当前已设置的标记（标记名 → 值）。</summary>
    public IReadOnlyDictionary<string, string> Flags => _flags;

    /// <summary>
    /// 计算过程中需要提示用户的问题（例如步骤可见性依赖未收敛）；正常配置为空。
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>某个插件当前是否被选中（<see cref="FomodPluginType.NotUsable"/> 恒视为未选中）。</summary>
    /// <param name="entry">插件条目。</param>
    /// <returns>选中时返回 <see langword="true"/>。</returns>
    public bool IsSelected(FomodSelectionEntry entry) =>
        entry.Plugin.Type != FomodPluginType.NotUsable && _selected.Contains(entry);

    /// <summary>某个插件是否被锁定（Required、SelectAll 分组，或不可用项）。</summary>
    /// <param name="entry">插件条目。</param>
    /// <returns>锁定时返回 <see langword="true"/>。</returns>
    public static bool IsLocked(FomodSelectionEntry entry) =>
        entry.Plugin.Type == FomodPluginType.NotUsable || entry.IsRequired || entry.IsSelectAllGroup;

    /// <summary>安装步骤当前是否可见（依赖全部满足）。</summary>
    /// <param name="step">安装步骤。</param>
    /// <returns>可见时返回 <see langword="true"/>。</returns>
    public bool IsStepVisible(FomodInstallStep step) => IsStepVisibleUnder(step, _flags);

    /// <summary>按给定标记集合判断步骤是否可见（标记传播迭代时使用）。</summary>
    private bool IsStepVisibleUnder(FomodInstallStep step, IReadOnlyDictionary<string, string> flags) =>
        FomodDependencyEvaluator.IsSatisfiedNormalized(step.Visible, _files, flags);

    /// <summary>修改某个插件的选中状态（按分组类型做互斥/锁定处理）。</summary>
    /// <param name="entry">插件条目。</param>
    /// <param name="selected">目标状态。</param>
    public void SetSelected(FomodSelectionEntry entry, bool selected)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (IsLocked(entry))
        {
            return;
        }

        switch (entry.Group.Type)
        {
            case FomodGroupType.SelectExactlyOne:
                if (!selected)
                {
                    // 单选组必须保持一个选中：取消操作忽略（与单选控件一致）。
                    return;
                }

                DeselectGroup(entry.Group, except: entry);
                break;

            case FomodGroupType.SelectAtMostOne:
                if (selected)
                {
                    DeselectGroup(entry.Group, except: entry);
                }

                break;

            default:
                break;
        }

        if (selected)
        {
            _selected.Add(entry);
        }
        else
        {
            _selected.Remove(entry);
        }

        RebuildFlags();
    }

    /// <summary>校验当前选择是否合法（返回给用户的中文错误；空集合表示通过）。</summary>
    /// <returns>错误消息列表。</returns>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        foreach (FomodInstallStep step in _config.InstallSteps)
        {
            if (!IsStepVisible(step))
            {
                continue;
            }

            foreach (FomodGroup group in step.Groups)
            {
                // 不可用项既不参与安装，也不参与分组数量校验（否则会出现「必须选一个不可用项」）。
                List<FomodSelectionEntry> usable =
                [
                    .. _entries.Where(e => ReferenceEquals(e.Group, group)
                        && e.Plugin.Type != FomodPluginType.NotUsable),
                ];
                if (usable.Count == 0)
                {
                    continue;
                }

                int selected = usable.Count(e => _selected.Contains(e));
                switch (group.Type)
                {
                    case FomodGroupType.SelectExactlyOne when selected != 1:
                        errors.Add($"分组「{group.Name}」必须选择一个选项");
                        break;
                    case FomodGroupType.SelectAtLeastOne when selected == 0:
                        errors.Add($"分组「{group.Name}」至少需要选择一个选项");
                        break;
                    case FomodGroupType.SelectAtMostOne when selected > 1:
                        errors.Add($"分组「{group.Name}」最多只能选择一个选项");
                        break;
                    default:
                        break;
                }
            }
        }

        return errors;
    }

    /// <summary>解析最终要安装的文件映射（必装 + 可见步骤的选中项 + 条件安装），按 priority 排序。</summary>
    /// <returns>文件映射列表（未做落盘路径二次校验）。</returns>
    public IReadOnlyList<FomodFileMapping> ResolveFiles()
    {
        List<FomodFileMapping> mappings = [.. _config.RequiredInstallFiles];

        foreach (FomodInstallStep step in _config.InstallSteps)
        {
            if (!IsStepVisible(step))
            {
                continue;
            }

            foreach (FomodSelectionEntry entry in _entries)
            {
                if (ReferenceEquals(entry.Step, step)
                    && entry.Plugin.Type != FomodPluginType.NotUsable
                    && _selected.Contains(entry))
                {
                    mappings.AddRange(entry.Plugin.Files);
                }
            }
        }

        foreach (FomodInstallPattern pattern in _config.ConditionalInstalls)
        {
            if (FomodDependencyEvaluator.IsSatisfiedNormalized(pattern.Dependencies, _files, _flags))
            {
                mappings.AddRange(pattern.Files);
            }
        }

        // 稳定排序：同优先级保持配置文件里的先后顺序（按 priority 稳定排序）。
        return [.. mappings.OrderBy(m => m.Priority)];
    }

    private void BuildEntries()
    {
        for (int stepIndex = 0; stepIndex < _config.InstallSteps.Count; stepIndex++)
        {
            FomodInstallStep step = _config.InstallSteps[stepIndex];
            for (int groupIndex = 0; groupIndex < step.Groups.Count; groupIndex++)
            {
                FomodGroup group = step.Groups[groupIndex];
                foreach (FomodPlugin plugin in group.Plugins)
                {
                    _entries.Add(new FomodSelectionEntry(step, stepIndex, group, groupIndex, plugin));
                }
            }
        }
    }

    private void ApplyDefaults()
    {
        foreach (IGrouping<(int Step, int Group), FomodSelectionEntry> group in _entries.GroupBy(e => (e.StepIndex, e.GroupIndex)))
        {
            FomodGroup groupModel = group.First().Group;
            bool selectAll = groupModel.Type == FomodGroupType.SelectAll;
            foreach (FomodSelectionEntry entry in group)
            {
                // 不可用项永不默认选中（SelectAll 分组也不例外）。
                bool selected = entry.Plugin.Type != FomodPluginType.NotUsable
                    && (selectAll
                        || entry.Plugin.Type == FomodPluginType.Required
                        || entry.Plugin.Type == FomodPluginType.Recommended);
                if (selected)
                {
                    _selected.Add(entry);
                }
            }

            if (groupModel.Type == FomodGroupType.SelectExactlyOne
                && !group.Any(e => _selected.Contains(e)))
            {
                FomodSelectionEntry? fallback = group.FirstOrDefault(
                    e => e.Plugin.Type != FomodPluginType.NotUsable);
                if (fallback is not null)
                {
                    _selected.Add(fallback);
                }
            }
        }

        RebuildFlags();
    }

    private void DeselectGroup(FomodGroup group, FomodSelectionEntry except)
    {
        foreach (FomodSelectionEntry entry in _entries)
        {
            if (ReferenceEquals(entry.Group, group) && !ReferenceEquals(entry, except) && !IsLocked(entry))
            {
                _selected.Remove(entry);
            }
        }
    }

    /// <summary>按「选中项且所在步骤可见」重算标记集合（不动点迭代，隐藏步骤的标记不生效）。</summary>
    /// <remarks>
    /// 步骤可见性本身依赖标记，而标记只来自可见步骤，因此从空标记起步迭代：
    /// 每轮用上一轮的标记求可见步骤 → 重算标记，直到两轮结果一致（最多 max(1, 步骤数) + 1 轮；零步骤时为 2 轮）。
    /// 从空集起步保证隐藏步骤无法靠自己的标记把自己「点亮」。
    /// </remarks>
    private void RebuildFlags()
    {
        int maxPass = Math.Max(1, _config.InstallSteps.Count) + 1;
        Dictionary<string, string> previous = new(StringComparer.OrdinalIgnoreCase);
        bool converged = false;
        for (int pass = 0; pass < maxPass; pass++)
        {
            Dictionary<string, string> next = CollectFlags(previous);
            bool stable = previous.Count == next.Count
                && previous.All(pair => next.TryGetValue(pair.Key, out string? value)
                    && string.Equals(value, pair.Value, StringComparison.Ordinal));
            previous = next;
            if (stable)
            {
                converged = true;
                break;
            }
        }

        if (converged)
        {
            // 后续求值已收敛：清掉之前记录的未收敛告警，避免把过期问题带到安装提示。
            if (_convergenceWarningAdded)
            {
                _convergenceWarningAdded = false;
                _warnings.RemoveAll(static warning =>
                    warning.StartsWith("FOMOD 步骤可见性依赖未在限定轮数内收敛", StringComparison.Ordinal));
            }
        }
        else if (!_convergenceWarningAdded)
        {
            // 依赖链过长或标记翻转时不会收敛：明确告警，避免静默使用中间结果（C-15）。
            _convergenceWarningAdded = true;
            _warnings.Add("FOMOD 步骤可见性依赖未在限定轮数内收敛，已按最后一轮结果处理；请检查条件依赖是否存在循环。");
        }

        _flags.Clear();
        foreach ((string name, string value) in previous)
        {
            _flags[name] = value;
        }
    }

    /// <summary>按给定标记集合判断可见步骤，收集这些步骤里选中项设置的标记。</summary>
    private Dictionary<string, string> CollectFlags(IReadOnlyDictionary<string, string> visibleFlags)
    {
        Dictionary<string, string> flags = new(StringComparer.OrdinalIgnoreCase);
        foreach (FomodSelectionEntry entry in _entries)
        {
            if (!_selected.Contains(entry) || !IsStepVisibleUnder(entry.Step, visibleFlags))
            {
                continue;
            }

            foreach (FomodFlag flag in entry.Plugin.Flags)
            {
                if (!string.IsNullOrWhiteSpace(flag.Name))
                {
                    flags[flag.Name] = flag.Value ?? string.Empty;
                }
            }
        }

        return flags;
    }
}
