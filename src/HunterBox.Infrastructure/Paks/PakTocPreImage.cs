using System.Buffers.Binary;
using System.Text;
using HunterBox.Application.Common;
using HunterBox.Application.Paks;
using HunterBox.Infrastructure.Logging;

using HunterBox.Infrastructure.IO;
namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// pak 条目表前像：阻止「原地写 TOC 被中断」留下半截条目表，并让前像只在同一 pak 上生效。
/// </summary>
/// <remarks>
/// <para>
/// 两个文件：
/// <list type="bullet">
/// <item><c>&lt;pak&gt;.toc.pending</c>：本次写入的未提交日志，写入前生成、成功后提交/删除。
/// 下次读取/解包/改名时若发现它仍然存在，说明上次写入没走完，直接按它恢复条目表。</item>
/// <item><c>&lt;pak&gt;.toc.bak</c>：最近一次成功改名前的前像（可手工/自动回滚）。</item>
/// </list>
/// </para>
/// <para>
/// 文件格式（小端）：8 字节魔数 <c>HBTOCPRE</c>、4 字节版本、8 字节 pak 文件长度、4 字节 TOC 长度、
/// 1 字节 major、1 字节 minor、2 字节 feature、4 字节条目数、8 字节数据区采样哈希、8 字节保留，
/// 之后是原始 TOC 字节（加密 TOC 为密文）。恢复前校验这些指纹与当前 pak 一致，
/// 避免把重装/更新前的旧条目表写到新 pak 上。
/// </para>
/// <para>
/// <b>失败语义：</b>恢复失败分「前像不适用/损坏」与「当前 pak 无法读写」两类。
/// 前者删除未提交日志；后者**保留**日志并抛出可读的 <see cref="IOException"/>，
/// 绝不把唯一的恢复来源删掉。
/// </para>
/// </remarks>
internal static class PakTocPreImage
{
    /// <summary>成功改名前保留的前像后缀。</summary>
    public const string BackupSuffix = ".toc.bak";

    /// <summary>未提交写入日志后缀（崩溃恢复用）。</summary>
    public const string PendingSuffix = ".toc.pending";

    /// <summary>当前前像格式版本（2：数据区采样改为起点/中点/末尾三段）。</summary>
    internal const int CurrentVersion = 2;

    /// <summary>仍可读取的旧前像版本（1：只采样数据区前 4KB）。</summary>
    private const int LegacyVersion = 1;

    private const string Magic = "HBTOCPRE";
    private const int HeaderSize = 48;
    private const long MagicOffset = 0;
    private const long VersionOffset = 8;
    private const long PakLengthOffset = 12;
    private const long TocLengthOffset = 20;
    private const long MajorOffset = 24;
    private const long MinorOffset = 25;
    private const long FeatureOffset = 26;
    private const long CountOffset = 28;
    private const long DataSampleOffset = 32;

    /// <summary>数据区采样分片长度：起点/中点/末尾各取一段，足以识别「pak 被替换」。</summary>
    private const int DataSampleBytes = 4096;

    /// <summary>前像内容与对应 pak 的指纹。</summary>
    /// <param name="Version">前像格式版本（决定数据区采样算法）。</param>
    /// <param name="PakLength">写入前 pak 的文件长度。</param>
    /// <param name="Major">主版本。</param>
    /// <param name="Minor">次版本。</param>
    /// <param name="Feature">feature 位。</param>
    /// <param name="Count">条目数。</param>
    /// <param name="DataSample">数据区采样哈希。</param>
    /// <param name="Toc">原始 TOC 字节（加密 pak 为密文）。</param>
    internal readonly record struct Snapshot(
        int Version,
        long PakLength,
        byte Major,
        byte Minor,
        short Feature,
        int Count,
        ulong DataSample,
        byte[] Toc);

    /// <summary>恢复结果：区分「不需要/不适用」「成功」「pak 无法读写」。</summary>
    internal enum RecoveryOutcome
    {
        /// <summary>没有可恢复的前像，或前像与当前 pak 不匹配。</summary>
        NotApplicable,

        /// <summary>已把前像写回 pak。</summary>
        Restored,

        /// <summary>pak 或前像被占用/无权限等 IO 失败；未提交日志必须保留。</summary>
        IoError,
    }

