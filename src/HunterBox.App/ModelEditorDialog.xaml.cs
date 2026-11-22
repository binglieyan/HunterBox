using HunterBox.Application.Models;
using HunterBox.Domain.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace HunterBox.App;

/// <summary>「MOD文件修改」对话框：每个检测到的模型一行，选择目标模型（模型修改窗口）。</summary>
/// <remarks>
/// 改写按「完整模型身份」提交：<see cref="Changes"/> 返回完整身份路径形式
/// （护甲 <c>ch03/012/003</c>、猫装备 <c>ch05/003/0001</c>、武器 <c>it10/03/0000</c>），
/// 由 <c>PakModelRewriteService.RewritePaths</c> / <c>LooseModelRewriteService.Rewrite</c> 处理。
/// 当前模型无论是否在候选表/过滤结果里都会作为必选候选加入；没有已记住目标时默认选中它，避免空白选择。
/// 「拖文件夹到列表」复用本对话框（文件夹模式：不显示「仅显示未使用的ID」）。
/// 目标模型不是下拉框而是「按钮 + 弹出面板」：候选通常数百条（实测单行上限约 274 条），下拉框只能滚动查找；
/// 点击行内按钮弹出 <see cref="ModelPickerView"/>，面板顶部可按名称/完整 ID 模糊搜索，单击即选中。
/// </remarks>
public sealed partial class ModelEditorDialog : ContentDialog
{
    /// <summary>行内按钮右侧的下拉箭头字形（Segoe Fluent Icons 的 ChevronDown）。</summary>
    private const string ChevronGlyph = "\uE70D";

    private readonly IReadOnlyList<ModelDetail> _models;
    private readonly IModelCatalog _catalog;
    private readonly List<ModelIdentity> _ownIdentities;
    private readonly HashSet<string> _mergedUsed;
    private readonly Dictionary<string, string> _selections = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EditorRow> _rows = [];
    // 候选缓存跨 BuildRows 复用，切换过滤/改选时不重复过滤同一类型。
    private readonly Dictionary<int, IReadOnlyList<ModelChoice>> _weaponChoices = [];
    private readonly Dictionary<int, IReadOnlyList<ModelChoice>> _catChoices = [];
    private readonly ModelPickerView _picker;
    private readonly Flyout _pickerFlyout;
    private readonly bool _modEnabled;
    private EditorRow? _pickerRow;
    private bool _initialized;

    /// <summary>用检测结果构建行。</summary>
    /// <param name="models">检测到的模型（含名字与类型）。</param>
    /// <param name="catalog">目标模型目录。</param>
    /// <param name="usedIds">当前游戏全部 MOD 已占用的完整模型 ID（用于「仅显示未使用的ID」）；文件夹模式忽略。</param>
    /// <param name="modName">MOD 名字（只读显示）；<see langword="null"/> 时隐藏该行（兼容旧调用）。</param>
    /// <param name="modEnabled">MOD 是否已启用：启用时行只读（允许启用状态下打开查看，确定后由入口提示先禁用）。</param>
    /// <param name="folderMode">文件夹模式（拖文件夹改模型 ID）：标题不同且不显示「仅显示未使用的ID」。</param>
    public ModelEditorDialog(
        IReadOnlyList<ModelDetail> models,
        IModelCatalog catalog,
        ISet<string>? usedIds = null,
        string? modName = null,
        bool modEnabled = false,
        bool folderMode = false)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(catalog);

        InitializeComponent();

        // 候选面板整个对话框只建一次：换行时只换候选，避免每行都创建一套 ListView。
        _picker = new ModelPickerView { ChoiceChosen = OnPickerChoiceChosen };
        // 资源键改名时退回默认样式，避免构造期 KeyNotFound 崩溃。
        Style? presenterStyle = Resources.TryGetValue("ModelPickerPresenterStyle", out object? style)
            ? style as Style
            : null;
        _pickerFlyout = new Flyout
        {
            Content = _picker,
            Placement = FlyoutPlacementMode.Bottom,
            FlyoutPresenterStyle = presenterStyle,
        };
        // 面板打开后再要焦点：ShowAt 之前控件还没挂到弹出层，Focus 会落空。
        _pickerFlyout.Opened += (_, _) =>
        {
            // Flyout 不继承 ContentDialog 的主题，显式把对话框当前主题推给面板。
            _picker.RequestedTheme = ActualTheme;
            _picker.FocusSearch();
            // ShowAt 之前 ListView 还没布局，滚动必须放到打开之后再执行。
            _picker.ScrollToCurrent();
        };

