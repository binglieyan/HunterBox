using System.ComponentModel;
using HunterBox.Application.Models;
using HunterBox.Domain.Mods;

namespace HunterBox.App;

/// <summary>列表行：把 <see cref="ModRecord"/> 投影成界面绑定用的只读属性。</summary>
/// <remarks>勾选列是 OneWay 绑定；启用/禁用成功后必须通知 <see cref="Enabled"/> 变化，
/// 否则 ListView 容器回收/异步操作完成后勾选状态会与真实状态不一致。</remarks>
internal sealed partial class ModRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>通知界面重新读取 <see cref="Enabled"/> / <see cref="EnabledGlyph"/>（由主窗口在启用状态变化后调用）。</summary>
    internal void NotifyEnabledChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnabledGlyph)));
    }

    /// <summary>后台预热完成后原地回填「备注信息」列（不重建整表，保留滚动位置与选中行）。</summary>
    /// <param name="modelText">新的备注信息文本（缓存命中的结果）。</param>
    internal void UpdateModelText(string modelText)
    {
        // 元数据可能已在原地更新（自动分类写入分类/描述）：回填时一并刷新显示字段快照。
        CaptureDisplaySnapshot();
        ModelText = modelText;
        Signature = CurrentRowSignature(modelText);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModelText)));
    }

    public ModRow(ModRecord mod, string modelText, string textSignature)
    {
        Mod = mod;
        ModelText = modelText;
        // 列表重建时已算过一次文本签名，这里直接复用，构造期不再重复哈希。
        _cachedTextFiles = mod.RelativeFiles;
        _cachedTextDescription = mod.Metadata.Description ?? string.Empty;
        _cachedTextSignature = textSignature;
        CaptureDisplaySnapshot();
        Signature = CurrentRowSignature(modelText);
    }

    /// <summary>
    /// 记录是否仍是同一对象且显示字段未变：元数据是 init-only record，
    /// 任何修改都会替换实例，因此引用比较即可，无需每行重拼显示签名字符串。
    /// </summary>
    /// <param name="mod">待比较的记录。</param>
    /// <returns>可直接复用当前行时返回 <see langword="true"/>。</returns>
    internal bool HasSameDisplay(ModRecord mod) =>
        ReferenceEquals(Mod, mod)
        && ReferenceEquals(_displayMetadata, mod.Metadata)
        && _displayIndex == mod.Index
        && string.Equals(_displayTargetDirectory, mod.TargetDirectory, StringComparison.Ordinal)
        && _displayKind == mod.Kind;

    /// <summary>记录当前显示的元数据/索引/落点/类型快照（引用比较用）。</summary>
    private void CaptureDisplaySnapshot()
    {
        _displayMetadata = Mod.Metadata;
        _displayIndex = Mod.Index;
        _displayTargetDirectory = Mod.TargetDirectory;
        _displayKind = Mod.Kind;
    }

    /// <summary>显示字段快照：元数据实例（init-only，修改即换实例）。</summary>
    private ModMetadata _displayMetadata = null!;

    /// <summary>显示字段快照：索引。</summary>
    private int _displayIndex;

    /// <summary>显示字段快照：安装落点。</summary>
    private string? _displayTargetDirectory;

    /// <summary>显示字段快照：包种类。</summary>
    private ModKind _displayKind;

    /// <summary>创建时列表显示字段签名；用于判断操作后是否需要重建整表。</summary>
    public string Signature { get; private set; }

    public ModRecord Mod { get; }

    /// <summary>
    /// 描述 + 文件清单的缓存签名（操作后刷新为每行复用：直接按文件枚举增量哈希）。
    /// </summary>
    internal string TextSignature
    {
        get
        {
            string description = Mod.Metadata.Description ?? string.Empty;
            List<string> files = Mod.RelativeFiles;
            if (!ReferenceEquals(files, _cachedTextFiles)
                || !string.Equals(description, _cachedTextDescription, StringComparison.Ordinal))
            {
                _cachedTextFiles = files;
                _cachedTextDescription = description;
                _cachedTextSignature = ModelTextPrefetchService.TextSignature(Mod);
            }

            return _cachedTextSignature!;
        }
    }

    /// <summary>
    /// 用当前记录内容重算整行签名（模型文本变化/回填时用）；只使用固定长度文本签名，不复制整份文件清单。
    /// </summary>
    /// <param name="modelText">新的备注信息文本。</param>
    /// <returns>与 <see cref="Signature"/> 同格式的新行签名。</returns>
    internal string CurrentRowSignature(string modelText) =>
        ModListView.RowSignature(Mod, TextSignature)
        + ModelTextPrefetchService.RowSignatureSeparatorChar
        + modelText;

    /// <summary>文本签名缓存对应的文件清单实例（换清单时失效）。</summary>
    private List<string>? _cachedTextFiles;

    /// <summary>文本签名缓存对应的描述（元数据被原地改写时失效）。</summary>
    private string? _cachedTextDescription;

    /// <summary>描述 + 文件清单的缓存签名。</summary>
    private string? _cachedTextSignature;

    public int Index => Mod.Index;

    public string Name => Mod.DisplayName;

    public string Category => Mod.Metadata.Category ?? string.Empty;

    public string Description => Mod.Metadata.Description ?? string.Empty;

    /// <summary>
    /// 「备注信息」列文本：运行时模型名/完整 ID 列表（含 [pfb]/[pak]），
    /// 无模型时回退 <see cref="Description"/>；由 <see cref="MainWindow"/> 在列表重建时从缓存读取，
    /// 缺失时先显示占位文本，后台预热完成后经 <see cref="UpdateModelText"/> 回填。
    /// </summary>
    public string ModelText { get; private set; }

    /// <summary>整行悬浮提示：MOD 名 + 完整用户描述（列宽会截断模型信息，描述在 ToolTip 里可读全）。</summary>
    public string ToolTipText => string.IsNullOrWhiteSpace(Description)
        ? Name
        : Name + Environment.NewLine + Description;

    public string Version => Mod.Metadata.Version ?? string.Empty;

    public string NexusId => Mod.Metadata.NexusId?.ToString() ?? string.Empty;

    /// <summary>是否已启用（只读投影；写入由激活用例负责）。</summary>
    public bool Enabled => Mod.Enabled;

    /// <summary>勾选列显示字形（无状态，由 <see cref="Enabled"/> 每次渲染决定）。</summary>
    public string EnabledGlyph => Enabled ? "☑" : "☐";
}
