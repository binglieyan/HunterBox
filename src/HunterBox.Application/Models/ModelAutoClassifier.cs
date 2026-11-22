using HunterBox.Application.Paks;
using HunterBox.Application.Common;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using DomainIdentity = HunterBox.Domain.Models.ModelIdentity;

namespace HunterBox.Application.Models;

/// <summary>安装后自动识别模型类 MOD，补上分类与备注（安装后模型识别流程）。</summary>
/// <remarks>
/// 从 App 层下沉：pak 条目读取通过 <see cref="IPakEntryPathReader"/> 注入，显示名通过
/// <see cref="IModelNameResolver"/> 注入，日志通过 <c>warn</c> 回调交给调用方（本类不依赖日志实现）。
/// </remarks>
public static class ModelAutoClassifier
{
    /// <summary>扫描 MOD（散装文件 + pak 条目），识别模型并写入分类/备注。</summary>
    /// <remarks>
    /// 只修改内存里的 <paramref name="record"/>，<b>不落盘</b>；调用方必须在批量处理结束后统一
    /// 调一次 <c>store.Save()</c>，避免「每个 MOD 整表重写一次 index.json」的写放大（启动 / 刷新路径曾因此明显卡顿）。
    /// 需要同时算备注文本时请用 <c>Apply(record, entries, game, names)</c> 传入已收集的条目，
    /// 避免同一次预热把同一个 MOD 的 pak 扫两遍（P-08）。
    /// </remarks>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="record">刚安装的 MOD。</param>
    /// <param name="game">MOD 所属游戏（决定 pak 路径清单与识别规则）。</param>
    /// <param name="reader">pak 条目路径读取端口。</param>
    /// <param name="names">模型显示名解析端口。</param>
    /// <param name="warn">warning 回调（扫描失败的 pak、自动分类结果）；不传则不记录。</param>
    /// <param name="recompute">为 <see langword="true"/> 时强制用当前磁盘内容重算备注（用于模型改写后）。</param>
    /// <returns>识别到的完整模型 ID 列表。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<string> Apply(
        BoxPaths paths,
        ModRecord record,
        ModelGame game,
        IPakEntryPathReader reader,
        IModelNameResolver names,
        Action<string>? warn = null,
        bool recompute = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(names);
        return Apply(record, CollectEntries(paths, record, game, reader, warn), game, names, warn, recompute);
    }

    /// <summary>
    /// 用已收集的条目做自动分类（P-08：与备注文本共用一次 pak 扫描的结果）。
    /// </summary>
    /// <param name="record">刚安装/需要分类的 MOD（会被原地写入分类与描述）。</param>
    /// <param name="entries">已收集的扫描条目（散装文件 + pak 条目）。</param>
    /// <param name="game">MOD 所属游戏。</param>
    /// <param name="names">模型显示名解析端口。</param>
    /// <param name="warn">自动分类结果的日志回调；不传则不记录。</param>
    /// <param name="recompute">为 <see langword="true"/> 时强制重算描述。</param>
    /// <returns>识别到的完整模型 ID 列表。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<string> Apply(
        ModRecord record,
        IReadOnlyList<(string Path, string? PakFile)> entries,
        ModelGame game,
        IModelNameResolver names,
        Action<string>? warn = null,
        bool recompute = false) =>
        Apply(record, entries, game, names, out _, warn, recompute);

    /// <summary>
    /// 用已收集的条目做自动分类，并带回本次识别出的模型行供备注文本复用。
    /// </summary>
    /// <param name="record">刚安装/需要分类的 MOD（会被原地写入分类与描述）。</param>
    /// <param name="entries">已收集的扫描条目（散装文件 + pak 条目）。</param>
    /// <param name="game">MOD 所属游戏。</param>
    /// <param name="names">模型显示名解析端口。</param>
    /// <param name="detected">本次识别出的模型行（与分类共用一次检测）。</param>
    /// <param name="warn">自动分类结果的日志回调；不传则不记录。</param>
    /// <param name="recompute">为 <see langword="true"/> 时强制重算描述。</param>
    /// <returns>识别到的完整模型 ID 列表。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<string> Apply(
        ModRecord record,
        IReadOnlyList<(string Path, string? PakFile)> entries,
        ModelGame game,
        IModelNameResolver names,
        out IReadOnlyList<DetectedModel> detected,
        Action<string>? warn = null,
        bool recompute = false)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(names);

        // P-04/P-06：只检测一次；身份收集与模型检测共用同一次遍历，不再对同一份 entries 二次解析。
        List<DomainIdentity> identities = [];
        IReadOnlyList<DetectedModel> models = ModelDetector.Detect(entries, game, identities);
        detected = models;
        if (identities.Count == 0)
        {
            return [];
        }

        ModMetadata metadata = record.Metadata;
        string? category = string.IsNullOrWhiteSpace(metadata.Category) ? "模型替换" : metadata.Category;

        // 备注文本与「备注信息」列共用同一个格式化实现（A-18）：名字 (完整ID) [pfb] [pak]。
        string? description;
        if (!recompute && !string.IsNullOrWhiteSpace(metadata.Description))
        {
            description = metadata.Description;
        }
        else
        {
            // 检测不到模型行但识别到身份时，明确保留「模型替换：」上下文，而不是把不可达的回退参数交给格式化器。
            description = models.Count == 0
                ? "模型替换：" + string.Join(", ", identities.Select(i => i.Code))
                : ModelSummaryText.Build(
                    models.Select(model => new ModelSummaryRow(
                        names.Resolve(model.SamplePath),
                        model.RewriteId,
                        model.HasPrefab,
                        model.PakFile is not null)));
        }

        record.Metadata = metadata with { Category = category, Description = description };
        warn?.Invoke($"自动分类：{record.DisplayName} → {category} / {description}");
        // 不在这里 Save：调用方批量处理完统一落盘一次（见 Apply remarks）。
        return [.. identities.Select(i => i.Code).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// 检测模型并显式报告根层 pak 条目是否全部读取成功：
    /// 条目级不可读（坏 TOC、超限、不支持的 TOC）时为 <see langword="false"/>，调用方不得缓存空明细。
    /// </summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="record">目标 MOD。</param>
    /// <param name="game">MOD 所属游戏（决定 pak 路径清单与识别规则）。</param>
    /// <param name="reader">pak 条目路径读取端口。</param>
    /// <param name="names">模型显示名解析端口。</param>
    /// <param name="warn">warning 回调（扫描失败的 pak）。</param>
    /// <param name="allPaksReadable">全部根层 pak 条目读取成功（含空 pak）时为 <see langword="true"/>。</param>
    /// <returns>检测到的模型行（去重键 = 完整身份 + pak 文件名）。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<ModelDetail> DetectDetails(
        BoxPaths paths,
        ModRecord record,
        ModelGame game,
        IPakEntryPathReader reader,
        IModelNameResolver names,
        Action<string>? warn,
        out bool allPaksReadable)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(names);

        IReadOnlyList<(string Path, string? PakFile)> entries =
            CollectEntries(paths, record, game, reader, warn, out allPaksReadable);
        IReadOnlyList<DetectedModel> detected = DetectModels(entries, game);
        List<ModelDetail> details = new(detected.Count);
        foreach (DetectedModel model in detected)
        {
            details.Add(ModelDetail.FromDetected(model, names.Resolve(model.SamplePath)));
        }

        return details;
    }

    /// <summary>用已收集的条目计算「备注信息」列文本（P-08：与自动分类共用一次 pak 扫描的结果）。</summary>
    /// <param name="record">目标 MOD。</param>
    /// <param name="entries">已收集的扫描条目（散装文件 + pak 条目）。</param>
    /// <param name="game">MOD 所属游戏。</param>
    /// <param name="names">模型显示名解析端口。</param>
    /// <returns>列文本（没有模型且没有描述时为空串）。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static string BuildModelText(
        ModRecord record,
        IReadOnlyList<(string Path, string? PakFile)> entries,
        ModelGame game,
        IModelNameResolver names) =>
        BuildModelText(record, entries, game, names, DetectModels(entries, game));

    /// <summary>用已检测出的模型行计算「备注信息」列文本，避免与自动分类重复检测。</summary>
    /// <param name="record">目标 MOD。</param>
    /// <param name="entries">已收集的扫描条目（保留参数以维持调用契约）。</param>
    /// <param name="game">MOD 所属游戏（保留参数以维持调用契约）。</param>
    /// <param name="names">模型显示名解析端口。</param>
    /// <param name="detected">已算出的模型行（来自 <c>Apply</c> 的 out 参数）。</param>
    /// <returns>列文本（没有模型且没有描述时为空串）。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static string BuildModelText(
        ModRecord record,
        IReadOnlyList<(string Path, string? PakFile)> entries,
        ModelGame game,
        IModelNameResolver names,
        IReadOnlyList<DetectedModel> detected)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(detected);
        _ = game;

        string fallback = record.Metadata.Description ?? string.Empty;
        if (detected.Count == 0)
        {
            return fallback;
        }

        List<ModelSummaryRow> rows = new(detected.Count);
        foreach (DetectedModel model in detected)
        {
            rows.Add(new ModelSummaryRow(
                names.Resolve(model.SamplePath),
                model.RewriteId,
                model.HasPrefab,
                model.PakFile is not null));
        }

        return ModelSummaryText.Build(rows, fallback);
    }

    /// <summary>没有可编辑模型时给用户的提示（首行固定为「这个MOD没什么模型可以修改」）。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="record">目标 MOD。</param>
    /// <param name="game">MOD 所属游戏（世界 pak 不参与改写，不提示 pak 加密）。</param>
    /// <param name="repacker">pak 整包解析端口（只用于检查根层 pak 是否可解析）。</param>
    /// <param name="fileSystem">文件系统端口（判断 pak 文件是否存在）。</param>
    /// <returns>提示文本。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static string DescribeNoModels(
        BoxPaths paths,
        ModRecord record,
        ModelGame game,
        IPakRepacker repacker,
        IFileSystem fileSystem)
    {
        const string NoModels = "这个MOD没什么模型可以修改";

        IReadOnlyList<string> unsupportedPaks = InspectUnsupportedPaks(
            paths,
            record,
            game,
            repacker,
            fileSystem);
        if (unsupportedPaks.Count > 0)
        {
            return NoModels + Environment.NewLine
                + "该 MOD 含暂不支持改写的 pak：" + string.Join("、", unsupportedPaks)
                + "。请改用未加密的 MOD，或先把它解包成散装文件。";
        }

        return NoModels;
    }

    /// <summary>列出 MOD 里不支持改写的根层 pak（TOC 加密或无法解析），用于把原因带回界面。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="record">目标 MOD。</param>
    /// <param name="game">MOD 所属游戏（世界 pak 不支持内改路径，不检查）。</param>
    /// <param name="repacker">pak 整包解析端口（能解包/重打包的 pak 不视为不支持）。</param>
    /// <param name="fileSystem">文件系统端口（判断 pak 文件是否存在）。</param>
    /// <returns>形如「mod.pak（条目表加密）」的说明列表。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<string> InspectUnsupportedPaks(
        BoxPaths paths,
        ModRecord record,
        ModelGame game,
        IPakRepacker repacker,
        IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(repacker);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (game == ModelGame.World)
        {
            return [];
        }

        List<string> unsupported = [];
        foreach (string pak in record.RelativeFiles.Where(ModelPathRules.IsRootPak))
        {
            string full = Path.Combine(
                paths.FilesDirectory(record.Id),
                pak.Replace('/', Path.DirectorySeparatorChar));
            if (!fileSystem.FileExists(full))
            {
                continue;
            }

            try
            {
                // 能解包/重打包的 pak（含 TOC 加密）不再视为不支持；只有结构/加密方式确实不支持才提示。
                _ = repacker.Inspect(full);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                unsupported.Add($"{pak}（无法解析：{ex.Message}）");
            }
        }

        return unsupported;
    }

    /// <summary>从已收集的条目检测模型行（编辑器、「备注信息」列与预热共用同一份扫描结果，P-08）。</summary>
    private static IReadOnlyList<DetectedModel> DetectModels(
        IReadOnlyList<(string Path, string? PakFile)> entries,
        ModelGame game) =>
        game == ModelGame.Unknown
            ? ModelDetector.Detect(entries)
            : ModelDetector.Detect(entries, game);

    /// <summary>收集 MOD 的全部条目：散装文件原样保留，pak 条目按内置清单还原并记录来源 pak。</summary>
    /// <remarks>
    /// 每次调用都会重读根层 pak 的 TOC；预热/分类/备注文本需要多次读取时请只调用一次并把结果传入
    /// <c>Apply(record, entries, game, names)</c> / <c>BuildModelText(record, entries, game, names)</c>（P-08）。
    /// </remarks>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="record">目标 MOD。</param>
    /// <param name="game">MOD 所属游戏。</param>
    /// <param name="reader">pak 条目路径读取端口。</param>
    /// <param name="warn">warning 回调（扫描失败的 pak）；不传则不记录。</param>
    /// <returns>路径 + 来源 pak（散装为 <see langword="null"/>）。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<(string Path, string? PakFile)> CollectEntries(
        BoxPaths paths,
        ModRecord record,
        ModelGame game,
        IPakEntryPathReader reader,
        Action<string>? warn = null) =>
        CollectEntries(paths, record, game, reader, warn, out _);

    /// <summary>
    /// 收集条目并显式报告根层 pak 的读取状态：零条目但读取成功仍算成功。
    /// </summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="record">目标 MOD。</param>
    /// <param name="game">MOD 所属游戏。</param>
    /// <param name="reader">pak 条目路径读取端口。</param>
    /// <param name="warn">warning 回调（扫描失败的 pak）；不传则不记录。</param>
    /// <param name="allPaksReadable">全部根层 pak 都读取成功时为 <see langword="true"/>（空 pak 也算成功）。</param>
    /// <returns>路径 + 来源 pak（散装为 <see langword="null"/>）。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<(string Path, string? PakFile)> CollectEntries(
        BoxPaths paths,
        ModRecord record,
        ModelGame game,
        IPakEntryPathReader reader,
        Action<string>? warn,
        out bool allPaksReadable)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(reader);

        bool readable = true;
        IReadOnlyList<(string Path, string? PakFile)> entries = ModelDetector.CollectEntries(
            record.RelativeFiles,
            pak =>
            {
                IReadOnlyList<string>? pathsInPak = ReadPakEntries(paths, record, pak, game, reader, warn);
                if (pathsInPak is null)
                {
                    readable = false;
                }

                return pathsInPak;
            },
            game);
        allPaksReadable = readable;
        return entries;
    }

    /// <summary>读取根层 pak 条目并还原成路径；不支持的 pak 记一条 warning 后跳过（不中断分类）。</summary>
    private static IReadOnlyList<string>? ReadPakEntries(
        BoxPaths paths,
        ModRecord record,
        string pak,
        ModelGame game,
        IPakEntryPathReader reader,
        Action<string>? warn)
    {
        string full = Path.Combine(
            paths.FilesDirectory(record.Id),
            pak.Replace('/', Path.DirectorySeparatorChar));
        PakNameIndex index = reader.LoadNameIndex(game);
        IReadOnlyList<string>? entryPaths = reader.TryReadEntryPaths(full, pak, index, out string? reason);
        if (entryPaths is null)
        {
            warn?.Invoke(BuildPakReadWarning(pak, reason));
        }

        return entryPaths;
    }

    /// <summary>把 pak 读取失败原因拼成日志文案（与下沉到 Application 前一致：解析失败只带底层异常消息）。</summary>
    /// <param name="pak">pak 名。</param>
    /// <param name="reason">读取失败原因（<see cref="IPakEntryPathReader.TryReadEntryPaths"/> 的 reason）。</param>
    /// <returns>warning 文案。</returns>
    internal static string BuildPakReadWarning(string pak, string? reason)
    {
        const string ParsePrefix = "无法解析：";
        string detail = reason is not null && reason.StartsWith(ParsePrefix, StringComparison.Ordinal)
            ? reason[ParsePrefix.Length..]
            : reason ?? "未知原因";
        return $"跳过无法扫描的 pak：{pak}（{detail}）";
    }
}
