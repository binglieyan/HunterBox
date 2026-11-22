using HunterBox.Application.Install;
using HunterBox.Domain.Mods;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace HunterBox.App;

/// <summary>
/// 安装提交所需的数据快照：在 UI 线程从对话框控件读取一次，之后可在后台线程安全提交。
/// </summary>
/// <param name="Metadata">最终元数据（名称等已校验）。</param>
/// <param name="ManualMode">是否手动选文件模式。</param>
/// <param name="SelectedFiles">手动模式下勾选的文件。</param>
/// <param name="RootDirectories">手动模式下的根目录。</param>
/// <param name="InstallPrefix">安装前缀。</param>
/// <param name="HasVariants">是否有多组件变体。</param>
/// <param name="Variant">选中的变体（没有则为 <see langword="null"/>）。</param>
/// <param name="FomodState">FOMOD 选择状态（非 FOMOD 安装为 <see langword="null"/>）。</param>
/// <param name="SourceMode">安装源归档方式。</param>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "记录在 CaptureCommitRequest 中通过 new InstallCommitRequest(...) 实例化；CA1812 对 record 主构造函数存在误报。")]
internal sealed record InstallCommitRequest(
    ModMetadata Metadata,
    bool ManualMode,
    IReadOnlyList<string> SelectedFiles,
    IReadOnlyList<string> RootDirectories,
    string InstallPrefix,
    bool HasVariants,
    PackageVariant? Variant,
    FomodSelectionState? FomodState,
    InstallSourceMode SourceMode);

/// <summary>
/// 「安装 MOD」对话框：预览图/元数据、FOMOD 按步骤分组选择、多根手动文件树、安装源归档。
/// </summary>
/// <remarks>
/// 安装向导说明：
/// 字段补齐作者/来源/描述，名称必填且当前游戏内不能重名；FOMOD 按 InstallStep 分组并显示
/// 必选锁定/单选/至少一选校验/依赖可见性；手动树支持多根与节点 <c>[根]</c> 标记；取消需二次确认。
/// 底部「全选/全不选/已选 x/y」固定在滚动区外，避免被纵向裁切。
/// </remarks>
public sealed partial class InstallDialog : ContentDialog
{
    private readonly InstallStaging _staging;
    private readonly HunterBox.Application.Common.IFileSystem _fileSystem;
    private readonly InstallSourceMode _sourceMode;
    private readonly List<FileTreeItem> _treeRoots = [];
    private readonly List<FomodControl> _fomodControls = [];
    private readonly List<(FomodInstallStep Step, FrameworkElement Panel)> _fomodStepPanels = [];

    private FomodSelectionState? _fomodState;
    private bool _refreshGuard;
    private bool _allowClose;
    private bool _manualMode;
    private bool _initialized;
    private bool _countUpdateQueued;
    private bool _hasVariants;

    /// <summary>手动文件树是否已构建（P-10：FOMOD/多变体包切到手动模式时才建整棵树）。</summary>
    private bool _treeBuilt;

    /// <summary>用暂存结果初始化对话框。</summary>
    /// <param name="staging">已解压并识别的安装暂存。</param>
    /// <param name="fileSystem">文件系统端口（预览图存在性判断）。</param>
    /// <param name="sourceMode">安装源归档方式。</param>
    public InstallDialog(
        InstallStaging staging,
        HunterBox.Application.Common.IFileSystem fileSystem,
        InstallSourceMode sourceMode = InstallSourceMode.None)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(fileSystem);

        InitializeComponent();
        _staging = staging;
        _fileSystem = fileSystem;
        _sourceMode = sourceMode;

        ModMetadata suggested = staging.SuggestedMetadata;
        NameBox.Text = suggested.Name ?? string.Empty;
        CategoryBox.Text = suggested.Category ?? string.Empty;
        VersionBox.Text = suggested.Version ?? string.Empty;
        NexusBox.Text = suggested.NexusId?.ToString() ?? string.Empty;
        AuthorBox.Text = suggested.Author ?? string.Empty;
        HomeUrlBox.Text = suggested.HomeUrl ?? string.Empty;
        DescriptionBox.Text = suggested.Description ?? string.Empty;
        SummaryText.Text = BuildSummaryText();
        ShowPreview(staging.PreviewImagePath);
        ShowWarnings();

