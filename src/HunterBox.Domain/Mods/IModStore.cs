namespace HunterBox.Domain.Mods;

/// <summary>
/// MOD 记录的持久化抽象。
/// </summary>
/// <remarks>
/// <para>Domain / Application 只依赖本接口；具体实现（JSON 文件、内存、数据库）放在 Infrastructure。</para>
/// <para><b>线程契约（前半段是实现方必须满足的保证，后半段是调用方必须遵守的限制）：</b></para>
/// <list type="number">
/// <item>
/// <description><b>仓储对象自身线程安全。</b>同一实例的任意成员可被多个线程同时调用，实现必须串行化对内部记录集合、
/// 自增 ID 与落盘快照的访问：并发调用不得破坏集合、不得写坏索引、不得让 <see cref="NextId"/> 回退。
/// Infrastructure 的 JSON 实现用私有锁保护成员读改与 <see cref="Save"/> 的快照，序列化/落盘在锁外完成。</description>
/// </item>
/// <item>
/// <description><b><see cref="Mods"/> / <see cref="ModsFor"/> 返回列表快照，不是活集合。</b>每次调用都返回新的只读列表，
/// 成员组成与顺序固定于取快照的时刻；调用方可在任意线程安全枚举，之后的增删/重排不会改动已经发出的列表
/// （不会出现「集合已修改」异常）。</description>
/// </item>
/// <item>
/// <description><b>列表元素与 <see cref="Find"/> 返回的是仓储内部的活 <see cref="ModRecord"/>，不是副本。</b>
/// 就地修改记录字段（Enabled/Index/Metadata/RelativeFiles 等）会直接改到仓储状态，并由下一次 <see cref="Save"/> 落盘。
/// 需要稳定副本时由调用方自己复制（<see cref="ModRecord.WithGame"/> 是唯一的克隆入口，且只给分配游戏用）。</description>
/// </item>
/// <item>
/// <description><b><see cref="ModRecord"/> 对象本身不是线程安全的。</b>仓储的锁只保护「容器」与「落盘」，不保护记录字段：
/// 调用方不得在其它线程调用仓储成员（尤其 <see cref="Save"/>）的同时修改同一条记录，否则可能把「半更新状态」写进索引，
/// 或读到一条记录的混合字段。正确用法是「同一时刻只有一个写线程」（当前 App 由 <c>OperationRunner</c> 忙碌门禁 + UI 线程保证）。
/// 若以后要支持「后台写 + 前台读」，必须先把记录改成不可变快照（backlog 的并发专项），而不是只依赖仓储锁。</description>
/// </item>
/// <item>
/// <description><b>单次变更原子，<see cref="Save"/> 写一致快照。</b><see cref="Add"/> / <see cref="Remove"/> /
/// <c>AssignGame</c> / <c>ReorderForGame</c>（实现类扩展的写操作）各自原子生效；<see cref="Save"/> 在实现内部取一致快照后
/// 原子替换索引文件，不会写出半个 JSON，也不会只写一半成员。与 <see cref="Save"/> 并发的变更可能包含在本次文件里、
/// 也可能留给下一次 <see cref="Save"/>，但变更本身不会丢（已在内存生效，后续 <see cref="Save"/> 会带上）。
/// 并发 <see cref="Save"/> 由实现串行化，不需要调用方额外加锁。</description>
/// </item>
/// <item>
/// <description><b>ID 分配不是事务。</b><see cref="NextId"/> 只读取当前高水位，与随后的 <see cref="Add"/> 不是原子对：
/// 多线程各自「取 ID → 构造记录 → <see cref="Add"/>」可能拿到同一个 ID，先到者成功、其余抛
/// <see cref="InvalidOperationException"/>（实现必须拒绝重复 ID，绝不静默复用）。需要并发分配 ID 时，
/// 调用方必须把这一对调用串行化。</description>
/// </item>
/// <item>
/// <description><b>非阻塞读取。</b><see cref="Save"/> 只在取快照时持有实现内部锁，序列化与落盘在锁外串行化；
/// 落盘期间读者不被阻塞。调用方仍不应在 UI 线程对大批记录逐条 <see cref="Save"/>（App 已把批量操作合并成一次保存）。</description>
/// </item>
/// </list>
/// <para>实现必须保证：<see cref="NextId"/> 单调递增且不复用已删除的 ID。索引损坏时的降级/保留策略由实现决定：
/// JSON 实现会保留损坏原件、给出加载警告，并在无法先备份时拒绝覆盖，绝不静默当作空仓库。</para>
/// <para>适用范围：本契约只覆盖同一实例的多线程访问。多实例/跨进程同时写同一存储后端不在保证内
/// （App 用单实例保护，且只在启动时加载一次索引）。</para>
/// </remarks>
public interface IModStore
{
    /// <summary>按 <see cref="ModRecord.Index"/> 升序的列表快照。</summary>
    /// <remarks>
    /// <para>包含全部游戏与未归属记录；界面列表应使用 <see cref="ModsFor"/> 只取当前游戏。</para>
    /// <para>每次 getter 都返回新的只读列表（成员固定于取快照时刻），可跨线程安全枚举；元素是仓储内的活记录，不是副本——
    /// 就地修改字段会改到仓储，见接口 remarks 的线程契约。</para>
    /// <para>升序的前提是 <see cref="ModRecord.Index"/> 由 <see cref="Add"/> 自动分配或经实现类的 <c>ReorderForGame</c> 重排；
    /// 调用方显式传入非 0 <see cref="ModRecord.Index"/> 时，顺序由调用方自己保证。</para>
    /// </remarks>
    IReadOnlyList<ModRecord> Mods { get; }

