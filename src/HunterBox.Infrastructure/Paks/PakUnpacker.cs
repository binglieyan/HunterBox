using HunterBox.Application.Common;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Logging;

using HunterBox.Infrastructure.IO;
namespace HunterBox.Infrastructure.Paks;

/// <summary>把 pak 的全部条目解包到目录（含 TOC 解密、条目解密/解压）。</summary>
/// <remarks>
/// <para>
/// 解包是事务化的：先解到目标所在父目录下的同卷暂存目录，全部条目成功后才整体替换目标目录；
/// 中途失败 / 取消时只清理暂存目录，目标目录保持原样，不会留下半套文件。
/// </para>
/// <para>
/// 目标已存在时采用「整体替换」语义：先把旧目标改名为同卷回滚目录，再把暂存目录改名为目标；
/// 改名失败会恢复旧目标。旧目录清理失败只写警告日志，不影响解包结果。
/// </para>
/// </remarks>
internal sealed class PakUnpacker
{
    /// <summary>解包复制缓冲大小（64KB，与默认 FileStream 缓冲同量级）。</summary>
    private const int CopyBufferSize = 64 * 1024;

    private readonly PakNameIndex? _defaultNameIndex;
    private readonly IAppLogger _logger;

    /// <param name="defaultNameIndex">未显式指定路径清单时使用的默认清单；可为 <see langword="null"/>。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    public PakUnpacker(PakNameIndex? defaultNameIndex, IAppLogger? logger = null)
    {
        _defaultNameIndex = defaultNameIndex;
        _logger = logger ?? AppLogAdapter.Instance;
    }

