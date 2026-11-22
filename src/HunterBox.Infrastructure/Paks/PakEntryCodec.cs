using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using SharpCompress.Compressors.ZStandard;

namespace HunterBox.Infrastructure.Paks;

/// <summary>pak 条目数据的压缩 / 解压（DEFLATE 为裸 deflate 流，ZSTD 为单帧）。</summary>
internal static class PakEntryCodec
{
    /// <summary>不压缩。</summary>
    public const int CompressionNone = 0;

    /// <summary>裸 DEFLATE 流。</summary>
    public const int CompressionDeflate = 1;

    /// <summary>ZSTD 单帧。</summary>
    public const int CompressionZstd = 2;

    /// <summary>ZSTD 压缩级别（与格式常用值一致）。</summary>
    private const int ZstdLevel = 5;

    /// <summary>单条目解压输出的绝对上限（512MB）。</summary>
    /// <remarks>
    /// 条目表里的 <c>DecompressedSize</c> 来自不可信文件，可以被伪造成小值
    /// （压缩炸弹），所以解压既不能信声明尺寸，也不能等全部解压完再比对：按「声明尺寸（可信部分）
    /// 与绝对上限取小」做有界复制，一旦超出立即中止。
    /// </remarks>
    public const long MaxDecompressedSizeBytes = 512L * 1024 * 1024;

    /// <summary>有界复制的读取缓冲区大小。</summary>
    private const int BoundedCopyBufferSize = 64 * 1024;

    /// <summary>判断压缩方式是否受支持。</summary>
    public static bool IsSupportedCompression(int compressionType) =>
        compressionType is CompressionNone or CompressionDeflate or CompressionZstd;

    /// <summary>解压条目数据（输出有上限，超限立即中止）。</summary>
    /// <param name="compressionType">压缩方式。</param>
    /// <param name="data">压缩数据。</param>
    /// <param name="declaredDecompressedSize">
    /// 条目表声明的解压后大小；<c>0</c> 表示未知（只用绝对上限）。压缩分支中大于绝对上限时直接判定异常，不会先解压。
    /// </param>
    /// <returns>解压后的数据。</returns>
    /// <exception cref="NotSupportedException">压缩方式不受支持。</exception>
    /// <exception cref="InvalidDataException">
    /// 压缩流损坏、压缩分支的声明尺寸非法或输出超过上限（<see cref="CompressionNone"/> 直接返回原文，不校验声明尺寸）。
    /// </exception>
    public static byte[] Decompress(int compressionType, byte[] data, long declaredDecompressedSize = 0)
    {
        try
        {
            return compressionType switch
            {
                CompressionNone => data,
                CompressionDeflate => Inflate(data, declaredDecompressedSize),
                CompressionZstd => Unzstd(data, declaredDecompressedSize),
                _ => throw new NotSupportedException($"不支持的压缩方式: {compressionType}"),
            };
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or SharpCompress.Common.SharpCompressException)
        {
            throw new InvalidDataException($"解压条目失败: {exception.Message}", exception);
        }
    }

