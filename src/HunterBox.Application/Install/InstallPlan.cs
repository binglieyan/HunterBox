using HunterBox.Application.Common;
using HunterBox.Domain.Archives;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.Application.Install;

/// <summary>一次安装提交的只读计划：要复制的文件、记录字段与提示（A-12）。</summary>
/// <param name="Copies">源绝对路径、目标相对路径与安装后相对路径。</param>
/// <param name="Kind">记录里的包类型。</param>
/// <param name="TargetDirectory">记录里的额外落点前缀。</param>
/// <param name="Metadata">记录元数据（已处理用户覆盖）。</param>
/// <param name="RelativeFiles">记录里的文件清单。</param>
/// <param name="Warnings">提交结果要带上的提示。</param>
internal sealed record InstallCopyPlan(
    IReadOnlyList<(string SourceFullPath, string TargetRelative, string InstalledRelative)> Copies,
    ModKind Kind,
    string? TargetDirectory,
    ModMetadata Metadata,
    IReadOnlyList<string> RelativeFiles,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 安装暂存的只读部分：解压结果、识别结论、元数据、变体与 FOMOD，以及把「用户选择」规划成提交清单。
/// </summary>
/// <remarks>
/// 对应「安装 MOD」对话框的数据与校验；真正的落盘提交在 <see cref="InstallCommitter"/>。
/// 释放时清理临时工作目录（<c>Temp/&lt;guid&gt;</c>）。
/// </remarks>
internal sealed class InstallPlan : IDisposable
{
    /// <summary>预览图/截图参与安装的最大文件长度（8MB）。</summary>
    private const long MaxPreviewImageBytes = 8 * 1024 * 1024;

    private readonly IModStore _store;
    private readonly IFileSystem _fileSystem;
    private readonly string _contentRoot;
    private readonly string _gameId;
    private readonly ModRecord? _reinstallTarget;

    private InstallPlan(
        IModStore store,
        IFileSystem fileSystem,
        string workDirectory,
        string contentRoot,
        IReadOnlyList<string> files,
        PackageInspection inspection,
        ModMetadata suggestedMetadata,
        string sourcePath,
        string gameId,
        ModRecord? reinstallTarget)
    {
        _store = store;
        _fileSystem = fileSystem;
        _contentRoot = contentRoot;
        _gameId = gameId;
        _reinstallTarget = reinstallTarget;
        WorkDirectory = workDirectory;
        Files = files;
        Inspection = inspection;
        SuggestedMetadata = suggestedMetadata;
        SourcePath = sourcePath;
    }

    /// <summary>临时工作目录。</summary>
    public string WorkDirectory { get; }

    /// <summary>识别结果。</summary>
    public PackageInspection Inspection { get; }

    /// <summary>包里全部文件（相对路径、正斜杠、小写）。</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>从 <c>modinfo.ini</c>/FOMOD 元数据或压缩包名推断出的元数据。</summary>
    public ModMetadata SuggestedMetadata { get; }

    /// <summary>安装来源路径。</summary>
    public string SourcePath { get; }

    /// <summary>本次安装归属的游戏稳定 ID。</summary>
    public string GameId => _gameId;

    /// <summary>是否为「重装/更新」流程（提交时替换已有记录而不是新增）。</summary>
    public bool IsReinstall => _reinstallTarget is not null;

    /// <summary>包内容指纹（文件清单 + 大小）。</summary>
    public string PackageFingerprint { get; private init; } = string.Empty;

    /// <summary>旧版包指纹（无版本前缀、无关键文件内容哈希），用于匹配升级前的记录。</summary>
    public string LegacyPackageFingerprint { get; private init; } = string.Empty;

    /// <summary>多目录变体（如 Shop Tweaks）；普通包为空集合。</summary>
    public IReadOnlyList<PackageVariant> Variants { get; private init; } = [];

    /// <summary>预览图（优先 FOMOD moduleImage，否则包内第一张图片）绝对路径；没有则为 <see langword="null"/>。</summary>
    public string? PreviewImagePath { get; private init; }

    /// <summary>FOMOD 配置；普通包为 <see langword="null"/>。</summary>
    public FomodConfig? Fomod { get; private init; }

    /// <summary>重装目标记录；普通安装为 <see langword="null"/>。</summary>
    internal ModRecord? ReinstallTarget => _reinstallTarget;

    /// <summary>预览图在包内的相对路径（默认从安装文件里排除；用户显式勾选时保留）；没有预览图时为 <see langword="null"/>。</summary>
    private string? PreviewRelativePath => PreviewImagePath is null
        ? null
        : PackageInspector.Normalize(Path.GetRelativePath(_contentRoot, PreviewImagePath));

    /// <summary>解压来源并构建只读计划；后续步骤失败时清理临时目录。</summary>
    internal static InstallPlan Create(
        IModStore store,
        ModInstaller installer,
        IFileSystem fileSystem,
        string sourcePath,
        string gameId,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default,
        ModRecord? reinstallTarget = null)
    {
        (string workDir, string contentRoot, IReadOnlyList<string> files, PackageInspection inspection) =
            installer.StageCore(sourcePath, progress, cancellationToken);

        try
        {
            FomodConfig? fomod = installer.ReadFomod(contentRoot, files);
            ModMetadata suggested = installer.ReadSuggestedMetadata(contentRoot, files, sourcePath, fomod);

            // 旧版指纹要多枚举一遍文件，只有当前游戏仍存在升级前（无 v2: 前缀）记录时才计算。
            bool needsLegacyFingerprint = store.ModsFor(gameId).Any(static record =>
                !string.IsNullOrEmpty(record.PackageFingerprint)
                && !record.PackageFingerprint.StartsWith("v2:", StringComparison.OrdinalIgnoreCase));

            return new InstallPlan(
                store, fileSystem, workDir, contentRoot, files, inspection, suggested, sourcePath, gameId,
                reinstallTarget)
            {
                Fomod = fomod,
                PackageFingerprint = PackageFilters.ComputeFingerprint(contentRoot, files, fileSystem),
                LegacyPackageFingerprint = needsLegacyFingerprint
                    ? PackageFilters.ComputeLegacyFingerprint(contentRoot, files, fileSystem)
                    : string.Empty,
                PreviewImagePath = FindPreviewImage(contentRoot, files, fomod, fileSystem),
                Variants = PackageVariantScanner.Scan(files, relative =>
                {
                    string path = Path.Combine(contentRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    return fileSystem.FileExists(path) ? fileSystem.ReadAllText(path) : null;
                }, gameId),
            };
        }
        catch
        {
            // 后续步骤失败时 StageCore 已经解出的临时目录不能泄漏。
            fileSystem.TryDeleteDirectory(workDir);
            throw;
        }
    }

    /// <summary>把包内相对路径解析成临时目录里的绝对路径（用于预览图等只读场景）。</summary>
    /// <param name="relativePath">包内相对路径。</param>
    /// <returns>存在的绝对路径；路径为空/越界/文件不存在时为 <see langword="null"/>。</returns>
    public string? ResolveContentFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            string full = BoxPaths.ResolveInside(_contentRoot, PackageInspector.Normalize(relativePath));
            return _fileSystem.FileExists(full) ? full : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>校验 MOD 名称：必填且当前游戏内不能重名。</summary>
    /// <param name="name">用户输入的名称。</param>
    /// <returns>错误消息；合法时返回 <see langword="null"/>。</returns>
    public string? ValidateName(string? name)
    {
        string? trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (trimmed is null)
        {
            return "请填写 MOD 名称";
        }

        foreach (ModRecord existing in _store.ModsFor(_gameId))
        {
            if (_reinstallTarget is not null && existing.Id == _reinstallTarget.Id)
            {
                continue;
            }

            if (string.Equals(existing.DisplayName, trimmed, StringComparison.Ordinal))
            {
                return $"名称已被使用：{trimmed}";
            }
        }

        return null;
    }

    /// <summary>按文件选择/根目录/安装前缀规划一次普通安装。</summary>
    /// <exception cref="InvalidOperationException">一个文件都没选中。</exception>
    internal InstallCopyPlan PlanSelection(
        IReadOnlyCollection<string>? selectedFiles,
        ModMetadata? metadata,
        string? rootDirectory,
        string? installPrefix,
        IReadOnlyCollection<string>? rootDirectories,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool explicitSelection = selectedFiles is not null;
        // 选择集合先建 HashSet：Where(Files.Contains) 会退化成 O(选择数 × 包文件数)（P-04）。
        HashSet<string> available = new(Files, StringComparer.OrdinalIgnoreCase);
        List<string> selected = explicitSelection
            ? [.. selectedFiles!.Select(PackageInspector.Normalize).Where(available.Contains)]
            : [.. Files];

        if (selected.Count == 0)
        {
            throw new InvalidOperationException("至少需要选择一个文件");
        }

        // 「设为根」：把包内一个或多个子目录当成安装根；「安装前缀」：再整体挪到某个子目录下。
        List<string> roots = BuildRootList(rootDirectory, rootDirectories);
        string prefix = NormalizeSegment(installPrefix);
        string? preview = PreviewRelativePath;
        List<(string Source, string Target)> planned = [];
        foreach (string file in selected)
        {
            string? relative = StripRoot(file, roots);
            if (relative is null)
            {
                continue;
            }

            string target = prefix.Length == 0 ? relative : prefix + "/" + relative;

            // 排除清单：modinfo.ini 与 FOMOD 配置始终排除；预览图/截图默认排除，用户显式勾选时安装。
            if (PackageFilters.ShouldExclude(relative, allowPreview: explicitSelection))
            {
                continue;
            }

            // 用户显式勾选的文件优先：即使自动识别为预览图，也尊重用户选择（元数据仍硬排除）。
            if (!explicitSelection
                && preview is not null
                && string.Equals(file, preview, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 安装前缀来自用户输入：只有拼过前缀时才需要重新校验/规范化（拒绝 .. / 绝对路径）；
            // 没有前缀时 target 就是包内已规范化的相对路径，跳过每条目重复规范化（P-29）。
            planned.Add((file, prefix.Length == 0 ? relative : BoxPaths.NormalizeRelative(target)));
        }

        if (planned.Count == 0)
        {
            throw new InvalidOperationException("所选根目录下没有任何选中的文件");
        }

        List<(string SourceFullPath, string TargetRelative, string InstalledRelative)> copies = [.. planned
            .Select(p => (
                Path.Combine(_contentRoot, p.Source.Replace('/', Path.DirectorySeparatorChar)),
                p.Target,
                p.Target))];

        return new InstallCopyPlan(
            copies,
            Inspection.Kind,
            Inspection.TargetDirectory,
            metadata ?? SuggestedMetadata,
            [.. planned.Select(p => p.Target)],
            [.. Inspection.Warnings]);
    }

    /// <summary>规划「只安装某个变体」：去掉变体根目录前缀。</summary>
    /// <exception cref="InvalidOperationException">变体不存在或没有文件。</exception>
    internal InstallCopyPlan PlanVariant(
        PackageVariant variant,
        ModMetadata? metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variant);

        IReadOnlyList<PackageVariant> all = Variants;
        if (!all.Any(v => string.Equals(v.Root, variant.Root, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("变体不存在于该包中");
        }

        List<string> selected = [.. Files.Where(f =>
            f.StartsWith(variant.Root + "/", StringComparison.OrdinalIgnoreCase))];
        if (selected.Count == 0)
        {
            throw new InvalidOperationException("该变体没有文件");
        }

        return PlanSelection(
            selected,
            metadata ?? variant.Metadata,
            rootDirectory: variant.Root,
            installPrefix: null,
            rootDirectories: null,
            cancellationToken);
    }

    /// <summary>按完整 FOMOD 选择状态规划安装（必装文件、可见步骤与条件安装）。</summary>
    /// <exception cref="InvalidOperationException">选择非法或没有可安装文件。</exception>
    internal InstallCopyPlan PlanFomod(
        FomodSelectionState selection,
        ModMetadata? metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        FomodConfig fomod = Fomod ?? throw new InvalidOperationException("该 MOD 不是 FOMOD 包");

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> errors = selection.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", errors));
        }

        // 把选择状态计算期间产生的告警（如步骤可见性未收敛）带到安装结果里。
        List<string> warnings = [.. fomod.Warnings, .. selection.Warnings];
        List<(string Source, string Destination)> planned = [];
        foreach (FomodFileMapping mapping in selection.ResolveFiles())
        {
            try
            {
                string sourcePath = BoxPaths.ResolveInside(_contentRoot, mapping.Source);
                if (!_fileSystem.FileExists(sourcePath))
                {
                    warnings.Add($"FOMOD 引用的文件不存在，已跳过：{mapping.Source}");
                    continue;
                }

                string target = PackageInspector.Normalize(mapping.Destination);
                // 规划阶段就验证目标不会逃出内容根；越界映射跳过并告警，而不是到复制阶段整包失败。
                _ = BoxPaths.ResolveInside(_contentRoot, target);
                if (PackageFilters.ShouldExclude(target))
                {
                    warnings.Add($"已排除配置文件/预览图，不安装：{mapping.Destination}");
                    continue;
                }

                planned.Add((mapping.Source, target));
            }
            catch (ArgumentException ex)
            {
                warnings.Add($"跳过越界 FOMOD 映射：{mapping.Source} -> {mapping.Destination}（{ex.Message}）");
            }
        }

        if (planned.Count == 0)
        {
            throw new InvalidOperationException("FOMOD 没有可安装的安全文件（全部映射越界或缺失）");
        }

        List<(string SourceFullPath, string TargetRelative, string InstalledRelative)> copies = [.. planned
            .Select(p => (
                Path.Combine(_contentRoot, p.Source.Replace('/', Path.DirectorySeparatorChar)),
                p.Destination,
                p.Destination))];

        return new InstallCopyPlan(
            copies,
            ModKind.LooseFiles,
            TargetDirectory: null,
            metadata ?? SuggestedMetadata,
            [.. copies.Select(c => c.InstalledRelative)],
            [.. Inspection.Warnings, .. warnings]);
    }

    /// <summary>构造安装根列表：多根优先；单个根为空视为包根。</summary>
    private static List<string> BuildRootList(string? rootDirectory, IReadOnlyCollection<string>? rootDirectories)
    {
        List<string> roots = [];
        if (rootDirectories is not null && rootDirectories.Count > 0)
        {
            foreach (string root in rootDirectories)
            {
                string normalized = NormalizeSegment(root);
                if (normalized.Length > 0)
                {
                    roots.Add(normalized);
                }
            }

            return roots;
        }

        string single = NormalizeSegment(rootDirectory);
        return single.Length == 0 ? roots : [single];
    }

    /// <summary>去掉命中的安装根；没有根时原样返回，找不到匹配根时返回 <see langword="null"/>。</summary>
    private static string? StripRoot(string file, List<string> roots)
    {
        if (roots.Count == 0)
        {
            return file;
        }

        string? best = null;
        foreach (string root in roots)
        {
            if (file.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                && (best is null || root.Length > best.Length))
            {
                best = root;
            }
        }

        return best is null ? null : file[(best.Length + 1)..];
    }

    /// <summary>从包根附近的图片里挑一张当预览图（忽略超大文件与深层目录；FOMOD moduleImage 优先）。</summary>
    private static string? FindPreviewImage(
        string contentRoot,
        IReadOnlyList<string> files,
        FomodConfig? fomod,
        IFileSystem fileSystem)
    {
        if (fomod?.ModuleImage is not null)
        {
            try
            {
                string moduleImage = BoxPaths.ResolveInside(contentRoot, fomod.ModuleImage);
                if (fileSystem.FileExists(moduleImage)
                    && fileSystem.GetFileLength(moduleImage) <= MaxPreviewImageBytes)
                {
                    return moduleImage;
                }
            }
            catch (ArgumentException)
            {
                // moduleImage 越界时回退到包内第一张图片。
            }
        }

        string? candidate = PackageFilters.FindPreviewRelativePath(files);
        if (candidate is null)
        {
            return null;
        }

        string path = Path.Combine(contentRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
        return fileSystem.FileExists(path) && fileSystem.GetFileLength(path) <= MaxPreviewImageBytes
            ? path
            : null;
    }

    /// <summary>把用户输入的根目录/前缀规范成「无前后斜杠」的形式；空或 / 视为根。</summary>
    private static string NormalizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Replace('\\', '/').Trim().Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }

    /// <inheritdoc />
    public void Dispose() => _fileSystem.TryDeleteDirectory(WorkDirectory);
}
