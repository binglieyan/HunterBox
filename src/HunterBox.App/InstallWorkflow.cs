using HunterBox.Application.Common;
using HunterBox.Application.Install;
using HunterBox.Application.Models;
using HunterBox.Domain.Archives;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Logging;

namespace HunterBox.App;

/// <summary>
/// 安装/重装的后台服务：解压识别暂存、提交暂存、安装后自动分类与落盘。
/// </summary>
/// <remarks>
/// 从 MainWindow 的安装 UI 编排中抽出的无 UI 依赖部分；窗口只负责选包、安装向导、
/// busy/取消/进度与结果提示。本类不打开对话框、不访问控件。
/// </remarks>
internal sealed class InstallWorkflow
{
    private readonly BoxPaths _paths;
    private readonly IPakEntryPathReader _reader;
    private readonly IModelNameResolver _names;
    private readonly IAppLogger _logger;

    /// <summary>装配安装后台服务。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="reader">pak 条目路径读取端口（安装后自动分类用）。</param>
    /// <param name="names">模型显示名解析端口。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public InstallWorkflow(
        BoxPaths paths,
        IPakEntryPathReader reader,
        IModelNameResolver names,
        IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(names);
        _paths = paths;
        _reader = reader;
        _names = names;
        _logger = logger ?? AppLogAdapter.Instance;
    }

    /// <summary>在后台线程解析安装源（解压 + 识别），不访问 UI。</summary>
    /// <param name="installer">当前游戏的安装器。</param>
    /// <param name="source">安装源路径。</param>
    /// <param name="reinstall">是否重装/更新。</param>
    /// <param name="modId">重装目标 MOD ID。</param>
    /// <param name="progress">解压进度。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>安装暂存。</returns>
    public static InstallStaging Stage(
        ModInstaller installer,
        string source,
        bool reinstall,
        int modId,
        IProgress<ArchiveProgress>? progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(installer);
        return reinstall
            ? installer.StageReinstall(modId, source, progress, token)
            : installer.Stage(source, progress, token);
    }

    /// <summary>提交安装暂存，并在成功后就地完成自动分类与一次索引保存。</summary>
    /// <param name="staging">已解压并识别的安装暂存。</param>
    /// <param name="request">UI 线程捕获的安装向导控件快照。</param>
    /// <param name="game">当前模型改写游戏。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>安装结果。</returns>
    /// <exception cref="ArgumentNullException">参数为 <see langword="null"/>。</exception>
    public InstallResult Commit(
        InstallStaging staging,
        InstallCommitRequest request,
        ModelGame game,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(request);

        // 自动分类在记录落盘前执行，与安装记录合并为一次保存；分类失败只记日志，不能否定安装成功。
        void AutoClassify(ModRecord record)
        {
            try
            {
                ModelAutoClassifier.Apply(_paths, record, game, _reader, _names, _logger.Warn);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _logger.Warn($"安装后自动分类失败（{record.DisplayName}）：{ex.Message}");
            }
        }

        InstallResult result = request.ManualMode
            ? staging.Commit(
                request.SelectedFiles,
                request.Metadata,
                rootDirectories: request.RootDirectories,
                installPrefix: request.InstallPrefix,
                archiveMode: request.SourceMode,
                cancellationToken: token,
                finalizeRecord: AutoClassify)
            : staging.Fomod is not null && request.FomodState is not null
                ? staging.CommitFomod(
                    request.FomodState,
                    request.Metadata,
                    request.SourceMode,
                    token,
                    finalizeRecord: AutoClassify)
                : request.HasVariants && request.Variant is not null
                    ? staging.CommitVariant(
                        request.Variant,
                        request.Metadata,
                        request.SourceMode,
                        token,
                        finalizeRecord: AutoClassify)
                    : staging.Commit(
                        metadata: request.Metadata,
                        archiveMode: request.SourceMode,
                        cancellationToken: token,
                        finalizeRecord: AutoClassify);

        _logger.Info(
            $"{(staging.IsReinstall ? "重装" : "安装")}完成："
            + $"{result.Record.DisplayName}（{result.Record.RelativeFiles.Count} 个文件）");
        return result;
    }
}
