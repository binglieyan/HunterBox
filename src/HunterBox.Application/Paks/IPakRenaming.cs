namespace HunterBox.Application.Paks;

/// <summary>重命名 pak 内的条目（只改条目表里的哈希，不动数据区）。</summary>
public interface IPakEntryRenamer
{
    /// <summary>把某个条目的哈希改成新的值。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <param name="entryIndex">条目在表中的序号（从 0 开始，与 <c>PakArchiveInfo.Entries</c> 一致）。</param>
    /// <param name="newHash">新的组合哈希（见 <see cref="IPakPathHasher"/>）。</param>
    /// <param name="backup">是否保留 TOC 前像 <c>&lt;pak&gt;.toc.bak</c>（写入日志始终生成，用于崩溃恢复）。</param>
    void RenameEntry(string pakPath, int entryIndex, ulong newHash, bool backup = true);
}

/// <summary>按引擎规则计算路径的组合哈希。</summary>
public interface IPakPathHasher
{
    /// <summary>计算路径的组合哈希。</summary>
    /// <param name="path">相对路径。</param>
    /// <returns>组合哈希。</returns>
    ulong HashPath(string path);
}
