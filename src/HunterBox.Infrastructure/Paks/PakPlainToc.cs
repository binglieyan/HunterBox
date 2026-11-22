using System.Buffers.Binary;
using HunterBox.Application.Paks;

namespace HunterBox.Infrastructure.Paks;

/// <summary>明文 pak 包头里的字段（未做 feature / 条目校验，由调用方按各自语义解释）。</summary>
/// <param name="Major">主版本（2 / 4）。</param>
/// <param name="Minor">次版本（0 / 1）。</param>
/// <param name="Feature">feature 位。</param>
/// <param name="Count">条目数量。</param>
/// <param name="Fingerprint">pak 指纹。</param>
internal readonly record struct PakPlainHeader(byte Major, byte Minor, short Feature, int Count, uint Fingerprint);

/// <summary>
/// 明文 pak 的包头与条目表解析公共层。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RePakReader"/>（密文原样、不校验条目）与 <see cref="PakArchiveCodec"/>（解密 TOC、校验条目）
/// 共用这一层的「包头字段 / TOC 定位读取 / 条目布局解码」，格式规则只维护一份，避免两处漂移。
/// </para>
/// <para>
/// 本层刻意不做的判断（由调用方保留各自语义）：不校验 feature 白名单（<see cref="RePakReader"/> 接受任意 feature，
/// 只按位报告 TOC 是否加密）；不校验条目字段范围（<see cref="PakArchiveCodec"/> 在解码后再逐条校验）；
/// 不读取 TOC 之后的密钥块 / 附加数据（那是解密路径的事）。
/// </para>
/// </remarks>
internal static class PakPlainToc
{
    /// <summary>包头长度。</summary>
    public const int HeaderSize = 16;

    /// <summary>条目数量上限（防御损坏/恶意 pak 撑爆内存）。</summary>
    public const int MaxEntryCount = 5_000_000;

    /// <summary>读取并校验包头中与「明文 TOC 定位」共有的部分：长度、魔数、版本。</summary>
    /// <param name="stream">可定位的 pak 数据流；读取后停在包头末尾。</param>
    /// <returns>包头字段；条目数与 feature 是否合法由调用方判断。</returns>
    /// <exception cref="InvalidDataException">文件过短、魔数不符或版本不支持。</exception>
    public static PakPlainHeader ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long length = stream.Length;
        if (length < HeaderSize)
        {
            throw new InvalidDataException($"pak 文件过短（{length} 字节），不足 {HeaderSize} 字节包头");
        }

        stream.Position = 0;
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != RePakReader.Magic)
        {
            throw new InvalidDataException($"不是 RE Engine pak（magic=0x{magic:X8}）");
        }

        byte major = header[4];
        byte minor = header[5];
        if ((major != 2 && major != 4) || (minor != 0 && minor != 1))
        {
            throw new InvalidDataException($"不支持的 pak 版本 {major}.{minor}");
        }

        short feature = BinaryPrimitives.ReadInt16LittleEndian(header[6..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        uint fingerprint = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        return new PakPlainHeader(major, minor, feature, count, fingerprint);
    }

    /// <summary>TOC 结束（= 数据区/附加数据起点）的绝对偏移。</summary>
    /// <param name="header">包头字段。</param>
    /// <returns>TOC 末尾偏移。</returns>
    public static long TocEnd(PakPlainHeader header) =>
        HeaderSize + ((long)header.Count * PakEntryLayout.EntrySize(header.Major));

    /// <summary>校验条目数并读出原始 TOC 字节（不解密、不校验条目字段）。</summary>
    /// <param name="stream">已读过包头的 pak 数据流。</param>
    /// <param name="header">包头字段。</param>
    /// <returns>原始 TOC 字节（长度 = 条目数 × 单条长度）。</returns>
    /// <exception cref="InvalidDataException">条目数异常或条目表超出文件范围。</exception>
    public static byte[] ReadToc(Stream stream, PakPlainHeader header)
    {
        ArgumentNullException.ThrowIfNull(stream);

        int count = header.Count;
        if (count < 0 || count > MaxEntryCount)
        {
            throw new InvalidDataException($"条目数异常: {count}");
        }

        int entrySize = PakEntryLayout.EntrySize(header.Major);
        long tocEnd = TocEnd(header);
        long length = stream.Length;
        if (tocEnd > length)
        {
            throw new InvalidDataException($"条目表超出 pak 文件范围（需要 {tocEnd} 字节，实际 {length} 字节）");
        }

        byte[] toc = new byte[count * entrySize];
        stream.Position = HeaderSize;
        int read = 0;
        while (read < toc.Length)
        {
            int chunk = stream.Read(toc, read, toc.Length - read);
            if (chunk <= 0)
            {
                // 预检已保证正常文件不会走到这里；只有并发截断才可能短读。
                throw new InvalidDataException($"条目表超出 pak 文件范围（需要 {toc.Length} 字节，实际读到 {read} 字节）");
            }

            read += chunk;
        }

        return toc;
    }

    /// <summary>按版本把原始 TOC 字节解码成条目列表（不校验条目字段）。</summary>
    /// <param name="header">包头字段。</param>
    /// <param name="toc">原始 TOC 字节。</param>
    /// <returns>条目列表（顺序与 TOC 一致）。</returns>
    /// <exception cref="ArgumentException"><paramref name="toc"/> 长度不足。</exception>
    public static List<PakEntryInfo> ReadEntries(PakPlainHeader header, byte[] toc)
    {
        ArgumentNullException.ThrowIfNull(toc);

        int entrySize = PakEntryLayout.EntrySize(header.Major);
        long required = (long)header.Count * entrySize;
        if (toc.LongLength < required)
        {
            throw new ArgumentException($"TOC 字节长度不足：{toc.LongLength} < {required}", nameof(toc));
        }

        List<PakEntryInfo> entries = new(header.Count);
        for (int i = 0; i < header.Count; i++)
        {
            entries.Add(PakEntryLayout.Read(header.Major, toc.AsSpan(i * entrySize, entrySize)));
        }

        return entries;
    }
}
