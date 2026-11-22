using System.Buffers.Binary;
using HunterBox.Application.Common;
using HunterBox.Application.Paks;
using HunterBox.Infrastructure.Logging;

namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// 只重写条目表里 8 字节哈希，用来给 pak 内的文件改名。
/// </summary>
/// <remarks>
/// <para>
/// 条目表紧跟在 16 字节包头之后，每项固定长度（v4 = 48，v2 = 24）。哈希字段位置按版本不同：
/// <b>v4 在条目起始（+0），v2 在 offset/decompressedSize 之后（条目内 +16）</b>，
/// 由 <see cref="PakEntryLayout"/> 单点定义，不能再假设「哈希占每项前 8 字节」。
/// 数据区、偏移、大小、checksum 一律不动。
/// </para>
/// <para>
/// <b>原地写 TOC：</b>改名只影响 TOC，因此不再复制整包到临时副本，也不生成整包 <c>.bak</c>——
/// 只把 TOC 区（包头之后、条目表长度）读进内存改完再写回。GB 级 pak 的 I/O 从 O(pak) 降到 O(TOC)。
/// TOC 加密的 pak（feature 8/24）用包内密钥块先解密、改哈希、再按同一密钥块重新加密写回，
/// 因此加密 pak 也能一次聚合多个 change，而不是每个 change 重打包一次。
/// </para>
/// <para>
/// <b>崩溃恢复：</b>每次写入前把原始 TOC 写成 <c>&lt;pak&gt;.toc.pending</c>，
/// 写成功后再提升为 <c>&lt;pak&gt;.toc.bak</c>（前一次改名的前像，可回滚）；进程被杀/断电时
/// <c>.toc.pending</c> 还在，下次打开同名 pak 会自动写回原始条目表。前像头部记录 pak 长度、
/// 版本/feature/条目数与数据区采样哈希，重装/替换后的旧前像不会误套到新 pak 上。
/// </para>
/// <para>
/// 批量改写（<see cref="RenameEntries"/>）只读一次 TOC、把所有目标哈希一次写回、最后只 fsync 一次；
/// 逐条调用 <see cref="RenameEntry"/> 是它的单条特例。同一 pak 路径的并发改名由进程内引用计数路径锁
/// 串行化，不会互相覆盖或写出半新半旧的 TOC。
/// </para>
/// </remarks>
public sealed class RePakRenamer : IPakEntryRenamer, IBatchPakEntryRenamer, IPakTocRecovery
{
    private const short FeatureTocEncrypted = 8;
    private const short FeatureExtraTocWord = 16;

    private readonly IAppLogger _logger;

    /// <summary>装配改名前像日志端口。</summary>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    public RePakRenamer(IAppLogger? logger = null)
    {
        _logger = logger ?? AppLogAdapter.Instance;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">条目序号越界。</exception>
    /// <exception cref="InvalidDataException">pak 结构不合法或 TOC 解密失败。</exception>
    /// <exception cref="IOException">读取/备份/写入 pak 失败。</exception>
    public void RenameEntry(string pakPath, int entryIndex, ulong newHash, bool backup = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);

        RenameEntries(pakPath, [new PakEntryRename(entryIndex, newHash)], backup);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="renames"/> 含重复条目序号。</exception>
    /// <exception cref="ArgumentOutOfRangeException">任一条目序号越界。</exception>
    /// <exception cref="InvalidDataException">pak 结构不合法或 TOC 解密失败。</exception>
    /// <exception cref="IOException">读取/备份/写入 pak 失败。</exception>
    public void RenameEntries(string pakPath, IReadOnlyList<PakEntryRename> renames, bool backup = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentNullException.ThrowIfNull(renames);
        if (renames.Count == 0)
        {
            return;
        }

        // 先做纯内存校验：非法/重复条目序号在读取 TOC 前拒绝；条目序号上界在读完 TOC、写 TOC 前统一拒绝，避免半批落盘。
        HashSet<int> indexes = new(renames.Count);
        foreach (PakEntryRename rename in renames)
        {
            if (rename.EntryIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(renames), rename.EntryIndex, "条目序号不能为负");
            }

            if (!indexes.Add(rename.EntryIndex))
            {
                throw new ArgumentException($"同一批次里条目序号 {rename.EntryIndex} 重复", nameof(renames));
            }
        }

        string targetPath = Path.GetFullPath(pakPath);
        using PakPathLock.Scope scope = PakPathLock.Acquire(targetPath);
        RenameEntriesCore(targetPath, renames, backup, _logger);
    }

    /// <inheritdoc />
    public bool TryRecoverPending(string pakPath, out string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        return PakTocPreImage.TryRecoverPending(pakPath, out message, _logger);
    }

    /// <inheritdoc />
    public bool TryRecover(string pakPath, out string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        return PakTocPreImage.TryRecover(pakPath, out message, _logger);
    }

    /// <inheritdoc />
    public IDisposable? AcquireReadLock(string pakPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        return PakPathLock.Acquire(Path.GetFullPath(pakPath));
    }