    /// <summary>取指定游戏的 MOD 记录列表快照（按 <see cref="ModRecord.Index"/> 升序）。</summary>
    /// <param name="gameId">当前游戏稳定 ID。</param>
    /// <returns>仅属于该游戏的记录；未归属记录（<see cref="ModRecord.GameId"/> 为空）不会出现在任何游戏结果里。</returns>
    /// <remarks>
    /// <para>与 <see cref="Mods"/> 相同：列表是快照、元素是活记录。默认实现按 <see cref="ModRecord.BelongsTo"/>
    /// 过滤 <see cref="Mods"/> 快照；实现方可覆写以复用已排好序的缓存。</para>
    /// <para>实现必须保证多线程并发调用安全（默认实现只读 <see cref="Mods"/>，同样安全）。</para>
    /// </remarks>
    IReadOnlyList<ModRecord> ModsFor(string gameId) =>
        [.. Mods.Where(m => m.BelongsTo(gameId))];

    /// <summary>加载索引时的可读告警（如损坏索引已保留原件）；正常加载时返回 <see langword="null"/>。</summary>
    /// <remarks>默认实现返回 <see langword="null"/>；需要暴露加载问题的实现覆写本属性，由界面提示用户。</remarks>
    string? LoadWarning => null;

    /// <summary>没有归属游戏的记录快照（<see cref="ModRecord.GameId"/> 为空）。</summary>
    /// <remarks>默认实现从 <see cref="Mods"/> 过滤；元素与 <see cref="Mods"/> 一样是仓储内的活记录。</remarks>
    IReadOnlyList<ModRecord> UnassignedMods =>
        [.. Mods.Where(record => string.IsNullOrWhiteSpace(record.GameId))];

    /// <summary>把未归属记录分配给指定游戏（实现可用新实例替换旧实例）。</summary>
    /// <param name="id">MOD 持久 ID。</param>
    /// <param name="gameId">目标游戏稳定 ID。</param>
    /// <returns>分配后的记录。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    /// <exception cref="InvalidOperationException">记录不存在，或已经归属某个游戏。</exception>
    ModRecord AssignGame(int id, string gameId);

