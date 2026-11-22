using HunterBox.Application.Paks;

namespace HunterBox.Infrastructure.Paks;

/// <summary>解析后的 pak：解密后的条目表、TOC 加密材料与数据区起点。</summary>
internal sealed class ParsedPak
{
    /// <summary>包头与（解密后的）条目表。</summary>
    public required PakArchiveInfo Archive { get; init; }

    /// <summary>包内 128 字节 TOC 密钥块；未加密时为空数组。</summary>
    public required byte[] TocKeyBlock { get; init; }

    /// <summary>TOC 与密钥块之间的 4 字节附加数据（feature 含 0x10 时存在）；否则为空数组。</summary>
    public required byte[] TocFeaturePayload { get; init; }

    /// <summary>数据区起始偏移（头和 TOC/密钥块之后）。</summary>
    public required long DataStart { get; init; }
}

/// <summary>pak 二进制格式的读取、TOC 解密与条目表校验。</summary>
internal static class PakArchiveCodec
{
    /// <summary>包头长度。</summary>
    public const int HeaderSize = PakPlainToc.HeaderSize;

    /// <summary>条目数量上限（防御损坏/恶意 pak 撑爆内存）。</summary>
    public const int MaxEntryCount = PakPlainToc.MaxEntryCount;

    private const short FeatureTocEncrypted = 8;
    private const short FeatureExtraTocWord = 16;

    /// <summary>条目在 pak 占用的字节数（压缩大小为 0 时回退到解压大小）。</summary>
    public static long StoredSize(PakEntryInfo entry) =>
        entry.CompressedSize > 0 ? entry.CompressedSize : entry.DecompressedSize;

    /// <summary>读取包头、解密（如需）并解析条目表。</summary>
    /// <param name="stream">可定位的 pak 数据流，从 0 开始读取。</param>
    /// <returns>解析结果。</returns>
    /// <exception cref="InvalidDataException">结构不合法、条目越界或 TOC 解密失败。</exception>
    /// <exception cref="NotSupportedException">版本 / feature / 压缩 / 加密方式不受支持。</exception>
    public static ParsedPak Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("pak 数据流必须可定位", nameof(stream));
        }

        long length = stream.Length;

        // 包头 / TOC / 条目布局解码与 RePakReader 共用 PakPlainToc；本方法在明文层之上再做
        // feature 白名单、TOC 解密与逐条 ValidateEntry（RePakReader 刻意不做这些）。
        PakPlainHeader header = PakPlainToc.ReadHeader(stream);
        if (header.Feature is not (0 or 8 or 24))
        {
            throw new NotSupportedException($"不支持的 pak feature={header.Feature}（仅支持 0 / 8 / 24）");
        }

        byte[] toc = PakPlainToc.ReadToc(stream, header);

        byte[] featurePayload = [];
        byte[] keyBlock = [];
        long position = PakPlainToc.TocEnd(header);
        if ((header.Feature & FeatureExtraTocWord) != 0)
        {
            if (position + 4 > length)
            {
                throw new InvalidDataException("pak 在 TOC 后缺少 4 字节附加数据");
            }

            featurePayload = new byte[4];
            stream.ReadExactly(featurePayload);
            position += 4;
        }

        if ((header.Feature & FeatureTocEncrypted) != 0)
        {
            if (position + PakCipher.TocKeyBlockSize > length)
            {
                throw new InvalidDataException("pak 在 TOC 后缺少 TOC 密钥块");
            }

            keyBlock = new byte[PakCipher.TocKeyBlockSize];
            stream.ReadExactly(keyBlock);
            position += PakCipher.TocKeyBlockSize;
            toc = PakCipher.CryptToc(toc, keyBlock);
        }

        long dataStart = position;
        List<PakEntryInfo> entries = PakPlainToc.ReadEntries(header, toc);
        for (int i = 0; i < entries.Count; i++)
        {
            ValidateEntry(entries[i], i, dataStart, length);
        }

        PakArchiveInfo archive = new(header.Major, header.Minor, header.Feature, header.Fingerprint, entries);
        return new ParsedPak
        {
            Archive = archive,
            TocKeyBlock = keyBlock,
            TocFeaturePayload = featurePayload,
            DataStart = dataStart,
        };
    }

    private static void ValidateEntry(PakEntryInfo entry, int index, long dataStart, long fileLength)
    {
        int compression = entry.CompressionType;
        int encryption = entry.EncryptionType;
        if (compression != 0 && !PakEntryCodec.IsSupportedCompression(compression))
        {
            throw new NotSupportedException($"条目 {index}（hash={entry.Hash:X16}）的压缩方式 {compression} 不受支持");
        }

        if (encryption is < 0 or > 5)
        {
            throw new NotSupportedException($"条目 {index}（hash={entry.Hash:X16}）的加密方式 {encryption} 不受支持");
        }

        if (entry.Offset < 0 || entry.CompressedSize < 0 || entry.DecompressedSize < 0)
        {
            throw new InvalidDataException($"条目 {index}（hash={entry.Hash:X16}）的偏移或大小非法");
        }

        long stored = StoredSize(entry);
        if (stored > 0 && compression != 0 && entry.CompressedSize <= 0)
        {
            throw new InvalidDataException($"条目 {index}（hash={entry.Hash:X16}）声明了压缩方式但没有压缩大小");
        }

        if (stored == 0)
        {
            if (entry.Offset > fileLength)
            {
                throw new InvalidDataException($"条目 {index}（hash={entry.Hash:X16}）超出 pak 文件范围");
            }

            return;
        }

        if (entry.Offset < dataStart || entry.Offset > fileLength - stored)
        {
            throw new InvalidDataException($"条目 {index}（hash={entry.Hash:X16}）超出 pak 数据区范围");
        }
    }
}
