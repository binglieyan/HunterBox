namespace HunterBox.Application.Common;

/// <summary>
/// 文件系统端口：用例只依赖本接口做删除/清理，具体实现由 Infrastructure 提供（A-29/A-02）。
/// </summary>
/// <remarks>
/// <para>
/// 仅 <c>Try*</c> 系列返回 <see langword="false"/> 表示失败；严格删除（<see cref="DeleteFile"/>、
/// <see cref="DeleteDirectory"/>、<see cref="DeleteEmptyDirectory"/>）保持 BCL 语义，失败时抛
/// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>。
/// 强制删除会先清只读属性并支持取消；链接（含断链）只删除链接本身，子树含链接或安全扫描失败时整体跳过（fail-closed）。
/// </para>
/// <para>
/// 读写方法保持 BCL 语义：失败时抛出对应异常（<see cref="IOException"/> /
/// <see cref="UnauthorizedAccessException"/>），由用例决定是否转为用户提示。
/// </para>
/// </remarks>
public interface IFileSystem
{
    /// <summary>文件是否存在。断链的文件链接返回 <see langword="false"/>（只看链接目标）。</summary>
    /// <param name="path">文件路径，可为 <see langword="null"/>。</param>
    /// <returns>存在时返回 <see langword="true"/>。</returns>
    bool FileExists(string? path);

    /// <summary>目录是否存在（链接按存在处理）。</summary>
    /// <param name="path">目录路径，可为 <see langword="null"/>。</param>
    /// <returns>存在时返回 <see langword="true"/>。</returns>
    bool DirectoryExists(string? path);

    /// <summary>创建目录（含父目录）；已存在时为空操作。</summary>
    /// <param name="path">目录路径。</param>
    /// <exception cref="IOException">创建失败。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限。</exception>
    void CreateDirectory(string path);

    /// <summary>复制文件。</summary>
    /// <param name="source">源文件。</param>
    /// <param name="destination">目标文件。</param>
    /// <param name="overwrite">目标已存在时是否覆盖。</param>
    /// <exception cref="IOException">复制失败或目标已存在且不允许覆盖。</exception>
    void CopyFile(string source, string destination, bool overwrite = false);

    /// <summary>移动文件（可跨卷复制后删除）。</summary>
    /// <param name="source">源文件。</param>
    /// <param name="destination">目标文件。</param>
    /// <param name="overwrite">目标已存在时是否覆盖。</param>
    /// <exception cref="IOException">移动失败。</exception>
    void MoveFile(string source, string destination, bool overwrite = false);

    /// <summary>移动目录（同卷改名；目标已存在时抛出）。</summary>
    /// <param name="source">源目录。</param>
    /// <param name="destination">目标目录。</param>
    /// <exception cref="IOException">移动失败或目标已存在。</exception>
    void MoveDirectory(string source, string destination);

    /// <summary>严格删除文件（失败抛异常；链接只删链接本身）。</summary>
    /// <param name="path">文件路径。</param>
    /// <exception cref="IOException">删除失败。</exception>
    void DeleteFile(string path);

    /// <summary>递归删除目录（失败抛异常；链接只删链接本身）。</summary>
    /// <param name="path">目录路径。</param>
    /// <exception cref="IOException">删除失败。</exception>
    void DeleteDirectory(string path);

    /// <summary>严格删除空目录（非递归；目录非空时抛异常，绝不递归删除新增内容）。</summary>
    /// <remarks>
    /// 供「先判空、再删除」的清理路径使用：判空与删除之间若被写入新文件，
    /// 非递归删除会抛 <see cref="IOException"/>，由调用方降级为警告并保留文件。
    /// </remarks>
    /// <param name="path">目录路径。</param>
    /// <exception cref="IOException">目录非空或删除失败。</exception>
    void DeleteEmptyDirectory(string path);

    /// <summary>打开文件读取（共享读）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>只读流，调用方负责释放。</returns>
    /// <exception cref="IOException">文件不存在或无法打开。</exception>
    Stream OpenRead(string path);

    /// <summary>打开文件写入（创建或截断，独占写）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>可写流，调用方负责释放。</returns>
    /// <exception cref="IOException">无法创建或写入。</exception>
    Stream OpenWrite(string path);

