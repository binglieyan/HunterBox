using HunterBox.Domain.Layout;
using HunterBox.Application.Common;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;

namespace HunterBox.Application.Models;

/// <summary>一次模型改写的结果汇总（供界面弹窗/状态栏展示，不得丢弃 warnings）。</summary>
/// <param name="PaksScanned">尝试扫描/改写的 pak 数（不支持改写的游戏被跳过时为 0）。</param>
/// <param name="EntriesChanged">被改名的 pak 条目总数。</param>
/// <param name="FilesMoved">被改名的散装文件总数。</param>
/// <param name="Warnings">未能处理的路径/条目说明。</param>
/// <param name="UnsupportedPaks">不支持改写的 pak（条目表已加密），格式为 <c>文件名：原因</c>。</param>
public sealed record ModelRewriteSummary(
    int PaksScanned,
    int EntriesChanged,
    int FilesMoved,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnsupportedPaks)
{
    /// <summary>实际改写的路径数（pak 条目 + 散装文件）。</summary>
    public int TotalChanges => EntriesChanged + FilesMoved;

    /// <summary>是否确实改动了内容。</summary>
    public bool HasChanges => TotalChanges > 0;
}

/// <summary>
/// 一次模型改写操作的编排：对 MOD 的根层 pak 逐个按完整身份改 TOC 哈希，再改写散装文件路径。
/// </summary>
/// <remarks>
/// 每个 <c>(From, To)</c> 都应用到所有 pak 与散装文件；pak 只重写条目表里的路径哈希，
/// 数据区/压缩/加密属性不动，也不会修改文件内容。
/// 荒野/崛起的 pak 可以按路径清单还原条目路径后改名；世界的 pak 不属于模型改写范围，
/// 会被跳过并记录到 warnings。
/// 不支持改写的 pak（TOC 加密）不会中断整批操作，而是记录到
/// <see cref="ModelRewriteSummary.UnsupportedPaks"/> 由界面提示；
/// 写回前由改名器生成 TOC 前像 <c>.toc.bak</c>（改条目路径只动条目表，整包 <c>.bak</c> 仅用于内容替换）。
/// </remarks>
public sealed class ModelRewriteCoordinator
{
    private readonly BoxPaths _paths;
    private readonly PakModelRewriteService _pakService;
    private readonly IFileSystem _fileSystem;

    /// <summary>装配编排器。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="pakService">pak 改写服务。</param>
    /// <param name="fileSystem">文件系统端口（散装文件改名与空目录清理）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public ModelRewriteCoordinator(BoxPaths paths, PakModelRewriteService pakService, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(pakService);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _paths = paths;
        _pakService = pakService;
        _fileSystem = fileSystem;
    }

    /// <summary>按指定游戏批量改写一个 MOD 的模型路径。</summary>
    /// <param name="mod">目标 MOD（文件在HunterBox目录里，调用前应已禁用）。</param>
    /// <param name="changes">源/目标身份路径形式对（源自模型编辑对话框）。</param>
    /// <param name="index">pak 路径清单索引（hash → 路径），应传当前游戏对应的索引。</param>
    /// <param name="game">所属游戏（世界会跳过 pak，只改散装文件）。</param>
    /// <param name="backup">是否保留 pak 的 TOC 前像（<c>.toc.bak</c>）。</param>
    /// <returns>汇总结果（总改动数、warnings、不支持的 pak）。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">身份格式/类别/游戏不合法。</exception>
    public ModelRewriteSummary Rewrite(
        ModRecord mod,
        IEnumerable<(string From, string To)> changes,
        PakNameIndex index,
        ModelGame game,
        bool backup = true)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(index);

        List<(string From, string To)> changeList = [.. changes];
        List<string> allPaks = [.. mod.RelativeFiles.Where(ModelPathRules.IsRootPak)];
        bool pakSupported = ModelPathRules.SupportsPakRewrite(game);
        List<string> paks = pakSupported ? allPaks : [];
        List<string> warnings = [];
        List<string> unsupported = [];
        int entriesChanged = 0;
        int filesMoved = 0;

        if (!pakSupported && allPaks.Count > 0)
        {
            warnings.Add(
                $"{ModelGameInfo.GetDisplayName(game)}的 pak 不支持按路径改写，已跳过 {allPaks.Count} 个 pak："
                + string.Join("、", allPaks.Take(5))
                + (allPaks.Count > 5 ? " 等" : string.Empty));
        }

        if (changeList.Count > 0)
        {
            string filesDirectory = _paths.FilesDirectory(mod.Id);
            // 按 pak 聚合全部 change，一次 RenameEntries；不再 change×pak 次整包复制。
            foreach (string pak in paks)
            {
                string pakPath = Path.Combine(filesDirectory, pak);
                try
                {
                    ModelRewriteReport report = _pakService.RewritePaths(pakPath, changeList, index, backup);
                    entriesChanged += report.EntriesChanged;
                    warnings.AddRange(report.Warnings);
                }
                catch (NotSupportedException ex)
                {
                    // TOC 加密：记录原因并继续处理其它 pak（不中断整批，也不写坏文件）
                    unsupported.Add($"{pak}：{ex.Message}");
                }
                catch (FileNotFoundException ex)
                {
                    warnings.Add($"{pak}：{ex.Message}");
                }
                catch (InvalidDataException ex)
                {
                    warnings.Add($"{pak} 不是有效的 pak：{ex.Message}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // 单个 pak 写失败不应中断整批；如实带回原因，继续处理其它 pak。
                    warnings.Add($"{pak} 改写失败：{ex.Message}");
                }
            }

            LooseModelRewriteService loose = new(_paths, _fileSystem);
            foreach ((string from, string to) in changeList)
            {
                try
                {
                    LooseModelRewriteReport looseReport = loose.Rewrite(mod, from, to);
                    filesMoved += looseReport.FilesMoved;
                    warnings.AddRange(looseReport.Warnings);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"散装文件改写失败：{ex.Message}");
                }
            }
        }

        return new ModelRewriteSummary(
            paks.Count,
            entriesChanged,
            filesMoved,
            [.. warnings.Distinct(StringComparer.Ordinal)],
            [.. unsupported.Distinct(StringComparer.Ordinal)]);
    }

}
