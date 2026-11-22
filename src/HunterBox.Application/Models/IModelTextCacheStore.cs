namespace HunterBox.Application.Models;

/// <summary>持久化的模型备注文本缓存项。</summary>
/// <param name="ModId">MOD ID。</param>
/// <param name="Signature">写入时的文本签名（描述 + 文件清单的 SHA-256 摘要）。</param>
/// <param name="Text">备注文本。</param>
/// <param name="PakStamp">写入时根层 pak 的「文件名 + 字节数 + 最后写入时间」戳；无 pak 时为空串。</param>
/// <param name="GameId">所属游戏稳定 ID；用于切换游戏时只裁剪当前游戏的缓存。</param>
public sealed record ModelTextCacheEntry(
    int ModId,
    string Signature,
    string Text,
    string PakStamp = "",
    string GameId = "");

/// <summary>
/// 模型备注文本缓存的持久化端口：跨会话复用预热结果，签名不匹配时由调用方重算。
/// </summary>
/// <remarks>
/// 读写失败不得抛异常：读取失败返回 <see langword="false"/>（由调用方禁用本会话写盘，避免覆盖仍在磁盘上的缓存），
/// 写入返回 <see langword="false"/> 并记录日志，由调用方决定是否重试。
/// </remarks>
public interface IModelTextCacheStore
{
    /// <summary>
    /// 读取上次会话保存的缓存项。
    /// </summary>
    /// <remarks>
    /// 文件不存在、格式版本不受支持属于正常情况，返回 <see langword="true"/> 与空列表（后续可写入新格式）；
    /// 基础文件损坏/过大但成功隔离（改名保留）时同样返回 <see langword="true"/>，允许本次整份重写；
    /// 增量日志过大、损坏或读取失败只丢弃增量并返回 <see langword="true"/>，不影响基础数据；
    /// 写基础失败但保留原日志等待下次重放时同样返回 <see langword="true"/>。
    /// 只有基础文件读取 IO 失败或基础文件隔离也失败（被占用/无权限）时才返回 <see langword="false"/>，
    /// 调用方据此禁用本会话写盘。
    /// 调用方不需要区分「完全成功」与「保留日志待重放」：两者都可以继续使用内存状态并允许后续保存重试。
    /// </remarks>
    /// <param name="entries">读到的缓存项；失败时为空列表。</param>
    /// <returns>读取成功（包括「没有缓存」）时为 <see langword="true"/>。</returns>
    bool TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries);

    /// <summary>
    /// 保存本次会话的最新快照（基础文件 + 增量日志；不是每次整份覆盖）。
    /// 首次整份写免等待；其后只有真实整份写按 8MB/s 节流，单次 Save 累计等待默认封顶 10 秒且可注入。
    /// </summary>
    /// <remarks>
    /// 调用顺序：先 <see cref="TryLoad"/> 让实现建立基础快照，再调用本方法追加变化；
    /// 未成功装载基础文件时实现会整份重写基础文件。
    /// 实现会持有 <paramref name="entries"/> 引用做差异比较；调用方在返回前不得原地修改列表或元素。
    /// 追加失败是本次保存的瞬时结果：实现会退回整份写，下一次保存仍会重新尝试追加。
    /// 旧日志未能删除/改名/截断时进入清理待办，增量写仍可继续；仅追加日志的保存不等待，
    /// 零变化但日志达到压缩阈值仍会整份写并等待。
    /// 等待在锁外进行，窗口内等待至间隔满足后写入，不会用 <see langword="false"/> 表示延后。
    /// 追加成功或数据已落在增量日志中即返回 <see langword="true"/>；
    /// 只有追加与整份写都失败、未成功装载基础文件时整份写失败，或日志接近硬上限且重写失败时才返回
    /// <see langword="false"/>。
    /// </remarks>
    /// <param name="entries">要保存的完整快照（实现自行计算差异）。</param>
    /// <returns>数据已落盘或仍在日志中时为 <see langword="true"/>。</returns>
    bool Save(IReadOnlyList<ModelTextCacheEntry> entries);
}
