using HunterBox.Application.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Logging;

using HunterBox.Infrastructure.IO;
namespace HunterBox.Infrastructure.Storage;

/// <summary>
/// 基于 JSON 文件的 MOD 记录存储（<c>Mods/index.json</c>）。
/// </summary>
/// <remarks>
/// 文件结构：<c>{ "SchemaVersion": 1, "NextId": n, "Mods": [...] }</c>（沿用本的 PascalCase 字段约定）。
/// <list type="bullet">
/// <item><see cref="Save"/> 只在 <c>_gate</c> 内取记录快照，序列化与落盘在锁外完成；
/// 同一实例的并发保存由独立的 <c>_saveGate</c> 串行化。写入使用同目录的 GUID 临时名 +
/// <see cref="FileStream.Flush(bool)"/> 刷盘，最后 <see cref="File.Replace(string, string, string?)"/> 原子替换，
/// 并在替换前把上一版索引留成 <c>index.json.bak</c>，避免中途崩溃留下半截文件或丢数据。</item>
/// <item>读取到损坏/半截 JSON 时绝不静默当作空仓库：原文件会被重命名为
/// <c>index.json.bad-&lt;yyyyMMddHHmmss&gt;</c> 保留，仓库返回空并暴露 <see cref="LoadWarning"/>，
/// 因此后续 <see cref="Save"/> 只会写新文件、不会覆盖损坏原件。</item>
/// <item>损坏索引连改名备份都失败（被占用/只读）时也不向上抛：返回空仓库 + <see cref="LoadWarning"/>
/// 让程序能启动；<see cref="Save"/> 写入前会再试一次备份，仍失败则拒绝覆盖未备份的原件（抛 <see cref="IOException"/>）。</item>
/// <item>为兼容早期只有数组的格式，读取时会回退尝试数组反序列化；<see cref="ModKind"/> 按默认数字形式
/// 序列化以索引（不启用字符串枚举转换器）。</item>
/// <item>非损坏类的读取 IO 异常（无权限、文件被占用等）直接向上抛，由 App 层兜底提示，
/// 不静默当成空仓库（否则下一次保存会覆盖读不出来的数据）。</item>
/// </list>
/// <para><b>线程模型（与 <see cref="IModStore"/> 的线程契约一一对应）：</b></para>
/// <list type="bullet">
/// <item>全部公开成员都在私有 <c>_gate</c> 锁内读写 <c>_mods</c> / <c>_nextId</c>。<see cref="Save"/> 只在
/// <c>_gate</c> 内取「记录列表 + NextId」快照，序列化与原子替换在锁外完成；并发保存由 <c>_saveGate</c>
/// 串行化，不会写出半截 JSON，也不会让旧快照覆盖新快照。</item>
/// <item><see cref="Mods"/> / <see cref="ModsFor"/> / <see cref="UnassignedMods"/> 返回新的列表快照（成员固定于调用时刻），
/// 但元素是仓储内部的活 <see cref="ModRecord"/> 引用；<see cref="Find"/> 直接返回活引用；<see cref="AssignGame"/> 用
/// 新实例替换旧实例（此前拿到的旧引用变成孤儿）。</item>
/// <item><see cref="ModRecord"/> 的字段不在锁保护范围内：调用方不得在其它线程调用本类成员（尤其 <see cref="Save"/>）
/// 的同时就地修改记录字段，否则可能落盘「半更新状态」。当前 App 由 <c>OperationRunner</c> 忙碌门禁 + UI 线程保证
/// 同一时刻只有一个写线程。</item>
/// <item>少量成员会在锁内重入调用其它公开成员（<see cref="Add"/> → <see cref="NextIndex"/>/<see cref="NextIndexFor"/>、
/// <see cref="AssignGame"/> → <see cref="NextIndexFor"/>、<see cref="ReorderForGame"/> → <see cref="Find"/>）。
/// C# <c>Monitor</c>（<c>lock</c>）可重入，这样写是安全的；但不要把 <c>_gate</c> 换成不可重入的同步原语
/// （<c>SemaphoreSlim</c> 等），否则会自锁。</item>
/// <item><see cref="Load"/> 是静态工厂，只在实例发布前读盘，不与任何写线程并发；本类不负责多个实例/进程并发写
/// 同一索引文件（App 有单实例保护）。</item>
/// </list>
/// </remarks>
public sealed class ModRepository : IModStore
{
    /// <summary>当前索引格式版本；读取兼容没有该字段的旧文件。</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<ModRecord> _mods;
    private readonly IAppLogger _logger;

