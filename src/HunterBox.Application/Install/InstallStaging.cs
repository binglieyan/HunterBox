using HunterBox.Application.Common;
using HunterBox.Domain.Archives;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.Application.Install;

/// <summary>
/// 安装暂存：来源已解压到临时目录并识别完成，等待界面确认「装哪些文件 / 用什么元数据」。
/// </summary>
/// <remarks>
/// <para>对应「安装 MOD」对话框：用户可以取消整个安装，也可以只勾选部分文件。
/// 释放时清理临时目录；<see cref="Commit"/> 只清理 <c>Mods/.staging</c>，<c>Temp/&lt;guid&gt;</c> 由 Dispose 清理。</para>
/// <para>职责拆分（A-12）：只读预览与规划在 <see cref="InstallPlan"/>，原子落盘与记录在
/// <see cref="InstallCommitter"/>；本类只做两者的门面，保持既有调用方 API 不变。</para>
/// </remarks>
public sealed class InstallStaging : IDisposable
{
    private readonly InstallPlan _plan;
    private readonly InstallCommitter _committer;

    private InstallStaging(InstallPlan plan, InstallCommitter committer)
    {
        _plan = plan;
        _committer = committer;
    }

    /// <summary>临时工作目录。</summary>
    public string WorkDirectory => _plan.WorkDirectory;

    /// <summary>识别结果。</summary>
    public PackageInspection Inspection => _plan.Inspection;

    /// <summary>包里全部文件（相对路径、正斜杠、小写）。</summary>
    public IReadOnlyList<string> Files => _plan.Files;

    /// <summary>从 <c>modinfo.ini</c>/FOMOD 元数据或压缩包名推断出的元数据。</summary>
    public ModMetadata SuggestedMetadata => _plan.SuggestedMetadata;

    /// <summary>安装来源路径。</summary>
    public string SourcePath => _plan.SourcePath;

    /// <summary>本次安装归属的游戏稳定 ID。</summary>
    public string GameId => _plan.GameId;

    /// <summary>是否为「重装/更新」流程（提交时替换已有记录而不是新增）。</summary>
    public bool IsReinstall => _plan.IsReinstall;

    /// <summary>包内容指纹（文件清单 + 大小）。</summary>
    public string PackageFingerprint => _plan.PackageFingerprint;

    /// <summary>多目录变体（如 Shop Tweaks）；普通包为空集合。</summary>
    public IReadOnlyList<PackageVariant> Variants => _plan.Variants;

    /// <summary>预览图（优先 FOMOD moduleImage，否则包内第一张图片）绝对路径；没有则为 <see langword="null"/>。</summary>
    public string? PreviewImagePath => _plan.PreviewImagePath;

    /// <summary>FOMOD 配置；普通包为 <see langword="null"/>。</summary>
    public FomodConfig? Fomod => _plan.Fomod;

    internal static InstallStaging Create(
        BoxPaths paths,
        IModStore store,
        ModInstaller installer,
        string sourcePath,
        string gameId,
        IFileSystem fileSystem,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default,
        ModRecord? reinstallTarget = null)
    {
        InstallPlan plan = InstallPlan.Create(
            store, installer, fileSystem, sourcePath, gameId, progress, cancellationToken, reinstallTarget);
        return new InstallStaging(plan, new InstallCommitter(paths, store, fileSystem, plan));
    }

    /// <summary>把包内相对路径解析成临时目录里的绝对路径（用于预览图等只读场景）。</summary>
    /// <param name="relativePath">包内相对路径。</param>
    /// <returns>存在的绝对路径；路径为空/越界/文件不存在时为 <see langword="null"/>。</returns>
    public string? ResolveContentFile(string? relativePath) => _plan.ResolveContentFile(relativePath);

    /// <summary>校验 MOD 名称：必填且当前游戏内不能重名。</summary>
    /// <param name="name">用户输入的名称。</param>
    /// <returns>错误消息；合法时返回 <see langword="null"/>。</returns>
    public string? ValidateName(string? name) => _plan.ValidateName(name);