    /// <summary>解包。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <param name="destinationDirectory">目标目录（已存在时会被整体替换）。</param>
    /// <param name="nameIndex">路径清单；为 <see langword="null"/> 时使用默认清单。</param>
    /// <param name="progress">进度回调；可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public void Unpack(
        string pakPath,
        string destinationDirectory,
        PakNameIndex? nameIndex,
        IProgress<PakRepackProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(pakPath))
        {
            throw new FileNotFoundException("pak 文件不存在", pakPath);
        }

        // 上次原地写 TOC 被中断（进程被杀/断电）时先自动恢复条目表，再正常解包。
        PakTocPreImage.TryRecover(pakPath, out _, _logger);

        string destinationRoot = Path.GetFullPath(destinationDirectory);
        if (File.Exists(destinationRoot))
        {
            throw new ArgumentException("解包目标路径已存在同名文件", nameof(destinationDirectory));
        }

        string parentDirectory = Path.GetDirectoryName(destinationRoot)
            ?? throw new ArgumentException("无法确定解包目录的父目录", nameof(destinationDirectory));

        // 暂存目录与目标同父目录 = 同卷：最后一步 Directory.Move 才是原子 rename，而不是跨卷复制。
        string stagingRoot = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(destinationRoot)}.{Guid.NewGuid():N}.tmp");

        try
        {
            PakNameIndex? index = nameIndex ?? _defaultNameIndex;
            // 整个解包过程持有路径锁：并发改名会等待而不是失败。
            // 读句柄（FileShare.Read）也保证不会读到半写 TOC；解包较长时读取方需等待，属可接受代价。
            using PakPathLock.Scope scope = PakPathLock.Acquire(Path.GetFullPath(pakPath));
            using FileStream stream = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            ParsedPak parsed = PakArchiveCodec.Read(stream);

            // 目录创建放在解析成功之后：非法 pak 不产生任何磁盘副作用。
            Directory.CreateDirectory(parentDirectory);
            Directory.CreateDirectory(stagingRoot);

            IReadOnlyList<PakEntryInfo> entries = parsed.Archive.Entries;
            HashSet<string> writtenPaths = new(StringComparer.OrdinalIgnoreCase);

            // 整个 pak 的解包共用一个缓冲，避免每个条目都分配 64KB（P-06）。
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PakEntryInfo entry = entries[i];
                    string relativePath = ResolveRelativePath(entry, index);
                    string fullPath = ResolveSafeFilePath(stagingRoot, relativePath);
                    if (!writtenPaths.Add(relativePath))
                    {
                        throw new InvalidDataException($"多个条目解包到同一路径: {relativePath}");
                    }

                    string? parentOfFile = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(parentOfFile))
                    {
                        Directory.CreateDirectory(parentOfFile);
                    }

                    if (entry.CompressionType == PakEntryCodec.CompressionNone
                        && entry.EncryptionType == 0)
                    {
                        // 明文裸条目直接流式写盘：避免 500MB～2GB 条目在「整块读入 + WriteAllBytes」
                        // 中同时驻留多份内存。
                        CopyRawEntryToFile(stream, entry, fullPath, buffer);
                    }
                    else
                    {
                        // 压缩/加密条目：解压输出流式写盘，避免 decoded 大数组与 raw 同时驻留。
                        DecodeEntryToFile(stream, entry, fullPath, buffer);
                    }

                    progress?.Report(new PakRepackProgress(i + 1, entries.Count, relativePath));
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }

            // 全部条目成功写入暂存目录后，才把结果整体提交到目标。
            Commit(stagingRoot, destinationRoot, _logger);
        }
        catch
        {
            // 失败 / 取消：目标目录保持原样，清掉暂存目录，不留半套文件；异常原样上抛便于诊断。
            SystemFileSystem.Instance.TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    /// <summary>把压缩/加密条目解码后流式写入目标文件。</summary>
    /// <param name="pak">pak 数据流（可定位）。</param>
    /// <param name="entry">条目信息。</param>
    /// <param name="destination">目标文件路径（父目录已创建）。</param>
    /// <param name="buffer">整包共用的复制缓冲。</param>
    /// <exception cref="NotSupportedException">压缩/加密方式不受支持。</exception>
    /// <exception cref="InvalidDataException">数据损坏或解压后长度不符。</exception>
    private static void DecodeEntryToFile(Stream pak, PakEntryInfo entry, string destination, byte[] buffer)
    {
        if (!PakEntryCodec.IsSupportedCompression(entry.CompressionType))
        {
            throw new NotSupportedException(
                $"条目 hash={entry.Hash:X16} 的压缩方式 {entry.CompressionType} 不受支持");
        }

        if (entry.EncryptionType is < 0 or > 5)
        {
            throw new NotSupportedException(
                $"条目 hash={entry.Hash:X16} 的加密方式 {entry.EncryptionType} 不受支持");
        }

        long stored = PakArchiveCodec.StoredSize(entry);
        if (stored < 0 || entry.Offset < 0 || entry.Offset > pak.Length - stored)
        {
            throw new InvalidDataException($"条目超出 pak 文件范围（offset={entry.Offset}, size={stored}）");
        }

        // 从 pak 流里切出该条目的区间，再叠加「按块解密」的视图：不再把整条密文读进内存。
        pak.Position = entry.Offset;
        using Stream raw = PakEntryCodec.CreateBoundedStream(pak, stored);
        using Stream payload = entry.EncryptionType != 0
            ? PakCipher.CreateResourceDecryptionStream(raw, stored)
            : raw;

        if (entry.CompressionType == PakEntryCodec.CompressionNone)
        {
            // 仅加密的条目：解密后就是文件内容，直接流式写盘。
            using FileStream plain = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize);
            CopyToFile(payload, plain, entry, buffer);
            return;
        }

        using Stream decoder = PakEntryCodec.CreateDecompressionStream(entry.CompressionType, payload);
        using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize);
        long limit = PakEntryCodec.ResolveOutputLimit(entry.DecompressedSize);
        long written = 0;
        while (true)
        {
            int read = decoder.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            written += read;
            if (written > limit)
            {
                throw new InvalidDataException(
                    $"条目 hash={entry.Hash:X16} 解压输出超过上限 {limit} 字节，已中止");
            }

            output.Write(buffer, 0, read);
        }

        if (entry.DecompressedSize > 0 && written != entry.DecompressedSize)
        {
            throw new InvalidDataException(
                $"条目 hash={entry.Hash:X16} 解压后长度不符：{written} != {entry.DecompressedSize}");
        }
    }

    /// <summary>把解密后的条目内容流式写入目标文件（仅加密、不压缩的条目）。</summary>
    /// <param name="source">解密流。</param>
    /// <param name="destination">目标文件流。</param>
    /// <param name="entry">条目信息（用于异常信息与长度校验）。</param>
    /// <param name="buffer">整包共用的复制缓冲。</param>
    /// <exception cref="InvalidDataException">写入长度与声明不符。</exception>
    private static void CopyToFile(Stream source, Stream destination, PakEntryInfo entry, byte[] buffer)
    {
        long written = 0;
        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            written += read;
        }

        if (entry.DecompressedSize > 0 && written != entry.DecompressedSize)
        {
            throw new InvalidDataException(
                $"条目 hash={entry.Hash:X16} 解密后长度不符：{written} != {entry.DecompressedSize}");
        }
    }

    /// <summary>把明文裸条目从 pak 流式复制到目标文件（支持超过 2GB 的 stored 条目）。</summary>
    /// <param name="source">pak 数据流（可定位）。</param>
    /// <param name="entry">条目信息。</param>
    /// <param name="destination">目标文件路径（父目录已创建）。</param>
    /// <param name="buffer">整包共用的复制缓冲。</param>
    /// <exception cref="InvalidDataException">条目偏移/长度越界或数据短读。</exception>
    private static void CopyRawEntryToFile(Stream source, PakEntryInfo entry, string destination, byte[] buffer)
    {
        long stored = entry.CompressedSize > 0 ? entry.CompressedSize : entry.DecompressedSize;
        if (stored < 0 || entry.Offset < 0 || entry.Offset > source.Length - stored)
        {
            throw new InvalidDataException($"条目超出 pak 文件范围（offset={entry.Offset}, size={stored}）");
        }

        source.Position = entry.Offset;
        using FileStream target = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize);
        long remaining = stored;
        while (remaining > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new InvalidDataException($"条目数据短读（还差 {remaining} 字节）");
            }

            target.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>把已完整写入的暂存目录整体提交为目标目录（目标已存在时带回滚）。</summary>
    /// <param name="stagingRoot">同卷暂存目录。</param>
    /// <param name="destinationRoot">目标目录。</param>
    /// <param name="logger">日志端口。</param>
    /// <exception cref="IOException">提交失败且无法恢复原目标目录。</exception>
    private static void Commit(string stagingRoot, string destinationRoot, IAppLogger logger)
    {
        if (!Directory.Exists(destinationRoot))
        {
            Directory.Move(stagingRoot, destinationRoot);
            return;
        }

        // 目标已存在：先整体改名到同卷回滚目录，再让暂存目录顶上；两步都是 rename。
        string rollbackRoot = Path.Combine(
            Path.GetDirectoryName(stagingRoot)!,
            $".{Path.GetFileName(destinationRoot)}.{Guid.NewGuid():N}.old");
        Directory.Move(destinationRoot, rollbackRoot);
        try
        {
            Directory.Move(stagingRoot, destinationRoot);
        }
        catch (Exception exception)
        {
            // 新目录顶不上时把旧目标恢复回去，避免目标目录「消失」。
            if (!TryMoveDirectory(rollbackRoot, destinationRoot))
            {
                throw new IOException(
                    $"解包提交失败且无法恢复原目标目录；原内容保留在 {rollbackRoot}",
                    exception);
            }

            throw;
        }

        if (!SystemFileSystem.Instance.TryDeleteDirectory(rollbackRoot))
        {
            logger.Warn($"解包替换后清理旧目录失败，残留：{rollbackRoot}");
        }
    }

    /// <summary>尽力把目录改名到目标位置。</summary>
    /// <param name="source">源目录。</param>
    /// <param name="destination">目标目录（必须不存在）。</param>
    /// <returns>改名成功时返回 <see langword="true"/>。</returns>
    private static bool TryMoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveRelativePath(PakEntryInfo entry, PakNameIndex? index)
    {
        string? resolved = index?.TryResolve(entry.Hash);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            // 路径清单里没有的条目：用 16 位大写哈希命名，重打包时按文件名还原哈希。
            return $"__Unknown/{entry.Hash:X16}";
        }

        return resolved;
    }

    /// <summary>把包内相对路径解析为解包目录下的绝对路径，拒绝绝对路径、<c>..</c>、非法字符与保留设备名。</summary>
    /// <remarks>校验规则集中在 <see cref="PakPathSafety"/>，本方法只负责拼接与最终的范围夹紧。</remarks>
    private static string ResolveSafeFilePath(string destinationRoot, string relativePath)
    {
        string[] segments = PakPathSafety.SplitSafeSegments(relativePath);
        string combined = Path.GetFullPath(
            Path.Combine(destinationRoot, string.Join(Path.DirectorySeparatorChar, segments)));
        string rootPrefix = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"条目路径超出解包目录: {relativePath}");
        }

        return combined;
    }
}