    /// <summary>前像文件读取结果。</summary>
    internal enum ReadOutcome
    {
        /// <summary>读取成功且格式合法。</summary>
        Ok,

        /// <summary>文件不存在、格式不合法或内容截断。</summary>
        Invalid,

        /// <summary>文件被占用/无权限，读不出来。</summary>
        IoError,
    }

    /// <summary>是否存在未提交的写入日志（上次原地写 TOC 没有走完）。</summary>
    /// <param name="pakPath">pak 绝对路径。</param>
    /// <returns>存在时返回 <see langword="true"/>。</returns>
    internal static bool HasPending(string pakPath) =>
        !string.IsNullOrWhiteSpace(pakPath) && File.Exists(pakPath + PendingSuffix);

    /// <summary>计算 pak 数据区采样哈希（起点/中点/末尾各最多 4KB，空数据区返回 0）。</summary>
    /// <param name="stream">pak 数据流（可定位）。</param>
    /// <param name="dataStart">数据区起始偏移。</param>
    /// <returns>FNV-1a 64 采样哈希。</returns>
    internal static ulong ComputeDataSample(Stream stream, long dataStart)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long length = stream.Length;
        if (dataStart < 0 || dataStart >= length)
        {
            return 0;
        }

        long dataLength = length - dataStart;
        int chunk = (int)Math.Min(DataSampleBytes, dataLength);
        List<long> starts = [dataStart];
        if (dataLength > chunk * 2)
        {
            starts.Add(dataStart + (dataLength / 2) - (chunk / 2));
        }

        if (dataLength > chunk)
        {
            starts.Add(length - chunk);
        }

        starts.Sort();
        ulong hash = 14695981039346656037UL;
        byte[] buffer = new byte[chunk];
        long hashedUntil = -1;
        foreach (long start in starts)
        {
            if (start < hashedUntil)
            {
                continue;
            }

            int size = (int)Math.Min(chunk, length - start);
            stream.Position = start;
            stream.ReadExactly(buffer.AsSpan(0, size));
            for (int i = 0; i < size; i++)
            {
                hash ^= buffer[i];
                hash *= 1099511628211UL;
            }

            hashedUntil = start + size;
        }