    /// <summary>提交安装。</summary>
    /// <param name="selectedFiles">要安装的文件（相对路径）；为 <see langword="null"/> 时安装全部。</param>
    /// <param name="metadata">覆盖元数据；为 <see langword="null"/> 时用推断结果。</param>
    /// <param name="rootDirectory">把包内哪个子目录当作安装根；<see langword="null"/> 表示包根。</param>
    /// <param name="installPrefix">安装前缀（再套一层子目录）；<see langword="null"/> 或 <c>/</c> 表示不套。</param>
    /// <param name="rootDirectories">多根安装（可设置多个根目录）；非空时优先于 <paramref name="rootDirectory"/>。</param>
    /// <param name="archiveMode">安装源归档方式（0 无操作 / 1 复制 / 2 移动）。</param>
    /// <param name="cancellationToken">
    /// 取消令牌：只在写暂存目录的阶段检查；取消时删除暂存内容，不写记录、不推进 Id。
    /// </param>
    /// <param name="finalizeRecord">记录落盘前的内存修改（自动分类），与记录保存合并。</param>
    /// <returns>安装结果。</returns>
    /// <exception cref="InvalidOperationException">一个文件都没选中；包指纹重复或重装目标已启用。</exception>
    /// <exception cref="OperationCanceledException">令牌在复制阶段被触发（不会留下半成品/记录）。</exception>
    public InstallResult Commit(
        IReadOnlyCollection<string>? selectedFiles = null,
        ModMetadata? metadata = null,
        string? rootDirectory = null,
        string? installPrefix = null,
        IReadOnlyCollection<string>? rootDirectories = null,
        InstallSourceMode archiveMode = InstallSourceMode.None,
        CancellationToken cancellationToken = default,
        Action<ModRecord>? finalizeRecord = null)
    {
        InstallCopyPlan plan = _plan.PlanSelection(
            selectedFiles, metadata, rootDirectory, installPrefix, rootDirectories, cancellationToken);
        return _committer.Commit(plan, archiveMode, cancellationToken, finalizeRecord);
    }

    /// <summary>只安装某个变体（去掉变体根目录前缀）。</summary>
    /// <param name="variant">选中的变体。</param>
    /// <param name="metadata">覆盖元数据。</param>
    /// <param name="archiveMode">安装源归档方式。</param>
    /// <param name="cancellationToken">取消令牌（语义同 <see cref="Commit"/>）。</param>
    /// <param name="finalizeRecord">记录落盘前的内存修改（自动分类），与记录保存合并。</param>
    /// <returns>安装结果。</returns>
    /// <exception cref="InvalidOperationException">变体不存在或没有文件。</exception>
    /// <exception cref="OperationCanceledException">令牌在复制阶段被触发（不会留下半成品/记录）。</exception>
    public InstallResult CommitVariant(
        PackageVariant variant,
        ModMetadata? metadata = null,
        InstallSourceMode archiveMode = InstallSourceMode.None,
        CancellationToken cancellationToken = default,
        Action<ModRecord>? finalizeRecord = null)
    {
        InstallCopyPlan plan = _plan.PlanVariant(variant, metadata, cancellationToken);
        return _committer.Commit(plan, archiveMode, cancellationToken, finalizeRecord);
    }

    /// <summary>按完整 FOMOD 选择状态提交安装（界面使用；含必装文件、可见步骤与条件安装）。</summary>
    /// <param name="selection">选择状态（由 <see cref="FomodSelectionState"/> 构造）。</param>
    /// <param name="metadata">覆盖元数据。</param>
    /// <param name="archiveMode">安装源归档方式。</param>
    /// <param name="cancellationToken">取消令牌（语义同 <see cref="Commit"/>）。</param>
    /// <param name="finalizeRecord">记录落盘前的内存修改（自动分类），与记录保存合并。</param>
    /// <returns>安装结果。</returns>
    /// <exception cref="InvalidOperationException">选择非法或没有可安装文件。</exception>
    /// <exception cref="OperationCanceledException">令牌在复制阶段被触发（不会留下半成品/记录）。</exception>
    public InstallResult CommitFomod(
        FomodSelectionState selection,
        ModMetadata? metadata = null,
        InstallSourceMode archiveMode = InstallSourceMode.None,
        CancellationToken cancellationToken = default,
        Action<ModRecord>? finalizeRecord = null)
    {
        InstallCopyPlan plan = _plan.PlanFomod(selection, metadata, cancellationToken);
        return _committer.Commit(plan, archiveMode, cancellationToken, finalizeRecord);
    }

    /// <inheritdoc />
    public void Dispose() => _plan.Dispose();
}