    /// <summary>保护 <see cref="_mods"/> / <see cref="_nextId"/> 的读改与 <see cref="Save"/> 的快照。</summary>
    private readonly object _gate = new();

    /// <summary>串行化并发保存（保证旧快照不会覆盖新快照）；不阻塞读者。</summary>
    private readonly object _saveGate = new();

    private int _nextId;

    /// <summary>
    /// 本次会话从损坏索引降级启动（无论原件是否已改名保留）。
    /// 保存前若发现 index.json 已变成有效文件，说明用户恢复了 .bak，必须拒绝覆盖。
    /// </summary>
    private bool _corruptIndexRecovered;

    private ModRepository(
        string indexPath,
        List<ModRecord> mods,
        int nextId,
        string? loadWarning = null,
        bool corruptIndexUnpreserved = false,
        bool corruptIndexRecovered = false,
        IAppLogger? logger = null)
    {
        IndexPath = indexPath;
        _mods = mods;
        _logger = logger ?? AppLogAdapter.Instance;
        _nextId = Math.Max(nextId, mods.Count == 0 ? 1 : mods.Max(m => m.Id) + 1);
        LoadWarning = loadWarning;
        _corruptIndexRecovered = corruptIndexRecovered || corruptIndexUnpreserved;
        Normalize();
    }

    /// <summary>索引文件路径。</summary>
    public string IndexPath { get; }

    /// <summary>索引备份文件路径：<c>index.json.bak</c>（覆盖已有索引前生成；首次保存不生成）。</summary>
    public string BackupPath => IndexPath + ".bak";

    /// <inheritdoc />
    /// <remarks>
    /// <para>返回列表快照（成员固定于调用时刻）：后台安装/保存与界面刷新并发时，遍历不会被「集合已修改」打断，
    /// 之后的增删/重排也不会改动已经发出的列表。</para>
    /// <para>元素是仓储内的活记录，不是副本：修改字段会改到仓储，并由下一次 <see cref="Save"/> 落盘（见线程模型）。</para>
    /// </remarks>
    public IReadOnlyList<ModRecord> Mods
    {
        get
        {
            lock (_gate)
            {
                return [.. _mods];
            }
        }
    }

    /// <summary>
    /// 加载时发现索引损坏/无法解析的警告文案（含损坏原件的保留路径）；正常加载时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>界面应在启动时提示用户，并引导从 <c>.bak</c> 或重新安装恢复，而不是静默显示空列表。</remarks>
    public string? LoadWarning { get; }