    /// <summary>创建「把原始数据压缩后写入 <paramref name="output"/>」的压缩流（Dispose 时结束压缩帧）。</summary>
    /// <param name="compressionType">压缩方式（不能为 0）。</param>
    /// <param name="output">压缩数据输出流（调用方负责关闭）。</param>
    /// <returns>压缩流；Dispose 后 <paramref name="output"/> 里是完整的压缩数据。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="NotSupportedException">压缩方式不受支持。</exception>
    public static Stream CreateCompressionStream(int compressionType, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return compressionType switch
        {
            CompressionDeflate => new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true),
            CompressionZstd => new CompressionStream(output, level: ZstdLevel, bufferSize: 0, leaveOpen: true),
            _ => throw new NotSupportedException($"不支持的压缩方式: {compressionType}"),
        };
    }

    /// <summary>为已解密的压缩数据创建解压流（调用方负责关闭；输出上限由调用方按 <see cref="ResolveOutputLimit"/> 控制）。</summary>
    /// <param name="compressionType">压缩方式（不能为 <see cref="CompressionNone"/>）。</param>
    /// <param name="input">压缩数据流。</param>
    /// <returns>解压流；Dispose 时结束解压帧。</returns>
    /// <exception cref="NotSupportedException">压缩方式不受支持。</exception>
    internal static Stream CreateDecompressionStream(int compressionType, Stream input) =>
        compressionType switch
        {
            CompressionDeflate => new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true),
            CompressionZstd => new DecompressionStream(
                input,
                bufferSize: 0,
                checkEndOfStream: true,
                leaveOpen: true),
            _ => throw new NotSupportedException($"不支持的压缩方式: {compressionType}"),
        };

    private static byte[] Inflate(byte[] data, long declaredDecompressedSize)
    {
        using MemoryStream input = new(data);
        using DeflateStream deflate = new(input, CompressionMode.Decompress);
        return BoundedCopy(deflate, ResolveOutputLimit(declaredDecompressedSize), "DEFLATE");
    }

    private static byte[] Unzstd(byte[] data, long declaredDecompressedSize)
    {
        using MemoryStream input = new(data);
        using DecompressionStream decompression = new(input, bufferSize: 0, checkEndOfStream: true, leaveOpen: false);
        return BoundedCopy(decompression, ResolveOutputLimit(declaredDecompressedSize), "ZSTD");
    }

    /// <summary>计算本次解压允许的最大输出字节数：声明大小（&gt;0）与绝对上限取小。</summary>
    /// <param name="declaredDecompressedSize">条目表声明的解压后大小。</param>
    /// <returns>输出上限。</returns>
    /// <exception cref="InvalidDataException">声明大小超过绝对上限。</exception>
    internal static long ResolveOutputLimit(long declaredDecompressedSize)
    {
        if (declaredDecompressedSize < 0)
        {
            throw new InvalidDataException($"条目声明解压大小为负数: {declaredDecompressedSize}");
        }

        if (declaredDecompressedSize > MaxDecompressedSizeBytes)
        {
            // 先判声明尺寸：异常 pak 不必真的解压一遍就能拒绝。
            throw new InvalidDataException(
                $"条目声明解压大小 {declaredDecompressedSize} 字节超过绝对上限 {MaxDecompressedSizeBytes} 字节");
        }

        return declaredDecompressedSize > 0 ? declaredDecompressedSize : MaxDecompressedSizeBytes;
    }

    /// <summary>在数据流当前位置上包一层「最多读 <paramref name="length"/> 字节」的只读视图。</summary>
    /// <param name="source">底层数据流（不负责关闭）。</param>
    /// <param name="length">可读取的最大字节数。</param>
    /// <returns>不可定位的只读流；读完或底层提前结束时返回 0。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> 为负。</exception>
    internal static Stream CreateBoundedStream(Stream source, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new BoundedReadStream(source, length);
    }

    /// <summary>
    /// 有界复制：边解压边计数，累计输出超过 <paramref name="limit"/> 时立即抛
    /// <see cref="InvalidDataException"/>，不会先把压缩炸弹完整解进内存再比较。
    /// </summary>
    /// <param name="decompressor">解压流。</param>
    /// <param name="limit">允许的最大输出字节数。</param>
    /// <param name="algorithm">算法名（用于异常信息）。</param>
    /// <returns>解压后的数据。</returns>
    /// <exception cref="InvalidDataException">输出超过上限。</exception>
    private static byte[] BoundedCopy(Stream decompressor, long limit, string algorithm)
    {
        using MemoryStream output = new();
        byte[] buffer = new byte[BoundedCopyBufferSize];
        long total = 0;
        while (true)
        {
            int read = decompressor.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > limit)
            {
                throw new InvalidDataException(
                    $"{algorithm} 解压输出超过上限 {limit} 字节，已中止（可能是压缩炸弹或声明尺寸不符）");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>底层数据流上「最多读 N 字节」的只读视图（不关闭底层流）。</summary>
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "只读视图不拥有底层流，关闭由调用方负责。")]
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _source;
        private long _remaining;
        private bool _disposed;

        public BoundedReadStream(Stream source, long length)
        {
            _source = source;
            _remaining = length;
        }

        public override bool CanRead => !_disposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (buffer.Length - offset < count)
            {
                throw new ArgumentException("offset + count 超出缓冲区长度", nameof(count));
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_remaining <= 0)
            {
                return 0;
            }

            int read = _source.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_remaining <= 0)
            {
                return 0;
            }

            int read = _source.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // 底层 pak 流由调用方管理，这里只标记自身失效。
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