        // 注意：必须先把字段准备好、再把「仅显示未使用的ID」置为勾选；
        // 该复选框的 Checked 事件会同步触发 Filter_Changed，虽然此时 _initialized 仍为 false
        // 会被守卫忽略，但字段未赋值时即使不构建行也可能被其它代码读到。
        _models = models;
        _catalog = catalog;
        _ownIdentities = [.. models.Select(m => m.Identity)];
        // 占用集合 + 本 MOD 身份只合并一次，切换过滤/重建行时不再重复复制 usedIds。
        _mergedUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (usedIds is not null)
        {
            foreach (string id in usedIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _mergedUsed.Add(id);
                }
            }
        }

        foreach (ModelIdentity identity in _ownIdentities)
        {
            _mergedUsed.Add(identity.Code);
        }

        _modEnabled = modEnabled;

        if (folderMode)
        {
            Title = "MOD文件夹修改";
            FilterBox.Visibility = Visibility.Collapsed;
            EmptyText.Text = "这个文件夹没什么模型可以修改";
        }
        else
        {
            EmptyText.Text = "这个MOD没什么模型可以修改";
        }

        if (string.IsNullOrWhiteSpace(modName))
        {
            ModNameRow.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModNameText.Text = modName;
        }

        // 先设默认值（FilterBox 的 Checked 事件会同步触发 Filter_Changed，
        // 但 _initialized 仍为 false，守卫会忽略这次由构造引起的“伪用户操作”），末尾再标记已初始化并构建一次行。
        if (!folderMode)
        {
            FilterBox.IsChecked = true;
        }

        BuildRows();
        _initialized = true;
    }

    /// <summary>需要改写的项（源完整身份 → 目标完整身份，均为路径形式）。</summary>
    public IReadOnlyList<(string From, string To)> Changes
    {
        get
        {
            List<(string From, string To)> changes = [];
            foreach (EditorRow row in _rows)
            {
                if (row.Selected is { } choice && !ModelEditorPlanner.IsCurrent(row.Detail.Identity, choice))
                {
                    changes.Add((
                        row.Detail.IdentityForm,
                        ModelEditorPlanner.BuildTargetForm(row.Detail.Identity, choice)));
                }
            }

            return changes;
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            // XAML 加载期间控件属性变化可能提前触发，此时字段/控件尚未准备好。
            return;
        }

        RememberSelections();
        BuildRows();
    }

    /// <summary>
    /// 确定时只做自撞校验：同 MOD 其它模型的目标 ID 会覆盖自身文件。
    /// 「已启用 MOD」「没有任何改动」不在这里拦截——对话框正常关闭，由入口统一提示（对齐原流程）。
    /// </summary>
    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Changes 只构造一次，自撞与一致性校验复用同一份结果。
        IReadOnlyList<(string From, string To)> changes = Changes;
        if (changes.Count == 0)
        {
            return;
        }

        // 取消勾选「仅显示未使用的ID」时，同 MOD 其它模型的 ID 仍可选择；确定前拦截自撞。
        foreach (EditorRow row in _rows)
        {
            if (row.Selected is not { } choice)
            {
                continue;
            }

            string? collision = ModelEditorPlanner.DescribeSelfCollision(
                _ownIdentities, row.Detail.Identity, choice);
            if (collision is not null)
            {
                ShowStatus(collision + "（本次不会写入）");
                args.Cancel = true;
                return;
            }
        }
        // 同一模型身份可能出现在多个 pak 行：必须为它们选择同一个目标，
        // 否则一次 change 列表会应用到每个 pak，结果与逐 pak 选择不一致。
        Dictionary<string, string> targetBySource = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string from, string to) in changes)
        {
            if (targetBySource.TryGetValue(from, out string? existing)
                && !string.Equals(existing, to, StringComparison.OrdinalIgnoreCase))
            {
                ShowStatus("同一模型出现在多个 pak 中：请为它们选择相同的目标 ID（本次不会写入）");
                args.Cancel = true;
                return;
            }

            targetBySource[from] = to;
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>收起状态提示：改选或重建行后，旧的红色结论不再成立。</summary>
    private void ClearStatus()
    {
        StatusText.Text = string.Empty;
        StatusText.Visibility = Visibility.Collapsed;
    }

    /// <summary>按类型键缓存候选列表，同一类型只构建一次（P-17）。</summary>
    /// <param name="cache">缓存字典。</param>
    /// <param name="key">类型键（武器类型 / 猫装备类别）。</param>
    /// <param name="factory">首次访问时构建候选。</param>
    /// <returns>该类型的候选列表。</returns>
    private static IReadOnlyList<ModelChoice> GetOrAdd(
        Dictionary<int, IReadOnlyList<ModelChoice>> cache,
        int key,
        Func<IReadOnlyList<ModelChoice>> factory)
    {
        if (!cache.TryGetValue(key, out IReadOnlyList<ModelChoice>? value))
        {
            value = factory();
            cache[key] = value;
        }

        return value;
    }

    private void RememberSelections()
    {
        foreach (EditorRow row in _rows)
        {
            _selections[SelectionKey(row.Detail)] = row.Selected?.Code ?? row.Detail.RewriteId;
        }
    }

    /// <summary>
    /// 记住目标的行级键：同一模型出现在多个 pak 时 IdentityForm 相同，
    /// 只用它会互相覆盖；把 PakFile 并入键才能保留逐行选择。
    /// </summary>
    /// <param name="detail">行对应的模型信息。</param>
    /// <returns>稳定的行级键。</returns>
    private static string SelectionKey(ModelDetail detail) =>
        ModelEditorPlanner.SelectionKey(detail.PakFile, detail.IdentityForm);

    /// <summary>点击行内按钮：把该行当前候选交给面板，并在按钮下方弹出。</summary>
    /// <param name="row">目标行。</param>
    /// <param name="visible">该行当前可见候选（已按「仅显示未使用的ID」过滤）。</param>
    private void OpenPicker(EditorRow row, IReadOnlyList<ModelChoice> visible)
    {
        _pickerRow = row;
        string currentCode = row.Selected?.Code ?? row.Detail.RewriteId;
        _picker.SetCandidates(
            visible,
            $"选择目标模型：{row.Detail.DisplayName}（当前 {currentCode}）",
            currentCode);
        _pickerFlyout.ShowAt(row.Button);
    }

    /// <summary>面板选中候选：写回该行按钮文本与「记住的目标」，然后关闭面板。</summary>
    /// <param name="choice">用户点选的候选。</param>
    private void OnPickerChoiceChosen(ModelChoice choice)
    {
        if (_pickerRow is not { } row)
        {
            return;
        }

        // 用户已改选，旧的红色校验结论不再成立。
        ClearStatus();
        row.Apply(choice);
        _selections[SelectionKey(row.Detail)] = choice.Code;
        _pickerRow = null;
        _pickerFlyout.Hide();
    }

    private void BuildRows()
    {
        // 过滤可能收起正开着的面板：先关掉，避免面板里的候选与重建后的行对不上。
        _pickerRow = null;
        _pickerFlyout.Hide();
        // 重建后旧的校验/提示结论不再成立，下面按当前状态重新给出。
        ClearStatus();

        RowsPanel.Children.Clear();
        _rows.Clear();

        bool filter = FilterBox.IsChecked == true;
        int noTargetCount = 0;

        // P-17 / 占用集合在构造期合并一次，跨行共享，不再每行复制一份 usedIds。
        // 当前行自己的 ID 仍在集合里也无妨：BuildCandidates 会按 currentCode 强制保留当前模型。
        foreach (ModelDetail detail in _models)
        {
            IReadOnlyList<ModelChoice> all = detail.Kind switch
            {
                ModelKind.CatEquip => GetOrAdd(_catChoices, detail.SecondId, () => _catalog.CatTargets(detail.SecondId)),
                ModelKind.Weapon => GetOrAdd(_weaponChoices, detail.Type, () => _catalog.WeaponTargets(detail.Type)),
                _ => _catalog.ArmorTargets,
            };

            string current = _selections.TryGetValue(SelectionKey(detail), out string? remembered)
                ? remembered
                : detail.RewriteId;

            // 除其它 MOD 占用的 ID 外，还要排除「同一 MOD 其它模型行」的当前 ID，
            // 否则把目标改成同 MOD 另一个模型的 ID 时改写会覆盖自身文件。
            IReadOnlyList<ModelChoice> visible = ModelEditorPlanner.BuildCandidates(
                detail.Identity,
                detail.DisplayName,
                detail.HasChain,
                all,
                _mergedUsed,
                filter,
                current);

            if (all.Count == 0)
            {
                noTargetCount++;
            }

            // 目标模型按钮：文本显示当前选择，右侧箭头提示可展开；点击弹出带搜索的候选面板。
            TextBlock selectionText = new()
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            FontIcon chevron = new()
            {
                Glyph = ChevronGlyph,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid buttonContent = new()
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            };
            buttonContent.Children.Add(selectionText);
            Grid.SetColumn(chevron, 1);
            buttonContent.Children.Add(chevron);

            // BuildCandidates 必定加回当前模型；仍按「记住目标 → 当前模型」顺序回退，
            // 两者都找不到时禁用按钮，避免回退到候选表第一项。
            int selectedIndex = IndexOf(visible, current);
            if (selectedIndex < 0)
            {
                selectedIndex = IndexOf(visible, detail.RewriteId);
            }

            ModelChoice? selected = selectedIndex >= 0 ? visible[selectedIndex] : null;
            Button button = new()
            {
                Content = buttonContent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                // 已启用的 MOD 只读；没有候选时也没有可选的，同样禁用。
                IsEnabled = !_modEnabled && selected is not null,
            };
            // 让 Narrator 能读出该按钮属于哪个模型行。
            AutomationProperties.SetName(
                button,
                $"目标模型：{detail.DisplayName}，当前 {selected?.Display ?? "无"}");
            AutomationProperties.SetHelpText(button, "点击打开候选面板，可按模型名称或完整 ID 搜索");
            // pak 行的 ToolTip 显示 pak 文件名（散装行显示完整行文本，便于看被截断的内容）。
            ToolTipService.SetToolTip(button, detail.PakFile);
            ToolTipService.SetPlacement(button, PlacementMode.Top);

            EditorRow editorRow = new(detail, button, selectionText, selected);
            button.Click += (_, _) => OpenPicker(editorRow, visible);

            TextBlock label = new()
            {
                Text = detail.Display,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTipService.SetToolTip(label, detail.PakFile ?? detail.Display);
            ToolTipService.SetPlacement(label, PlacementMode.Top);

            Grid rowGrid = new()
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(340) },
                },
                ColumnSpacing = 8,
                Opacity = _modEnabled ? 0.6 : 1,
            };
            Grid.SetColumn(label, 0);
            rowGrid.Children.Add(label);
            Grid.SetColumn(button, 1);
            rowGrid.Children.Add(button);

            RowsPanel.Children.Add(rowGrid);
            _rows.Add(editorRow);
        }

        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = _rows.Count > 0;
        if (_modEnabled)
        {
            ShowStatus("该 MOD 已启用：当前只能查看，请先禁用 MOD 再修改。");
        }
        else if (noTargetCount > 0)
        {
            ShowStatus($"{noTargetCount} 个模型在目标表里没有同类型候选，已保留当前模型。");
        }
    }

    private static int IndexOf(IReadOnlyList<ModelChoice> choices, string code)
    {
        for (int i = 0; i < choices.Count; i++)
        {
            if (string.Equals(choices[i].Code, code, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>一行：模型信息 + 目标选择按钮（点击弹出候选面板）。</summary>
    private sealed class EditorRow
    {
        private readonly TextBlock _selectionText;

        /// <summary>登记一行并初始化按钮文本。</summary>
        /// <param name="detail">该行的模型信息。</param>
        /// <param name="button">目标选择按钮（面板的锚点）。</param>
        /// <param name="selectionText">按钮内显示当前目标的文本块。</param>
        /// <param name="selected">初始选中的候选；<see langword="null"/> 表示没有候选。</param>
        public EditorRow(ModelDetail detail, Button button, TextBlock selectionText, ModelChoice? selected)
        {
            Detail = detail;
            Button = button;
            _selectionText = selectionText;
            Selected = selected;
            _selectionText.Text = selected?.Display ?? "没有可选的模型";
        }

        /// <summary>该行的模型信息。</summary>
        public ModelDetail Detail { get; }

        /// <summary>目标选择按钮。</summary>
        public Button Button { get; }

        /// <summary>当前选中的目标候选。</summary>
        public ModelChoice? Selected { get; private set; }

        /// <summary>改用新的目标候选，并同步按钮上的显示文本。</summary>
        /// <param name="choice">面板选中的候选。</param>
        public void Apply(ModelChoice choice)
        {
            Selected = choice;
            _selectionText.Text = choice.Display;
            // 改选后同步无障碍名称，Narrator 读到的目标与界面一致。
            AutomationProperties.SetName(
                Button,
                $"目标模型：{Detail.DisplayName}，当前 {choice.Display}");
        }
    }
}