    /// <summary>只重排指定游戏内的 <see cref="ModRecord.Index"/>（各游戏顺序互不影响）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="idsInOrder">该游戏期望的 ID 顺序；不属于该游戏或不存在的 ID 会被忽略。</param>
    /// <exception cref="ArgumentNullException"><paramref name="idsInOrder"/> 为 <see langword="null"/>。</exception>
    void ReorderForGame(string gameId, IEnumerable<int> idsInOrder);

    /// <summary>取下一个可用 ID（不回收已删除的号）。</summary>
    /// <returns>尚未被使用的 ID。</returns>
    /// <remarks>
    /// <para>只读取当前高水位：返回值单调不减，删除记录不会让号回退或复用。</para>
    /// <para>与随后的 <see cref="Add"/> 不是原子对；多线程同时分配 ID 时调用方必须串行化（见接口 remarks 的 ID 契约）。</para>
    /// </remarks>
    int NextId();

    /// <summary>按 ID 查找。</summary>
    /// <param name="id">MOD 持久 ID。</param>
    /// <returns>找到的活记录（仓储内部实例，不是副本）；不存在时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 返回的引用在记录存在期间始终指向仓储内的同一条记录：修改它的字段会改到仓储状态（见接口 remarks 的线程契约）。
    /// 记录被 <see cref="Remove"/> 后，此前拿到的引用只是孤儿，再修改它不影响仓储。
    /// 并发写同一条记录时，读取方可能读到字段的混合状态——读取也需要与写线程串行化。
    /// </remarks>
    ModRecord? Find(int id);

    /// <summary>加入一条新记录（不自动保存）。</summary>
    /// <param name="record">待加入的记录；加入后归仓储所有（不是复制）。</param>
    /// <exception cref="InvalidOperationException">ID 与已有记录重复。</exception>
    /// <remarks>
    /// <para>传入的实例被直接放进仓储，之后 <see cref="Find"/> / <see cref="Mods"/> 返回的就是它；调用方在
    /// <see cref="Add"/> 返回后不得再把该实例交给其它线程并发修改。</para>
    /// <para><see cref="ModRecord.Index"/> 小于等于 0 时由仓储分配（所属游戏末尾 / 未归属记录用全局序号），
    /// 因此 <see cref="Add"/> 可能修改传入记录；显式传入的非 0 Index 原样保留，记录直接追加（顺序由调用方负责）。</para>
    /// <para>ID 重复时抛异常且不改变仓储；失败/删除都不会让 <see cref="NextId"/> 回退。与其它成员并发安全。</para>
    /// </remarks>
    void Add(ModRecord record);

    /// <summary>按 ID 移除记录（不自动保存）。</summary>
    /// <param name="id">MOD 持久 ID。</param>
    /// <returns>确实移除时返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 移除只影响仓储；调用方此前拿到的记录引用会变成孤儿（再改它不影响仓储）。被移除的 ID 不会复用。
    /// 与其它成员并发安全（接口线程契约）。
    /// </remarks>
    bool Remove(int id);

    /// <summary>把所有变更落盘。</summary>
    /// <remarks>
    /// <para>实现取「当前仓储状态的一致快照」后原子替换索引文件（临时文件 + 原子替换，不会留下半截 JSON），
    /// 与任何单次变更都不会写出「一半」的结果；并发 <see cref="Save"/> 由实现串行化。
    /// 与 <see cref="Save"/> 并发的变更要么包含在本次文件里、要么留给下一次 <see cref="Save"/>，不会丢。</para>
    /// <para>调用方必须保证「记录字段的就地修改」不在其它线程与 <see cref="Save"/> 并发发生（接口线程契约）；
    /// 仓储锁不保护 <see cref="ModRecord"/> 的字段。<see cref="Save"/> 不会自动排序/去重，也不会替调用方重试；
    /// 落盘在锁外完成，读者不被阻塞。</para>
    /// <para>失败语义由实现定义：JSON 实现只抛 IO 类异常（<c>IOException</c> / <c>UnauthorizedAccessException</c>），
    /// 并保证不覆盖无法先备份的损坏索引。</para>
    /// </remarks>
    void Save();
}
