using HunterBox.Application.Models;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using HunterBox.Application.Paks;
using HunterBox.Infrastructure.Logging;

using HunterBox.Application.Common;
using HunterBox.Application.Concurrency;

namespace HunterBox.App;

/// <summary>模型编辑器的后台准备结果。</summary>
/// <param name="Models">目标 MOD 的模型行。</param>
/// <param name="UsedIds">当前游戏全部 MOD 已占用的模型身份。</param>
/// <param name="NoModelsMessage">目标 MOD 没有可改模型时的提示。</param>
/// <param name="CacheEntries">本次新识别出的模型行（回 UI 线程后写入明细缓存）。</param>
/// <param name="UsedIdEntries">本次算出的「MOD → 已用模型身份」（回 UI 线程后写入增量缓存，P-11）。</param>
internal sealed record ModelEditorPreparation(
    IReadOnlyList<ModelDetail> Models,
    HashSet<string> UsedIds,
    string? NoModelsMessage,
    IReadOnlyList<(int Id, string Signature, IReadOnlyList<ModelDetail> Details)> CacheEntries,
    IReadOnlyList<(int Id, string Signature, IReadOnlyList<string> UsedIds)> UsedIdEntries);

/// <summary>启动预热结果：自动分类是否改动，以及算好的备注文本缓存项。</summary>
/// <param name="ClassificationChanged">是否有 MOD 补上了自动分类/描述（取消时也包含取消前已完成的项）。</param>
/// <param name="Canceled">
/// 预热是否被取消。取消时 <see cref="TextEntries"/> 等只含已完成项，调用方<b>仍须</b>把已完成项
/// 写回缓存并落盘，保证界面显示与磁盘一致（C-23）。
/// </param>
/// <param name="TextEntries">MOD ID → (签名, 备注文本, 根层 pak 内容戳)。</param>
/// <param name="NoModelEntries">已确认无模型的 MOD ID 与文件签名，供后续刷新跳过重扫。</param>
/// <param name="InvalidatedIds">
/// 后台校验发现根层 pak 内容戳已变化的 MOD ID；回 UI 线程后须先移除旧文本再回填本轮结果。
/// </param>
internal sealed record ModelPrewarmResult(
    bool ClassificationChanged,
    bool Canceled,
    IReadOnlyList<(int Id, string Signature, string Text, string PakStamp)> TextEntries,
    IReadOnlyList<(int Id, string Signature)> NoModelEntries,
    IReadOnlyList<int> InvalidatedIds);

/// <summary>
/// 模型识别/备注/缓存：模型文本缓存、模型身份缓存、后台准备工作。
/// </summary>
/// <remarks>
/// 从 MainWindow 抽出的第三块职责；纯读取/缓存，不打开对话框、不启动任务。
/// <b>线程模型：</b>
/// <list type="bullet">
/// <item>文本缓存、pak 戳与所属游戏标记由 <see cref="ModelTextCache"/> 内部锁保护，UI 线程与后台预热共用；</item>
/// <item><see cref="ModelIdentityCache"/> 内部自带锁，明细/已用身份按 <c>(游戏, MOD)</c> 隔离；
/// 后台准备使用 <see cref="IdentitySnapshot"/> 一次物化的只读快照，在途结果不会写到另一个游戏；</item>
/// <item>写盘去抖/重试由 Application 的 <c>ModelTextCachePersistScheduler</c> 管理，本类只负责启动后台任务。</item>
/// </list>
/// </remarks>
internal sealed class ModelPanel
{
    private readonly BoxPaths _paths;
    private readonly IPakEntryPathReader _reader;
    private readonly IModelNameResolver _names;
    private readonly IPakRepacker _repacker;
    private readonly IFileSystem _fileSystem;
    private readonly IAppLogger _logger;
    private readonly IModelTextCacheStore? _textCacheStore;
    private readonly IBackgroundTaskHost? _backgroundTasks;

    /// <summary>
    /// 模型文本缓存初始容量；实际容量随 MOD 数上调，但不超过
    /// <see cref="ModelTextPrefetchService.MaxTextCacheCapacity"/>（5000，超出按 LRU 淘汰）。
    /// </summary>
    private const int InitialTextCacheCapacity = 512;

    /// <summary>模型身份缓存容量（单条含模型行，较小）。</summary>
    /// <remarks>
    /// P-11：打开编辑器需要的「已用模型身份」由 <see cref="_identityCache"/> 的增量缓存维护，
    /// 本容量只影响「目标 MOD 的明细行」是否命中。
    /// </remarks>
    private const int DetailsCacheCapacity = 256;

