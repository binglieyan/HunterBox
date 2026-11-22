namespace HunterBox.Application.Paks;

/// <summary>一次条目哈希改写请求。</summary>
/// <param name="EntryIndex">条目在 TOC 中的序号（从 0 开始，与 <c>PakArchiveInfo.Entries</c> 一致）。</param>
/// <param name="NewHash">新的组合哈希（见引擎路径哈希约定）。</param>
public readonly record struct PakEntryRename(int EntryIndex, ulong NewHash);

/// <summary>
/// 「一次提交多条改名」的 pak 条目改名能力：实现可以只读一次 TOC、内存改完后单次写回 + 一次 flush，
/// 避免调用方逐条调用 <see cref="IPakEntryRenamer.RenameEntry"/> 造成 O(n²) 的 TOC 重读与落盘（P-02）。
/// </summary>
/// <remarks>
/// 生产实现见 <c>HunterBox.Infrastructure.Paks.RePakRenamer</c>；不具备批量能力的实现仍可逐条调用，
/// 因此调用方按「支持则批量、否则逐条」降级（见 <c>PakModelRewriteService</c>）。
/// </remarks>
public interface IBatchPakEntryRenamer
{
    /// <summary>批量改写条目哈希：先整体校验，再一次性写回并 flush。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <param name="renames">要改写的条目及新哈希；为空时不做任何事（不备份、不读 TOC）。</param>
    /// <param name="backup">是否保留 TOC 前像 <c>&lt;pak&gt;.toc.bak</c>（写入日志始终生成，用于崩溃恢复）。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一条目序号越界。</exception>
    /// <exception cref="InvalidDataException">pak 结构不合法或 TOC 解密失败。</exception>
    void RenameEntries(string pakPath, IReadOnlyList<PakEntryRename> renames, bool backup = true);
}