        // P-10：整棵文件树按需构建 —— 有分组/变体时默认按向导安装，切到手动模式才建树；
        // 普通包只有手动模式，下面立即构建。旧实现无论哪种模式都在向导出现前为每个文件建 TreeViewNode。
        BuildFomod(staging.Fomod);
        BuildVariants(staging.Variants);
        // 普通包（只有手动模式）也延后到对话框 Loaded 再建树：先让窗口出现，避免大包在构造期卡白屏。
        ManualPanel.Loaded += OnManualPanelLoaded;

        bool hasGroupMode = staging.Fomod is not null || _hasVariants;
        ManualSwitch.Visibility = hasGroupMode ? Visibility.Visible : Visibility.Collapsed;
        if (hasGroupMode)
        {
            // 默认按分组/变体安装；可切到手动文件树（自定义安装开关）。
            _manualMode = false;
            ManualSwitch.IsOn = false;
            ManualPanel.Visibility = Visibility.Collapsed;
            ManualActionBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            _manualMode = true;
            ManualPanel.Visibility = Visibility.Visible;
            ManualActionBar.Visibility = Visibility.Visible;
        }

        UpdateCount();
        _initialized = true;
    }

    /// <summary>普通包对话框首次显示后再建文件树；分组/变体包保持按需（切手动模式才建）。</summary>
    private void OnManualPanelLoaded(object sender, RoutedEventArgs e)
    {
        ManualPanel.Loaded -= OnManualPanelLoaded;
        if (!_manualMode)
        {
            return;
        }

        EnsureFileTree();
        UpdateCount();
    }

    /// <summary>
    /// 在 UI 线程读取当前勾选与表单内容，生成可传给后台线程的提交快照。
    /// </summary>
    /// <returns>提交快照。</returns>
    /// <exception cref="InvalidOperationException">名称为空/重名或没有可安装文件。</exception>
    /// <remarks>必须在 UI 线程调用；快照生成后由 <see cref="InstallWorkflow"/> 在后台提交，不再访问任何控件。</remarks>
    internal InstallCommitRequest CaptureCommitRequest()
    {
        string? nameError = _staging.ValidateName(NameBox.Text);
        if (nameError is not null)
        {
            throw new InvalidOperationException(nameError);
        }

        ModMetadata metadata = BuildMetadata();

        List<string> selected = [];
        List<string> roots = [];
        if (_manualMode)
        {
            // 理论上切到手动模式时已建树；这里兜底保证 UI 线程读取前树一定存在（P-10）。
            EnsureFileTree();
            selected = [.. _treeRoots.SelectMany(r => r.SelectedFiles())];
            if (selected.Count == 0)
            {
                throw new InvalidOperationException("请至少勾选一个要安装的文件");
            }

            roots = [.. _treeRoots
                .SelectMany(r => r.CollectRoots())
                .Select(r => r.RelativePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))];
        }

        PackageVariant? variant = _hasVariants ? VariantBox.SelectedItem as PackageVariant : null;
        return new InstallCommitRequest(
            metadata,
            _manualMode,
            selected,
            roots,
            PrefixBox.Text,
            _hasVariants,
            variant,
            _fomodState,
            _sourceMode);
    }

    private ModMetadata BuildMetadata()
    {
        ModMetadata suggested = _staging.SuggestedMetadata;
        return new ModMetadata
        {
            Name = NameBox.Text.Trim(),
            Category = NullIfEmpty(CategoryBox.Text),
            Version = NullIfEmpty(VersionBox.Text),
            NexusId = int.TryParse(NexusBox.Text, out int nexus) && nexus > 0 ? nexus : null,
            Author = NullIfEmpty(AuthorBox.Text) ?? suggested.Author,
            HomeUrl = NullIfEmpty(HomeUrlBox.Text) ?? suggested.HomeUrl,
            Description = NullIfEmpty(DescriptionBox.Text) ?? suggested.Description,
        };
    }

    private string BuildSummaryText()
    {
        PackageInspection inspection = _staging.Inspection;
        string summary = $"识别为 {DescribeKind(inspection.Kind)}，共 {_staging.Files.Count} 个文件"
            + (inspection.TargetDirectory is null ? string.Empty : $"，安装到 {inspection.TargetDirectory}");
        if (_staging.Fomod is not null)
        {
            summary += $" · FOMOD「{_staging.Fomod.ModuleName}」";
            if (_staging.Fomod.RequiredInstallFiles.Count > 0)
            {
                summary += $"（含 {_staging.Fomod.RequiredInstallFiles.Count} 个必装文件）";
            }
        }

        if (_staging.IsReinstall)
        {
            summary += " · 重装模式（保留 ID/分组）";
        }

        return summary;
    }

    private void ShowWarnings()
    {
        List<string> warnings = [.. _staging.Inspection.Warnings];
        if (_staging.Fomod is not null)
        {
            warnings.AddRange(_staging.Fomod.Warnings);
        }

        if (warnings.Count == 0)
        {
            return;
        }

        // 统一截断常量；调用方已把全部告警写入日志，这里只展示前 N 条。
        WarningBar.Message = DialogTextLimits.JoinTruncated(
            warnings,
            DialogTextLimits.WarningLines,
            "\n",
            unit: "条",
            logFile: AppServices.LogFile);
        WarningBar.IsOpen = true;
    }

    private void ShowPreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_fileSystem.FileExists(path))
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            PreviewImage.Source = new BitmapImage(new Uri(path));
            PreviewImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is UriFormatException or IOException or ArgumentException)
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildVariants(IReadOnlyList<PackageVariant> variants)
    {
        _hasVariants = variants.Count > 0 && _staging.Fomod is null;
        if (!_hasVariants)
        {
            return;
        }

        VariantBox.ItemsSource = variants;
        VariantBox.DisplayMemberPath = nameof(PackageVariant.Name);
        VariantBox.SelectedIndex = 0;
        VariantBox.Visibility = Visibility.Visible;
    }

    private void VariantBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VariantBox.SelectedItem is not PackageVariant variant)
        {
            return;
        }

        ApplyVariantMetadata(variant.Metadata);
    }

    private void ApplyVariantMetadata(ModMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Name))
        {
            NameBox.Text = metadata.Name;
        }

        ReplaceIfPresent(metadata.Category, value => CategoryBox.Text = value);
        ReplaceIfPresent(metadata.Version, value => VersionBox.Text = value);
        ReplaceIfPresent(metadata.Author, value => AuthorBox.Text = value);
        ReplaceIfPresent(metadata.HomeUrl, value => HomeUrlBox.Text = value);
        ReplaceIfPresent(metadata.Description, value => DescriptionBox.Text = value);
        ReplaceIfPresent(metadata.NexusId?.ToString(), value => NexusBox.Text = value);
    }

    private static void ReplaceIfPresent(string? value, Action<string> apply)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            apply(value);
        }
    }

    private void BuildFomod(FomodConfig? fomod)
    {
        if (fomod is null)
        {
            return;
        }

        _fomodState = FomodSelectionState.Create(fomod, _staging.Files);
        GroupsPanel.Children.Clear();
        GroupsPanel.Visibility = Visibility.Visible;

        if (fomod.InstallSteps.Count == 0)
        {
            GroupsPanel.Children.Add(new TextBlock
            {
                Text = fomod.RequiredInstallFiles.Count > 0
                    ? $"该 FOMOD 没有可选安装步骤，将安装 {fomod.RequiredInstallFiles.Count} 个必装文件。"
                    : "该 FOMOD 没有可选安装步骤，也没有必装文件。",
                Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        for (int stepIndex = 0; stepIndex < fomod.InstallSteps.Count; stepIndex++)
        {
            FomodInstallStep step = fomod.InstallSteps[stepIndex];
            StackPanel stepPanel = new() { Spacing = 6 };
            stepPanel.Children.Add(new TextBlock
            {
                Text = step.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
            });

            for (int groupIndex = 0; groupIndex < step.Groups.Count; groupIndex++)
            {
                FomodGroup group = step.Groups[groupIndex];
                stepPanel.Children.Add(BuildFomodGroup(stepIndex, groupIndex, step, group));
            }

            _fomodStepPanels.Add((step, stepPanel));
            GroupsPanel.Children.Add(stepPanel);
        }

        UpdateFomodStepVisibility();
    }

    private Border BuildFomodGroup(
        int stepIndex,
        int groupIndex,
        FomodInstallStep step,
        FomodGroup group)
    {
        StackPanel panel = new() { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{group.Name}  ·  {DescribeGroupType(group.Type)}",
            FontWeight = FontWeights.SemiBold,
        });

        IReadOnlyList<FomodSelectionEntry> entries = _fomodState?.Entries ?? [];
        foreach (FomodSelectionEntry entry in entries
            .Where(e => ReferenceEquals(e.Step, step) && ReferenceEquals(e.Group, group)))
        {
            panel.Children.Add(BuildFomodPlugin(stepIndex, groupIndex, group, entry));
        }

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["LayerFillColorDefaultBrush"],
            Child = panel,
        };
    }

    private Control BuildFomodPlugin(
        int stepIndex,
        int groupIndex,
        FomodGroup group,
        FomodSelectionEntry entry)
    {
        bool locked = FomodSelectionState.IsLocked(entry);
        bool selected = _fomodState?.IsSelected(entry) == true;

        StackPanel label = new() { Spacing = 2 };
        label.Children.Add(new TextBlock
        {
            Text = entry.Plugin.Name + (entry.IsRequired ? "（必需）" : string.Empty),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(entry.Plugin.Description))
        {
            label.Children.Add(new TextBlock
            {
                Text = entry.Plugin.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }

        if (group.Type == FomodGroupType.SelectExactlyOne && !locked)
        {
            RadioButton radio = new()
            {
                Content = label,
                GroupName = $"fomod_{stepIndex}_{groupIndex}",
                IsChecked = selected,
            };
            radio.Checked += (_, _) => OnFomodToggled(entry, true);
            _fomodControls.Add(new FomodControl(entry, radio));
            return radio;
        }

        CheckBox check = new()
        {
            Content = label,
            IsChecked = selected,
            IsEnabled = !locked,
        };
        check.Checked += (_, _) => OnFomodToggled(entry, true);
        check.Unchecked += (_, _) => OnFomodToggled(entry, false);
        _fomodControls.Add(new FomodControl(entry, check));
        return check;
    }

    private void OnFomodToggled(FomodSelectionEntry entry, bool selected)
    {
        if (_refreshGuard || _fomodState is null)
        {
            return;
        }

        _fomodState.SetSelected(entry, selected);
        _refreshGuard = true;
        try
        {
            SyncFomodControls();
            UpdateFomodStepVisibility();
        }
        finally
        {
            _refreshGuard = false;
        }

        if (selected)
        {
            ShowPluginPreview(entry);
        }
    }

    private void SyncFomodControls()
    {
        if (_fomodState is null)
        {
            return;
        }

        foreach (FomodControl row in _fomodControls)
        {
            bool selected = _fomodState.IsSelected(row.Entry);
            bool locked = FomodSelectionState.IsLocked(row.Entry);
            switch (row.Control)
            {
                case RadioButton radio:
                    radio.IsChecked = selected;
                    break;
                case CheckBox check:
                    check.IsChecked = selected;
                    check.IsEnabled = !locked;
                    break;
                default:
                    break;
            }
        }
    }

    private void UpdateFomodStepVisibility()
    {
        if (_fomodState is null)
        {
            return;
        }

        foreach ((FomodInstallStep step, FrameworkElement panel) in _fomodStepPanels)
        {
            panel.Visibility = _fomodState.IsStepVisible(step) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowPluginPreview(FomodSelectionEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Plugin.Image))
        {
            return;
        }

        ShowPreview(_staging.ResolveContentFile(entry.Plugin.Image));
    }

    private void ManualSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        _manualMode = ManualSwitch.IsOn;
        GroupsPanel.Visibility = _manualMode ? Visibility.Collapsed : Visibility.Visible;
        VariantBox.Visibility = !_manualMode && _hasVariants ? Visibility.Visible : Visibility.Collapsed;
        ManualPanel.Visibility = _manualMode ? Visibility.Visible : Visibility.Collapsed;
        ManualActionBar.Visibility = _manualMode ? Visibility.Visible : Visibility.Collapsed;
        if (_manualMode)
        {
            // P-10：首次切到手动模式才构建整棵树（大包只在这里付一次代价），随后按真实勾选更新计数。
            EnsureFileTree();
            UpdateCount();
        }

        ClearError();
    }

    /// <summary>
    /// 懒构建手动文件树（P-10）：只在「普通包默认手动模式」或用户切到手动模式时调用；重复调用无副作用。
    /// </summary>
    private void EnsureFileTree()
    {
        if (_treeBuilt)
        {
            return;
        }

        BuildTree(_staging.Files);
        _treeBuilt = true;
    }

    private void BuildTree(IReadOnlyList<string> files)
    {
        Dictionary<string, FileTreeItem> directories = new(StringComparer.OrdinalIgnoreCase);
        // P-10：路径增量拼（每层只追加一段），只在真正新建节点时物化字符串；
        // 旧实现每层都 string.Join('/', segments[..i])，几万文件时是 O(n²) 次分配。
        StringBuilder path = new();
        foreach (string file in files)
        {
            string[] segments = file.Split('/');
            FileTreeItem? parent = null;
            path.Clear();
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    path.Append('/');
                }

                path.Append(segments[i]);
                bool isFile = i == segments.Length - 1;
                // 增量 StringBuilder 已是当前节点的完整相对路径，直接物化一次复用为字典键（P-22）。
                string key = path.ToString();

                if (!directories.TryGetValue(key, out FileTreeItem? node))
                {
                    node = new FileTreeItem
                    {
                        Name = segments[i],
                        RelativePath = isFile ? file : key,
                        IsFile = isFile,
                        Parent = parent,
                    };
                    node.PropertyChanged += OnTreeItemPropertyChanged;

                    directories[key] = node;
                    if (parent is null)
                    {
                        _treeRoots.Add(node);
                    }
                    else
                    {
                        parent.Children.Add(node);
                    }

                    // 安装说明/预览图/modinfo.ini/FOMOD 元数据默认不勾选；用户可在树里手动勾选，
                    // 但 FOMOD 配置与 fomod/ 目录在提交阶段仍会按元数据排除。
                    if (isFile && ShouldDefaultUncheck(file))
                    {
                        node.IsChecked = false;
                    }
                }

                parent = node;
            }
        }

        foreach (FileTreeItem root in _treeRoots)
        {
            TreeViewNode node = new() { Content = root, IsExpanded = true };
            FileTree.RootNodes.Add(node);
            AddChildren(node, root);
        }

        UpdateRootText();
    }

    /// <summary>
    /// 安装文件中默认不勾选的文件（任意层级）：modinfo.ini、FOMOD 元数据、.txt、.md、.png、.jpg、.jpeg。
    /// FOMOD 配置即使被手动勾选，提交阶段仍按元数据排除。
    /// </summary>
    private static bool ShouldDefaultUncheck(string relativePath) =>
        InstallFileRules.ShouldDefaultUncheck(relativePath);

    private void OnTreeItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileTreeItem.IsChecked) || _countUpdateQueued)
        {
            return;
        }

        // 级联勾选会连续产生大量属性通知：合并成一次 UpdateCount，避免每次通知都 O(n) 重算。
        _countUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _countUpdateQueued = false;
            UpdateCount();
        }))
        {
            _countUpdateQueued = false;
            UpdateCount();
        }
    }

    private static void AddChildren(TreeViewNode node, FileTreeItem item)
    {
        foreach (FileTreeItem child in item.Children)
        {
            TreeViewNode childNode = new() { Content = child, IsExpanded = item.Children.Count > 0 };
            node.Children.Add(childNode);
            AddChildren(childNode, child);
        }
    }

    private void SetRoot_Click(object sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedNode?.Content is not FileTreeItem item)
        {
            ShowError("请先在文件树里选择一个目录");
            return;
        }

        if (item.IsFile)
        {
            ShowError("只能把目录设为根");
            return;
        }

        // 设置一个根时清掉祖先与后代根，兄弟目录可以同时为根（多根）。
        for (FileTreeItem? parent = item.Parent; parent is not null; parent = parent.Parent)
        {
            parent.IsRoot = false;
        }

        foreach (FileTreeItem child in item.Children)
        {
            child.SetRootRecursive(false);
        }

        item.IsRoot = true;
        UpdateRootText();
        ClearError();
    }

    private void UnsetRoot_Click(object sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedNode?.Content is not FileTreeItem item)
        {
            ShowError("请先在文件树里选择一个目录");
            return;
        }

        FileTreeItem? root = item;
        while (root is not null && !root.IsRoot)
        {
            root = root.Parent;
        }

        if (root is not null)
        {
            root.IsRoot = false;
            UpdateRootText();
            ClearError();
            return;
        }

        ShowError("所选目录不是根");
    }

    private void ResetRoot_Click(object sender, RoutedEventArgs e)
    {
        foreach (FileTreeItem root in _treeRoots)
        {
            root.SetRootRecursive(false);
        }

        UpdateRootText();
        ClearError();
    }

    private void UpdateRootText()
    {
        List<string> roots = [.. _treeRoots
            .SelectMany(r => r.CollectRoots())
            .Select(r => r.RelativePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))];
        RootText.Text = roots.Count == 0
            ? "根：包根目录（选中文件按包内路径安装）"
            : "根：" + string.Join(" | ", roots);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (FileTreeItem root in _treeRoots)
        {
            root.IsChecked = value;
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        // P-10：树还没建时按「默认勾选规则」直接算计数（不为了显示计数先建整棵树）。
        int selected = _treeBuilt
            ? _treeRoots.Sum(root => root.SelectedFiles().Count())
            : _staging.Files.Count(file => !ShouldDefaultUncheck(file));
        CountText.Text = $"已选 {selected} / {_staging.Files.Count}";
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? nameError = _staging.ValidateName(NameBox.Text);
        if (nameError is not null)
        {
            args.Cancel = true;
            ShowError(nameError);
            return;
        }

        if (_manualMode)
        {
            EnsureFileTree();
            if (!_treeRoots.SelectMany(r => r.SelectedFiles()).Any())
            {
                args.Cancel = true;
                ShowError("请至少勾选一个要安装的文件");
                return;
            }
        }
        else if (_staging.Fomod is not null)
        {
            IReadOnlyList<string> errors = _fomodState?.Validate() ?? [];
            if (errors.Count > 0)
            {
                args.Cancel = true;
                ShowError(string.Join("\n", errors));
                return;
            }
        }
        else if (_hasVariants && VariantBox.SelectedItem is not PackageVariant)
        {
            args.Cancel = true;
            ShowError("请选择一个变体");
            return;
        }

        ClearError();
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_allowClose || args.Result == ContentDialogResult.Primary)
        {
            return;
        }

        // 取消/关闭前二次确认：大包已经解压，误点会重新解析。
        args.Cancel = true;
        ConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void ConfirmDiscard_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        Hide();
    }

    private void ConfirmContinue_Click(object sender, RoutedEventArgs e) =>
        ConfirmOverlay.Visibility = Visibility.Collapsed;

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void ClearError()
    {
        ErrorBar.Message = string.Empty;
        ErrorBar.IsOpen = false;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string DescribeKind(ModKind kind) => kind switch
    {
        ModKind.LooseFiles => "散装 MOD",
        ModKind.PakFiles => "pak MOD",
        ModKind.RefScripts => "REF 脚本",
        ModKind.RefPlugins => "REF 插件",
        ModKind.Fomod => "FOMOD",
        ModKind.RefFrameworkItself => "REFramework 本体",
        _ => "未知类型",
    };

    private static string DescribeGroupType(FomodGroupType type) => type switch
    {
        FomodGroupType.SelectExactlyOne => "必选一个",
        FomodGroupType.SelectAtMostOne => "最多选一个",
        FomodGroupType.SelectAtLeastOne => "至少选一个",
        FomodGroupType.SelectAll => "全部必选",
        _ => "可多选",
    };

    private sealed record FomodControl(FomodSelectionEntry Entry, Control Control);
}
