using System.Security;
using HunterBox.Domain.Archives;

namespace HunterBox.Application.Common;

/// <summary>文件级压缩格式探测：读取文件头后交给 Domain 的纯魔数规则。</summary>
/// <remarks>
/// Domain 的 <see cref="ArchiveFormatDetector.Detect(ReadOnlySpan{byte})"/> 只处理字节，不做 IO（A-03）；
/// 安装源识别、解压前判断等需要文件路径的场景统一使用本类。
/// </remarks>
public static class ArchiveFileProbe
{
    /// <summary>读取的文件头字节数；TAR 的 ustar 标记位于 257，512 字节足够覆盖全部签名。</summary>
    private const int HeaderBytes = 512;

    /// <summary>探测文件格式，并区分「不是压缩包」与「文件读不出来」。</summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="fileSystem">文件系统端口（读取文件头）。</param>
    /// <returns>探测结果；读取失败时 <see cref="ArchiveProbeResult.Error"/> 非空。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    public static ArchiveProbeResult Probe(string? filePath, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return Probe(filePath, fileSystem, new byte[HeaderBytes]);
    }

    /// <summary>用调用方提供的缓冲区探测文件格式，避免批量探测时逐文件分配。</summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="fileSystem">文件系统端口（读取文件头）。</param>
    /// <param name="header">复用缓冲区；长度至少 <see cref="HeaderBytes"/>。</param>
    /// <returns>探测结果；读取失败时 <see cref="ArchiveProbeResult.Error"/> 非空。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 或 <paramref name="header"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="header"/> 长度不足。</exception>
    public static ArchiveProbeResult Probe(string? filePath, IFileSystem fileSystem, byte[] header)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(header);
        if (header.Length < HeaderBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(header), $"探测缓冲区至少需要 {HeaderBytes} 字节。");
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new ArchiveProbeResult(ArchiveFormat.Unknown, null);
        }

        try
        {
            int read;
            using (Stream stream = fileSystem.OpenRead(filePath))
            {
                read = stream.ReadAtLeast(header, HeaderBytes, throwOnEndOfStream: false);
            }

            return new ArchiveProbeResult(ArchiveFormatDetector.Detect(header.AsSpan(0, read)), null);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            // 读不出来不是「类型不支持」：把原因带回去，让调用方提示重试/关闭占用程序。
            return new ArchiveProbeResult(ArchiveFormat.Unknown, ex);
        }
    }

    /// <summary>按内容魔数探测文件格式（不依赖扩展名）。</summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="fileSystem">文件系统端口（读取文件头）。</param>
    /// <returns>识别出的格式；文件不存在、不可读或不是已知压缩格式时为 <see cref="ArchiveFormat.Unknown"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    public static ArchiveFormat Detect(string? filePath, IFileSystem fileSystem) =>
        Probe(filePath, fileSystem).Format;

    /// <summary>文件是否是内容可识别的压缩包（不依赖扩展名）。</summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="fileSystem">文件系统端口（读取文件头）。</param>
    /// <returns>是压缩包时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    public static bool IsSupportedArchive(string? filePath, IFileSystem fileSystem) =>
        Detect(filePath, fileSystem) != ArchiveFormat.Unknown;
}