    /// <summary>「备注信息」列的模型文本缓存：线程安全的签名/文本/戳/游戏标记容器。</summary>
    private readonly ModelTextCache _textCache = new(InitialTextCacheCapacity);

    /// <summary>模型身份缓存：明细行与「已用身份」按 (游戏, MOD) 隔离。</summary>
    private readonly ModelIdentityCache _identityCache = new(DetailsCacheCapacity, InitialUsedIdCacheCapacity);

    /// <summary>身份内容戳的会话内缓存：同一记录实例且库未变化时不重复全库 stat。</summary>
    private readonly ModelIdentityStampCache _stampCache = new();

    /// <summary>持久缓存加载失败后禁用本会话写盘，避免用不完整内存覆盖磁盘缓存；读写跨线程，使用 volatile。</summary>
    private volatile bool _persistDisabled;

    /// <summary>持久缓存的后台加载任务；失败或取消后允许下次预热重新尝试。</summary>
    private Task? _loadTask;

    /// <summary>加载任务的重建锁：UI 线程与后台预热可能同时触发首次加载。</summary>
    private readonly object _loadGate = new();

    /// <summary>文本缓存写盘调度（去抖/重试状态机在 Application，可单测）；无持久化端口时为 <see langword="null"/>。</summary>
    private readonly ModelTextCachePersistScheduler? _persistQueue;

    /// <summary>「已用身份」缓存初始容量；实际容量按 MOD 数上调。</summary>
    private const int InitialUsedIdCacheCapacity = 2048;

    /// <summary>每轮编辑器准备刷新过期内容戳的时间预算（毫秒）。</summary>
    private const int StampRefreshBudgetMilliseconds = 20;

    /// <summary>
    /// 过期内容戳的轮换游标（按游戏隔离）：每次打开编辑器在 20ms 预算内尽量刷新，
    /// 其余复用旧戳并在后续打开中轮换。只在后台准备任务内读写。
    /// </summary>
    private readonly Dictionary<string, int> _stampRefreshCursors = [];