        return hash;
    }

    /// <summary>按旧版（只采样数据区前 4KB）计算，用于读取 v1 前像。</summary>
    /// <param name="stream">pak 数据流。</param>
    /// <param name="dataStart">数据区起点。</param>
    /// <returns>FNV-1a 64 采样哈希。</returns>
    private static ulong ComputeLegacyDataSample(Stream stream, long dataStart)
    {
        long length = stream.Length;
        if (dataStart < 0 || dataStart >= length)
        {
            return 0;
        }

        int size = (int)Math.Min(DataSampleBytes, length - dataStart);
        byte[] buffer = new byte[size];
        stream.Position = dataStart;
        stream.ReadExactly(buffer);
        ulong hash = 14695981039346656037UL;
        foreach (byte value in buffer)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    /// <summary>pak 数据区起点（TOC + feature 附加字 + TOC 密钥块之后）。</summary>
    /// <param name="header">包头字段。</param>
    /// <returns>数据区起始偏移。</returns>
    internal static long DataStart(PakPlainHeader header)
    {
        long position = PakPlainToc.TocEnd(header);
        if ((header.Feature & 0x10) != 0)
        {
            position += 4;
        }

        if ((header.Feature & 0x08) != 0)
        {
            position += PakCipher.TocKeyBlockSize;
        }

        return position;
    }

    /// <summary>把本次写入前的 TOC 写成未提交日志（覆盖同目录的旧日志）。</summary>
    /// <param name="pakPath">pak 绝对路径。</param>
    /// <param name="snapshot">前像内容与指纹。</param>
    /// <exception cref="ArgumentException"><paramref name="pakPath"/> 为空或无法确定所在目录。</exception>
    /// <exception cref="IOException">写入或提交失败。</exception>
    internal static void WritePending(string pakPath, Snapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        string directory = Path.GetDirectoryName(pakPath)
            ?? throw new ArgumentException("无法确定 pak 所在目录", nameof(pakPath));
        string pendingPath = pakPath + PendingSuffix;
        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(pakPath)}.tocpending.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Span<byte> header = stackalloc byte[HeaderSize];
                header.Clear();
                Encoding.ASCII.GetBytes(Magic).CopyTo(header[(int)MagicOffset..]);
                BinaryPrimitives.WriteInt32LittleEndian(header[(int)VersionOffset..], CurrentVersion);
                BinaryPrimitives.WriteInt64LittleEndian(header[(int)PakLengthOffset..], snapshot.PakLength);
                BinaryPrimitives.WriteInt32LittleEndian(header[(int)TocLengthOffset..], snapshot.Toc.Length);
                header[(int)MajorOffset] = snapshot.Major;
                header[(int)MinorOffset] = snapshot.Minor;
                BinaryPrimitives.WriteInt16LittleEndian(header[(int)FeatureOffset..], snapshot.Feature);
                BinaryPrimitives.WriteInt32LittleEndian(header[(int)CountOffset..], snapshot.Count);
                BinaryPrimitives.WriteUInt64LittleEndian(header[(int)DataSampleOffset..], snapshot.DataSample);
                stream.Write(header);
                stream.Write(snapshot.Toc);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, pendingPath, overwrite: true);
        }
        catch
        {
            SystemFileSystem.Instance.TryDeleteFile(tempPath);
            throw;
        }
    }

    /// <summary>TOC 写入成功后把未提交日志提升为可回滚前像。</summary>
    /// <param name="pakPath">pak 绝对路径。</param>
    internal static void Commit(string pakPath)
    {
        string pendingPath = pakPath + PendingSuffix;
        if (!File.Exists(pendingPath))
        {
            return;
        }

        File.Move(pendingPath, pakPath + BackupSuffix, overwrite: true);
    }

    /// <summary>丢弃未提交日志（写入已回滚或不需要前像时）。</summary>
    /// <param name="pakPath">pak 绝对路径。</param>
    internal static void DiscardPending(string pakPath) =>
        SystemFileSystem.Instance.TryDeleteFile(pakPath + PendingSuffix);

    /// <summary>
    /// 只处理未提交日志：存在就尝试恢复。签名与旧版一致，供读取/解包等热路径使用。
    /// </summary>
    /// <param name="pakPath">pak 绝对路径。</param>
    /// <param name="message">成功或失败说明；无可恢复内容时为空。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    /// <returns>确实恢复时返回 <see langword="true"/>。</returns>
    /// <exception cref="IOException">pak 被占用/无权限，未提交日志已保留。</exception>
    internal static bool TryRecoverPending(string pakPath, out string message, IAppLogger? logger = null)
    {
        message = string.Empty;
        if (!HasPending(pakPath))
        {
            return false;
        }

        logger ??= AppLogAdapter.Instance;

        string targetPath = Path.GetFullPath(pakPath);
        using PakPathLock.Scope scope = PakPathLock.Acquire(targetPath);
        switch (RestoreFrom(targetPath + PendingSuffix, targetPath))
        {
            case RecoveryOutcome.Restored:
                SystemFileSystem.Instance.TryDeleteFile(targetPath + PendingSuffix);
                message = "为恢复上次中断的写入，已修改 MOD pak 的条目表（.toc.pending）";
                logger.Warn(message + "：" + targetPath);
                return true;

            case RecoveryOutcome.IoError:
                message = "pak 的上次写入未完成，但当前无法自动恢复（文件可能被游戏/杀毒软件占用）："
                    + $"{targetPath}；请关闭占用该文件的程序后重试，恢复日志已保留";
                logger.Warn(message);
                throw new IOException(message);

            default:
                // 指纹不符（pak 已被替换）或日志损坏：作废日志，绝不覆盖当前 pak。
                SystemFileSystem.Instance.TryDeleteFile(targetPath + PendingSuffix);
                return false;
        }
    }

    /// <summary>
    /// 尝试自动恢复 pak 的条目表：优先用未提交日志；没有日志时，当前 TOC 结构损坏才用已提交前像。
    /// </summary>
    /// <param name="pakPath">pak 绝对路径。</param>
    /// <param name="message">成功时的说明（供日志/界面展示）；失败为空。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    /// <returns>确实恢复了条目表时返回 <see langword="true"/>。</returns>
    /// <exception cref="IOException">未提交日志存在但 pak 无法读写（日志已保留）。</exception>
    internal static bool TryRecover(string pakPath, out string message, IAppLogger? logger = null)
    {
        if (TryRecoverPending(pakPath, out message, logger))
        {
            return true;
        }

        logger ??= AppLogAdapter.Instance;

        message = string.Empty;
        if (string.IsNullOrWhiteSpace(pakPath) || !File.Exists(pakPath))
        {
            return false;
        }

        string targetPath = Path.GetFullPath(pakPath);
        string backupPath = targetPath + BackupSuffix;
        if (!File.Exists(backupPath))
        {
            return false;
        }

        using PakPathLock.Scope scope = PakPathLock.Acquire(targetPath);
        if (IsTocStructurallyValid(targetPath, out bool readFailed))
        {
            return false;
        }

        if (readFailed)
        {
            // 读当前 pak 都失败：没有证据表明是半写 TOC，跳过 backup 恢复并留下日志。
            logger.Warn($"pak 当前不可读，已跳过 .toc.bak 恢复尝试：{targetPath}");
            return false;
        }

        switch (RestoreFrom(backupPath, targetPath))
        {
            case RecoveryOutcome.Restored:
                message = "pak 条目表结构损坏：为恢复可用状态，已用 .toc.bak 前像改写 MOD pak 的条目表";
                logger.Warn(message + "：" + targetPath);
                return true;

            case RecoveryOutcome.IoError:
                message = $"pak 条目表损坏且当前无法写入恢复（文件可能被占用）：{targetPath}";
                logger.Warn(message);
                throw new IOException(message);

            default:
                return false;
        }
    }

    /// <summary>读取前像文件，区分成功、格式不合法与 IO 失败。</summary>
    /// <param name="path">前像路径。</param>
    /// <param name="snapshot">读出的内容。</param>
    /// <returns>读取结果。</returns>
    internal static ReadOutcome ReadSnapshot(string path, out Snapshot snapshot)
    {
        snapshot = default;
        try
        {
            // 只按头部声明读取，不把整个前像一次性读进内存。
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            if (length < HeaderSize)
            {
                return ReadOutcome.Invalid;
            }

            Span<byte> header = stackalloc byte[HeaderSize];
            stream.ReadExactly(header);
            if (!header[..Magic.Length].SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
            {
                return ReadOutcome.Invalid;
            }

            int version = BinaryPrimitives.ReadInt32LittleEndian(header[(int)VersionOffset..]);
            if (version is not (CurrentVersion or LegacyVersion))
            {
                return ReadOutcome.Invalid;
            }

            long pakLength = BinaryPrimitives.ReadInt64LittleEndian(header[(int)PakLengthOffset..]);
            int tocLength = BinaryPrimitives.ReadInt32LittleEndian(header[(int)TocLengthOffset..]);
            if (tocLength < 0 || HeaderSize + (long)tocLength != length)
            {
                return ReadOutcome.Invalid;
            }

            byte[] toc = new byte[tocLength];
            stream.ReadExactly(toc);
            snapshot = new Snapshot(
                version,
                pakLength,
                header[(int)MajorOffset],
                header[(int)MinorOffset],
                BinaryPrimitives.ReadInt16LittleEndian(header[(int)FeatureOffset..]),
                BinaryPrimitives.ReadInt32LittleEndian(header[(int)CountOffset..]),
                BinaryPrimitives.ReadUInt64LittleEndian(header[(int)DataSampleOffset..]),
                toc);
            return ReadOutcome.Ok;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // HasPending 与 Open 之间文件被删/改名：前像已不存在，按「不适用」处理。
            return ReadOutcome.Invalid;
        }
        catch (Exception ex) when (ex is EndOfStreamException)
        {
            // 头部合法但 TOC 被截断：这是内容损坏，不是占用；必须归 Invalid 才能清掉日志。
            return ReadOutcome.Invalid;
        }
        catch (Exception ex) when (ex is PathTooLongException or NotSupportedException)
        {
            // 路径过长/格式不支持：属于永久性失败，不应反复按「占用」重试。
            return ReadOutcome.Invalid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 前像文件本身被占用/无权限：不能当成「不适用」删掉恢复日志。
            return ReadOutcome.IoError;
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            return ReadOutcome.Invalid;
        }
    }

    /// <summary>前像指纹与当前 pak 一致时，把前像里的 TOC 写回 pak。</summary>
    /// <param name="preImagePath">前像文件路径。</param>
    /// <param name="pakPath">pak 路径。</param>
    /// <returns>恢复结果（成功 / 前像不适用 / 前像或 pak 无法读写）。</returns>
    private static RecoveryOutcome RestoreFrom(string preImagePath, string pakPath)
    {
        switch (ReadSnapshot(preImagePath, out Snapshot snapshot))
        {
            case ReadOutcome.IoError:
                return RecoveryOutcome.IoError;

            case ReadOutcome.Invalid:
                return RecoveryOutcome.NotApplicable;
        }

        try
        {
            long fileLength;
            PakPlainHeader header;
            long dataStart;
            using (FileStream source = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fileLength = source.Length;
                header = PakPlainToc.ReadHeader(source);
                dataStart = DataStart(header);
                if (!Matches(snapshot, fileLength, header, source, dataStart))
                {
                    return RecoveryOutcome.NotApplicable;
                }
            }

            if (PakPlainToc.TocEnd(header) > fileLength)
            {
                return RecoveryOutcome.NotApplicable;
            }

            using FileStream writable = new(pakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            writable.Position = PakPlainToc.HeaderSize;
            writable.Write(snapshot.Toc);
            writable.Flush(flushToDisk: true);
            return RecoveryOutcome.Restored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RecoveryOutcome.IoError;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return RecoveryOutcome.NotApplicable;
        }
    }

    private static bool Matches(
        Snapshot snapshot,
        long fileLength,
        PakPlainHeader header,
        Stream stream,
        long dataStart)
    {
        if (snapshot.PakLength != fileLength
            || snapshot.Major != header.Major
            || snapshot.Minor != header.Minor
            || snapshot.Feature != header.Feature
            || snapshot.Count != header.Count
            || snapshot.Toc.Length != PakPlainToc.TocEnd(header) - PakPlainToc.HeaderSize)
        {
            return false;
        }

        ulong sample = snapshot.Version == LegacyVersion
            ? ComputeLegacyDataSample(stream, dataStart)
            : ComputeDataSample(stream, dataStart);
        return snapshot.DataSample == sample;
    }

    /// <summary>按条目字段校验 TOC 是否还能用（偏移落在数据区内、大小不越界）。</summary>
    /// <param name="pakPath">pak 路径。</param>
    /// <param name="readFailed">读取 pak 本身失败（IO）时为 <see langword="true"/>。</param>
    /// <returns>结构合法时返回 <see langword="true"/>；解析失败返回 <see langword="false"/>。</returns>
    private static bool IsTocStructurallyValid(string pakPath, out bool readFailed)
    {
        readFailed = false;
        try
        {
            using FileStream stream = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            PakPlainHeader header = PakPlainToc.ReadHeader(stream);
            byte[] storedToc = PakPlainToc.ReadToc(stream, header);
            byte[] plainToc = storedToc;
            if ((header.Feature & 0x08) != 0)
            {
                long keyOffset = PakPlainToc.TocEnd(header) + ((header.Feature & 0x10) != 0 ? 4 : 0);
                if (keyOffset + PakCipher.TocKeyBlockSize > stream.Length)
                {
                    return false;
                }

                stream.Position = keyOffset;
                byte[] keyBlock = new byte[PakCipher.TocKeyBlockSize];
                stream.ReadExactly(keyBlock);
                plainToc = PakCipher.CryptToc(storedToc, keyBlock);
            }

            long dataStart = DataStart(header);
            long length = stream.Length;
            foreach (PakEntryInfo entry in PakPlainToc.ReadEntries(header, plainToc))
            {
                if (entry.HasEmptyPathHash)
                {
                    // 清零型半写：条目哈希全为 0，属于结构损坏。
                    return false;
                }

                if (entry.Offset < 0 || entry.CompressedSize < 0 || entry.DecompressedSize < 0)
                {
                    return false;
                }

                long stored = entry.CompressedSize > 0 ? entry.CompressedSize : entry.DecompressedSize;
                if (stored == 0)
                {
                    continue;
                }

                if (entry.Offset < dataStart || entry.Offset > length - stored)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            readFailed = true;
            return false;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
