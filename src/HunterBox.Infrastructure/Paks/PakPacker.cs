using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using HunterBox.Application.Paks;

using HunterBox.Infrastructure.IO;
namespace HunterBox.Infrastructure.Paks;

/// <summary>把目录重新打包成 pak（TOC 加密按原算法重新加密，先写临时文件再原子替换）。</summary>
/// <remarks>
/// <para>
/// 条目按 64 位组合哈希升序写入条目表；条目数据默认原样存放，只有配置了压缩方式且确实能压缩变小时才压缩，
/// 避免压缩后反而变大。失败时删除临时文件与临时条目，目标 pak 保持原样。
/// </para>
/// <para>
/// 全流程流式分块读写（P-03）：不再把整个文件读进内存，也不在压缩后再复制一份内存副本；
/// 原样条目一次遍历同时完成「写数据 + 算 checksum」，压缩条目先流式压缩到临时条目文件、
/// 同时计算原内容 checksum，只有确实变小时才把它写到输出（无收益时回退原样，同样流式）。
/// 因此峰值内存与单个文件大小无关，只与分块缓冲/压缩器缓冲有关。
/// </para>
/// </remarks>
internal static class PakPacker
{
    private const string UnknownEntryPrefix = "__Unknown/";

    /// <summary>流式读写的分块大小（1MB）。</summary>
    private const int StreamBufferSize = 1024 * 1024;

