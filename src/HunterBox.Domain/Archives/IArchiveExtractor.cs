namespace HunterBox.Domain.Archives;

/// <summary>
/// 把压缩包解开到临时目录。
/// </summary>
/// <remarks>
/// 实现必须拒绝任何会逃出目标目录的条目（zip-slip），并在解压前创建目标目录。
/// 具体压缩格式支持由实现决定（Infrastructure 层用 SharpCompress 覆盖 zip/7z/rar/tar）。
/// </remarks>
public interface IArchiveExtractor
{
    /// <summary>解压压缩包。</summary>
    /// <param name="archivePath">压缩包绝对路径。</param>
    /// <param name="destinationDirectory">解压目标目录（不存在会被创建）。</param>
    /// <param name="options">解压选项（密码/进度/取消/嵌套解压）；为 <see langword="null"/> 时使用默认值。</param>
    /// <returns>本次解出的相对文件列表（正斜杠、小写）；不包含目标目录里预先存在的文件。</returns>
    /// <exception cref="FileNotFoundException">压缩包不存在。</exception>
    /// <exception cref="InvalidDataException">条目路径非法或越界、加密包密码错误。</exception>
    /// <exception cref="NotSupportedException">压缩格式不受支持。</exception>
    /// <exception cref="OperationCanceledException">取消令牌被触发。</exception>
    IReadOnlyList<string> Extract(
        string archivePath,
        string destinationDirectory,
        ArchiveExtractionOptions? options = null);
}