    private static void RenameEntriesCore(
        string targetPath,
        IReadOnlyList<PakEntryRename> renames,
        bool backup,
        IAppLogger logger)
    {
        // 上次写入被中断（进程被杀/断电）时先自动恢复条目表，再继续本次改名。
        PakTocPreImage.TryRecover(targetPath, out _, logger);

        int entrySize;
        int hashFieldOffset;
        int entryCount;
        long fileLength;
        long dataStart;
        short feature;
        byte major;
        byte minor;
        byte[] storedToc;
        byte[] plainToc;
        byte[] keyBlock;
        ulong dataSample;
        using (FileStream source = new(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            PakPlainHeader header = PakPlainToc.ReadHeader(source);
            storedToc = PakPlainToc.ReadToc(source, header);
            entrySize = PakEntryLayout.EntrySize(header.Major);
            hashFieldOffset = PakEntryLayout.HashFieldOffset(header.Major);
            entryCount = header.Count;
            fileLength = source.Length;
            major = header.Major;
            minor = header.Minor;
            feature = header.Feature;
            // feature 0x10（附加字）即便没有加密 TOC 也会把数据区后移 4 字节。
            dataStart = PakTocPreImage.DataStart(header);

            keyBlock = [];
            plainToc = storedToc;
            if ((header.Feature & FeatureTocEncrypted) != 0)
            {
                // feature 8/24：TOC 以包内 128 字节密钥块做异或混淆；先解出明文，改完再按同一密钥块加密写回。
                long keyOffset = PakPlainToc.TocEnd(header);
                if ((header.Feature & FeatureExtraTocWord) != 0)
                {
                    keyOffset += 4;
                }

                if (keyOffset + PakCipher.TocKeyBlockSize > source.Length)
                {
                    throw new InvalidDataException("pak 在 TOC 后缺少 TOC 密钥块");
                }

                source.Position = keyOffset;
                keyBlock = new byte[PakCipher.TocKeyBlockSize];
                source.ReadExactly(keyBlock);
                plainToc = PakCipher.CryptToc(storedToc, keyBlock);
            }

            dataSample = PakTocPreImage.ComputeDataSample(source, dataStart);
        }

        foreach (PakEntryRename rename in renames)
        {
            if (rename.EntryIndex >= entryCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(renames),
                    rename.EntryIndex,
                    $"条目序号越界（共 {entryCount} 个条目）");
            }
        }

        // 在原始 TOC 的副本上原地写哈希：包头、其余条目字段、数据区都不动。
        byte[] updatedToc = plainToc.ToArray();
        Span<byte> hashBytes = stackalloc byte[sizeof(ulong)];
        foreach (PakEntryRename rename in renames.OrderBy(static rename => rename.EntryIndex))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(hashBytes, (uint)(rename.NewHash & 0xFFFFFFFF));
            BinaryPrimitives.WriteUInt32LittleEndian(hashBytes[4..], (uint)(rename.NewHash >> 32));
            hashBytes.CopyTo(updatedToc.AsSpan((rename.EntryIndex * entrySize) + hashFieldOffset, sizeof(ulong)));
        }

        // 加密 TOC 用同一密钥块重新混淆：密钥块本身留在原位置不动。
        byte[] storedUpdatedToc = keyBlock.Length > 0 ? PakCipher.CryptToc(updatedToc, keyBlock) : updatedToc;

        // 未提交日志必须先于任何写入：pending 写入失败时 pak 一个字节都没动；
        // TOC 写入中途失败依赖回滚，回滚失败时保留 pending 日志待下次恢复。
        PakTocPreImage.WritePending(
            targetPath,
            new PakTocPreImage.Snapshot(
                PakTocPreImage.CurrentVersion,
                fileLength,
                major,
                minor,
                feature,
                entryCount,
                dataSample,
                storedToc));

        try
        {
            WriteToc(targetPath, storedUpdatedToc);
        }
        catch (Exception exception)
        {
            // 写入中途失败（磁盘满/IO 错误）：用内存里的原始 TOC 立即回滚。
            try
            {
                WriteToc(targetPath, storedToc);
            }
            catch (Exception restoreFailure) when (restoreFailure is IOException or UnauthorizedAccessException)
            {
                // 回滚也失败：保留未提交日志，下次打开 pak 会自动恢复。
                throw new IOException(
                    $"改名写入失败且无法自动回滚 pak 条目表；下次打开会自动从 "
                    + $"{Path.GetFileName(targetPath)}{PakTocPreImage.PendingSuffix} 恢复",
                    new AggregateException(exception, restoreFailure));
            }

            PakTocPreImage.DiscardPending(targetPath);
            throw;
        }

        if (!backup)
        {
            PakTocPreImage.DiscardPending(targetPath);
            return;
        }

        try
        {
            PakTocPreImage.Commit(targetPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 前像提交失败不能让 pak 停留在「已改写但没有备份」的状态：回滚并保留未提交日志。
            try
            {
                WriteToc(targetPath, storedToc);
                PakTocPreImage.DiscardPending(targetPath);
            }
            catch (Exception restoreFailure) when (restoreFailure is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"前像提交失败且无法回滚 pak 条目表；下次打开会自动从 "
                    + $"{Path.GetFileName(targetPath)}{PakTocPreImage.PendingSuffix} 恢复",
                    new AggregateException(exception, restoreFailure));
            }

            throw;
        }
    }

    /// <summary>把 TOC 字节写回 pak 包头之后并刷盘。</summary>
    /// <param name="pakPath">pak 绝对路径。</param>
    /// <param name="toc">要写入的 TOC 字节（加密 pak 为密文）。</param>
    /// <exception cref="IOException">写入失败。</exception>
    private static void WriteToc(string pakPath, byte[] toc)
    {
        using FileStream writable = new(pakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        writable.Position = PakPlainToc.HeaderSize;
        writable.Write(toc);
        writable.Flush(flushToDisk: true);
    }
}
