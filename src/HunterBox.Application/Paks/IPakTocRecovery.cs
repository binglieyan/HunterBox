namespace HunterBox.Application.Paks;

/// <summary>
/// pak 条目表前像的自动恢复能力（原地写 TOC 被中断后的兜底）。
/// </summary>
/// <remarks>
/// 改名实现可以选择实现本接口：调用方在 pak 读取失败或再次改写前先尝试恢复，
/// 避免用户手工把 <c>.toc.bak</c> 拼回 pak。<see cref="TryRecover"/> 必须只恢复
/// 与当前 pak 指纹一致的前像，绝不把旧 pak 的条目表写到新 pak 上。
/// </remarks>
public interface IPakTocRecovery
{
    /// <summary>只处理未提交日志（未完成写入）：存在且指纹匹配时恢复条目表。</summary>
    /// <param name="pakPath">pak 文件绝对路径。</param>
    /// <param name="message">成功时的说明；无可恢复内容时为空字符串。</param>
    /// <returns>确实恢复了条目表时返回 <see langword="true"/>。</returns>
    /// <exception cref="IOException">pak 无法读写；未提交日志已保留。</exception>
    bool TryRecoverPending(string pakPath, out string message);

    /// <summary>尝试用前像恢复 pak 的条目表（未提交日志优先，其次在 TOC 结构损坏时用已提交前像）。</summary>
    /// <param name="pakPath">pak 文件绝对路径。</param>
    /// <param name="message">成功时的说明（供日志/界面展示）；失败为空字符串。</param>
    /// <returns>确实恢复了条目表时返回 <see langword="true"/>。</returns>
    /// <exception cref="IOException">pak 无法读写；未提交日志已保留。</exception>
    bool TryRecover(string pakPath, out string message);

    /// <summary>获取只读锁：在读取条目表期间阻止并发的原地写入。</summary>
    /// <param name="pakPath">pak 文件绝对路径。</param>
    /// <returns>释放后允许写入；实现不支持时返回 <see langword="null"/>。</returns>
    IDisposable? AcquireReadLock(string pakPath);
}
