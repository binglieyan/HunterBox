using System.ComponentModel;

namespace HunterBox.Application.Presentation;

/// <summary>
/// 主窗口的界面状态：忙碌、选中与列表非空，以及由它们派生的按钮可用性（A-06）。
/// </summary>
/// <remarks>
/// <para>
/// 无 UI 依赖的纯状态：App 层的 <c>MainWindowViewModel</c> 只把它适配成 x:Bind 可订阅的属性，
/// 因此状态迁移可以在 Application 测试里直接验证。
/// </para>
/// <para>窗口只在状态变化时调用 <see cref="SetState"/>；选中状态由 <see cref="SetSelectedIds"/> 单独维护。</para>
/// </remarks>
public sealed class MainWindowState : INotifyPropertyChanged
{
    private readonly HashSet<int> _selectedIds = [];
    private bool _isBusy;
    private bool _cancelRequested;
    private bool _hasMods;
    private bool _ready;
    private string _status = string.Empty;
    private string _filterKeyword = string.Empty;
    private string _countText = string.Empty;
    private string _gamePathText = string.Empty;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>状态栏文本。</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (string.Equals(_status, value, StringComparison.Ordinal))
            {
                return;
            }

            _status = value;
            OnPropertyChanged(nameof(Status));
        }
    }

    /// <summary>把一条提示追加到当前状态栏文本末尾，保留已有内容。</summary>
    /// <param name="notice">要追加的提示；为空时不修改。</param>
    public void AppendStatus(string notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        Status = string.IsNullOrWhiteSpace(Status) ? notice : Status + "；" + notice;
    }

    /// <summary>
    /// 返回把指定前缀替换为新前缀后的状态文本（<b>不修改</b>当前状态）；前缀不匹配时原样返回。
    /// 供调用方按需再赋回 <see cref="Status"/>。
    /// </summary>
    /// <param name="obsoletePrefix">要替换的旧前缀；状态不以其开头（或为空串）时返回当前状态文本。</param>
    /// <param name="replacement">替换后的前缀。</param>
    /// <returns>替换后的状态文本；前缀不匹配时返回当前状态文本。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="obsoletePrefix"/> 或 <paramref name="replacement"/> 为 <see langword="null"/>。
    /// </exception>
    public string ReplaceStatusPrefix(string obsoletePrefix, string replacement)
    {
        ArgumentNullException.ThrowIfNull(obsoletePrefix);
        ArgumentNullException.ThrowIfNull(replacement);
        if (obsoletePrefix.Length == 0
            || !_status.StartsWith(obsoletePrefix, StringComparison.Ordinal))
        {
            return _status;
        }

        return replacement + _status[obsoletePrefix.Length..];
    }

    /// <summary>搜索关键词（对应过滤输入框）；空串表示非搜索模式。</summary>
    public string FilterKeyword
    {
        get => _filterKeyword;
        set
        {
            if (string.Equals(_filterKeyword, value, StringComparison.Ordinal))
            {
                return;
            }

            _filterKeyword = value;
            OnPropertyChanged(nameof(FilterKeyword));
        }
    }

    /// <summary>当前列表计数文本（例如「12 个」）。</summary>
    public string CountText
    {
        get => _countText;
        set
        {
            if (string.Equals(_countText, value, StringComparison.Ordinal))
            {
                return;
            }

            _countText = value;
            OnPropertyChanged(nameof(CountText));
        }
    }

    /// <summary>顶部游戏目录文本（未设置时显示提示语）。</summary>
    public string GamePathText
    {
        get => _gamePathText;
        set
        {
            if (string.Equals(_gamePathText, value, StringComparison.Ordinal))
            {
                return;
            }

            _gamePathText = value;
            OnPropertyChanged(nameof(GamePathText));
        }
    }

    /// <summary>是否正有一个长任务在跑。</summary>
    public bool IsBusy => _isBusy;

    /// <summary>列表是否有选中项。</summary>
    public bool HasSelection => _selectedIds.Count > 0;

    /// <summary>判断指定 MOD 是否处于选中状态。</summary>
    /// <param name="modId">MOD ID。</param>
    /// <returns>选中时返回 <see langword="true"/>。</returns>
    public bool IsSelected(int modId) => _selectedIds.Contains(modId);

    /// <summary>整体替换选中集合；与当前集合相同则不做任何通知。</summary>
    /// <param name="modIds">当前可见的选中 MOD ID。</param>
    /// <exception cref="ArgumentNullException"><paramref name="modIds"/> 为 <see langword="null"/>。</exception>
    public void SetSelectedIds(IEnumerable<int> modIds)
    {
        ArgumentNullException.ThrowIfNull(modIds);
        HashSet<int> next = [.. modIds];
        if (_selectedIds.SetEquals(next))
        {
            return;
        }

        _selectedIds.Clear();
        _selectedIds.UnionWith(next);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanUseSelectedActions));
    }

    /// <summary>把单个 MOD 加入选中集合（移动后保持该行选中）。</summary>
    /// <param name="modId">MOD ID。</param>
    public void AddSelected(int modId)
    {
        if (!_selectedIds.Add(modId))
        {
            return;
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanUseSelectedActions));
    }

    /// <summary>全局操作（设置/刷新/搜索/安装/清理/游戏选择/列表交互）是否可用。</summary>
    /// <remarks>首次 <see cref="SetState"/> 之前保持禁用，避免启动阶段提前点击。</remarks>
    public bool CanUseGlobalActions => _ready && !_isBusy;

    /// <summary>依赖选中项的操作（移动/重装/更新/卸载/启用）是否可用。</summary>
    public bool CanUseSelectedActions => _ready && !_isBusy && HasSelection;

    /// <summary>依赖「列表非空」的操作（禁用全部/重新复制）是否可用。</summary>
    public bool CanUseListActions => _ready && !_isBusy && _hasMods;

    /// <summary>是否显示取消按钮（忙碌时显示）。</summary>
    public bool ShowCancel => _isBusy;

    /// <summary>取消按钮是否可用：忙碌且尚未请求取消。</summary>
    public bool CanCancel => _isBusy && !_cancelRequested;

    /// <summary>标记已请求取消；按钮随绑定自动禁用，不会被下一次忙碌通知重新启用。</summary>
    public void MarkCancelRequested()
    {
        if (_cancelRequested)
        {
            return;
        }

        _cancelRequested = true;
        OnPropertyChanged(nameof(CanCancel));
    }

    /// <summary>整体更新忙碌与列表非空状态；选中状态由 <see cref="SetSelectedIds"/> 单独维护。</summary>
    /// <param name="isBusy">是否忙碌。</param>
    /// <param name="hasMods">列表是否有 MOD。</param>
    public void SetState(bool isBusy, bool hasMods)
    {
        bool readyChanged = !_ready;
        bool busyChanged = _isBusy != isBusy;
        bool modsChanged = _hasMods != hasMods;
        if (!readyChanged && !busyChanged && !modsChanged)
        {
            return;
        }

        _ready = true;
        _isBusy = isBusy;
        _hasMods = hasMods;

        if (readyChanged || busyChanged)
        {
            OnPropertyChanged(nameof(CanUseGlobalActions));
            OnPropertyChanged(nameof(CanUseSelectedActions));
        }

        if (readyChanged || busyChanged || modsChanged)
        {
            OnPropertyChanged(nameof(CanUseListActions));
        }

        if (busyChanged)
        {
            // 新一轮忙碌重新允许取消；结束忙碌时也复位，供下一次任务使用。
            _cancelRequested = false;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(ShowCancel));
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
