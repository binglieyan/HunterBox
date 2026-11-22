namespace HunterBox.Application.Paks;

/// <summary>读取 pak的包头与条目表。</summary>
public interface IPakArchiveReader
{
    /// <summary>解析 pak。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <returns>包头与条目信息。</returns>
    PakArchiveInfo Read(string pakPath);
}

/// <summary>读取 pak 内单个条目的内容。</summary>
/// <remarks>
/// 生产路径不实例化：当前仅 <c>RePakEntryReader</c> 实现，保留为测试校验工具（读取条目原始内容做断言）。
/// 新生产代码不应依赖本接口。
/// </remarks>
public interface IPakEntryReader
{
    /// <summary>读取并解压一个条目。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <param name="entry">条目信息。</param>
    /// <returns>解压后的字节。</returns>
    byte[] ReadEntry(string pakPath, PakEntryInfo entry);
}
