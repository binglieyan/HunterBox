using HunterBox.Application.Presentation;

namespace HunterBox.Tests;

/// <summary>
/// App 层视图状态的纯逻辑：按钮可用性、选中裁剪、取消状态与状态前缀替换
/// 不依赖 WinUI，可直接回归。
/// </summary>
public class MainWindowStateTests
{
    [Fact]
    public void SetState_FirstSync_EnablesGlobalActionsOnly()
    {
        MainWindowState state = new();
        Assert.False(state.CanUseGlobalActions);

        state.SetState(isBusy: false, hasMods: false);

        Assert.True(state.CanUseGlobalActions);
        Assert.False(state.CanUseSelectedActions);
        Assert.False(state.CanUseListActions);
        Assert.False(state.ShowCancel);
    }

    [Fact]
    public void SetState_Busy_DisablesAllActionsAndShowsCancel()
    {
        MainWindowState state = new();
        state.SetState(isBusy: false, hasMods: true);
        state.SetSelectedIds([1]);

        state.SetState(isBusy: true, hasMods: true);

        Assert.False(state.CanUseGlobalActions);
        Assert.False(state.CanUseSelectedActions);
        Assert.False(state.CanUseListActions);
        Assert.True(state.ShowCancel);
        Assert.True(state.CanCancel);
    }

    [Fact]
    public void SetSelectedIds_VisibleSubsetWrittenBack_DisablesSelectedActionsWhenEmpty()
    {
        MainWindowState state = new();
        state.SetState(isBusy: false, hasMods: true);
        state.SetSelectedIds([1, 2]);
        Assert.True(state.HasSelection);
        Assert.True(state.CanUseSelectedActions);

        // 过滤/卸载后调用方只回写可见行：选中项仍存在时按钮可用。
        state.SetSelectedIds([1]);
        Assert.True(state.HasSelection);

        // 可见行全部消失：选中集合清空，依赖选中的按钮必须禁用。
        state.SetSelectedIds([]);
        Assert.False(state.HasSelection);
        Assert.False(state.CanUseSelectedActions);
    }

    [Fact]
    public void MarkCancelRequested_DisablesCancelUntilNextBusyCycle()
    {
        MainWindowState state = new();
        state.SetState(isBusy: true, hasMods: true);
        Assert.True(state.CanCancel);

        state.MarkCancelRequested();
        Assert.False(state.CanCancel);

        state.SetState(isBusy: false, hasMods: true);
        state.SetState(isBusy: true, hasMods: true);
        Assert.True(state.CanCancel);
    }

    [Fact]
    public void AppendStatus_PreservesExistingText()
    {
        MainWindowState state = new();
        state.Status = "已加载 3 个 MOD";

        state.AppendStatus("索引保存失败");

        Assert.Equal("已加载 3 个 MOD；索引保存失败", state.Status);

        MainWindowState empty = new();
        empty.AppendStatus("索引保存失败");
        Assert.Equal("索引保存失败", empty.Status);
    }

    /// <summary>替换一次性失败提示时必须保留其后追加的内容（如索引保存失败）。</summary>
    [Fact]
    public void ReplaceStatusPrefix_AppendedText_PreservesAppendedPart()
    {
        const string WarmFailure = "模型识别失败，列表暂显示占位（详见日志）";
        MainWindowState state = new();
        state.Status = WarmFailure;
        state.AppendStatus("索引保存失败");

        state.Status = state.ReplaceStatusPrefix(WarmFailure, "已加载 3 个 MOD");

        Assert.Equal("已加载 3 个 MOD；索引保存失败", state.Status);

        // 前缀不匹配时原样返回，避免误覆盖用户操作产生的状态。
        Assert.Equal(
            "已加载 3 个 MOD；索引保存失败",
            state.ReplaceStatusPrefix("其它提示", "已加载 4 个 MOD"));
        Assert.Equal("已加载 3 个 MOD；索引保存失败", state.Status);

        // 空前缀不能让 StartsWith 恒真而整体替换状态。
        Assert.Equal(
            "已加载 3 个 MOD；索引保存失败",
            state.ReplaceStatusPrefix(string.Empty, "已加载 4 个 MOD"));

        // 状态恰好等于前缀时只返回新前缀，不残留分隔符。
        MainWindowState exact = new();
        exact.Status = WarmFailure;
        Assert.Equal("已加载 3 个 MOD", exact.ReplaceStatusPrefix(WarmFailure, "已加载 3 个 MOD"));

        Assert.Throws<ArgumentNullException>(() => state.ReplaceStatusPrefix(null!, "已加载 4 个 MOD"));
    }

    [Fact]
    public void Setters_UnchangedValue_DoNotRaisePropertyChanged()
    {
        MainWindowState state = new();
        int statusChanges = 0;
        int filterChanges = 0;
        state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowState.Status))
            {
                statusChanges++;
            }

            if (args.PropertyName == nameof(MainWindowState.FilterKeyword))
            {
                filterChanges++;
            }
        };

        state.Status = "x";
        state.Status = "x";
        state.FilterKeyword = string.Empty;
        state.FilterKeyword = "a";

        Assert.Equal(1, statusChanges);
        Assert.Equal(1, filterChanges);
    }
}
