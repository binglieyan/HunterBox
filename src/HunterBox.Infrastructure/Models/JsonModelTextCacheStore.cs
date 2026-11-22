using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HunterBox.Application.Common;
using HunterBox.Application.Models;
using HunterBox.Infrastructure.IO;

namespace HunterBox.Infrastructure.Models;

/// <summary>把模型备注文本缓存写到HunterBox目录的 JSON 文件。</summary>
/// <remarks>
/// <para>
/// 基础文件带版本号与世代号；写入先写同目录临时文件再原子替换，避免中断后留下半截 JSON。
/// 损坏/超限文件会改名成 <c>.bad-&lt;时间戳&gt;</c> 保留后重新生成；只有改名也失败时才禁用本会话写盘。
/// </para>
/// <para>
/// 装载后再次保存只把变化条目追加到 <c>.journal</c>（JSON Lines，
/// 每行最多 100 条变化且不超过 256KB），单 MOD 变化的写盘量为 O(该条)；本次保存的变更总量估算字节达到压缩阈值
/// max(1MB, 基础文件/4)（或加上已有日志会跨过阈值）时直接整份写，避免同批数据先写日志再立即压缩；
/// 小体积的大量删除继续走日志，不为约 200KB 的变化重写 2–21MB。
/// 基础文件每次整份重写都会生成新的世代号，日志行引用写入时的世代号；基础文件被外部重写后旧日志自动失效；
/// 混世代/混版本日志跳过对应行继续回放，不会用旧行阻断当前世代变化。
/// 日志损坏/末尾半截时保留已回放部分：先写基础文件，成功后才隔离日志；写基础失败则把合法前缀重写成干净日志；
/// 两级都失败时保留原日志不隔离，下次启动会重新回放合法前缀，删除项不会复活。
/// 基础文件整份写成功后清理旧日志分三级：删除 → 改名隔离 → 截断为 0；三者都失败时记入清理待办，
/// 按指数退避（30 秒起、上限 5 分钟）重试，每次进入待办只告警一次。
/// 追加失败只是本次保存的瞬时结果：下一次保存仍会重新尝试追加，锁释放后立即恢复增量写。
/// 清理待办期间若追加成功，日志中的当前世代行只存在于日志；此时保存开始时的清理会被跳过，
/// 必须等下一次整份写把快照下沉后才能删除日志，否则重启会丢失已返回成功的变更。
/// 除本次会话第一次整份写外，真实整份写按单调时钟节流（至少 1 秒且不超过 8MB/s，
/// 单次 Save 累计等待默认封顶 10 秒、可注入）；实际等待在 <c>_gate</c> 之外进行；
/// 仅追加不等待，零变化但日志达到压缩阈值仍会等待整份写，不把「延后」伪装成失败。
/// 加载基础文件与回放日志都统一经 <see cref="ModelTextPrefetchService.NormalizeEntry"/> 归一。
/// </para>
/// </remarks>
public sealed class JsonModelTextCacheStore : IModelTextCacheStore
{
    /// <summary>当前缓存文件格式版本（加入世代号后升版，旧缓存自然作废）。</summary>
    internal const int CurrentVersion = 3;

    /// <summary>
    /// 缓存文件大小上限（128MB）：超过视为损坏/异常文件，改名保留后重新生成。
    /// </summary>
    /// <remarks>
    /// 单条文本最坏约 12KB（控制字符转义）、内容戳最坏 3KB（512 字符控制字符）加字段开销，
    /// 5000 条约 77MB，因此 128MB 只会命中真正的损坏/异常文件。
    /// </remarks>
    internal const int MaxFileBytes = 128 * 1024 * 1024;

    /// <summary>增量日志大小上限：超过后视为损坏/异常日志并回退到基础文件。</summary>
    internal const int MaxJournalBytes = 32 * 1024 * 1024;

    /// <summary>
    /// 增量日志压缩的字节阈值：日志达到 max(该值, 基础文件/4) 时压缩。
    /// 本次保存的变更总量估算字节达到同一阈值时直接整份写，避免先写日志再立即压缩。
    /// </summary>
    internal const int MinJournalBytesForCompaction = 1024 * 1024;

    /// <summary>增量日志每条记录最多包含的变化条数，避免单行多 MB 造成回放峰值。</summary>
    internal const int JournalChunkSize = 100;

    /// <summary>单条变化的 JSON 包装/字段开销估算（分块上界与实际字节估算共用）。</summary>
    private const int ChangeOverheadBytes = 128;

    /// <summary>压缩失败后的初始退避时间。</summary>
    private static readonly TimeSpan InitialCompactionBackoff = TimeSpan.FromSeconds(30);

    /// <summary>压缩失败退避的上限。</summary>
    private static readonly TimeSpan MaxCompactionBackoff = TimeSpan.FromMinutes(5);

    private readonly string _path;
    private readonly string _journalPath;
    private readonly IAppLogger _logger;
    private readonly SystemFileSystem _fileSystem = SystemFileSystem.Instance;
    private readonly IClock _clock = SystemClock.Instance;

    /// <summary>存储操作串行化：调度器与关窗等待可能并发调用 Save。</summary>
    private readonly object _gate = new();

    /// <summary>当前基础文件已达到 <see cref="CurrentVersion"/> 且带世代号，可以安全追加增量日志。</summary>
    private bool _baseReady;

    /// <summary>基础文件字节数（压缩阈值判断）。</summary>
    private long _baseBytes;

    /// <summary>基础文件世代号：日志行只在世代号一致时回放。</summary>
    private string _generation = string.Empty;

    /// <summary>上次持久化后的完整快照；为 <see langword="null"/> 时必须整份重写。</summary>
    private Dictionary<(string GameId, int ModId), ModelTextCacheEntry>? _snapshot;

    /// <summary>基础文件不可用时的原因（用于统一日志与隔离）。</summary>
    private string _lastQuarantineReason = string.Empty;

    /// <summary>
    /// 基础文件已包含完整快照，但旧日志尚未删除/改名/截断：清理待办，按退避重试。
    /// 与追加失败区分：追加失败只表示本次保存瞬时不可写，下一次保存仍会重新尝试追加。
    /// </summary>
    private bool _journalCleanupPending;

    /// <summary>
    /// 日志中存在当前世代的已追加行：这些行只存在于日志、尚未下沉到基础文件，
    /// 清理前必须先整份写，否则重启会丢失。
    /// 每次成功追加日志都会置位（只有清理待办期间才会被读取）；装载回放不置位，
    /// 整份写成功与日志改名隔离会复位。清理成功依赖调用方保证进入时已为 false。
    /// </summary>
    private bool _journalHasCurrentGenerationRows;

    /// <summary>清理退避截止时间与长度；进入清理待办时只告警一次，清理成功后重置。</summary>
    private DateTimeOffset _journalCleanupBlockedUntil;
    private TimeSpan _journalCleanupBackoff = InitialCompactionBackoff;

    /// <summary>当前清理待办周期是否已告警，避免每个保存都重复刷日志。</summary>
    private bool _journalCleanupWarned;

    /// <summary>整份写的最小间隔：最坏频率 1 次/秒。</summary>
    private static readonly TimeSpan MinimumBaseWriteInterval = TimeSpan.FromSeconds(1);

    /// <summary>整份写的字节速率上限（8MB/s），所有整份写共用，避免大缓存只按频率节流。</summary>
    private const long MaxBaseWriteBytesPerSecond = 8 * 1024 * 1024;

    /// <summary>
    /// 单次 <see cref="Save"/> 的整份写等待总上限默认值（10 秒），与 App 的启动保存/单次缓存空闲等待一致
    /// （`PendingSaveWaitTimeout`、`StartupSaveWaitTimeout`）；关窗总预算是 60 秒墙钟上限，
    /// 单次保存不会独自耗尽它。超过上限后直接写入，128MB 极端缓存单次可能略超 8MB/s，
    /// 但优先保证关窗及时。
    /// </summary>
    internal static readonly TimeSpan MaxBaseWriteWait = TimeSpan.FromSeconds(10);

    /// <summary>本实例的单次 Save 等待预算；默认 <see cref="MaxBaseWriteWait"/>，测试可注入更小值。</summary>
    private readonly TimeSpan _maxBaseWriteWait;

    /// <summary>单调毫秒时间源与等待委托；测试可注入，避免真实休眠并覆盖边界。</summary>
    /// <remarks>
    /// 默认 <see cref="Thread.Sleep(TimeSpan)"/> 的粒度误差最多约 15.6ms，对秒级等待约 1–2%，
    /// 短等待的额外延迟可接受。
    /// </remarks>
    private readonly Func<long> _tickSource;
    private readonly Action<TimeSpan> _delay;

    /// <summary>
    /// 上次整份写尝试的单调时间戳；成功与失败都会更新，保证连续失败也不会超过写盘速率上限。
    /// </summary>
    private long _lastBaseWriteAttemptTick;

    /// <summary>测试用换入失败注入；为 <see langword="null"/> 时执行真实换入。</summary>
    private readonly Func<bool, bool>? _moveBaseFailureInjection;

    /// <summary>
    /// 本次会话是否已经真正尝试过整份写；第一次写完全免等待，避免装载最大缓存后被误判为「刚写过」。
    /// </summary>
    private bool _hasWrittenBase;

    /// <summary>基础文件整份写尝试次数（含失败），仅供回归测试断言写盘次数。</summary>
    internal int BaseWriteAttempts { get; private set; }

    /// <summary>增量日志追加尝试次数（含失败），仅供回归测试断言瞬时失败后的重试。</summary>
    internal int JournalAppendAttempts { get; private set; }

    /// <summary>日志目录已确认存在：避免每次增量保存都调用 <c>CreateDirectory</c>。</summary>
    private bool _journalDirectoryReady;

    /// <summary>已跟踪的增量日志字节数：追加/重写/删除后维护，避免每次保存重复读取文件元数据。</summary>
    private long _journalBytes;

    /// <summary>压缩失败后的退避截止时间与当前退避长度。</summary>
    private DateTimeOffset _compactionBlockedUntil;
    private TimeSpan _compactionBackoff = InitialCompactionBackoff;