    /// <summary>没有归属游戏的旧记录（<see cref="ModRecord.GameId"/> 为空），供界面提示用户分配。</summary>
    /// <remarks>
    /// <para>用户确认归属后可调用 <see cref="AssignGame"/> 迁移；老数据不会静默丢弃、也不会出现在任何游戏列表里。</para>
    /// <para>与 <see cref="Mods"/> 相同：返回列表快照、元素是活记录；可在任意线程安全枚举。</para>
    /// </remarks>
    public IReadOnlyList<ModRecord> UnassignedMods
    {
        get
        {
            lock (_gate)
            {
                return [.. _mods.Where(m => string.IsNullOrWhiteSpace(m.GameId))];
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 列表快照（成员固定于调用时刻）、元素是仓储内活记录；过滤在锁内完成，可跨线程安全调用。
    /// </remarks>
    public IReadOnlyList<ModRecord> ModsFor(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        lock (_gate)
        {
            return [.. _mods.Where(m => m.BelongsTo(gameId))];
        }
    }

    /// <summary>从磁盘加载；索引文件不存在时返回空仓库。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    /// <returns>加载完成的仓库。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="IOException">索引文件存在但读取失败（被占用、I/O 错误等）。</exception>
    /// <exception cref="UnauthorizedAccessException">没有读取索引文件的权限（原样向上抛出）。</exception>
    /// <remarks>
    /// 索引内容损坏（JSON 解析失败/结构不对）时不抛异常：返回空仓库并在 <see cref="LoadWarning"/> 中说明
    /// 损坏原件的保留位置或无法保留的原因，避免损坏索引阻断启动。
    /// </remarks>
    public static ModRepository Load(BoxPaths paths, IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        logger ??= AppLogAdapter.Instance;

        string indexPath = Path.Combine(paths.ModsDirectory, "index.json");
        // 启动期清理历史遗留的多余索引备份，与文本缓存行为一致。
        CorruptFileQuarantine.Prune(indexPath, SystemFileSystem.Instance, keep: null, logger.Warn);
        if (!File.Exists(indexPath))
        {
            logger.Info($"未找到 MOD 索引，按空列表启动：{indexPath}");
            return new ModRepository(indexPath, [], 1, logger: logger);
        }

        string json;
        try
        {
            json = File.ReadAllText(indexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error($"读取 MOD 索引失败：{indexPath}", ex);
            throw;
        }

        IndexDocument? document = TryDeserialize<IndexDocument>(json);
        if (document?.Mods is not null)
        {
            List<ModRecord> mods = Sanitize(document.Mods);
            int dropped = document.Mods.Count - mods.Count;
            string? sanitizeWarning = dropped > 0
                ? BuildSanitizeWarning(dropped, indexPath)
                : null;
            if (sanitizeWarning is not null)
            {
                logger.Warn(sanitizeWarning);
            }

            ModRepository loaded = new(
                indexPath, mods, document.NextId, loadWarning: sanitizeWarning, logger: logger);
            logger.Info($"已加载 MOD 索引：{loaded._mods.Count} 条（{indexPath}）");
            return loaded;
        }

        // 兼容只有数组的旧格式
        List<ModRecord>? legacy = TryDeserialize<List<ModRecord>>(json);
        if (legacy is not null)
        {
            List<ModRecord> mods = Sanitize(legacy);
            int dropped = legacy.Count - mods.Count;
            string? sanitizeWarning = dropped > 0
                ? BuildSanitizeWarning(dropped, indexPath)
                : null;
            if (sanitizeWarning is not null)
            {
                logger.Warn(sanitizeWarning);
            }

            ModRepository loaded = new(
                indexPath, mods, 1, loadWarning: sanitizeWarning, logger: logger);
            logger.Info($"已加载旧数组格式 MOD 索引：{loaded._mods.Count} 条（{indexPath}）");
            return loaded;
        }

        // 损坏/半截/非索引 JSON：保留原件再返回空仓库，绝不能让它被后续 Save 覆盖。
        string? badPath = TryMoveCorruptIndexAside(indexPath, logger);
        if (badPath is not null)
        {
            string warning =
                $"MOD 索引（{indexPath}）已损坏，无法解析。原文件已保留为：{badPath}。" +
                "请从同目录的 index.json.bak 恢复，或重新安装 MOD；本次操作不会覆盖损坏文件。";
            logger.Error($"MOD 索引损坏，已保留原件：{badPath}（原路径 {indexPath}）");
            return new ModRepository(
                indexPath,
                [],
                1,
                warning,
                corruptIndexRecovered: true,
                logger: logger);
        }

        // 连改名备份都失败（被占用/目录只读）也不阻断启动：原文件保持原样并拒绝后续覆盖，
        // 让用户还有机会手工恢复，而不是程序起不来。
        string unpreservedWarning =
            $"MOD 索引（{indexPath}）已损坏，无法解析，且暂时无法改名备份（可能被其它程序占用或目录只读）。" +
            "本次已按空列表启动，损坏文件仍原样保留；保存前会再次尝试备份，失败则拒绝覆盖该文件。" +
            "请关闭占用程序或修复目录权限后重启。";
        logger.Error($"MOD 索引损坏且无法改名保留，已按空列表启动：{indexPath}");
        return new ModRepository(
            indexPath,
            [],
            1,
            unpreservedWarning,
            corruptIndexUnpreserved: true,
            logger: logger);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 只返回当前高水位：与随后的 <see cref="Add"/> 不是原子对——多线程各自「<see cref="NextId"/> → <see cref="Add"/>」
    /// 可能拿到同一个 ID，先到者成功、其余 <see cref="Add"/> 抛 <see cref="InvalidOperationException"/>（绝不静默复用）。
    /// 需要并发分配 ID 时调用方必须串行化这一对调用。
    /// </remarks>
    public int NextId()
    {
        lock (_gate)
        {
            return _nextId;
        }
    }

    /// <summary>取下一个全局排序号（跨游戏的最大 <see cref="ModRecord.Index"/> + 1）。</summary>
    /// <returns>全局下一个排序号。</returns>
    /// <remarks>只读取当前值的快照，加锁保证与并发增删/重排不会读到半更新集合；本身不修改排序号。</remarks>
    public int NextIndex()
    {
        lock (_gate)
        {
            return _mods.Count == 0 ? 1 : _mods.Max(m => m.Index) + 1;
        }
    }

    /// <summary>取指定游戏内下一个排序号（各游戏从 1 独立编号）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <returns>该游戏内最大 <see cref="ModRecord.Index"/> + 1；该游戏还没有记录时为 1。</returns>
    /// <remarks>只读取当前值的快照；与调用方自己的「取号 → 改记录」组合不是原子操作，多线程下仍需调用方串行化。</remarks>
    public int NextIndexFor(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        lock (_gate)
        {
            // 直接对过滤序列取最大，避免每次调用都分配临时列表。
            return NextIndexIn(_mods.Where(m => m.BelongsTo(gameId)));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 返回仓储内的活记录（不是副本）：修改它的字段会改到仓储状态，并由下一次 <see cref="Save"/> 落盘；
    /// 记录被 <see cref="Remove"/> 后，此前拿到的引用只是孤儿。并发写同一条记录时，读取方可能读到字段的混合状态，
    /// 因此读取也需要与写线程串行化（见类 remarks 的线程模型）。
    /// </remarks>
    public ModRecord? Find(int id)
    {
        lock (_gate)
        {
            return _mods.FirstOrDefault(m => m.Id == id);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="record"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// <para>传入实例被直接放进仓储（不是复制），之后 <see cref="Find"/> / <see cref="Mods"/> 返回的就是它；
    /// <see cref="Add"/> 返回后调用方不得再把该实例交给其它线程并发修改。</para>
    /// <para><see cref="ModRecord.Index"/> 小于等于 0 时由仓储在锁内分配（所属游戏末尾 / 未归属用全局序号），
    /// 因此会修改传入记录；显式传入的非 0 Index 原样保留并直接追加（顺序由调用方负责）。</para>
    /// <para>ID 重复时抛异常且不改变仓储；失败/删除都不会让 <see cref="NextId"/> 回退。</para>
    /// </remarks>
    public void Add(ModRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (_mods.Exists(m => m.Id == record.Id))
            {
                throw new InvalidOperationException($"MOD ID 重复: {record.Id}");
            }

            if (record.Index <= 0)
            {
                // 各游戏的列表顺序独立：新记录默认排在所属游戏的末尾。
                record.Index = string.IsNullOrWhiteSpace(record.GameId)
                    ? NextIndex()
                    : NextIndexFor(record.GameId);
            }

            _mods.Add(record);
            _nextId = Math.Max(_nextId, record.Id + 1);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 移除只影响仓储；调用方此前拿到的记录引用变成孤儿（再改它不影响仓储），被移除的 ID 不会被复用。
    /// 与其它成员并发安全：查找与移除在同一把锁内完成。
    /// </remarks>
    public bool Remove(int id)
    {
        lock (_gate)
        {
            ModRecord? record = _mods.FirstOrDefault(m => m.Id == id);
            return record is not null && _mods.Remove(record);
        }
    }

    /// <summary>把一条未归属的旧记录分配到指定游戏（迁移旧数据用；不自动保存）。</summary>
    /// <param name="id">MOD 持久 ID。</param>
    /// <param name="gameId">目标游戏稳定 ID。</param>
    /// <returns>分配后放进仓储的活记录（由旧记录整体复制而来；<see cref="ModRecord.GameId"/> 是 init 属性，无法原地修改）。</returns>
    /// <remarks>
    /// <para>用 <see cref="ModRecord.WithGame"/> 整体复制，其余字段（含安装源）原样保留；仓储里旧实例被新实例替换，
    /// 调用方此前持有的旧引用随之变成孤儿，后续读取请重新 <see cref="Find"/>。</para>
    /// <para>分配与排序号计算都在锁内完成；返回的实例就是仓储内的活记录（并发修改限制见类 remarks 的线程模型）。</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="InvalidOperationException">记录不存在，或已经归属某个游戏。</exception>
    public ModRecord AssignGame(int id, string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        lock (_gate)
        {
            int position = _mods.FindIndex(m => m.Id == id);
            if (position < 0)
            {
                throw new InvalidOperationException($"MOD 不存在: {id}");
            }

            ModRecord existing = _mods[position];
            if (!string.IsNullOrWhiteSpace(existing.GameId))
            {
                throw new InvalidOperationException($"MOD 已归属游戏 {existing.GameId}: {id}");
            }

            ModRecord assigned = existing.WithGame(gameId, NextIndexFor(gameId));
            _mods[position] = assigned;
            Normalize();
            return assigned;
        }
    }

    /// <summary>只重排指定游戏内的 <see cref="ModRecord.Index"/>（各游戏顺序互不影响）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="idsInOrder">该游戏期望的 ID 顺序；不属于该游戏或不存在的 ID 会被忽略。</param>
    /// <remarks>
    /// <para>整次重排在同一把锁内完成：按 <paramref name="idsInOrder"/> 顺序把该游戏记录的 Index 改为 1..N 后排序。
    /// 未出现在入参里的该游戏记录保留原 Index，可能与新分配的号重复（调用方应传该游戏的完整顺序，如 <see cref="ModsFor"/> 快照）。</para>
    /// <para><paramref name="idsInOrder"/> 在锁内枚举；调用方应传已物化的列表/数组，不要传依赖其它线程状态的惰性序列。
    /// 就地修改的是仓储内的活记录，调用方在重排期间不要并发读写同一条记录（见类 remarks 的线程模型）。</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="idsInOrder"/> 为 <see langword="null"/>。</exception>
    public void ReorderForGame(string gameId, IEnumerable<int> idsInOrder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(idsInOrder);

        lock (_gate)
        {
            // 锁内先建 ID 索引：避免对每个 id 都线性 Find（P-13）。
            Dictionary<int, ModRecord> byId = new(_mods.Count);
            foreach (ModRecord record in _mods)
            {
                byId[record.Id] = record;
            }

            int index = 1;
            foreach (int id in idsInOrder)
            {
                if (byId.TryGetValue(id, out ModRecord? record) && record.BelongsTo(gameId))
                {
                    record.Index = index++;
                }
            }

            Normalize();
        }
    }

    /// <inheritdoc />
    /// <exception cref="IOException">
    /// 写入失败（文件被占用、I/O 错误等）；索引损坏且无法先改名备份时也会抛出，以保证不覆盖未备份的原件。
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">没有写入权限（原样向上抛出）。</exception>
    /// <remarks>
    /// <para>同一实例的并发保存由 <c>_saveGate</c> 串行化：锁内先取「记录列表 + NextId」快照，序列化与落盘在锁外完成；
    /// 写入使用同目录的 GUID 临时名 + <see cref="FileStream.Flush(bool)"/> 刷盘 + <see cref="File.Replace(string, string, string?)"/> 原子替换。
    /// 因此并发 <see cref="Add"/>/<see cref="Remove"/>/<see cref="AssignGame"/>/<see cref="ReorderForGame"/> 不会让本方法
    /// 写出「一半」的结果：与保存并发的变更要么进本次文件、要么留给下一次保存（变更已在内存生效，不会丢）。</para>
    /// <para><b>不保护记录字段：</b>锁只护容器与落盘，<see cref="ModRecord"/> 的字段由调用方负责串行化。若其它线程正在就地
    /// 修改记录（Enabled/Metadata/RelativeFiles 等），本次保存可能捕获到半更新状态；正确用法是同一时刻只有一个写线程
    /// （见类 remarks 的线程模型）。</para>
    /// <para>保护锁只覆盖「取快照」的瞬间；序列化与 fsync 在锁外完成，读者不会被慢盘/杀软扫描长期阻塞。
    /// 并发保存由独立的保存门串行化，旧快照不会覆盖新快照。本方法不会自动排序/去重，也不会替调用方重试。</para>
    /// </remarks>
    public void Save()
    {
        // 并发保存串行化（保证旧快照不会覆盖新快照）；锁内只取快照，序列化与 fsync 在 _gate 之外，
        // 避免 UI 线程保存大索引时把 Find/ModsFor 全部阻塞数百毫秒（N-16）。
        lock (_saveGate)
        {
            try
            {
                HandleCorruptIndexBeforeSave();

                IndexDocument document;
                int count;
                lock (_gate)
                {
                    document = new IndexDocument
                    {
                        SchemaVersion = CurrentSchemaVersion,
                        NextId = _nextId,
                        Mods = [.. _mods],
                    };
                    count = _mods.Count;
                }

                string json = JsonSerializer.Serialize(document, JsonOptions);
                WriteIndexAtomically(json);
                _logger.Info($"已保存 MOD 索引：{count} 条（{IndexPath}）");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error($"保存 MOD 索引失败：{IndexPath}", ex);
                throw;
            }
        }
    }

    /// <summary>保存前处理「本次会话从损坏索引降级启动」的保护逻辑。</summary>
    /// <exception cref="IOException">用户已恢复有效索引（拒绝覆盖），或无法保留损坏原件。</exception>
    private void HandleCorruptIndexBeforeSave()
    {
        bool recovered;
        lock (_gate)
        {
            recovered = _corruptIndexRecovered;
        }

        if (!recovered)
        {
            return;
        }

        if (!File.Exists(IndexPath))
        {
            // 损坏原件已被外部删除：没有需要保护的内容，清掉标志正常写新索引。
            SetCorruptIndexFlags(recovered: false);
            return;
        }

        if (IsValidIndexFile(IndexPath))
        {
            // 用户可能刚把 .bak 恢复成 index.json：此时不能再用当前空仓库覆盖恢复的索引。
            throw new IOException(
                $"检测到 MOD 索引（{IndexPath}）已被恢复或替换为有效文件。"
                + "为避免用当前会话的空仓库覆盖它，请重启程序后再操作。");
        }

        string? badPath = TryMoveCorruptIndexAside(IndexPath, _logger);
        if (badPath is null)
        {
            throw new IOException(
                $"保存 MOD 索引失败：原索引已损坏且无法改名备份，拒绝覆盖未备份的原文件：{IndexPath}");
        }

        SetCorruptIndexFlags(recovered: false);
        _logger.Warn($"损坏的 MOD 索引已保留为：{badPath}");
    }

    private void SetCorruptIndexFlags(bool recovered)
    {
        lock (_gate)
        {
            _corruptIndexRecovered = recovered;
        }
    }

    /// <summary>写同目录 GUID 临时文件并刷盘，再原子替换成索引文件（失败清理临时文件）。</summary>
    /// <param name="json">已序列化的索引内容。</param>
    /// <exception cref="IOException">写入/替换失败。</exception>
    /// <exception cref="UnauthorizedAccessException">没有写权限。</exception>
    private void WriteIndexAtomically(string json)
    {
        string? directory = Path.GetDirectoryName(IndexPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 临时名带 GUID：并发保存/上次崩溃残留的 .tmp 不会互相踩。
        string temp = $"{IndexPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                // Flush(true) 把文件内容刷到磁盘，减少掉电/崩溃后索引变成半截文件。
                stream.Flush(flushToDisk: true);
            }

            // 先把当前索引留成 .bak，再原子替换；File.Replace 找不到目标时退化为首次 Move。
            if (File.Exists(IndexPath))
            {
                File.Replace(temp, IndexPath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, IndexPath);
            }
        }
        catch
        {
            SystemFileSystem.Instance.TryDeleteFile(temp);
            throw;
        }
    }

    private static T? TryDeserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// 清洗反序列化结果：JSON 里的 <c>null</c> 元素/null 字段会绕开属性初始化器，
    /// 必须在构造仓库前修补，否则 <see cref="ModRecord.DisplayName"/> 等使用点会 NRE（C-04）。
    /// </summary>
    /// <remarks>
    /// 同时丢弃无效主键（Id≤0、重复 Id）、过滤 <see cref="ModRecord.RelativeFiles"/> 内的
    /// null/空白元素，并把非正 <see cref="ModRecord.Index"/> 追加到列表末尾。
    /// </remarks>
    /// <param name="mods">反序列化得到的记录列表，元素可能为 <see langword="null"/>。</param>
    /// <returns>过滤并修补后的新列表。</returns>
    private static List<ModRecord> Sanitize(List<ModRecord>? mods)
    {
        if (mods is null)
        {
            return [];
        }

        List<ModRecord> cleaned = new(mods.Count);
        HashSet<int> ids = [];
        int maxIndex = 0;
        foreach (ModRecord? mod in mods)
        {
            if (mod is null || mod.Id <= 0 || !ids.Add(mod.Id))
            {
                continue;
            }

            mod.Metadata ??= new ModMetadata();
            mod.RelativeFiles ??= [];
            // 整体替换清单实例而不是原地 RemoveAll，保证按清单实例缓存的内容戳/目录集合不会读到旧内容。
            if (mod.RelativeFiles.Any(string.IsNullOrWhiteSpace))
            {
                mod.RelativeFiles = [.. mod.RelativeFiles.Where(file => !string.IsNullOrWhiteSpace(file))];
            }
            maxIndex = Math.Max(maxIndex, mod.Index);
            cleaned.Add(mod);
        }

        foreach (ModRecord mod in cleaned)
        {
            if (mod.Index <= 0)
            {
                mod.Index = ++maxIndex;
            }
        }

        return cleaned;
    }

    /// <summary>清洗丢弃记录时的加载警告：明确告知用户记录已从界面消失，且下次保存会写回清洗后的列表。</summary>
    /// <param name="dropped">被丢弃的记录数。</param>
    /// <param name="indexPath">索引文件路径。</param>
    /// <returns>面向用户的警告文案。</returns>
    private static string BuildSanitizeWarning(int dropped, string indexPath) =>
        $"MOD 索引含 {dropped} 条无效记录（null/重复 Id/Id≤0），已在加载时忽略；"
        + $"下次保存会把清洗后的列表写回 {indexPath}，如需保留原始记录请先备份该文件。";

    /// <summary>判断磁盘上的索引文件当前是否是有效索引（用于识别用户手工恢复的 .bak）。</summary>
    /// <param name="path">索引文件路径。</param>
    /// <returns>能解析成索引文档或旧数组格式时返回 <see langword="true"/>。</returns>
    private static bool IsValidIndexFile(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            return TryDeserialize<IndexDocument>(json)?.Mods is not null
                || TryDeserialize<List<ModRecord>>(json) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不出来就不能确认是恢复文件：按损坏处理，后续改名备份失败会拒绝覆盖。
            return false;
        }
    }

    /// <summary>尝试把损坏的索引重命名成带时间戳的 <c>.bad-*</c> 文件（不覆盖已有 bad 文件）。</summary>
    /// <param name="indexPath">索引路径。</param>
    /// <param name="logger">日志端口。</param>
    /// <returns>成功时返回保留后的 <c>.bad-*</c> 路径；改名失败（被占用、目录只读等）时返回 <see langword="null"/>，不抛异常。</returns>
    private static string? TryMoveCorruptIndexAside(string indexPath, IAppLogger logger)
    {
        string? badPath = CorruptFileQuarantine.TryMoveAside(indexPath, SystemFileSystem.Instance, out Exception? error);
        if (badPath is null)
        {
            // 保留失败不能让启动失败：调用方降级为空仓库 + LoadWarning，并在 Save 前重试。
            logger.Error($"保留损坏索引文件失败（可能被占用）：{indexPath}", error);
            return null;
        }

        // 索引的隔离文件同样限制保留份数，避免长期累积。
        CorruptFileQuarantine.Prune(
            indexPath,
            SystemFileSystem.Instance,
            badPath,
            logger.Warn);
        return badPath;
    }

    private static int NextIndexIn(IEnumerable<ModRecord> mods)
    {
        int max = 0;
        foreach (ModRecord mod in mods)
        {
            max = Math.Max(max, mod.Index);
        }

        return max + 1;
    }

    private void Normalize() => _mods.Sort((a, b) =>
    {
        int byIndex = a.Index.CompareTo(b.Index);
        // Index 相同时按 Id 兜底，保证排序稳定、顺序不漂移（C-28）。
        return byIndex != 0 ? byIndex : a.Id.CompareTo(b.Id);
    });

    private sealed class IndexDocument
    {
        public int SchemaVersion { get; set; }

        public int NextId { get; set; }

        /// <remarks>显式允许为 null，用于把「结构不对但解析成功」的 JSON 也判定为损坏。</remarks>
        public List<ModRecord>? Mods { get; set; }
    }
}