    /// <summary>打包。</summary>
    /// <param name="sourceDirectory">源目录。</param>
    /// <param name="pakPath">输出 pak 路径。</param>
    /// <param name="profile">重打包配置。</param>
    /// <param name="progress">进度回调；可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static void Pack(
        string sourceDirectory,
        string pakPath,
        PakRepackProfile profile,
        IProgress<PakRepackProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        string sourceRoot = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"源目录不存在: {sourceRoot}");
        }

        string targetPath = Path.GetFullPath(pakPath);
        string targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException("无法确定输出 pak 所在目录", nameof(pakPath));
        PackPlan plan = CreatePlan(profile);
        Directory.CreateDirectory(targetDirectory);

        List<string> files = CollectFiles(sourceRoot, targetPath);
        int total = files.Count;
        int entrySize = PakEntryLayout.EntrySize(profile.Major);
        long tocLength = (long)total * entrySize;
        if (tocLength > int.MaxValue)
        {
            throw new InvalidDataException($"源目录文件过多（{total} 个），超出 pak 条目表容量");
        }

        long dataStart = PakArchiveCodec.HeaderSize + tocLength + plan.ExtraBytes.Length;
        List<PackedEntry> entries = new(total);

        string tempPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            using (FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                WriteHeader(output, profile, total);
                WriteZeros(output, tocLength + plan.ExtraBytes.Length);

                long position = dataStart;
                for (int i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string file = files[i];
                    string relativePath = GetRelativePath(sourceRoot, file);
                    ulong hash = ResolveEntryHash(relativePath);
                    EntryWriteResult written = WriteEntry(
                        output,
                        file,
                        position,
                        profile,
                        targetDirectory,
                        Path.GetFileName(targetPath),
                        buffer,
                        cancellationToken);

                    entries.Add(new PackedEntry(
                        hash,
                        position,
                        written.StoredLength,
                        written.ContentLength,
                        written.Attributes,
                        written.Checksum));
                    position += written.StoredLength;

                    progress?.Report(new PakRepackProgress(i + 1, total, relativePath));
                }

                entries.Sort(static (left, right) => left.Hash.CompareTo(right.Hash));
                ThrowIfDuplicateHashes(entries);

                byte[] toc = BuildToc(entries, profile, entrySize, tocLength);
                if (profile.IsTocEncrypted)
                {
                    toc = PakCipher.CryptToc(toc, plan.KeyBlock);
                }

                output.Position = PakArchiveCodec.HeaderSize;
                output.Write(toc);
                if (plan.ExtraBytes.Length > 0)
                {
                    output.Position = PakArchiveCodec.HeaderSize + tocLength;
                    output.Write(plan.ExtraBytes);
                }

                output.Flush(flushToDisk: true);
            }

            // 同目录重命名替换：Move 失败时目标保持原样；替换成功后不保留旧版本（需要旧文件请自行备份）。
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            SystemFileSystem.Instance.TryDeleteFile(tempPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>把一个源文件写成 pak 内的一条条目（流式，边写边算 checksum）。</summary>
    /// <param name="output">输出 pak 流。</param>
    /// <param name="sourcePath">源文件绝对路径。</param>
    /// <param name="position">条目数据起始偏移。</param>
    /// <param name="profile">打包配置。</param>
    /// <param name="tempDirectory">临时条目文件所在目录（与输出同卷）。</param>
    /// <param name="targetFileName">输出 pak 文件名（用于临时文件命名过滤）。</param>
    /// <param name="buffer">共享分块缓冲。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果（存储长度、原内容长度、属性、checksum）。</returns>
    private static EntryWriteResult WriteEntry(
        FileStream output,
        string sourcePath,
        long position,
        PakRepackProfile profile,
        string tempDirectory,
        string targetFileName,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        if (profile.Major < 4 || profile.CompressionType == PakEntryCodec.CompressionNone)
        {
            (long rawLength, ulong rawChecksum) = WriteRaw(output, sourcePath, position, buffer, cancellationToken);
            return new EntryWriteResult(rawLength, rawLength, 0, rawChecksum);
        }

        // 压缩路径：先把源文件流式压缩到临时条目文件，同时计算原内容 checksum（一次读盘完成两件事）。
        string entryTempPath = Path.Combine(
            tempDirectory,
            $".{targetFileName}.{Guid.NewGuid():N}.entry.tmp");
        try
        {
            long contentLength = 0;
            PakChecksumAccumulator checksum = PakChecksum.CreateAccumulator();
            long compressedLength;
            using (FileStream source = OpenSource(sourcePath))
            using (FileStream compressedTemp = new(entryTempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1))
            {
                using (Stream compressor = PakEntryCodec.CreateCompressionStream(profile.CompressionType, compressedTemp))
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = source.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            break;
                        }

                        checksum.Append(buffer.AsSpan(0, read));
                        compressor.Write(buffer, 0, read);
                        contentLength += read;
                    }
                }

                compressedLength = compressedTemp.Length;
            }

            // 资源加密按 16× 膨胀：压缩数据超过 32MB 时加密后就会超过 512MB 存储上限，
            // 直接回退明文存储。加密本身已流式化，峰值不再是内存问题，但输出体积仍需受控。
            bool encryptedTooLarge = profile.EncryptionType != 0
                && compressedLength > PakEntryCodec.MaxDecompressedSizeBytes / 16;
            if (compressedLength >= contentLength
                || contentLength > PakEntryCodec.MaxDecompressedSizeBytes
                || encryptedTooLarge)
            {
                // 压缩没有收益、解压后超过 512MB 上限，或加密膨胀过大：按原样存储。
                // 保证 pack→unpack 闭环——否则会生成一个「自己都拒绝解包」的合法大条目（N-18/）。
                (long fallbackLength, ulong fallbackChecksum) =
                    WriteRaw(output, sourcePath, position, buffer, cancellationToken);
                return new EntryWriteResult(fallbackLength, fallbackLength, 0, fallbackChecksum);
            }

            long attributes = (long)profile.CompressionType | ((long)profile.EncryptionType << 16);
            output.Position = position;
            if (profile.EncryptionType == 0)
            {
                // 明文压缩数据：直接从临时条目文件流式拷到输出。
                using FileStream tempRead = new(entryTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
                CopyStream(tempRead, output, buffer, cancellationToken);
            }
            else
            {
                // 128 字节密文块彼此独立：直接从临时压缩文件流式加密到 pak，峰值只与单块缓冲有关。
                using FileStream compressedInput = new(
                    entryTempPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1);
                PakCipher.EncryptResource(compressedInput, output, compressedInput.Length, cancellationToken);
                compressedLength = PakCipher.EncryptResourceSize(compressedInput.Length);
            }

            return new EntryWriteResult(compressedLength, contentLength, attributes, checksum.Value);
        }
        finally
        {
            SystemFileSystem.Instance.TryDeleteFile(entryTempPath);
        }
    }

    /// <summary>把源文件原样流式写入输出，同时计算 checksum。</summary>
    private static (long Length, ulong Checksum) WriteRaw(
        FileStream output,
        string sourcePath,
        long position,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        output.Position = position;
        PakChecksumAccumulator checksum = PakChecksum.CreateAccumulator();
        long length = 0;
        using FileStream source = OpenSource(sourcePath);
        if (source.Length > int.MaxValue)
        {
            // 条目长度字段按 32 位有符号整数表示，超过 2GB 无法安全写入 TOC。
            throw new NotSupportedException(
                $"条目 {Path.GetFileName(sourcePath)} 大小 {source.Length} 字节超过 2GB，当前 pak 格式不支持");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            checksum.Append(buffer.AsSpan(0, read));
            length += read;
        }

        return (length, checksum.Value);
    }

    /// <summary>把输入流流式拷到输出（复用分块缓冲）。</summary>
    private static void CopyStream(
        Stream source,
        Stream destination,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return;
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static FileStream OpenSource(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);

    private static PackPlan CreatePlan(PakRepackProfile profile)
    {
        if (!PakArchiveInfo.IsSupportedMajorMinor(profile.Major, profile.Minor))
        {
            throw new ArgumentException(
                $"不支持的 pak 版本 {profile.Major}.{profile.Minor}（仅支持主版本 2/4 且次版本 0/1）",
                nameof(profile));
        }

        if (!PakArchiveInfo.IsSupportedFeature(profile.Feature))
        {
            throw new ArgumentException($"不支持的 pak feature {profile.Feature}（仅支持 0 / 8 / 24）", nameof(profile));
        }

        if (!PakEntryCodec.IsSupportedCompression(profile.CompressionType))
        {
            throw new ArgumentException($"不支持的条目压缩方式 {profile.CompressionType}", nameof(profile));
        }

        if (profile.EncryptionType is < 0 or > 5)
        {
            throw new ArgumentException($"不支持的条目加密方式 {profile.EncryptionType}（支持 0–5）", nameof(profile));
        }

        if (profile.Major == 2 && (profile.CompressionType != 0 || profile.EncryptionType != 0))
        {
            throw new ArgumentException("v2 pak 的条目表没有压缩/加密字段，不能配置条目压缩或加密", nameof(profile));
        }

        if (profile.EncryptionType != 0 && profile.CompressionType == 0)
        {
            throw new ArgumentException("条目加密必须同时配置压缩方式", nameof(profile));
        }

        if (profile.TocKeyBlob.Length is not (0 or PakCipher.TocKeyBlockSize))
        {
            throw new ArgumentException(
                $"TOC 密钥块长度必须为 {PakCipher.TocKeyBlockSize} 字节或为空",
                nameof(profile));
        }

        if (profile.Feature24Payload.Length > 4)
        {
            throw new ArgumentException("feature 0x10 附加数据长度不能超过 4 字节", nameof(profile));
        }

        byte[] keyBlock = [];
        if (profile.IsTocEncrypted)
        {
            keyBlock = profile.TocKeyBlob.Length > 0
                ? [.. profile.TocKeyBlob]
                : PakCipher.CreateTocKeyBlock();
        }

        // feature 已在上面校验为 0 / 8 / 24：24 = TOC 加密 + 4 字节附加数据，0 / 8 只有（可能为空的）TOC 密钥块。
        byte[] extraBytes;
        if (profile.Feature == 24)
        {
            extraBytes = new byte[4 + keyBlock.Length];
            profile.Feature24Payload.CopyTo(extraBytes, 0);
            keyBlock.CopyTo(extraBytes, 4);
        }
        else
        {
            extraBytes = keyBlock;
        }

        return new PackPlan(keyBlock, extraBytes);
    }

    private static List<string> CollectFiles(string sourceRoot, string targetPath)
    {
        string targetFileName = Path.GetFileName(targetPath);
        List<string> files = [];
        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string fullPath = Path.GetFullPath(file);
            if (string.Equals(fullPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relativePath = GetRelativePath(sourceRoot, fullPath);
            if (string.Equals(relativePath, "file.list", StringComparison.OrdinalIgnoreCase))
            {
                // 解包清单是外部工具的元数据，不属于 pak 内容。
                continue;
            }

            if (IsTemporaryArtifact(relativePath, targetFileName))
            {
                continue;
            }

            files.Add(fullPath);
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static bool IsTemporaryArtifact(string relativePath, string targetFileName)
    {
        // 输出 pak 可能位于源目录的子目录里：按文件名判断，任意层级的临时产物都要过滤掉。
        string name = Path.GetFileName(relativePath);
        return name.StartsWith('.' + targetFileName + '.', StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRelativePath(string sourceRoot, string fullPath) =>
        Path.GetRelativePath(sourceRoot, fullPath).Replace('\\', '/');

    private static ulong ResolveEntryHash(string relativePath)
    {
        if (!relativePath.StartsWith(UnknownEntryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // 引擎约定：组合哈希低 32 位 = hash(全小写路径)、高 32 位 = hash(全大写路径)。
            // 磁盘上的大小写不可信（Windows 大小写不敏感、用户可能随意改名），必须先归一为小写；
            // 这与 PakModelRewriteService 改写路径时的约定一致（写回前统一 ToLowerInvariant）。
            return PakPathHasher.HashPath(relativePath.ToLowerInvariant());
        }

        string fileName = relativePath[UnknownEntryPrefix.Length..];
        if (fileName.Contains('/'))
        {
            throw new InvalidDataException($"未知条目路径不应包含子目录: {relativePath}");
        }

        int dotIndex = fileName.IndexOf('.');
        string hex = dotIndex >= 0 ? fileName[..dotIndex] : fileName;
        if (hex.Length is 0 or > 16
            || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
        {
            throw new InvalidDataException($"无法从 __Unknown 文件名解析条目哈希: {relativePath}");
        }

        return hash;
    }

    private static void WriteHeader(Stream output, PakRepackProfile profile, int entryCount)
    {
        Span<byte> header = stackalloc byte[PakArchiveCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, RePakReader.Magic);
        header[4] = profile.Major;
        header[5] = profile.Minor;
        BinaryPrimitives.WriteInt16LittleEndian(header[6..], profile.Feature);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], profile.Fingerprint);
        output.Write(header);
    }

    private static byte[] BuildToc(
        List<PackedEntry> entries,
        PakRepackProfile profile,
        int entrySize,
        long tocLength)
    {
        byte[] toc = new byte[tocLength];
        for (int i = 0; i < entries.Count; i++)
        {
            PackedEntry entry = entries[i];
            Span<byte> raw = toc.AsSpan(i * entrySize, entrySize);
            // 条目字段布局由共享布局单点定义（v2 / v4 的哈希位置不同）。
            PakEntryLayout.Write(
                profile.Major,
                raw,
                entry.Hash,
                entry.Offset,
                entry.StoredLength,
                entry.DecompressedLength,
                entry.Attributes,
                entry.Checksum);
        }

        return toc;
    }

    private static void ThrowIfDuplicateHashes(List<PackedEntry> entries)
    {
        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].Hash == entries[i - 1].Hash)
            {
                throw new InvalidDataException(
                    $"多个文件映射到同一条目哈希 {entries[i].Hash:X16}，请检查路径大小写或 __Unknown 文件");
            }
        }
    }

    private static void WriteZeros(Stream output, long count)
    {
        Span<byte> zeros = stackalloc byte[4096];
        while (count > 0)
        {
            int chunk = (int)Math.Min(count, zeros.Length);
            output.Write(zeros[..chunk]);
            count -= chunk;
        }
    }

    private sealed record PackPlan(byte[] KeyBlock, byte[] ExtraBytes);

    /// <summary>单条目写入结果。</summary>
    /// <param name="StoredLength">实际写入 pak 的字节数（压缩/加密后）。</param>
    /// <param name="ContentLength">原内容字节数。</param>
    /// <param name="Attributes">压缩/加密属性位。</param>
    /// <param name="Checksum">原内容校验和。</param>
    private readonly record struct EntryWriteResult(
        long StoredLength,
        long ContentLength,
        long Attributes,
        ulong Checksum);

    private sealed record PackedEntry(
        ulong Hash,
        long Offset,
        long StoredLength,
        long DecompressedLength,
        long Attributes,
        ulong Checksum);
}
