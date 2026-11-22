namespace HunterBox.Application.Models;

/// <summary>模型文本缓存的写盘快照：条目与产生快照时的缓存版本。</summary>
/// <remarks>
/// 版本只由写入/移除/裁剪等实际改变条目的操作递增；<c>Load</c> 装载磁盘内容不递增。
/// 调度器据此跳过「无变化」的重复写盘。
/// </remarks>
/// <param name="Entries">当前内存条目快照。</param>
/// <param name="Version">缓存版本号。</param>
public readonly record struct ModelTextCacheSnapshot(IReadOnlyList<ModelTextCacheEntry> Entries, long Version);
