using System.Diagnostics;
using HunterBox.Application.Models;
using HunterBox.Domain.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HunterBox.App;

/// <summary>
/// 目标模型选择面板：上方搜索框按模型名称/完整 ID 模糊过滤，下方列表单击即选中。
/// </summary>
/// <remarks>
/// <para>
/// 面板由宿主对话框放进 <see cref="Flyout"/> 显示：<see cref="ContentDialog"/> 内不能再嵌套对话框，
/// 所以「选目标模型」用弹出面板承载（与存档管理的内联确认面板同一条约束）。
/// </para>
/// <para>
/// 过滤规则在 <see cref="ModelEditorPlanner.Search"/>（纯逻辑，可单测）；本类只管显示与转发选中项。
/// 搜索词为空时直接复用传入的候选列表，不复制也不重新分配。
/// </para>
/// </remarks>
public sealed partial class ModelPickerView : UserControl
{
    /// <summary>全量候选（宿主已按「仅显示未使用的ID」过滤）。</summary>
    private IReadOnlyList<ModelChoice> _candidates = [];

    /// <summary>当前搜索命中的候选；搜索框按回车时选取第一项。</summary>
    private IReadOnlyList<ModelChoice> _visible = [];

    /// <summary>换候选时抑制 TextChanged，避免同一批候选被过滤两次。</summary>
    private bool _suppressFilter;

    /// <summary>当前行目标的完整 ID；面板打开、列表完成布局后滚动到该项。</summary>
    private string? _currentCode;

    /// <summary>创建面板。</summary>
    public ModelPickerView() => InitializeComponent();

    /// <summary>用户选中候选后的回调；由宿主对话框设置，用于把结果写回对应行。</summary>
    public Action<ModelChoice>? ChoiceChosen { get; set; }

    /// <summary>换一批候选并清空搜索框，同时更新面板标题。</summary>
    /// <param name="candidates">候选列表。</param>
    /// <param name="title">面板顶部标题，通常是「正在修改哪一行、当前目标是什么」。</param>
    /// <param name="currentCode">当前行目标的完整 ID；用于把列表滚动到该候选。</param>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> 为 <see langword="null"/>。</exception>
    public void SetCandidates(IReadOnlyList<ModelChoice> candidates, string title, string? currentCode = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        // BuildCandidates 必定加回当前模型：这里是唯一的不变式声明，空态只保留「没有匹配的模型」。
        Debug.Assert(candidates.Count > 0, "候选列表必须包含当前模型（ModelEditorPlanner.BuildCandidates）");
        _candidates = candidates;
        _currentCode = currentCode;
        TitleText.Text = title;
        _suppressFilter = true;
        SearchBox.Text = string.Empty;
        _suppressFilter = false;
        ApplyFilter();
    }

    /// <summary>
    /// 把列表滚动到当前目标；必须在 Flyout 打开、列表完成布局后调用，
    /// 否则 ScrollIntoView 会被忽略。不改变焦点（仍在搜索框）。
    /// </summary>
    public void ScrollToCurrent()
    {
        if (string.IsNullOrWhiteSpace(_currentCode))
        {
            return;
        }

        ModelChoice? current = _visible.FirstOrDefault(
            choice => string.Equals(choice.Code, _currentCode, StringComparison.OrdinalIgnoreCase));
        if (current is not null)
        {
            _ = DispatcherQueue.TryEnqueue(() => ChoiceList.ScrollIntoView(current));
        }
    }

    /// <summary>把键盘焦点放到搜索框，便于面板打开后直接输入。</summary>
    public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    /// <summary>按当前搜索词重建列表与计数文本。</summary>
    private void ApplyFilter()
    {
        _visible = ModelEditorPlanner.Search(_candidates, SearchBox.Text);
        ChoiceList.ItemsSource = _visible;
        bool empty = _visible.Count == 0;
        EmptyText.Text = "没有匹配的模型";
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = BuildCountText();
    }

    /// <summary>底部计数文案：区分「未搜索」与「已搜索」，并提示回车快捷选取。</summary>
    /// <returns>计数文本。</returns>
    private string BuildCountText()
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            return $"共 {_candidates.Count} 个候选；输入名称或模型 ID 可快速定位";
        }

        return $"匹配 {_visible.Count} / {_candidates.Count} 个候选；回车选取第一个，Esc 关闭";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressFilter)
        {
            ApplyFilter();
        }
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        // 必须标记已处理：否则回车会继续冒泡，被 ContentDialog 当成「确定」直接关闭整个编辑器。
        e.Handled = true;
        // 空查询时 _visible[0] 只是候选表第一条，不能当成「搜索命中的第一项」静默改目标。
        if (ModelEditorPlanner.CanChooseFirstOnEnter(SearchBox.Text) && _visible.Count > 0)
        {
            ChoiceChosen?.Invoke(_visible[0]);
        }
    }

    private void ChoiceList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ModelChoice choice)
        {
            ChoiceChosen?.Invoke(choice);
        }
    }
}
