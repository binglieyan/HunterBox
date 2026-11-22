using Microsoft.UI.Xaml.Controls;

namespace HunterBox.App;

/// <summary>
/// 「快速移动」对话框：把选中的 MOD 移动到指定位置（位置从 1 开始）。
/// </summary>
/// <remarks>直接读搜索框数字容易与过滤冲突，这里改为独立对话框。</remarks>
public sealed partial class MoveToDialog : ContentDialog
{
    /// <summary>按当前列表数量与选中位置初始化对话框。</summary>
    /// <param name="count">列表里 MOD的总数（即最大可移动位置）。</param>
    /// <param name="current">当前选中的位置（1 起始），作为默认值。</param>
    public MoveToDialog(int count, int current)
    {
        InitializeComponent();
        PositionBox.Maximum = Math.Max(1, count);
        PositionBox.Value = Math.Clamp(current, 1, Math.Max(1, count));
        HintText.Text = $"把选中的 MOD 移动到第几个位置？（范围 1-{Math.Max(1, count)}，当前是 {current}）";
    }

    /// <summary>目标位置（1 起始）；输入无效时为 -1。</summary>
    public int Position => double.IsNaN(PositionBox.Value) ? -1 : (int)PositionBox.Value;
}