    /// <summary>装配模型面板。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="reader">pak 条目路径读取端口（模型识别扫描用）。</param>
    /// <param name="names">模型显示名解析端口。</param>
    /// <param name="repacker">pak 整包解析端口（「没有可改模型」提示用）。</param>
    /// <param name="fileSystem">文件系统端口（判断 pak 文件是否存在）。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志。</param>
    /// <param name="textCacheStore">跨会话文本缓存端口；为 <see langword="null"/> 时只使用内存缓存。</param>
    /// <param name="backgroundTasks">
    /// 后台任务宿主：文本缓存写盘走它以便关窗等待；为 <see langword="null"/> 时退化为线程池任务。
    /// </param>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public ModelPanel(
        BoxPaths paths,
        IPakEntryPathReader reader,
        IModelNameResolver names,
        IPakRepacker repacker,
        IFileSystem fileSystem,
        IAppLogger? logger = null,
        IModelTextCacheStore? textCacheStore = null,
        IBackgroundTaskHost? backgroundTasks = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(repacker);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _paths = paths;
        _reader = reader;
        _names = names;
        _repacker = repacker;
        _fileSystem = fileSystem;
        _logger = logger ?? AppLogAdapter.Instance;
        _textCacheStore = textCacheStore;
        _backgroundTasks = backgroundTasks;
        if (textCacheStore is not null)
        {
            _persistQueue = new ModelTextCachePersistScheduler(
                textCacheStore,
                // 写盘快照必须等持久缓存加载完成，避免用未合并的内存覆盖磁盘缓存。
                SnapshotTextCacheForPersist,
                _logger);
            // 跨会话缓存改到后台加载，构造期不阻塞 UI；预热开始前等待加载完成。
            _loadTask = Task.Run(LoadPersistedTextCache);
        }
    }

    /// <summary>Application 模型逻辑的 warning 回调：统一写日志端口。</summary>
    private void Warn(string message) => _logger.Warn(message);

    /// <summary>
    /// 按当前 MOD 数上调模型缓存容量：N&gt;512 时固定容量会每轮淘汰前面条目，
    /// 让备注列长期停在「正在识别模型…」；容量取 2×MOD 数（至少 1024），且不超过
    /// <see cref="ModelTextPrefetchService.MaxTextCacheCapacity"/>（5000）。
    /// 已用身份缓存同步上调，避免大库每次打开编辑器固定重扫容量外的 MOD。
    /// </summary>
    /// <param name="modCount">当前游戏的 MOD 数。</param>
    public void EnsureTextCacheCapacity(int modCount)
    {
        _textCache.EnsureCapacity(modCount);
        _identityCache.EnsureUsedIdsCapacity(modCount);
    }

    /// <summary>身份缓存的一次性快照（明细 + 已用身份）：后台准备读取一次物化结果，避免分别复制两份。</summary>
    public ModelIdentitySnapshot IdentitySnapshot => _identityCache.SnapshotAll();

    /// <summary>批量查询文本缓存（一次加锁）：结果写入调用方缓冲。</summary>
    /// <param name="requests">查询项（MOD ID、签名、游戏稳定 ID）。</param>
    /// <param name="results">与请求等长的结果缓冲；未命中的位置为 <see langword="null"/>。</param>
    public void TryGetCachedTexts(
        IReadOnlyList<(int ModId, string Signature, string GameId)> requests,
        Span<string?> results) =>
        _textCache.TryGetMany(requests, results);

    /// <summary>带游戏校验的文本缓存查询：条目属于其它游戏或缺少游戏标记时视为未命中。</summary>
    /// <remarks>
    /// 列表刷新（<c>ReloadMods</c> / 操作后轻量刷新）在 UI 线程执行，<b>不允许</b>在这里同步扫描 pak：
    /// 缓存未命中时 UI 只放占位文本，真正的 <c>ModelAutoClassifier.BuildModelText</c> 由
    /// <see cref="PrewarmMissing"/> 在后台算好后经 <see cref="StoreText"/> 回填。
    /// 缓存键 = 描述 + 文件清单摘要，文件/描述没变时始终命中，所以刷新不会每次触发全量扫描。
    /// </remarks>
    /// <param name="modId">目标 MOD ID。</param>
    /// <param name="textSignature">缓存签名。</param>
    /// <param name="gameId">目标 MOD 所属游戏；为空串时视为未命中。</param>
    /// <param name="text">缓存命中的文本；未命中时为空串。</param>
    /// <returns>命中时为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="textSignature"/> 为 <see langword="null"/>。</exception>
    public bool TryGetCachedText(int modId, string textSignature, string? gameId, out string text) =>
        _textCache.TryGet(modId, textSignature, gameId ?? string.Empty, out text);

    /// <summary>让某个 MOD 的模型文本/模型身份缓存失效（改写/重装后文件内容变化）。</summary>
    /// <param name="id">MOD ID。</param>
    public void Invalidate(int id)
    {
        _textCache.Remove(id);
        _identityCache.Remove(id);
        // 只失效该 MOD 的戳缓存，其它 MOD 的会话缓存不受影响。
        _stampCache.Invalidate(id);

        // 失效必须落盘，否则退出后仍会命中旧文本；写盘已改到后台合并。
        SchedulePersistedTextCacheSave();
    }

    /// <summary>
    /// 应用后台预热发现的失效项：移除旧文本与戳，等待本轮结果回填。
    /// </summary>
    /// <param name="ids">根层 pak 内容戳已变化的 MOD ID。</param>
    /// <param name="gameId">预热所属游戏；只丢弃该游戏的条目，避免切换游戏后误删新游戏缓存。</param>
    public void DropInvalidated(IReadOnlyList<int> ids, string gameId)
    {
        _textCache.DropInvalidated(ids, gameId);
        // 内容戳变化时同步失效明细/已用身份缓存，避免旧明细被重新盖上新戳。
        _identityCache.Remove(gameId, ids);
        // 只失效这批 MOD 的戳缓存；空列表不触发任何清理。
        _stampCache.Invalidate(gameId, ids);
    }

    /// <summary>按当前游戏存活 MOD 裁剪模型缓存；其它游戏的条目保留。</summary>
    /// <param name="gameId">当前游戏稳定 ID。</param>
    /// <param name="aliveIds">当前游戏仍存在的 MOD ID（调用方只构造一次）。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="aliveIds"/> 为 <see langword="null"/>。</exception>
    public void Prune(string gameId, IReadOnlySet<int> aliveIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(aliveIds);
        string normalizedGame = GameProfile.NormalizeId(gameId);
        _textCache.Prune(gameId, aliveIds);

        // 身份缓存内部有锁，可从后台读取；键含游戏 ID，切走的游戏条目保留。
        // 键含游戏 ID，切回游戏时不再全库重扫。
        _identityCache.Prune(normalizedGame, aliveIds);
        // 戳缓存按存活集合裁剪，删除的 MOD 不长期占用缓存。
        _stampCache.Prune(normalizedGame, aliveIds);
    }

    /// <summary>
    /// 启动预热（后台线程）：给未分类 MOD 补自动分类，并预计算全部列表备注文本，避免首帧 Reload 在 UI 线程扫 pak。
    /// </summary>
    /// <remarks>
    /// <b>取消语义</b>：不再抛 <see cref="OperationCanceledException"/>，而是返回已完成项的
    /// <see cref="ModelPrewarmResult.Canceled"/>=<see langword="true"/> 结果；自动分类在后台<b>已经</b>
    /// 原地写入内存记录，调用方必须把返回结果写回缓存并 <c>Save</c>，否则会出现「界面已分类、磁盘没有记录」。
    /// </remarks>
    /// <param name="mods">当前游戏 MOD 快照。</param>
    /// <param name="game">当前模型改写游戏。</param>
    /// <param name="token">取消令牌；在下一条 MOD 边界生效，已完成项保留。</param>
    /// <param name="knownSignatures">
    /// UI 首帧已算好的行签名：同一记录实例直接复用，避免启动时全库哈希两遍；
    /// 为 <see langword="null"/> 时全部重新计算。
    /// </param>
    /// <returns>预热结果；文本缓存需由调用方回 UI 线程写入。</returns>
    public ModelPrewarmResult Prewarm(
        IReadOnlyList<ModRecord> mods,
        ModelGame game,
        CancellationToken token,
        IReadOnlyDictionary<int, KnownTextSignature>? knownSignatures = null) =>
        PrewarmCore(mods, textIds: null, classificationIds: null, game, token, knownSignatures);

    /// <summary>
    /// 刷新后的增量预热（后台线程）：只处理调用方点名的缺失项，已缓存的行不重扫。
    /// </summary>
    /// <remarks>
    /// UI 线程用 <c>TryGetCachedText</c> 找出缓存未命中的 MOD 后调用这里；
    /// 只扫描这些条目，避免「每次刷新都后台全量扫」。需要补分类的 MOD 必须同时出现在
    /// <paramref name="textIds"/> 里（分类会改描述，缓存签名随之变化）。
    /// 取消语义同 <see cref="Prewarm"/>：返回已完成项并置 <see cref="ModelPrewarmResult.Canceled"/>，不抛异常。
    /// </remarks>
    /// <param name="mods">需要处理的 MOD 子集（至少包含 <paramref name="textIds"/> 与
    /// <paramref name="classificationIds"/> 中的条目）。</param>
    /// <param name="textIds">需要计算备注文本的 MOD ID。</param>
    /// <param name="classificationIds">需要补自动分类的 MOD ID。</param>
    /// <param name="game">当前模型改写游戏。</param>
    /// <param name="token">取消令牌；在下一条 MOD 边界生效，已完成项保留。</param>
    /// <returns>预热结果；文本缓存需由调用方回 UI 线程写入。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mods"/>、<paramref name="textIds"/>
    /// 或 <paramref name="classificationIds"/> 为 <see langword="null"/>。</exception>
    public ModelPrewarmResult PrewarmMissing(
        IReadOnlyList<ModRecord> mods,
        IReadOnlySet<int> textIds,
        IReadOnlySet<int> classificationIds,
        ModelGame game,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(textIds);
        ArgumentNullException.ThrowIfNull(classificationIds);
        return PrewarmCore(mods, textIds, classificationIds, game, token, knownSignatures: null);
    }

    /// <summary>预热/增量预热的公共实现：按需补分类、按需计算备注文本。</summary>
    /// <remarks>
    /// 取消检查放在每条 MOD 之前：取消后不抛异常，而是带着「已完成项」返回并置
    /// <see cref="ModelPrewarmResult.Canceled"/>，让调用方把已写入内存的自动分类落盘（C-23）。
    /// 同一 MOD 的 pak 扫描只做一次（P-08）：先收集条目，再复用于身份识别/描述与备注文本。
    /// </remarks>
    /// <param name="mods">MOD 列表（启动预热为全量，增量预热为缺失子集）。</param>
    /// <param name="textIds">需要文本的 MOD ID；<see langword="null"/> 表示全部。</param>
    /// <param name="classificationIds">需要分类的 MOD ID；<see langword="null"/> 表示按「无描述」推断。</param>
    /// <param name="game">当前模型改写游戏。</param>
    /// <param name="token">取消令牌；在下一条 MOD 边界生效，已完成项保留。</param>
    /// <param name="knownSignatures">UI 首帧已算好的行签名；为 <see langword="null"/> 时全部重算。</param>
    /// <returns>预热结果。</returns>
    private ModelPrewarmResult PrewarmCore(
        IReadOnlyList<ModRecord> mods,
        IReadOnlySet<int>? textIds,
        IReadOnlySet<int>? classificationIds,
        ModelGame game,
        CancellationToken token,
        IReadOnlyDictionary<int, KnownTextSignature>? knownSignatures)
    {
        ArgumentNullException.ThrowIfNull(mods);
        // 等待（或在失败后重试）构造期启动的后台缓存加载，避免预热与加载竞争导致整库重扫。
        EnsureTextCacheLoaded();
        bool classificationChanged = false;
        bool canceled = false;
        List<(int Id, string Signature, string Text, string PakStamp)> texts = [];
        List<(int Id, string Signature)> noModels = [];
        List<int> invalidated = [];

        foreach (ModRecord mod in mods)
        {
            if (token.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            bool classify = ModelTextPrefetchService.ShouldClassify(mod, classificationIds);
            bool named = textIds is null || textIds.Contains(mod.Id);

            // 根层 pak 内容戳校验从启动 UI 线程移到后台预热，每个 MOD 只读一次元数据。
            // 增量预热只对点名/需要分类的 MOD 校验戳，不重算全部已缓存条目。
            bool stampComputed = false;
            bool staleStamp = false;
            string pakStamp = string.Empty;
            bool hasStoredStamp = _textCache.TryGetPakStamp(mod.Id, mod.GameId ?? string.Empty, out string storedStamp);

            if (hasStoredStamp && (textIds is null || named || classify))
            {
                pakStamp = ModelTextPrefetchService.PakStamp(_paths, mod, _fileSystem, Warn);
                stampComputed = true;
                staleStamp = !string.Equals(storedStamp, pakStamp, StringComparison.Ordinal);
                if (staleStamp)
                {
                    invalidated.Add(mod.Id);
                }
            }

            // 增量预热未点名的 MOD 不拼文件清单签名；只有需要判断缓存时才查询。
            string? textSignature = null;
            bool needText = staleStamp;
            if (!needText && textIds is not null)
            {
                needText = textIds.Contains(mod.Id);
            }
            else if (!needText)
            {
                // 启动预热复用首帧行构建已算好的签名，同一记录实例不再二次哈希。
                textSignature = ModelTextPrefetchService.ReuseOrComputeSignature(mod, knownSignatures);
                needText = !TryGetCachedText(mod.Id, textSignature, mod.GameId ?? string.Empty, out _);
            }

            // P-08：分类（识别/描述）与备注文本共用同一份扫描结果，同一 MOD 的 pak TOC 只读一遍。
            IReadOnlyList<(string Path, string? PakFile)> entries = [];
            bool scanSucceeded = false;
            bool paksReadable = true;
            if (classify || needText)
            {
                try
                {
                    // 由扫描 API 显式报告 pak 读取状态；零条目但读取成功仍算成功。
                    entries = ModelAutoClassifier.CollectEntries(_paths, mod, game, _reader, Warn, out paksReadable);
                    scanSucceeded = true;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    _logger.Warn($"模型扫描失败（{mod.DisplayName}）：{ex.Message}");
                }
            }

            bool classified = false;
            bool classifySucceeded = false;
            IReadOnlyList<DetectedModel> detected = [];
            if (classify && scanSucceeded)
            {
                try
                {
                    classifySucceeded = true;
                    // P-04：分类与备注文本共用同一次模型检测，避免同一 MOD 预热时检测两遍。
                    if (ModelAutoClassifier.Apply(mod, entries, game, _names, out detected, Warn).Count > 0)
                    {
                        classificationChanged = true;
                        classified = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    classifySucceeded = false;
                    _logger.Warn($"模型自动分类失败（{mod.DisplayName}）：{ex.Message}");
                }
            }

            // 签名必须在 Apply 之后取：Apply 可能刚写入描述，缓存键要用「这一刻」的内容状态。
            // 分类确实改了描述时丢弃扫描前算出的签名并重算，避免缓存键失配导致下次刷新再扫一次。
            if (classified)
            {
                textSignature = null;
            }

            // 什么都不用写的 MOD（文本已缓存且不需要分类）直接跳过，连签名都不拼。
            // 只有「扫描成功且 Apply 成功、但没有模型」才写负缓存；扫描/分类异常都必须允许下次重试。
            // 判据在 Application 内组合，App 只传状态（空 pak/零条目仍算成功）。
            bool needNoModelEntry = ModelTextPrefetchService.ShouldRecordNoModel(
                classify,
                scanSucceeded,
                classifySucceeded,
                classified,
                paksReadable);
            if (!needText && !needNoModelEntry)
            {
                continue;
            }

            string signature = textSignature ?? ModelTextPrefetchService.TextSignature(mod);
            if (needNoModelEntry)
            {
                // 已确认没有可识别模型：本会话不再为它重复全量扫描。
                noModels.Add((mod.Id, signature));
            }

            if (!needText)
            {
                continue;
            }

            string text;
            if (!scanSucceeded)
            {
                // 扫描失败时与 BuildModelText 的兜底一致：退回用户描述，不再为同一 MOD 重扫一次。
                text = ModelTextPrefetchService.FallbackText(mod);
            }
            else
            {
                try
                {
                    // 分类成功时复用其检测结果；只算文本的增量路径仍需现场检测一次。
                    text = classifySucceeded
                        ? ModelAutoClassifier.BuildModelText(mod, entries, game, _names, detected)
                        : ModelAutoClassifier.BuildModelText(mod, entries, game, _names);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warn($"计算模型备注失败（{mod.DisplayName}）：{ex.Message}");
                    text = ModelTextPrefetchService.FallbackText(mod);
                }
            }

            texts.Add((mod.Id, signature, text, stampComputed
                ? pakStamp
                : ModelTextPrefetchService.PakStamp(_paths, mod, _fileSystem, Warn)));
        }

        return new ModelPrewarmResult(classificationChanged, canceled, texts, noModels, invalidated);
    }

    /// <summary>把启动预热算好的备注写入缓存（回 UI 线程后调用）。</summary>
    /// <param name="gameId">预热所属游戏稳定 ID。</param>
    /// <param name="entries">预热结果中的文本项（含根层 pak 内容戳）。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public void StoreText(
        string gameId,
        IReadOnlyList<(int Id, string Signature, string Text, string PakStamp)> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(entries);

        // 缓存内部按游戏隔离：预热期间切换游戏时，旧游戏结果不会覆盖新游戏缓存。
        // 本方法只写内存并请求去抖写盘，实际序列化在后台调度器执行，不阻塞 UI 线程。
        _textCache.Store(gameId, entries);
        SchedulePersistedTextCacheSave();
    }

    /// <summary>读取上次会话的文本缓存；签名不匹配的条目会在查询时自然失效。</summary>
    /// <remarks>
    /// 读取失败时抛出使加载任务进入失败态（下次预热重试），
    /// 同时由 <see cref="EnsureTextCacheLoaded"/> 记录「本会话不写盘」，避免用空内存覆盖磁盘缓存；
    /// 重试成功后复位写盘标志。
    /// </remarks>
    private void LoadPersistedTextCache()
    {
        if (_textCacheStore is null)
        {
            return;
        }

        if (!_textCacheStore.TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries))
        {
            throw new InvalidDataException("模型文本缓存读取失败（详见日志）");
        }

        // 装载前先按持久条目数扩容，避免固定 512 容量把跨会话缓存截断。
        _textCache.Load(entries, Warn);
        _persistDisabled = false;
    }

    /// <summary>确保持久缓存已加载；失败/取消后下次调用重新尝试，预热降级为空缓存。</summary>
    private void EnsureTextCacheLoaded()
    {
        if (_textCacheStore is null)
        {
            return;
        }

        Task load;
        lock (_loadGate)
        {
            if (_loadTask is { IsCompletedSuccessfully: true })
            {
                return;
            }

            if (_loadTask is null || _loadTask.IsFaulted || _loadTask.IsCanceled)
            {
                _loadTask = Task.Run(LoadPersistedTextCache);
            }

            load = _loadTask;
        }

        try
        {
            load.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 缓存是启动加速手段：加载失败降级为空缓存，下次预热会再试，绝不让模型功能整体失效。
            // 本会话同时禁用写盘，避免用不完整内存覆盖仍可能可用的磁盘缓存。
            _persistDisabled = true;
            _logger.Warn($"加载模型文本缓存失败，本次使用空缓存且不写盘：{ex.Message}");
        }
    }

    /// <summary>
    /// 写盘用的文本缓存快照：先等待（必要时重试）持久缓存加载完成，再把内存条目合并写盘。
    /// 加载失败时禁用本会话写盘，避免用不完整内存覆盖磁盘缓存。
    /// </summary>
    /// <returns>完整的内存条目快照与版本。</returns>
    private ModelTextCacheSnapshot SnapshotTextCacheForPersist()
    {
        EnsureTextCacheLoaded();
        if (_persistDisabled)
        {
            // 版本 0 与调度器初始基线一致：本次请求被视为「无变化」，不会写盘。
            return new ModelTextCacheSnapshot([], 0);
        }

        return _textCache.SnapshotWithVersion();
    }

    /// <summary>
    /// 请求把内存文本缓存写盘：调度状态机在 Application，
    /// UI 线程只发起请求，快照与序列化都在后台线程完成。
    /// </summary>
    private void SchedulePersistedTextCacheSave()
    {
        if (_persistQueue is null || _persistDisabled || !_persistQueue.Request())
        {
            return;
        }

        if (_backgroundTasks is not null)
        {
            _ = _backgroundTasks.EnqueueAsync(_persistQueue.FlushAsync);
            return;
        }

        _ = Task.Run(_persistQueue.FlushAsync);
    }

    /// <summary>是否还有排队/重试中的文本缓存写盘（关窗等待用）。</summary>
    public bool HasPendingCacheWrite => _persistQueue?.IsPending == true;

    /// <summary>等待文本缓存写盘（含失败重试）全部结束；无持久化端口时立即完成。</summary>
    /// <returns>空闲时完成的任务。</returns>
    public Task WhenCacheIdleAsync() => _persistQueue?.WhenIdleAsync() ?? Task.CompletedTask;

    /// <summary>后台准备模型编辑器数据：目标 MOD 的模型行 + 当前游戏全部 MOD 的已用模型身份。</summary>
    /// <remarks>
    /// 每个 MOD 都先取内容戳：散装分支 O(目录数)、pak 分支 O(pak 数)；
    /// 戳按记录实例做会话内缓存，同一记录再次打开时不重复全库 stat。
    /// 命中缓存也必须戳一致才复用，外部替换 pak 后不会把已占用 ID 当作空闲；散装目录增删/改名由目录戳覆盖。
    /// 扫描失败（IO/损坏/权限）时不写缓存，下次打开会重试。
    /// 缓存键含游戏 ID，在途结果回到 UI 后只会写到准备开始时的游戏命名空间。
    /// </remarks>
    /// <param name="gameId">准备任务所属游戏稳定 ID（调用方在开始时快照）。</param>
    /// <param name="target">要编辑的 MOD。</param>
    /// <param name="mods">当前游戏全部 MOD（UI 线程已快照）。</param>
    /// <param name="identity">身份缓存的一次性快照（明细 + 已用身份）。</param>
    /// <param name="game">模型改写游戏。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>后台准备结果。</returns>
    public ModelEditorPreparation BuildPreparation(
        string gameId,
        ModRecord target,
        IReadOnlyList<ModRecord> mods,
        ModelIdentitySnapshot identity,
        ModelGame game,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(identity);

        string normalizedGame = GameProfile.NormalizeId(gameId);
        HashSet<string> usedIds = new(StringComparer.OrdinalIgnoreCase);
        List<(int Id, string Signature, IReadOnlyList<ModelDetail> Details)> cacheEntries = [];
        List<(int Id, string Signature, IReadOnlyList<string> UsedIds)> usedIdEntries = [];
        List<ModRecord> expiredStamps = [];
        IReadOnlyList<ModelDetail>? targetDetails = null;

        foreach (ModRecord mod in mods)
        {
            token.ThrowIfCancellationRequested();
            bool isTarget = mod.Id == target.Id;
            (string GameId, int ModId) key = (normalizedGame, mod.Id);

            // 命中也要先取轻量戳：同一记录实例复用会话内缓存的戳（TTL 内），
            // 目标 MOD 每次打开都强制重算：过期条目先复用旧戳并登记，本轮只刷新有限条。
            string stamp;
            if (isTarget)
            {
                stamp = _stampCache.GetOrCompute(
                    normalizedGame,
                    mod,
                    () => ModelTextPrefetchService.IdentityStamp(_paths, mod, _fileSystem, Warn),
                    forceRefresh: true);
            }
            else if (_stampCache.TryGetCached(normalizedGame, mod, out string cachedStamp, out bool expired))
            {
                stamp = cachedStamp;
                if (expired)
                {
                    // 不可读条目同样纳入预算与 30 秒退避，避免每次打开编辑器都同步扫描全部无权限 MOD。
                    expiredStamps.Add(mod);
                }
            }
            else
            {
                stamp = _stampCache.GetOrCompute(
                    normalizedGame,
                    mod,
                    () => ModelTextPrefetchService.IdentityStamp(_paths, mod, _fileSystem, Warn));
            }

            // 不可读戳是一次性的，不能写明细/已用身份缓存。
            bool stampUnreadable = ModelTextPrefetchService.IsUnreadableStamp(stamp);

            if (!isTarget
                && identity.UsedIds.TryGetValue(key, out ModelUsedIdsCacheEntry? known)
                && known is not null
                && ModelIdentityCache.StampMatches(known.Stamp, stamp))
            {
                foreach (string usedId in known.UsedIds)
                {
                    usedIds.Add(usedId);
                }

                continue;
            }

            // 目标 MOD 与缓存未命中项才重扫；明细缓存同样必须与戳一致。
            IReadOnlyList<ModelDetail> details;
            bool cacheable;
            if (identity.Details.TryGetValue(key, out ModelDetailsCacheEntry? cached)
                && cached is not null
                && !stampUnreadable
                && ModelIdentityCache.StampMatches(cached.Stamp, stamp))
            {
                details = cached.Details;
                cacheable = true;
            }
            else
            {
                try
                {
                    // 条目级不可读（坏 TOC/超限/不支持 TOC）与不可读戳同策略，不缓存空明细。
                    details = ModelAutoClassifier.DetectDetails(
                        _paths, mod, game, _reader, _names, Warn, out bool entriesReadable);
                    cacheable = ModelTextPrefetchService.CanCacheModelDetails(stamp, entriesReadable);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // 单个 MOD 扫描失败不影响其它行与「已用ID」集合：失败结果不写缓存，下次重试。
                    _logger.Warn($"读取模型身份失败（{mod.DisplayName}）：{ex.Message}");
                    details = [];
                    cacheable = false;
                }

                if (cacheable)
                {
                    cacheEntries.Add((mod.Id, stamp, details));
                }
            }

            List<string> modUsedIds = new(details.Count);
            foreach (ModelDetail detail in details)
            {
                usedIds.Add(detail.RewriteId);
                modUsedIds.Add(detail.RewriteId);
            }

            if (cacheable)
            {
                usedIdEntries.Add((mod.Id, stamp, modUsedIds));
            }

            if (isTarget)
            {
                targetDetails = details;
            }
        }

        RefreshExpiredStamps(normalizedGame, expiredStamps, token);

        targetDetails ??= [];
        string? noModelsMessage = targetDetails.Count == 0
            ? ModelAutoClassifier.DescribeNoModels(_paths, target, game, _repacker, _fileSystem)
            : null;
        return new ModelEditorPreparation(
            targetDetails,
            usedIds,
            noModelsMessage,
            cacheEntries,
            usedIdEntries);
    }

    /// <summary>
    /// 在时间预算内轮换刷新过期内容戳：每轮按游戏游标轮换，最多 <see cref="StampRefreshBudgetMilliseconds"/> 毫秒，
    /// 其余 MOD 继续复用旧戳；后续打开推进游标，保证大库在多次打开内覆盖全部过期条目。
    /// </summary>
    /// <param name="gameId">规范化后的游戏 ID。</param>
    /// <param name="expired">本轮发现已过期的 MOD。</param>
    /// <param name="token">准备任务的取消令牌。</param>
    private void RefreshExpiredStamps(string gameId, List<ModRecord> expired, CancellationToken token)
    {
        if (expired.Count == 0)
        {
            return;
        }

        int cursor = _stampRefreshCursors.GetValueOrDefault(gameId);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        // 直接消费过期列表，不再为同一批 MOD 构造 ids/byId/rotated 三份集合。
        // 取消与单项异常隔离由 Application 核心处理，游标在核心内取模后写回。
        ModelStampRefresh.RefreshWithinBudget(
            expired,
            cursor,
            StampRefreshBudgetMilliseconds,
            () => watch.ElapsedMilliseconds,
            mod => _ = _stampCache.GetOrCompute(
                gameId,
                mod,
                () => ModelTextPrefetchService.IdentityStamp(_paths, mod, _fileSystem, Warn),
                forceRefresh: true),
            token,
            _logger.WarnBatch,
            out int nextCursor);

        _stampRefreshCursors[gameId] = nextCursor;
    }

    /// <summary>把后台准备结果写回缓存（只在 UI 线程调用）：按准备开始时的游戏写入明细行与「已用身份」缓存。</summary>
    /// <param name="gameId">准备任务所属游戏稳定 ID。</param>
    /// <param name="preparation">后台返回的准备结果。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public void StorePreparation(string gameId, ModelEditorPreparation preparation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(preparation);
        foreach ((int id, string stamp, IReadOnlyList<ModelDetail> details) in preparation.CacheEntries)
        {
            _identityCache.StoreDetails(gameId, id, stamp, details);
        }

        foreach ((int id, string stamp, IReadOnlyList<string> usedIds) in preparation.UsedIdEntries)
        {
            _identityCache.StoreUsedIds(gameId, id, stamp, usedIds);
        }
    }
}