    /// <summary>读取文本（UTF-8）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>文件内容。</returns>
    /// <exception cref="IOException">读取失败。</exception>
    string ReadAllText(string path);

    /// <summary>写入文本（UTF-8，无 BOM；覆盖写入）。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="contents">内容。</param>
    /// <exception cref="IOException">写入失败。</exception>
    void WriteAllText(string path, string contents);

    /// <summary>枚举文件（不保证顺序）。</summary>
    /// <param name="directory">目录。</param>
    /// <param name="searchPattern">匹配模式。</param>
    /// <param name="searchOption">是否递归。</param>
    /// <returns>文件全路径数组。</returns>
    /// <exception cref="IOException">枚举失败。</exception>
    string[] EnumerateFiles(
        string directory,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly);

    /// <summary>惰性枚举文件（不保证顺序），调用方可逐项检查取消。</summary>
    /// <remarks>与 <see cref="EnumerateFiles"/> 不同，本方法不在调用时物化全部路径；异常在迭代过程中抛出。</remarks>
    /// <param name="directory">目录。</param>
    /// <param name="searchPattern">匹配模式。</param>
    /// <param name="searchOption">是否递归。</param>
    /// <returns>文件全路径序列。</returns>
    /// <exception cref="IOException">枚举失败（迭代时抛出）。</exception>
    IEnumerable<string> EnumerateFilesLazy(
        string directory,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly);

    /// <summary>枚举目录（不保证顺序）。</summary>
    /// <param name="directory">目录。</param>
    /// <param name="searchPattern">匹配模式。</param>
    /// <param name="searchOption">是否递归。</param>
    /// <returns>目录全路径数组。</returns>
    /// <exception cref="IOException">枚举失败。</exception>
    string[] EnumerateDirectories(
        string directory,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly);

    /// <summary>取文件长度。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>字节数。</returns>
    /// <exception cref="IOException">文件不存在或无法读取。</exception>
    long GetFileLength(string path);

    /// <summary>
    /// 一次读取文件长度与最后写入时间（UTC）：内容戳只需要一次元数据查询。
    /// </summary>
    /// <remarks>没有默认实现：所有实现都必须基于同一次文件元数据读取，避免两次独立调用带来的不一致与额外开销。</remarks>
    /// <param name="path">文件路径。</param>
    /// <returns>字节数与 UTC 最后写入时间。</returns>
    /// <exception cref="IOException">文件不存在或无法读取。</exception>
    (long Length, DateTime LastWriteTimeUtc) GetFileLengthAndLastWriteTimeUtc(string path);

    /// <summary>枚举文件与目录（不保证顺序）。</summary>
    /// <param name="directory">目录。</param>
    /// <returns>条目全路径数组。</returns>
    /// <exception cref="IOException">枚举失败。</exception>
    string[] EnumerateFileSystemEntries(string directory);

    /// <summary>判断目录是否为空（读到第一项即返回，不物化全部条目）。</summary>
    /// <param name="directory">目录路径。</param>
    /// <returns>没有任何文件/子目录时返回 <see langword="true"/>。</returns>
    /// <exception cref="IOException">目录不存在或无法枚举。</exception>
    bool IsDirectoryEmpty(string directory);

    /// <summary>取目录最后写入时间（UTC）。</summary>
    /// <param name="path">目录路径。</param>
    /// <returns>UTC 时间。</returns>
    /// <exception cref="IOException">目录不存在或无法读取。</exception>
    DateTime GetDirectoryLastWriteTimeUtc(string path);

    /// <summary>
    /// 一次读取目录「是否存在 + 最后写入时间（UTC）」：内容戳避免每个目录两次系统调用。
    /// </summary>
    /// <remarks>
    /// 实现必须在一次元数据读取内返回两项；目录不存在时返回 <see langword="false"/> 与 <see langword="default"/>。
    /// 无权限通常抛 <see cref="UnauthorizedAccessException"/>，其它读取失败抛 <see cref="IOException"/>；
    /// 调用方必须把两者都视为「不可读」并放弃缓存，而不是当成「目录不存在」。
    /// 所有平台实现都必须提供（不提供默认实现，避免语义漂移）。
    /// </remarks>
    /// <param name="path">目录路径。</param>
    /// <returns>存在标志与 UTC 最后写入时间；不存在时时间为 <see langword="default"/>。</returns>
    /// <exception cref="IOException">目录读取失败。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限。</exception>
    (bool Exists, DateTime LastWriteTimeUtc) GetDirectoryExistenceAndLastWriteTimeUtc(string path);