    /// <summary>基础文件根对象的固定 JSON 包装开销估算。</summary>
    private const int CacheFileOverheadBytes = 64;

    /// <summary>
    /// 保存差异的复用缓冲；无变化时复用空缓冲。等待计划接管后换用同容量新缓冲，
    /// 避免复制整份变更列表与反复扩容。
    /// </summary>
    private List<JournalChange> _changesScratch = [];
    private readonly HashSet<(string GameId, int ModId)> _seenKeysScratch = [];

    /// <summary>
    /// 快照版本：<see cref="ApplyChanges"/>、成功的整份写与装载都会递增。
    /// 等待整份写后版本未变且 entries 引用一致时复用差异，避免重试循环重复全量 diff。
    /// </summary>
    private int _stateVersion;

    /// <summary>全量差异计算次数，仅供回归测试断言等待后复用计划。</summary>
    internal int ChangeDiffCount { get; private set; }

    /// <summary>最近一次全量差异 + 字节估算的耗时（毫秒），供性能回归测试读取。</summary>
    internal double LastChangeDiffMilliseconds { get; private set; }

    /// <summary>序列化选项：宽松编码器让中文按 UTF-8 原样写入，避免 \uXXXX 转义把缓存体积放大近一倍。</summary>
    /// <remarks>
    /// 取舍：宽松编码器对 <c>&lt;&gt;&amp;'</c> 与 U+2028/U+2029 原样写入。缓存只在本机读取、
    /// 绝不嵌入 HTML/JS；若未来引入此类用法，必须改用默认编码器重新评估。
    /// </remarks>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>装配缓存文件存储。</summary>
    /// <param name="path">缓存文件绝对路径。</param>
    /// <param name="logger">日志端口。</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> 为 <see langword="null"/>。</exception>
    public JsonModelTextCacheStore(string path, IAppLogger logger)
        : this(path, logger, clock: null)
    {
    }

