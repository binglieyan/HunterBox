using System.ComponentModel;
using HunterBox.Application.Presentation;
using Microsoft.UI.Xaml;

namespace HunterBox.App;

/// <summary>
/// 主窗口 ViewModel：把无 UI 依赖的 <see cref="MainWindowState"/> 适配成 x:Bind 可订阅的属性。
/// </summary>
/// <remarks>
/// 纯状态与派生按钮可用性在 Application；这里只负责把 <see cref="MainWindowState.ShowCancel"/>
/// 映射成 <see cref="Visibility"/> 并转发属性变更通知。
/// </remarks>
public sealed partial class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly MainWindowState _state = new();

    /// <summary>创建 ViewModel 并订阅底层状态变更。</summary>
    public MainWindowViewModel() => _state.PropertyChanged += OnStateChanged;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>状态栏文本。</summary>
    public string Status
    {
        get => _state.Status;
        set => _state.Status = value;
    }

    /// <summary>搜索关键词（对应过滤输入框）；空串表示非搜索模式。</summary>
    public string FilterKeyword
    {
        get => _state.FilterKeyword;
        set => _state.FilterKeyword = value;
    }

    /// <summary>返回把指定前缀替换为新前缀后的状态文本（<b>不修改</b> ViewModel 状态）；前缀不匹配时原样返回。</summary>
    /// <param name="obsoletePrefix">要替换的旧前缀；状态不以其开头（或为空串）时返回当前状态文本。</param>
    /// <param name="replacement">替换后的前缀。</param>
    /// <returns>替换后的状态文本；前缀不匹配时返回当前状态文本。</returns>
    public string ReplaceStatusPrefix(string obsoletePrefix, string replacement) =>
        _state.ReplaceStatusPrefix(obsoletePrefix, replacement);

    /// <summary>当前列表计数文本（例如「12 个」）。</summary>
    public string CountText
    {
        get => _state.CountText;
        set => _state.CountText = value;
    }

    /// <summary>顶部游戏目录文本（未设置时显示提示语）。</summary>
    public string GamePathText
    {
        get => _state.GamePathText;
        set => _state.GamePathText = value;
    }

    /// <summary>是否正有一个长任务在跑。</summary>
    public bool IsBusy => _state.IsBusy;

    /// <summary>判断指定 MOD 是否处于选中状态。</summary>
    /// <param name="modId">MOD ID。</param>
    /// <returns>选中时返回 <see langword="true"/>。</returns>
    public bool IsSelected(int modId) => _state.IsSelected(modId);

    /// <summary>整体替换选中集合。</summary>
    /// <param name="modIds">当前可见的选中 MOD ID。</param>
    public void SetSelectedIds(IEnumerable<int> modIds) => _state.SetSelectedIds(modIds);

    /// <summary>把单个 MOD 加入选中集合。</summary>
    /// <param name="modId">MOD ID。</param>
    public void AddSelected(int modId) => _state.AddSelected(modId);

    /// <summary>全局操作是否可用。</summary>
    public bool CanUseGlobalActions => _state.CanUseGlobalActions;

    /// <summary>依赖选中项的操作是否可用。</summary>
    public bool CanUseSelectedActions => _state.CanUseSelectedActions;

    /// <summary>依赖列表非空的操作是否可用。</summary>
    public bool CanUseListActions => _state.CanUseListActions;

    /// <summary>取消按钮可见性（忙碌时显示）。</summary>
    public Visibility CancelVisibility => _state.ShowCancel ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>取消按钮是否可用。</summary>
    public bool CanCancel => _state.CanCancel;

    /// <summary>标记已请求取消。</summary>
    public void MarkCancelRequested() => _state.MarkCancelRequested();

    /// <summary>整体更新忙碌与列表非空状态。</summary>
    /// <param name="isBusy">是否忙碌。</param>
    /// <param name="hasMods">列表是否有 MOD。</param>
    public void SetState(bool isBusy, bool hasMods) => _state.SetState(isBusy, hasMods);

    /// <summary>把一条提示追加到当前状态栏文本末尾，保留已有内容。</summary>
    /// <param name="notice">要追加的提示。</param>
    public void AppendStatus(string notice) => _state.AppendStatus(notice);

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(MainWindowState.ShowCancel), StringComparison.Ordinal))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CancelVisibility)));
            return;
        }

        PropertyChanged?.Invoke(this, e);
    }
}