    /// <summary>取目录创建时间（UTC）；重命名/改写内容不会改变它（备份排序用）。</summary>
    /// <param name="path">目录路径。</param>
    /// <returns>UTC 时间。</returns>
    /// <exception cref="IOException">目录不存在或无法读取。</exception>
    DateTime GetDirectoryCreationTimeUtc(string path);

    /// <summary>取文件最后写入时间（UTC）；文件不存在时返回 1601-01-01T00:00:00Z，不抛异常。</summary>
    /// <remarks>
    /// 实现直接调用 <c>File.GetLastWriteTimeUtc</c>，因此「不存在」不是异常；
    /// 调用方若需要区分缺失，应另行判断或依赖后续读取抛出的 <see cref="FileNotFoundException"/>。
    /// </remarks>
    /// <param name="path">文件或目录路径。</param>
    /// <returns>UTC 时间；路径不存在时为 <see cref="DateTime.FromFileTimeUtc(long)"/> 的 0 值。</returns>
    /// <exception cref="ArgumentException">路径为空或包含非法字符。</exception>
    /// <exception cref="PathTooLongException">路径超出系统上限。</exception>
    DateTime GetFileLastWriteTimeUtc(string path);

    /// <summary>取文件/目录属性。</summary>
    /// <param name="path">路径。</param>
    /// <returns>属性位。</returns>
    /// <exception cref="IOException">路径不存在或无法读取。</exception>
    FileAttributes GetAttributes(string path);

    /// <summary>
    /// 用调用方已读取的属性判断是否为真正的链接；OneDrive 占位文件等非链接重解析点返回
    /// <see langword="false"/>，按普通文件处理。
    /// </summary>
    /// <remarks>
    /// 读取链接目标失败时抛出；有返回值的严格路径必须按 fail-closed 处理，
    /// <c>Try*Force</c> 删除路径捕获后保留条目、不抛异常。
    /// </remarks>
    /// <param name="path">文件或目录路径。</param>
    /// <param name="attributes">该路径已读取的属性。</param>
    /// <returns>是链接时返回 <see langword="true"/>；非链接重解析点返回 <see langword="false"/>。</returns>
    /// <exception cref="IOException">无法读取链接目标信息。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限。</exception>
    bool IsReparseLink(string path, FileAttributes attributes);

    /// <summary>尽力删除文件；路径为空、文件不存在或删除失败时返回 <see langword="false"/>。</summary>
    /// <param name="path">要删除的文件路径，可为 <see langword="null"/>。</param>
    /// <returns>实际删除了文件时返回 <see langword="true"/>。</returns>
    bool TryDeleteFile(string? path);

    /// <summary>尽力递归删除目录；路径为空、目录不存在或删除失败时返回 <see langword="false"/>。</summary>
    /// <param name="path">要删除的目录路径，可为 <see langword="null"/>。</param>
    /// <returns>实际删除了目录时返回 <see langword="true"/>。</returns>
    bool TryDeleteDirectory(string? path);

    /// <summary>
    /// 强制删除文件：普通文件先清只读属性；文件链接只删链接本身（不清属性，避免作用到目标）；
    /// 目录与目录链接返回 <see langword="false"/>（不按文件删除）。
    /// </summary>
    /// <param name="path">要删除的文件路径，可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际删除了文件时返回 <see langword="true"/>。</returns>
    bool TryDeleteFileForce(string? path, CancellationToken cancellationToken = default);

    /// <summary>强制递归删除目录：先清只读属性；子树含链接或扫描失败时整体跳过。</summary>
    /// <param name="path">要删除的目录路径，可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际删除了目录时返回 <see langword="true"/>。</returns>
    bool TryDeleteDirectoryForce(string? path, CancellationToken cancellationToken = default);
}