    /// <summary>测试用装配：允许注入可推进时钟、单调时间源、等待委托与更小的等待预算。</summary>
    /// <param name="path">缓存文件绝对路径。</param>
    /// <param name="logger">日志端口。</param>
    /// <param name="clock">时钟端口；为 <see langword="null"/> 时使用系统时钟。</param>
    /// <param name="tickSource">单调毫秒时间源；为 <see langword="null"/> 时使用 <see cref="Environment.TickCount64"/>。</param>
    /// <param name="delay">等待委托；为 <see langword="null"/> 时使用 <see cref="Thread.Sleep(TimeSpan)"/>。</param>
    /// <param name="maxBaseWriteWait">单次 Save 的等待预算；为 <see langword="null"/> 时使用 <see cref="MaxBaseWriteWait"/>。</param>
    /// <param name="moveBaseFailureInjection">
    /// 测试用换入失败注入：返回 <see langword="false"/> 时本次换入按 <see cref="IOException"/> 处理，
    /// 用于覆盖「探测后目标被外部创建/删除」的重试分支；为 <see langword="null"/> 时执行真实换入。
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBaseWriteWait"/> 不为正数。</exception>
    internal JsonModelTextCacheStore(
        string path,
        IAppLogger logger,
        IClock? clock,
        Func<long>? tickSource = null,
        Action<TimeSpan>? delay = null,
        TimeSpan? maxBaseWriteWait = null,
        Func<bool, bool>? moveBaseFailureInjection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        _path = Path.GetFullPath(path);
        _journalPath = _path + ".journal";
        _logger = logger;
        _clock = clock ?? SystemClock.Instance;
        _tickSource = tickSource ?? (static () => Environment.TickCount64);
        _delay = delay ?? Thread.Sleep;
        _maxBaseWriteWait = maxBaseWriteWait ?? MaxBaseWriteWait;
        _moveBaseFailureInjection = moveBaseFailureInjection;
        if (_maxBaseWriteWait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBaseWriteWait), "整份写等待预算必须为正数");
        }

        // 首次整份写由 _hasWrittenBase 单独放行；该字段在第一次写尝试时才赋值。
    }

    /// <inheritdoc />
    public bool TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries)
    {
        lock (_gate)
        {
            entries = [];
            // 启动时合并清理基础文件与增量日志的 .bad-*，同一目录只枚举一次。
            CorruptFileQuarantine.PruneMany([_path, _journalPath], _fileSystem, _logger.Warn);

            bool baseOk = TryLoadBase(
                out Dictionary<(string GameId, int ModId), ModelTextCacheEntry> state,
                out bool currentVersion,
                out string? generation);
            if (!baseOk)
            {
                // 基础文件损坏：日志中的增量已失去上下文，一并隔离，允许本次重新生成。
                TryMoveJournalAside();
                _snapshot = null;
                _baseReady = false;
                return QuarantineOrFalse(_lastQuarantineReason);
            }

            bool journalCompromised = false;
            if (currentVersion && generation is not null)
            {
                _generation = generation;
                // 正常回放不收集副本；只有确认日志受损且需要重写时才再读一遍收集合法前缀。
                journalCompromised = TryApplyJournal(state, generation, applied: null);
            }
            else
            {
                // 没有有效基础文件时增量日志无法解释，隔离后整份重写。
                TryMoveJournalAside();
            }

            _snapshot = state;
            _baseReady = currentVersion && generation is not null;
            if (journalCompromised)
            {
                // 先写基础，成功后才隔离日志；写基础失败则把合法前缀重写成干净日志，
                // 保证重启后仍能重放删除项，而不是只停留在内存。
                // 启动装载是单线程路径，跳过节流检查，尽快恢复一致状态。
                // 这是装载路径唯一的同步整份写，只在日志损坏时发生一次。
                // 后台化会让并发保存追加到待隔离日志（会丢数据），因此保留同步修复。
                if (WriteBase([.. state.Values], allowEarly: true) == BaseWriteResult.Success)
                {
                    TryMoveJournalAside();
                }
                else
                {
                    // 异常路径才第二次回放并收集合法前缀（回放是幂等的）。
                    List<JournalChange> appliedChanges = [];
                    _ = TryApplyJournal(state, generation!, appliedChanges);
                    if (appliedChanges.Count == 0)
                    {
                        // 没有可重放的合法变化：隔离损坏日志即可。
                        TryMoveJournalAside();
                    }
                    else if (!RewriteJournal(appliedChanges))
                    {
                        // 两级都失败时绝不隔离原日志。日志里仍有已回放的删除/更新，
                        // 保留它可在下次启动重新回放；隔离后这些变化只存在于内存，重启会让删除项复活。
                        _baseReady = false;
                        _logger.Warn($"保留模型文本缓存增量日志等待下次启动重放：{_journalPath}");
                    }
                }
            }

            entries = [.. state.Values];
            // 装载建立了新快照，之前缓存的差异（理论上不存在）一律作废。
            _stateVersion++;
            return true;
        }
    }

    /// <summary>把已回放的合法前缀重写成干净日志（覆盖旧日志），失败返回 <see langword="false"/>。</summary>
    /// <param name="changes">已应用的合法变化（调用方保证至少一条）。</param>
    /// <returns>重写成功时返回 <see langword="true"/>。</returns>
    private bool RewriteJournal(IReadOnlyList<JournalChange> changes)
    {
        string temp = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            string? directory = Path.GetDirectoryName(_journalPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 重写与追加共用分块序列化；总字节由批次结果直接返回，不再二次求和。
            (List<byte[]> lines, _, long total) = BuildJournalLines(changes, long.MaxValue);
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WriteJournalLines(stream, lines);
            }

            File.Move(temp, _journalPath, overwrite: true);
            _journalDirectoryReady = true;
            _journalBytes = total;
            // 装载阶段清理待办必为 false，不在这里维护当前世代行标记；
            // 只有清理待办期间的追加（TryAppendJournal）需要置位。
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"重写模型文本缓存增量日志失败：{ex.Message}");
            return false;
        }
        finally
        {
            _fileSystem.TryDeleteFile(temp);
        }
    }

    /// <summary>读取基础文件；文件不存在或版本不受支持时返回空状态。</summary>
    /// <param name="state">装载结果。</param>
    /// <param name="currentVersion">基础文件为当前版本且带世代号时返回 <see langword="true"/>。</param>
    /// <param name="generation">基础文件世代号；非当前版本时为 <see langword="null"/>。</param>
    /// <returns>基础文件可解析（包括「不存在」）时返回 <see langword="true"/>。</returns>
    private bool TryLoadBase(
        out Dictionary<(string GameId, int ModId), ModelTextCacheEntry> state,
        out bool currentVersion,
        out string? generation)
    {
        state = [];
        currentVersion = false;
        generation = null;
        _baseBytes = 0;
        _lastQuarantineReason = string.Empty;
        try
        {
            using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // 长度只读取一次，同时用于上限判断与 _baseBytes。
            long length = stream.Length;
            if (length > MaxFileBytes)
            {
                stream.Dispose();
                _lastQuarantineReason = $"文件过大（{length} 字节，上限 {MaxFileBytes}）";
                return false;
            }

            byte[] head = new byte[64];
            int read = stream.Read(head, 0, head.Length);
            int contentStart = read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? 3 : 0;
            // 头部版本只解析一次；旧版本直接作废，缺少版本头才解析确认是否损坏。
            bool hasDeclaredVersion = TryReadDeclaredVersion(head.AsSpan(0, read), out int declared);
            if (!hasDeclaredVersion || declared != CurrentVersion)
            {
                if (hasDeclaredVersion)
                {
                    // 旧版本只读头部版本号即作废，不再把整份 21MB 文件解析成 JsonDocument。
                    _logger.Warn($"模型文本缓存版本不是 {CurrentVersion}（{declared}），本次启动将重新扫描");
                    return true;
                }

                // 没有可识别的版本头：解析确认是否损坏（完全损坏按损坏处理）。
                stream.Position = contentStart;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(stream);
                }
                catch (JsonException ex)
                {
                    stream.Dispose();
                    _lastQuarantineReason = $"内容无法解析：{ex.Message}";
                    return false;
                }

                _logger.Warn($"模型文本缓存缺少版本头，本次启动将重新扫描");
                return true;
            }

            stream.Position = contentStart;
            CacheFile? file = JsonSerializer.Deserialize<CacheFile>(stream);
            if (file is null
                || file.Version != CurrentVersion
                || string.IsNullOrWhiteSpace(file.Generation))
            {
                string version = file is null ? "空" : file.Version.ToString(CultureInfo.InvariantCulture);
                _logger.Warn($"模型文本缓存版本/世代不受支持（{version}），本次启动将重新扫描");
                return true;
            }

            foreach (ModelTextCacheEntry? entry in file.Entries ?? [])
            {
                // 损坏文件可能把字符串字段写成 null 或混入 null 元素，加载侧统一归一后再入表。
                if (entry is null)
                {
                    continue;
                }

                ModelTextCacheEntry safe = ModelTextPrefetchService.NormalizeEntry(entry);
                state[(safe.GameId, safe.ModId)] = safe;
            }

            _baseBytes = length;
            currentVersion = true;
            generation = file.Generation;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 没有缓存是正常状态：允许随后整份写入。
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _lastQuarantineReason = ex.Message;
            return false;
        }
    }

    /// <summary>把增量日志应用到基础状态；日志损坏或世代不匹配时保留已应用部分。</summary>
    /// <param name="state">基础状态（原地修改）。</param>
    /// <param name="generation">基础文件世代号。</param>
    /// <param name="applied">
    /// 已成功应用的合法变化；为 <see langword="null"/> 时不收集（正常回放零副本），
    /// 仅日志受损且需要重写时由调用方传入列表并再回放一次。
    /// </param>
    /// <returns>日志不完整/需要清理时返回 <see langword="true"/>。</returns>
    private bool TryApplyJournal(
        Dictionary<(string GameId, int ModId), ModelTextCacheEntry> state,
        string generation,
        List<JournalChange>? applied)
    {
        // 用同一个 FileInfo 读取存在性与长度，并纳入统一探测计数。
        FileInfo journalFile = ProbeFileInfo(_journalPath);
        if (!journalFile.Exists)
        {
            _journalBytes = 0;
            return false;
        }

        try
        {
            long length = journalFile.Length;
            _journalBytes = length;
            if (length > MaxJournalBytes)
            {
                _logger.Warn($"模型文本缓存增量日志过大（{length} 字节），已忽略：{_journalPath}");
                return true;
            }

            bool compromised = false;
            int versionMismatchLines = 0;
            int oldGenerationLines = 0;
            foreach (string line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JournalRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<JournalRecord>(line, SerializerOptions);
                }
                catch (JsonException)
                {
                    compromised = true;
                    break;
                }

                if (record is null)
                {
                    compromised = true;
                    continue;
                }

                if (record.Version != CurrentVersion)
                {
                    // 版本不一致只跳过该行，不能让旧/新版本行阻断后续当前版本变化。
                    // 按行汇总，回放结束后只告警一次。
                    versionMismatchLines++;
                    compromised = true;
                    continue;
                }

                if (!string.Equals(record.Generation, generation, StringComparison.Ordinal))
                {
                    // 混世代时跳过旧行继续回放，绝不能让旧行阻断后续当前世代变化。
                    oldGenerationLines++;
                    compromised = true;
                    continue;
                }

                foreach (JournalChange? change in record.Changes ?? [])
                {
                    // 损坏日志可能混入 null 变化或 null 字符串字段，统一跳过/归一。
                    if (change is null)
                    {
                        compromised = true;
                        continue;
                    }

                    string gameId = change.GameId ?? string.Empty;
                    (string GameId, int ModId) key = (gameId, change.ModId);
                    // 这里不维护 _journalHasCurrentGenerationRows——装载阶段清理待办必为 false，
                    // 该标记只在清理待办期间由 TryAppendJournal 置位、由整份写/清理复位。
                    if (change.Deleted || change.Entry is null)
                    {
                        state.Remove(key);
                        applied?.Add(change with { GameId = gameId });
                    }
                    else
                    {
                        ModelTextCacheEntry entry = ModelTextPrefetchService.NormalizeEntry(change.Entry);
                        state[key] = entry;
                        applied?.Add(change with { Entry = entry, GameId = gameId });
                    }
                }
            }

            if (compromised)
            {
                // 混版本/旧世代/损坏行只汇总一条告警，避免逐行刷日志。
                List<string> reasons = [];
                if (versionMismatchLines > 0)
                {
                    reasons.Add($"{versionMismatchLines} 行版本不匹配");
                }

                if (oldGenerationLines > 0)
                {
                    reasons.Add($"{oldGenerationLines} 行旧世代");
                }

                if (reasons.Count == 0)
                {
                    reasons.Add("存在损坏行");
                }

                _logger.Warn(
                    $"模型文本缓存增量日志不完整/过期（{string.Join("、", reasons)}），将保留合法前缀并重新生成：{_journalPath}");
            }

            return compromised;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"读取模型文本缓存增量日志失败，已忽略：{ex.Message}");
            return true;
        }
    }

    /// <summary>把不可用缓存改名保留后允许重新写盘；改名失败时返回 <see langword="false"/>（本会话禁写）。</summary>
    /// <param name="reason">不可用原因（写入日志）。</param>
    /// <returns>可以重新生成缓存时返回 <see langword="true"/>。</returns>
    private bool QuarantineOrFalse(string reason)
    {
        // 只服务这里的改名与修剪直接复用共享实现，不再保留纯转发层。
        string? badPath = TryMoveAsideCore(_path, "损坏的模型文本缓存");
        if (badPath is null)
        {
            _logger.Warn($"模型文本缓存不可用（{reason}），且无法改名保留，本会话将不写盘：{_path}");
            return false;
        }

        _logger.Warn($"模型文本缓存不可用（{reason}），已保留为 {Path.GetFileName(badPath)}，本次重新生成");
        return true;
    }

    /// <summary>
    /// 改名隔离文件并修剪旧副本；改名失败只记录一条告警并返回 <see langword="null"/>
    /// （基础文件与增量日志共用同一份改名与修剪逻辑）。
    /// </summary>
    /// <param name="path">要隔离的文件路径。</param>
    /// <param name="description">告警中的文件描述（如「损坏的模型文本缓存」）。</param>
    /// <returns>成功时返回保留后的路径；失败时返回 <see langword="null"/>。</returns>
    private string? TryMoveAsideCore(string path, string description)
    {
        string? badPath = CorruptFileQuarantine.TryMoveAside(path, _fileSystem, out Exception? error);
        if (badPath is null)
        {
            _logger.Warn($"保留{description}失败（可能被占用）：{error?.Message}");
            return null;
        }

        // 刚隔离的那份一定保留，其余按文件名时间戳只留最新几份。
        CorruptFileQuarantine.Prune(path, _fileSystem, badPath, _logger.Warn);
        return badPath;
    }

    /// <summary>
    /// 把增量日志改名隔离；<see cref="TryLoad"/> 在基础文件损坏或缺少世代时会直接调用，
    /// 因此「日志不存在」是正常路径而不是仅竞态可达。
    /// </summary>
    /// <returns>日志已不在原路径时返回 <see langword="true"/>；改名被拒时返回 <see langword="false"/>。</returns>
    private bool TryMoveJournalAside()
    {
        if (!ProbeFileExists(_journalPath))
        {
            _journalBytes = 0;
            _journalHasCurrentGenerationRows = false;
            return true;
        }

        // 日志不是「模型文本缓存」本体，告警必须指明增量日志，避免被误读成基础文件失败。
        if (TryMoveAsideCore(_journalPath, "模型文本缓存增量日志") is null)
        {
            return false;
        }

        _journalBytes = 0;
        _journalHasCurrentGenerationRows = false;
        return true;
    }

    /// <summary>
    /// 保存完整快照：日志追加成功或数据已在日志中即返回 <see langword="true"/>；
    /// 只有追加与整份写都失败、未成功装载基础文件时整份写失败，或日志接近硬上限且重写失败时才返回
    /// <see langword="false"/>。
    /// 除本次会话第一次整份写外，只有真实整份写才按单调时钟节流
    /// （至少 1 秒且不超过 8MB/s，单次 Save 累计等待默认封顶 10 秒、可注入）；
    /// 首次整份写免等待；零变化但日志达到压缩阈值同样需要整份写，因此也会等待；仅追加日志的保存不等待。
    /// 等待在锁外进行，等待后复用或重新计算差异。
    /// 端口是同步的，不接收取消令牌；单次 Save 累计等待封顶默认 10 秒，
    /// 与 App 的启动保存/单次缓存空闲等待一致，而关窗总预算是 60 秒墙钟上限，因此单次保存不会独自耗尽它；
    /// 到达上限后强制写入。调用方的等待超时只是不再等待结果，整份写的临时文件 + 原子替换与日志重放仍保证磁盘一致。
    /// </summary>
    /// <param name="entries">完整快照。</param>
    /// <returns>数据已落盘或仍在日志中时为 <see langword="true"/>。</returns>
    public bool Save(IReadOnlyList<ModelTextCacheEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        long budgetMilliseconds = (long)_maxBaseWriteWait.TotalMilliseconds;
        long waitedMilliseconds = 0;
        long estimatedWriteBytes = -1;
        SavePlan? plan = null;
        while (true)
        {
            long baseBytes;
            long lastAttemptTick;
            long nowTick;
            lock (_gate)
            {
                // 单次 Save 的累计等待达到预算后强制写入，避免多个节流窗口叠加。
                bool forceBaseWrite = waitedMilliseconds >= budgetMilliseconds;
                SaveAttemptOutcome outcome = SaveAttempt(entries, forceBaseWrite, plan, out SavePlan? nextPlan);
                plan = nextPlan;
                if (outcome != SaveAttemptOutcome.NeedsWait)
                {
                    return outcome == SaveAttemptOutcome.CompletedSuccess;
                }

                // WriteBase 已确认未到间隔；锁内只读取节流基准，
                // 整份快照估算移到锁外；变化量估算与 diff 仍在锁内，并随计划缓存。
                // 差异比较需在锁内读取一致快照，成本随条目数增长；
                // 等待路径已通过版本化计划避免重复 diff 与重复估算，剩余首轮 diff 不在本次拆分范围内。
                baseBytes = _baseBytes;
                lastAttemptTick = _lastBaseWriteAttemptTick;
                nowTick = _tickSource();
            }

            // 按「基础文件与本次预计写入字节的较大值」折算，增长写也不会超过 8MB/s。
            estimatedWriteBytes = estimatedWriteBytes < 0
                ? EstimateSnapshotBytes(entries)
                : estimatedWriteBytes;
            long writeBytes = Math.Max(baseBytes, estimatedWriteBytes);
            long waitMilliseconds = ComputeBaseWriteWaitMilliseconds(
                baseBytes,
                lastAttemptTick,
                nowTick,
                estimatedWriteBytes,
                waitedMilliseconds,
                budgetMilliseconds);

            // 没有剩余等待时直接重试，不记录日志也不调用 Sleep(0)。
            if (waitMilliseconds <= 0)
            {
                continue;
            }

            // 等待可观测，关窗时最多等一个累计预算窗口。
            _logger.Info(
                $"模型文本缓存整份写限速：等待 {waitMilliseconds} 毫秒（本次预计 {writeBytes} 字节，上限 {MaxBaseWriteBytesPerSecond / (1024 * 1024)}MB/s）");
            // Thread.Sleep 的粒度误差最多约 15.6ms；等待通常为秒级，短等待的额外延迟可接受。
            // 注入委托的用例推进单调时钟，不做真实休眠。
            _delay(TimeSpan.FromMilliseconds(waitMilliseconds));
            waitedMilliseconds += waitMilliseconds;
        }
    }

    /// <summary>
    /// 在锁内执行一次保存判定：完成日志追加或返回「需要整份写」让调用方在锁外等待后重试。
    /// </summary>
    /// <param name="entries">完整快照。</param>
    /// <param name="forceBaseWrite">
    /// 为 <see langword="true"/> 时本次 Save 的累计等待已达预算，整份写跳过剩余节流。
    /// </param>
    /// <param name="planned">本 Save 调用上一次等待前缓存的计划；版本或 entries 引用不一致时作废。</param>
    /// <param name="nextPlan">需要等待时输出本次计划；只供同一个 Save 调用的重试复用，与其它 Save 调用无关。</param>
    /// <returns>已结束时返回保存结果；需要等待后整份写时返回 <see cref="SaveAttemptOutcome.NeedsWait"/>。</returns>
    private SaveAttemptOutcome SaveAttempt(
        IReadOnlyList<ModelTextCacheEntry> entries,
        bool forceBaseWrite,
        SavePlan? planned,
        out SavePlan? nextPlan)
    {
        nextPlan = null;

        // 不再在保存顶部盲删日志（可能包含刚追加的当前世代行）；只按退避尝试恢复。
        TryRecoverJournalIfDue();

        // 计划只属于同一次 Save 调用，并绑定快照版本与 entries 引用；不匹配就重新计算。
        SavePlan? validPlan = planned is { } previous
            && previous.StateVersion == _stateVersion
            && ReferenceEquals(previous.Entries, entries)
            ? previous
            : null;

        if (!_baseReady || _snapshot is null)
        {
            SaveAttemptOutcome outcome = WriteBaseOrWait(entries, blockCompactionOnFailure: false, forceBaseWrite);
            nextPlan = BuildPlan(outcome, entries, changes: null, changeBytes: 0);
            return outcome;
        }

        List<JournalChange> changes;
        long changeBytes;
        if (validPlan is { } previousPlan)
        {
            changes = previousPlan.Changes;
            changeBytes = previousPlan.ChangeBytes;
        }
        else
        {
            // 差异与估算同一趟返回，不依赖字段的隐式调用顺序。
            changes = CollectChanges(entries, out changeBytes);
        }

        if (changes.Count == 0)
        {
            // 零变化也要处理积压的日志（超阈值时压缩，删除失败时重试）；
            // 未超阈值或处于压缩退避时直接结束。
            if (!JournalExceedsCompactionThreshold())
            {
                return SaveAttemptOutcome.CompletedSuccess;
            }

            SaveAttemptOutcome outcome = TryCompaction(entries, forceBaseWrite);
            nextPlan = BuildPlan(outcome, entries, changes: null, changeBytes: 0);
            return outcome;
        }

        if (ShouldWriteBaseDirectly(changeBytes))
        {
            // 本批加上已有日志会跨过压缩阈值时直接整份写，避免「先写日志、同一保存内再压缩」写两遍；
            // 小体积的大量删除仍走日志，不为约 200KB 的变化重写 2–21MB。
            BaseWriteResult result = WriteBase(entries, forceBaseWrite);
            if (result == BaseWriteResult.Deferred)
            {
                nextPlan = BuildPlan(SaveAttemptOutcome.NeedsWait, entries, changes, changeBytes);
                return SaveAttemptOutcome.NeedsWait;
            }

            if (result == BaseWriteResult.Success)
            {
                return SaveAttemptOutcome.CompletedSuccess;
            }

            // 整份写失败：退回日志保存本次变化；数据已在日志中即可继续使用。
            // 若失败发生在序列化之后（Replace/IO），回退日志必须重新序列化这批变化；
            // 跳过回退会丢失本次保存，因此接受这一次额外序列化。CanWriteBaseDestination 已在序列化前
            // 拦截常见的独占占用；但 FileShare.Read 共享句柄或杀毒/索引器临时锁定仍可能让替换失败，
            // 这类罕见情况下会付出一次重复序列化。
            if (!TryAppendJournal(changes, suppressIoWarning: _journalCleanupPending))
            {
                BlockCompaction();
                return SaveAttemptOutcome.CompletedFailure;
            }

            ApplyChanges(changes);
            BlockCompaction();
            // 日志仍保存着变化；只有接近硬上限且重写失败才要求调用方继续重试。
            return JournalExceedsHardLimit()
                ? SaveAttemptOutcome.CompletedFailure
                : SaveAttemptOutcome.CompletedSuccess;
        }

        // 追加失败只是本次保存的瞬时结果，下一次保存仍会重新尝试追加。
        if (TryAppendJournal(changes, suppressIoWarning: _journalCleanupPending))
        {
            ApplyChanges(changes);
            if (!JournalExceedsCompactionThreshold())
            {
                return SaveAttemptOutcome.CompletedSuccess;
            }

            // 变化已写入日志；压缩失败只退避，不丢数据。
            // 变化已应用（版本已递增），等待后的重试必须重新 diff，不能复用旧计划。
            SaveAttemptOutcome outcome = TryCompaction(entries, forceBaseWrite);
            nextPlan = BuildPlan(outcome, entries, changes: null, changeBytes: 0);
            return outcome;
        }

        // 追加失败后只有整份写成功才算保存；写盘被节流时先等待再重试。
        SaveAttemptOutcome fallback = WriteBaseOrWait(entries, blockCompactionOnFailure: true, forceBaseWrite);
        nextPlan = BuildPlan(fallback, entries, changes, changeBytes);
        return fallback;
    }

    /// <summary>
    /// 需要等待时为本次 Save 调用构建差异计划；已结束的判定返回 <see langword="null"/>。
    /// </summary>
    /// <param name="outcome">保存判定。</param>
    /// <param name="entries">调用方快照引用。</param>
    /// <param name="changes">本次差异；为 <see langword="null"/> 时表示零变化/基础未就绪。</param>
    /// <param name="changeBytes">本次差异的估算字节数；重试时直接复用，避免重复扫描。</param>
    /// <returns>等待计划或 <see langword="null"/>。</returns>
    private SavePlan? BuildPlan(
        SaveAttemptOutcome outcome,
        IReadOnlyList<ModelTextCacheEntry> entries,
        List<JournalChange>? changes,
        long changeBytes)
    {
        if (outcome != SaveAttemptOutcome.NeedsWait)
        {
            return null;
        }

        // 差异列表就是复用缓冲时直接把所有权交给计划，
        // 下一次 CollectChanges 用同容量新缓冲，避免复制整份变更列表。
        // 交出的缓冲由计划持有，不能回池复用，否则并发 Save 的计划可能被改写；
        // 等待路径罕见，接受一次同容量分配。
        List<JournalChange> planChanges = changes ?? [];
        if (ReferenceEquals(planChanges, _changesScratch))
        {
            _changesScratch = new List<JournalChange>(planChanges.Capacity);
        }

        return new SavePlan(_stateVersion, entries, planChanges, changeBytes);
    }

    /// <summary>
    /// 执行整份写；未到节流间隔时让调用方等待后重试，失败时按参数决定是否进入压缩退避。
    /// </summary>
    /// <param name="entries">完整快照。</param>
    /// <param name="blockCompactionOnFailure">整份写失败时是否进入压缩退避。</param>
    /// <param name="forceBaseWrite">累计等待已达预算时跳过剩余节流。</param>
    /// <returns>保存判定。</returns>
    private SaveAttemptOutcome WriteBaseOrWait(
        IReadOnlyList<ModelTextCacheEntry> entries,
        bool blockCompactionOnFailure,
        bool forceBaseWrite)
    {
        BaseWriteResult result = WriteBase(entries, allowEarly: forceBaseWrite);
        if (result == BaseWriteResult.Deferred)
        {
            return SaveAttemptOutcome.NeedsWait;
        }

        if (result == BaseWriteResult.Success)
        {
            return SaveAttemptOutcome.CompletedSuccess;
        }

        if (blockCompactionOnFailure)
        {
            BlockCompaction();
        }

        return SaveAttemptOutcome.CompletedFailure;
    }

    /// <summary>判断本批变化是否应直接整份写，避免先写日志再立即压缩导致同批数据写两遍。</summary>
    /// <param name="changeBytes">本批变化的估算字节数。</param>
    /// <returns>忽略本次归档会立即触发压缩时返回 <see langword="true"/>。</returns>
    private bool ShouldWriteBaseDirectly(long changeBytes) =>
        // 已有日志（含过期世代行）加上本批会跨过阈值时，追加后也会立刻压缩；直接写避免双写
        // （单个条件已覆盖「本批单独超阈值」）。
        _journalBytes + changeBytes >= CompactionThreshold;

    /// <summary>
    /// 计算整份写前需要等待的毫秒数：至少 <see cref="MinimumBaseWriteInterval"/>，且
    /// 「基础文件与本次预计写入字节的较大值」按 8MB/s 折算；并按本次 Save 的累计预算封顶。
    /// 纯函数，调用方在锁外计算，避免持有 <c>_gate</c> 做估算扫描。
    /// </summary>
    /// <param name="baseBytes">上次已知的基础文件字节数。</param>
    /// <param name="lastAttemptTick">上次写尝试的单调时间戳。</param>
    /// <param name="nowTick">当前单调时间戳。</param>
    /// <param name="writeBytes">本次预计写入的基础文件字节数。</param>
    /// <param name="alreadyWaitedMilliseconds">本次 Save 已经等待的毫秒数。</param>
    /// <param name="budgetMilliseconds">本次 Save 的累计等待预算。</param>
    /// <returns>需要等待的毫秒数；不需要等待或预算已用尽时为 0。</returns>
    private static long ComputeBaseWriteWaitMilliseconds(
        long baseBytes,
        long lastAttemptTick,
        long nowTick,
        long writeBytes,
        long alreadyWaitedMilliseconds,
        long budgetMilliseconds)
    {
        long remaining = ComputeBaseWriteIntervalMilliseconds(Math.Max(baseBytes, writeBytes))
            - (nowTick - lastAttemptTick);
        return ClampBaseWriteWait(remaining, alreadyWaitedMilliseconds, budgetMilliseconds);
    }

    /// <summary>
    /// 按单次 Save 的累计等待预算裁剪剩余等待；预算用尽返回 0，由调用方强制写入。
    /// </summary>
    /// <param name="remainingMilliseconds">按字节速率仍需要的等待毫秒数。</param>
    /// <param name="alreadyWaitedMilliseconds">本次 Save 已等待的毫秒数。</param>
    /// <param name="budgetMilliseconds">本次 Save 的累计等待预算。</param>
    /// <returns>本次还可等待的毫秒数。</returns>
    internal static long ClampBaseWriteWait(
        long remainingMilliseconds,
        long alreadyWaitedMilliseconds,
        long budgetMilliseconds)
    {
        if (remainingMilliseconds <= 0)
        {
            return 0;
        }

        long allowed = budgetMilliseconds - alreadyWaitedMilliseconds;
        return allowed <= 0 ? 0 : Math.Min(remainingMilliseconds, allowed);
    }

    /// <summary>
    /// 估算整份基础文件的序列化字节上界，用于按本次写入量折算节流。
    /// 与单条变化估算共用 <see cref="EstimateEntryJsonBytes"/>，实际基础文件只会更小。
    /// </summary>
    /// <param name="entries">完整快照。</param>
    /// <returns>估算字节数。</returns>
    private static long EstimateSnapshotBytes(IReadOnlyList<ModelTextCacheEntry> entries)
    {
        long total = CacheFileOverheadBytes;
        foreach (ModelTextCacheEntry entry in entries)
        {
            total += EstimateEntryJsonBytes(
                entry.GameId ?? string.Empty,
                entry.Signature ?? string.Empty,
                entry.Text ?? string.Empty,
                entry.PakStamp ?? string.Empty);
        }

        return total;
    }

    /// <summary>估算单个条目在 JSON 中的实际字节数（含包装开销：快照估算与变化估算共用）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="signature">文本签名。</param>
    /// <param name="text">备注文本。</param>
    /// <param name="pakStamp">pak 内容戳。</param>
    /// <returns>估算字节数。</returns>
    private static int EstimateEntryJsonBytes(
        string gameId,
        string signature,
        string text,
        string pakStamp) =>
        ChangeOverheadBytes
        + EstimateJsonStringBytes(gameId)
        + EstimateJsonStringBytes(signature)
        + EstimateJsonStringBytes(text)
        + EstimateJsonStringBytes(pakStamp);

    /// <summary>
    /// 整份写的最小间隔：至少 1 秒，且基础文件字节数按 8MB/s 折算。
    /// 字节上限对所有整份写生效，不再只限于清理待办期间。
    /// </summary>
    /// <param name="baseBytes">基础文件字节数（未知时为 0）。</param>
    /// <returns>间隔毫秒数。</returns>
    internal static long ComputeBaseWriteIntervalMilliseconds(long baseBytes)
    {
        long byteLimited = baseBytes / (MaxBaseWriteBytesPerSecond / 1000);
        return Math.Max((long)MinimumBaseWriteInterval.TotalMilliseconds, byteLimited);
    }

    /// <summary>压缩阈值：max(1MB, 基础文件/4)；直接整份写与日志压缩共用这一处。</summary>
    private long CompactionThreshold => Math.Max(MinJournalBytesForCompaction, _baseBytes / 4);

    /// <summary>记录一次压缩失败并延长退避。</summary>
    private void BlockCompaction()
    {
        _compactionBlockedUntil = _clock.UtcNow + _compactionBackoff;
        _compactionBackoff = TimeSpan.FromTicks(Math.Min(
            _compactionBackoff.Ticks * 2,
            MaxCompactionBackoff.Ticks));
    }

    /// <summary>
    /// 日志超过压缩阈值时整份写；压缩失败按指数退避，写盘被节流时让调用方等待后重试。
    /// </summary>
    /// <param name="entries">完整快照。</param>
    /// <param name="forceBaseWrite">累计等待已达预算时跳过剩余节流。</param>
    /// <returns>压缩完成、仍在退避期或需要等待后重试。</returns>
    private SaveAttemptOutcome TryCompaction(IReadOnlyList<ModelTextCacheEntry> entries, bool forceBaseWrite)
    {
        if (_clock.UtcNow < _compactionBlockedUntil && !JournalExceedsHardLimit())
        {
            // 退避期内不反复整份重写；日志仍保存着变化，下次再试。
            return SaveAttemptOutcome.CompletedSuccess;
        }

        BaseWriteResult result = WriteBase(entries, allowEarly: forceBaseWrite);
        if (result == BaseWriteResult.Deferred)
        {
            return SaveAttemptOutcome.NeedsWait;
        }

        if (result == BaseWriteResult.Success)
        {
            _compactionBackoff = InitialCompactionBackoff;
            _compactionBlockedUntil = default;
            return SaveAttemptOutcome.CompletedSuccess;
        }

        BlockCompaction();

        // 日志仍保存着变化；只有接近硬上限且重写失败才让调用方继续重试。
        return JournalExceedsHardLimit()
                ? SaveAttemptOutcome.CompletedFailure
                : SaveAttemptOutcome.CompletedSuccess;
    }

    /// <summary>日志是否接近硬上限（退避期内也必须尝试压缩）。</summary>
    /// <returns>超过上限 3/4 时返回 <see langword="true"/>。</returns>
    private bool JournalExceedsHardLimit() => _journalBytes >= MaxJournalBytes * 3L / 4;

    /// <summary>
    /// 计算与上次持久化快照的差异，并同趟累计估算字节数（复用缓冲，无变化时不分配）。
    /// </summary>
    /// <param name="entries">最新快照。</param>
    /// <param name="changeBytes">同趟累计的变化估算字节数。</param>
    /// <returns>新增/更新/删除的变化列表（内部复用缓冲）。</returns>
    private List<JournalChange> CollectChanges(IReadOnlyList<ModelTextCacheEntry> entries, out long changeBytes)
    {
        // 诊断计数，用于断言等待后的重试复用了计划而不是再次全量 diff。
        ChangeDiffCount++;
        long started = Stopwatch.GetTimestamp();
        changeBytes = 0;
        _changesScratch.Clear();
        _seenKeysScratch.Clear();
        foreach (ModelTextCacheEntry entry in entries)
        {
            ModelTextCacheEntry safe = ModelTextPrefetchService.NormalizeEntry(entry);
            (string GameId, int ModId) key = (safe.GameId, safe.ModId);
            _seenKeysScratch.Add(key);
            // 快照与日志都存收紧后的文本，未截断输入不会每次都产生差异。
            if (!_snapshot!.TryGetValue(key, out ModelTextCacheEntry? existing) || !existing.Equals(safe))
            {
                JournalChange change = new(safe.ModId, safe.GameId, safe, Deleted: false);
                _changesScratch.Add(change);
                changeBytes += EstimateSerializedBytes(change);
            }
        }

        foreach ((string GameId, int ModId) key in _snapshot!.Keys)
        {
            if (!_seenKeysScratch.Contains(key))
            {
                JournalChange change = new(key.ModId, key.GameId, null, Deleted: true);
                _changesScratch.Add(change);
                changeBytes += EstimateSerializedBytes(change);
            }
        }

        // 只统计 diff + 估算，不含日志写盘，避免慢 CI 墙钟误报。
        LastChangeDiffMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return _changesScratch;
    }

    /// <summary>增量日志单条记录的字节软上限（配合 100 条上限，保证单行大小有确定上界）。</summary>
    private const int JournalChunkMaxBytes = 256 * 1024;

    /// <summary>日志批次序列化次数（每次 <see cref="BuildJournalLines"/> 计 1 批），供早退用例断言。</summary>
    internal int JournalSerializeBatchCount { get; private set; }

    /// <summary>日志批次实际序列化的字节数（含换行），供用例断言接近上限时序列化量有界。</summary>
    internal long LastJournalSerializedBytes { get; private set; }

    /// <summary>因剩余容量不足而整批放弃追加并回退整份写的次数，供用例锁定截断语义。</summary>
    internal int JournalTruncatedBatchCount { get; private set; }

    /// <summary>本次会话是否已告警过日志容量不足，避免每次保存重复刷日志。</summary>
    private bool _journalTruncationWarned;

    /// <summary>把变化写入增量日志（按字节/条数分块，一次打开、一次刷盘）。</summary>
    /// <param name="changes">变化列表。</param>
    /// <param name="suppressIoWarning">
    /// 为 <see langword="true"/> 时只抑制 IO 失败告警（清理待办期间已告警过，避免每次保存重复刷日志）；
    /// 末尾残缺、达到上限等结构性问题始终告警。
    /// </param>
    /// <returns>追加成功时返回 <see langword="true"/>；日志超上限或 IO 失败时记录日志并返回 <see langword="false"/>。</returns>
    private bool TryAppendJournal(
        IReadOnlyList<JournalChange> changes,
        bool suppressIoWarning)
    {
        // 计数真实追加尝试，供用例断言每次保存都会重新尝试追加。
        JournalAppendAttempts++;
        try
        {
            if (!_journalDirectoryReady)
            {
                string? directory = Path.GetDirectoryName(_journalPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _journalDirectoryReady = true;
            }

            // 先打开日志并做结构早退（占用、残缺尾行）再序列化；
            // 序列化按剩余容量限制（见 BuildJournalLines），不会把整批数 MB 变化先序列化再丢弃。
            // 一次打开同时取得长度与末字节，不再额外读取文件元数据。
            using FileStream stream = new(_journalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            long existing = stream.Length;
            _journalBytes = existing;
            if (existing > 0)
            {
                stream.Position = existing - 1;
                if (stream.ReadByte() != '\n')
                {
                    // 残缺尾行会把新行粘在一起，回放到损坏行就整批丢弃；
                    // 改用整份重写；若整份写也失败，本次保存报告失败并由调用方重试。
                    _logger.Warn($"模型文本缓存增量日志末尾不完整，改为整份重写：{_journalPath}");
                    return false;
                }
            }

            long remaining = MaxJournalBytes - existing;
            (List<byte[]> lines, bool complete, long total) = BuildJournalLines(changes, remaining);
            if (!complete)
            {
                // 容量不足时整批不写入，绝不把截断后的前半批当成「已保存」；
                // 返回 false 让调用方改走整份写，Save 不会假成功。
                // 同一会话只告警一次，避免日志接近上限后每次保存都刷屏。
                JournalTruncatedBatchCount++;
                if (!_journalTruncationWarned)
                {
                    _journalTruncationWarned = true;
                    _logger.Warn(
                        $"模型文本缓存增量日志剩余容量不足（剩余 {remaining} 字节），本批不写入、改为整份重写：{_journalPath}");
                }

                return false;
            }

            // BuildJournalLines 已保证完整批次不超过剩余容量，不再保留不可达的二次容量兜底。
            stream.Position = existing;
            WriteJournalLines(stream, lines);

            stream.Flush();
            _journalBytes = existing + total;
            // 日志现在含尚未下沉的当前世代行，清理前必须先整份写。
            _journalHasCurrentGenerationRows = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录可能被外部删除：下次重新确认目录存在。
            _journalDirectoryReady = false;
            if (!suppressIoWarning)
            {
                _logger.Warn($"追加模型文本缓存增量日志失败，将整份重写：{ex.Message}");
            }

            return false;
        }
    }

    /// <summary>按条数、单行上界与剩余容量分块序列化日志行。</summary>
    /// <remarks>
    /// 每条变化的字节估算是最坏上界（6 字节/字符），仍以序列化后的精确字节复核。
    /// 容量不足时立即停止并返回 <c>Complete=false</c>：调用方必须整批放弃并改走整份写，
    /// 不得把截断后的前半批当成完整日志。
    /// </remarks>
    /// <param name="changes">变化列表。</param>
    /// <param name="remainingBytes">日志剩余容量（<c>MaxJournalBytes - 已有长度</c>）；重写整份日志时传 <see cref="long.MaxValue"/>。</param>
    /// <returns>日志行（不含换行）、是否完整序列化了全部变化、以及含换行的总字节数（只累加一次）。</returns>
    private (List<byte[]> Lines, bool Complete, long TotalBytes) BuildJournalLines(
        IReadOnlyList<JournalChange> changes,
        long remainingBytes)
    {
        // 诊断计数，用例断言日志早退路径不会先序列化整批变化。
        JournalSerializeBatchCount++;
        LastJournalSerializedBytes = 0;
        List<byte[]> lines = [];
        List<JournalChange> chunk = [];
        int chunkBytes = 0;
        long serializedBytes = 0;
        foreach (JournalChange change in changes)
        {
            int estimate = EstimateChunkUpperBoundBytes(change);
            if (chunk.Count > 0
                && (chunk.Count >= JournalChunkSize || chunkBytes + estimate > JournalChunkMaxBytes))
            {
                if (serializedBytes + chunkBytes + 1 > remainingBytes)
                {
                    LastJournalSerializedBytes = serializedBytes;
                    return (lines, false, serializedBytes);
                }

                byte[] line = SerializeJournalChunk(chunk);
                lines.Add(line);
                serializedBytes += line.Length + 1;
                chunk.Clear();
                chunkBytes = 0;
            }

            // 单条记录的上界就超过剩余容量时，不序列化这一批。
            if (chunk.Count == 0 && estimate + 1 > remainingBytes)
            {
                LastJournalSerializedBytes = serializedBytes;
                return (lines, false, serializedBytes);
            }

            chunk.Add(change);
            chunkBytes += estimate;
        }

        if (chunk.Count > 0)
        {
            if (serializedBytes + chunkBytes + 1 > remainingBytes)
            {
                LastJournalSerializedBytes = serializedBytes;
                return (lines, false, serializedBytes);
            }

            byte[] line = SerializeJournalChunk(chunk);
            lines.Add(line);
            serializedBytes += line.Length + 1;
        }

        LastJournalSerializedBytes = serializedBytes;
        return (lines, true, serializedBytes);
    }

    /// <summary>把已序列化的日志行逐行写入流（每行后补换行）；追加与重写共用。</summary>
    /// <param name="stream">目标流（位置已就绪）。</param>
    /// <param name="lines">UTF-8 行字节（不含换行）。</param>
    private static void WriteJournalLines(FileStream stream, IReadOnlyList<byte[]> lines)
    {
        foreach (byte[] line in lines)
        {
            stream.Write(line);
            stream.WriteByte((byte)'\n');
        }
    }

    /// <summary>序列化一条日志记录。</summary>
    /// <param name="chunk">当前分块。</param>
    /// <returns>UTF-8 行字节（不含换行）。</returns>
    private byte[] SerializeJournalChunk(List<JournalChange> chunk) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new JournalRecord(CurrentVersion, _generation, [.. chunk]),
            SerializerOptions);

    /// <summary>估算单条变化的日志字节上界（按 UTF-8 最坏 6 字节/字符，只用于分块）。</summary>
    /// <remarks>
    /// 短转义（\b \t \n \f \r）占 2 字符、其它控制字符占 <c>\uXXXX</c>（6 字符），
    /// 因此按 6 字节/字符估算仍是字符串字段的安全上界。
    /// </remarks>
    /// <param name="change">变化。</param>
    /// <returns>估算字节数（不小于实际序列化大小）。</returns>
    private static int EstimateChunkUpperBoundBytes(JournalChange change)
    {
        // 分块必须用最坏上界，防止控制字符把单行放大到超过 256KB。
        int gameBytes = (change.GameId?.Length ?? 0) * 6;
        if (change.Entry is null)
        {
            return ChangeOverheadBytes + gameBytes;
        }

        ModelTextCacheEntry entry = change.Entry;
        return ChangeOverheadBytes + gameBytes
            + ((entry.Text?.Length ?? 0) * 6)
            + ((entry.PakStamp?.Length ?? 0) * 6)
            + ((entry.Signature?.Length ?? 0) * 6);
    }

    /// <summary>估算单条变化的实际 JSON 字节数。</summary>
    /// <param name="change">变化。</param>
    /// <returns>估算字节数。</returns>
    private static int EstimateSerializedBytes(JournalChange change)
    {
        // 与快照估算共用同一个条目估算核心。
        return change.Entry is null
            ? EstimateEntryJsonBytes(change.GameId ?? string.Empty, string.Empty, string.Empty, string.Empty)
            : EstimateEntryJsonBytes(
                change.GameId ?? string.Empty,
                change.Entry.Signature ?? string.Empty,
                change.Entry.Text ?? string.Empty,
                change.Entry.PakStamp ?? string.Empty);
    }

    /// <summary>JSON 需要转义的字符集合：C0/DEL/C1 控制字符、双引号与反斜杠。</summary>
    private static readonly SearchValues<char> JsonEscapedChars = SearchValues.Create(BuildJsonEscapedChars());

    /// <summary>需要转义的字符总数：C0 32 个 + DEL/C1 33 个 + 双引号与反斜杠 2 个。</summary>
    internal const int JsonEscapedCharCount = 67;

    /// <summary>构造 <see cref="JsonEscapedChars"/> 需要包含的字符序列；按最大长度一次预分配。</summary>
    /// <returns>控制字符 0x00–0x1F、DEL/C1（0x7F–0x9F）加上 <c>"</c> 与 <c>\</c>。</returns>
    internal static string BuildJsonEscapedChars()
    {
        StringBuilder builder = new(JsonEscapedCharCount);
        for (char current = '\0'; current <= '\u001F'; current++)
        {
            builder.Append(current);
        }

        // 宽松编码器仍把 DEL 与 C1 控制字符写成 \uXXXX。
        for (char current = '\u007F'; current <= '\u009F'; current++)
        {
            builder.Append(current);
        }

        return builder.Append('"').Append('\\').ToString();
    }

    /// <summary>
    /// 估算字符串在 JSON 中的实际字节数（UTF-8 + 引号/反斜杠/控制字符转义增量）。
    /// 先判断是否含需转义字符：纯文本走向量化快路径，含转义时再用标量循环计算
    /// UTF-8 宽度与转义增量。
    /// </summary>
    /// <param name="value">字符串。</param>
    /// <returns>字节数。</returns>
    internal static int EstimateJsonStringBytes(string value)
    {
        ReadOnlySpan<char> span = value.AsSpan();
        if (!span.ContainsAny(JsonEscapedChars))
        {
            return Encoding.UTF8.GetByteCount(span);
        }

        int bytes = 0;
        for (int index = 0; index < span.Length; index++)
        {
            char current = span[index];
            if (current < 0x20)
            {
                // \b \t \n \f \r 在 JSON 中是 2 字符短转义，其余控制字符是 \uXXXX（6 字符）。
                bytes += current is '\b' or '\t' or '\n' or '\f' or '\r' ? 2 : 6;
            }
            else if (current is >= '\u007F' and <= '\u009F')
            {
                // 宽松编码器仍把 DEL 与 C1 控制字符写成 \uXXXX。
                bytes += 6;
            }
            else if (current is '"' or '\\')
            {
                bytes += 2;
            }
            else if (current < 0x80)
            {
                bytes += 1;
            }
            else if (current < 0x800)
            {
                bytes += 2;
            }
            else if (char.IsHighSurrogate(current)
                && index + 1 < span.Length
                && char.IsLowSurrogate(span[index + 1]))
            {
                bytes += 4;
                index++;
            }
            else
            {
                // BMP 字符为 3 字节；孤立代理项按 UTF-8 替换符同样计 3 字节。
                bytes += 3;
            }
        }

        return bytes;
    }

    /// <summary>把变化应用到内存快照。</summary>
    /// <param name="changes">变化列表。</param>
    private void ApplyChanges(IReadOnlyList<JournalChange> changes)
    {
        foreach (JournalChange change in changes)
        {
            (string GameId, int ModId) key = (change.GameId, change.ModId);
            if (change.Deleted || change.Entry is null)
            {
                _snapshot!.Remove(key);
            }
            else
            {
                _snapshot![key] = change.Entry;
            }
        }

        // 快照内容变化后，等待前缓存的差异不能再复用。
        _stateVersion++;
    }

    /// <summary>增量日志是否超过压缩阈值（max(1MB, 基础文件/4)）。</summary>
    /// <returns>需要压缩时返回 <see langword="true"/>。</returns>
    private bool JournalExceedsCompactionThreshold() => _journalBytes >= CompactionThreshold;

    /// <summary>整份写入基础文件并删除增量日志（写入端兜底收紧文本）。</summary>
    /// <param name="entries">完整快照。</param>
    /// <param name="allowEarly">
    /// 为 <see langword="true"/> 时跳过节流检查。两类场景：启动装载修复损坏日志；
    /// 单次 Save 累计等待预算耗尽后的强制写入（后者会从直接写、追加失败回退与压缩三个调用点传入）。
    /// 本次会话第一次整份写本来就免等待，无需该参数。
    /// </param>
    /// <returns>
    /// 写入成功、失败或未到节流间隔（<see cref="BaseWriteResult.Deferred"/>）。
    /// 首次整份写或 <paramref name="allowEarly"/> 为 <see langword="true"/> 时不会返回 Deferred。
    /// </returns>
    /// <remarks>
    /// 序列化与替换必须与 <c>_gate</c> 同持。若把写盘移出锁，期间并发保存追加的日志行
    /// 会引用旧世代号，换入新基础文件后这些行会被当成旧世代跳过，导致已返回成功的变更丢失；
    /// 因此保留「整份写全程持锁」，用锁外等待保证其他线程不被节流窗口阻塞。
    /// 首次整份写同样全程持锁；每个会话至多一次，通常发生在后台保存线程，关窗等待另有 60 秒总预算，
    /// 不为此单独引入无锁写路径。
    /// </remarks>
    private BaseWriteResult WriteBase(IReadOnlyList<ModelTextCacheEntry> entries, bool allowEarly = false)
    {
        if (!allowEarly && _hasWrittenBase)
        {
            // 调用方等待后必须复查；这里再判一次，防御未来新增调用方绕过 Save 的等待。
            // 本次会话第一次写完全免等待，即使装载的是 128MB 缓存也不会额外延迟。
            long remaining = ComputeBaseWriteIntervalMilliseconds(_baseBytes)
                - (_tickSource() - _lastBaseWriteAttemptTick);
            if (remaining > 0)
            {
                return BaseWriteResult.Deferred;
            }
        }

        // 每次尝试（含失败）都更新节流时间戳，连续失败也不会超过写盘速率上限。
        _hasWrittenBase = true;
        BaseWriteAttempts++;
        _lastBaseWriteAttemptTick = _tickSource();
        IReadOnlyList<ModelTextCacheEntry> safeEntries = EnsureClamped(entries);
        string? directory = Path.GetDirectoryName(_path);
        try
        {
            // 目录也可能只读/无权限，先建目录再做可写探测，避免第一保存失败。
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"模型文本缓存目录不可写：{ex.Message}");
            return BaseWriteResult.Failed;
        }

        if (!CanWriteBaseDestination(out bool destinationExists))
        {
            // 目标被占用时直接失败，不先写 21MB 临时文件再替换失败。
            return BaseWriteResult.Failed;
        }

        string generation = Guid.NewGuid().ToString("N");
        string temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        bool tempConsumed = false;
        long serializedBytes;
        try
        {
            // 临时文件创建本身就是目录可写探测，成功后再写内容，不再额外创建零字节探针。
            // 先写临时文件再改名：写入中断时旧缓存仍完整，不会留下半截 JSON。
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream,
                    new CacheFile(CurrentVersion, generation, safeEntries),
                    SerializerOptions);
                stream.Flush();
                serializedBytes = stream.Length;
            }

            // 复用写前的存在性探测结果选择 Replace/Move，正常路径只做一次 FileExists。
            // 探测与换入之间存在 TOCTOU 窗口；第一次失败时重新探测并重试一次，
            // 单次保存即可自愈。存在性按值传递，重试时的更新只留在换入方法内部。
            TryMoveBaseIntoPlace(temp, destinationExists);

            tempConsumed = true;
            _generation = generation;
            _baseBytes = serializedBytes;
            _baseReady = true;
            _snapshot = new Dictionary<(string GameId, int ModId), ModelTextCacheEntry>(safeEntries.Count);
            foreach (ModelTextCacheEntry entry in safeEntries)
            {
                _snapshot[(entry.GameId, entry.ModId)] = entry;
            }

            // 快照已整体替换，等待前缓存的差异作废。
            _stateVersion++;

            // 基础文件已包含完整快照；三级清理失败时进入退避，并清零旧日志计数。
            // 基础文件已含全部变化，日志中的当前世代行不再必需，可以安全清理。
            _journalHasCurrentGenerationRows = false;
            EnsureJournalCleanedAfterBase();
            return BaseWriteResult.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 失败不改写 _baseBytes（否则会永久放大后续节流间隔与压缩阈值）；
            // 下一次保存会用本次快照的估算字节重新计算等待，且这里不再读取文件长度，Save 不会因长度读取抛异常。
            _logger.Warn($"模型文本缓存不可写（可能被占用或目录只读）：{ex.Message}");
            return BaseWriteResult.Failed;
        }
        finally
        {
            // 替换成功后临时文件已被消费，不再对已删文件重复调用删除。
            if (!tempConsumed)
            {
                _fileSystem.TryDeleteFile(temp);
            }
        }
    }

    /// <summary>写入端兜底：任何入口的超长文本都在序列化前收紧。</summary>
    /// <param name="entries">原始快照。</param>
    /// <returns>无需截断时返回原列表，否则返回收紧后的新数组。</returns>
    private static IReadOnlyList<ModelTextCacheEntry> EnsureClamped(IReadOnlyList<ModelTextCacheEntry> entries)
    {
        bool needsClamp = false;
        foreach (ModelTextCacheEntry entry in entries)
        {
            if (!ReferenceEquals(ModelTextPrefetchService.NormalizeEntry(entry), entry))
            {
                needsClamp = true;
                break;
            }
        }

        if (!needsClamp)
        {
            return entries;
        }

        ModelTextCacheEntry[] clamped = new ModelTextCacheEntry[entries.Count];
        for (int index = 0; index < entries.Count; index++)
        {
            clamped[index] = ModelTextPrefetchService.NormalizeEntry(entries[index]);
        }

        return clamped;
    }

    /// <summary>
    /// 本类执行的文件存在性探测总次数：基础文件写前探测、日志清理探测与装载时的 <see cref="FileInfo"/> 探测都计入。
    /// 供用例断言没有绕过统一入口的「裸」探测路径。
    /// </summary>
    /// <remarks>
    /// 存在性探测统一走 <see cref="ProbeFileExists"/> 或 <see cref="ProbeFileInfo"/>；
    /// 日志清理的探测也计入是刻意保留的可观测性（一次整份写后最多 +1）。
    /// <see cref="CorruptFileQuarantine"/> 内部的探测属于独立组件，不计入本计数。
    /// </remarks>
    internal int FileExistsProbeCount { get; private set; }

    /// <summary>基础文件写前存在性探测次数（每次整份写恰好一次），供用例按场景稳定断言。</summary>
    internal int BaseDestinationProbeCount { get; private set; }

    /// <summary>统一的存在性探测入口：计数后转发到 <see cref="IFileSystem.FileExists"/>。</summary>
    /// <param name="path">要探测的文件路径。</param>
    /// <returns>文件存在时返回 <see langword="true"/>。</returns>
    private bool ProbeFileExists(string? path)
    {
        FileExistsProbeCount++;
        return _fileSystem.FileExists(path);
    }

    /// <summary>需要「存在性 + 长度」的元数据探测入口：一次 <see cref="FileInfo"/> 读取并计数。</summary>
    /// <param name="path">要探测的文件路径。</param>
    /// <returns>可用于读取 <see cref="FileInfo.Exists"/> 与 <see cref="FileInfo.Length"/> 的元数据对象。</returns>
    private FileInfo ProbeFileInfo(string path)
    {
        FileExistsProbeCount++;
        return new FileInfo(path);
    }

    /// <summary>
    /// 把临时文件换入基础文件；第一次失败时重新探测目标并重试一次。
    /// </summary>
    /// <remarks>
    /// 探测与换入之间的 TOCTOU 窗口只影响第一次尝试：外部在序列化期间创建/删除基础文件时，
    /// 重新探测即可选择正确的 Replace/Move；第二次仍失败则抛给 <see cref="WriteBase"/> 的失败路径。
    /// 重试不会掩盖持续占用（第二次失败同样返回失败），只是让单次保存能够自愈。
    /// 两次都失败时临时文件尚未消费，由 <see cref="WriteBase"/> 的 finally 删除，原基础文件保持不变。
    /// </remarks>
    /// <param name="temp">已写完的临时文件。</param>
    /// <param name="destinationExists">写前存在性探测结果；重试前会按磁盘现状更新（只在本方法内部使用）。</param>
    private void TryMoveBaseIntoPlace(string temp, bool destinationExists)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (_moveBaseFailureInjection is not null && !_moveBaseFailureInjection(destinationExists))
                {
                    // 仅测试注入时可达，模拟探测后目标被外部创建/删除导致的第一次换入失败。
                    throw new IOException("注入的基础文件换入失败");
                }

                if (destinationExists)
                {
                    // File.Replace 允许目标被共享读句柄打开时完成替换；
                    // File.Move(overwrite) 在同样条件下会被 Windows 拒绝。
                    try
                    {
                        File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                    catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
                    {
                        // 文件系统不支持 ReplaceFile 或瞬时共享冲突时回退改名；
                        // 目标仍被独占时这里会继续失败，由上层退避，绝不让平台异常穿出端口契约。
                        File.Move(temp, _path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(temp, _path);
                }

                return;
            }
            catch (Exception ex) when (attempt == 0 && (ex is IOException or UnauthorizedAccessException))
            {
                // 探测结果已过时，按磁盘现状重新探测后再试一次。
                destinationExists = ProbeFileExists(_path);
            }
        }
    }

    /// <summary>探测基础文件是否被独占：目录可写性由临时文件创建覆盖。</summary>
    /// <param name="destinationExists">基础文件是否已存在；结果复用于写后选择 Replace/Move。</param>
    /// <returns>基础文件不存在或可被替换时返回 <see langword="true"/>。</returns>
    private bool CanWriteBaseDestination(out bool destinationExists)
    {
        destinationExists = false;
        BaseDestinationProbeCount++;
        try
        {
            // 整个 WriteBase 只在这里做一次存在性探测，写后不再重复 FileExists。
            destinationExists = ProbeFileExists(_path);
            if (!destinationExists)
            {
                return true;
            }

            // 只读探测允许外部只读句柄共享，避免把杀毒/索引器的正常读取误判为文件被占用。
            using FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"模型文本缓存基础文件不可写（可能被占用）：{_path}（{ex.Message}）");
            return false;
        }
    }

    /// <summary>尝试删除增量日志，不在这里告警（由三级清理统一处理）。</summary>
    /// <returns>日志已不存在时返回 <see langword="true"/>。</returns>
    private bool TryDeleteJournalQuiet()
    {
        // 存在性以磁盘为准；删除失败不在这里告警，由三级清理统一处理。
        if (!ProbeFileExists(_journalPath))
        {
            _journalBytes = 0;
            return true;
        }

        if (!_fileSystem.TryDeleteFile(_journalPath))
        {
            return false;
        }

        _journalBytes = 0;
        return true;
    }

    /// <summary>删除与改名都失败时尝试截断为 0：文件可写但不可删时仍能继续使用日志。</summary>
    /// <returns>截断成功时返回 <see langword="true"/>。</returns>
    private bool TryTruncateJournal()
    {
        try
        {
            using FileStream stream = new(_journalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(0);
            stream.Flush();
            _journalBytes = 0;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>整份写成功后按退避窗口决定是否清理日志，避免每个保存都撞同一个锁。</summary>
    private void EnsureJournalCleanedAfterBase()
    {
        if (_journalCleanupPending && _clock.UtcNow < _journalCleanupBlockedUntil)
        {
            // 退避期内的读取点之一；期间只清零计数，不重复尝试删除/改名/截断。
            _journalBytes = 0;
            return;
        }

        TryCleanJournal();
    }

    /// <summary>保存开始时按退避窗口尝试恢复日志可用性。</summary>
    private void TryRecoverJournalIfDue()
    {
        if (!_journalCleanupPending || _clock.UtcNow < _journalCleanupBlockedUntil)
        {
            // 退避期内的读取点之一；未到期不重复清理。
            return;
        }

        if (_journalHasCurrentGenerationRows)
        {
            // 待办期间追加成功的当前世代行只存在于日志，删除会让重启丢失这些变化。
            // 保留日志，等下一次整份写把快照下沉后再清理（EnsureJournalCleanedAfterBase）。
            return;
        }

        TryCleanJournal();
    }

    /// <summary>
    /// 尝试三级清理日志：删除 → 改名隔离 → 截断为 0；全部失败时清零旧日志计数并进入清理待办
    /// （判定与失败后处理只保留这一处）。
    /// </summary>
    private void TryCleanJournal()
    {
        // 两个调用方都保证 _journalHasCurrentGenerationRows 已为 false
        // （WriteBase 成功后先清零；TryRecoverJournalIfDue 在标记为 true 时直接返回），这里不再重复复位。
        if (TryDeleteJournalQuiet() || TryMoveJournalAside() || TryTruncateJournal())
        {
            ResetJournalCleanupState();
            return;
        }

        // 旧日志即使保留也已过期（基础文件已写新世代），不能让它影响后续写盘量判断。
        _journalBytes = 0;
        EnterJournalCleanupBackoff();
    }

    /// <summary>
    /// 进入清理待办退避：每次进入待办只告警一次，清理成功后重置；退避指数增长到上限。
    /// </summary>
    private void EnterJournalCleanupBackoff()
    {
        _journalCleanupPending = true;
        _journalCleanupBlockedUntil = _clock.UtcNow + _journalCleanupBackoff;
        _journalCleanupBackoff = TimeSpan.FromTicks(
            Math.Min(_journalCleanupBackoff.Ticks * 2, MaxCompactionBackoff.Ticks));
        if (_journalCleanupWarned)
        {
            return;
        }

        _journalCleanupWarned = true;
        _logger.Warn(
            $"模型文本缓存增量日志无法删除/改名/截断，已记入清理待办；后续保存会继续尝试追加，含当前世代行时保留到下一次整份写后再清理：{_journalPath}");
    }

    /// <summary>日志恢复可用后重置退避与告警状态。</summary>
    private void ResetJournalCleanupState()
    {
        _journalCleanupPending = false;
        _journalCleanupBlockedUntil = default;
        _journalCleanupBackoff = InitialCompactionBackoff;
        _journalCleanupWarned = false;
    }

    /// <summary>从文件头读取 <c>"Version"</c> 整数值；只解析头部，不读取全文。</summary>
    /// <param name="head">文件头字节（UTF-8）。</param>
    /// <param name="version">声明的版本号。</param>
    /// <returns>成功读到版本号时返回 <see langword="true"/>。</returns>
    private static bool TryReadDeclaredVersion(ReadOnlySpan<byte> head, out int version)
    {
        version = 0;
        string text = Encoding.UTF8.GetString(head);
        const string Label = "\"Version\"";
        int index = text.IndexOf(Label, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        int cursor = index + Label.Length;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        if (cursor >= text.Length || text[cursor] != ':')
        {
            return false;
        }

        cursor++;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        int start = cursor;
        while (cursor < text.Length && char.IsAsciiDigit(text[cursor]))
        {
            cursor++;
        }

        return cursor > start
            && int.TryParse(
                text.AsSpan(start, cursor - start),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out version);
    }

    /// <summary>整份写尝试的结果：成功、失败或未到节流间隔（由调用方等待后重试）。</summary>
    private enum BaseWriteResult
    {
        /// <summary>写入并替换基础文件成功。</summary>
        Success,

        /// <summary>写入失败（占用、只读或 IO 错误）。</summary>
        Failed,

        /// <summary>距上次写尝试不足节流间隔，本次未写盘。</summary>
        Deferred,
    }

    /// <summary>一次保存判定的结果：成功、失败，或需要等待整份写节流后重试。</summary>
    private enum SaveAttemptOutcome
    {
        /// <summary>保存成功（数据已落盘或仍在日志中）。</summary>
        CompletedSuccess,

        /// <summary>保存失败，调用方需要重试。</summary>
        CompletedFailure,

        /// <summary>需要等待整份写节流后重试。</summary>
        NeedsWait,
    }

    /// <summary>
    /// 单次 Save 调用内的差异计划：只在该调用等待整份写后复用，绑定快照版本与调用方 entries 引用，
    /// 不同 Save 调用之间互不可见。
    /// </summary>
    private readonly struct SavePlan
    {
        /// <summary>创建计划。</summary>
        /// <param name="stateVersion">计算计划时的快照版本。</param>
        /// <param name="entries">调用方传入的快照引用。</param>
        /// <param name="changes">已计算的变化列表。</param>
        /// <param name="changeBytes">变化列表的估算字节数（重试复用）。</param>
        public SavePlan(
            int stateVersion,
            IReadOnlyList<ModelTextCacheEntry> entries,
            List<JournalChange> changes,
            long changeBytes)
        {
            StateVersion = stateVersion;
            Entries = entries;
            Changes = changes;
            ChangeBytes = changeBytes;
        }

        /// <summary>计算计划时的快照版本。</summary>
        public int StateVersion { get; }

        /// <summary>调用方传入的快照引用。</summary>
        public IReadOnlyList<ModelTextCacheEntry> Entries { get; }

        /// <summary>已计算的变化列表。</summary>
        public List<JournalChange> Changes { get; }

        /// <summary>变化列表的估算字节数（重试复用）。</summary>
        public long ChangeBytes { get; }
    }

    /// <summary>缓存文件根对象：版本号 + 世代号 + 条目列表。</summary>
    /// <param name="Version">文件格式版本。</param>
    /// <param name="Generation">世代号；每次整份重写都会变化。</param>
    /// <param name="Entries">缓存条目（直接引用调用方快照，不再复制）。</param>
    private sealed record CacheFile(int Version, string Generation, IReadOnlyList<ModelTextCacheEntry> Entries);

    /// <summary>增量日志的一行：版本号 + 世代号 + 本次变化。</summary>
    /// <param name="Version">文件格式版本。</param>
    /// <param name="Generation">写入时的基础文件世代号。</param>
    /// <param name="Changes">变化列表（最多 <see cref="JournalChunkSize"/> 条）。</param>
    private sealed record JournalRecord(int Version, string Generation, IReadOnlyList<JournalChange> Changes);

    /// <summary>单条变化：写入条目或删除。</summary>
    /// <param name="ModId">MOD ID。</param>
    /// <param name="GameId">游戏稳定 ID。</param>
    /// <param name="Entry">新条目；删除时为 <see langword="null"/>。</param>
    /// <param name="Deleted">是否为删除。</param>
    private sealed record JournalChange(int ModId, string GameId, ModelTextCacheEntry? Entry, bool Deleted);
}
