using HunterBox.Application.Paks;

namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// 只读的 RE Engine pak 解析器（荒野 / 崛起）。
/// </summary>
/// <remarks>
/// <para>
/// 格式见 <c>docs/PAK.md</c>：16 字节包头 + 固定长度条目表（v4 每项 48 字节，v2 每项 24 字节）。
/// 本类不做解密与解压：带加密 feature（8 / 24）的 pak 返回的是**密文原样解析**结果
/// （<see cref="PakArchiveInfo.IsTocEncrypted"/> 为 <see langword="true"/>，其中 offset / 大小 / 哈希字段均不可信），
/// 调用方必须先判断该状态，改用 <see cref="PakRepacker.Inspect"/>（先解密）或整包解包→重打包，不能把结果写回。
/// </para>
/// <para>
/// 明文结构（包头 / TOC 定位读取 / 条目布局解码）与 <see cref="PakArchiveCodec"/> 共用 <see cref="PakPlainToc"/>，
/// 但本类刻意保留宽松容忍度：不做 feature 白名单校验、不逐条校验条目字段（畸形/加密 pak 也能读出原始结构）。
/// </para>
/// </remarks>
public sealed class RePakReader : IPakArchiveReader
{
    /// <summary>pak 魔数（'KPKA'）。</summary>
    public const uint Magic = 1095454795u;

    /// <summary>只读取包头与条目表。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <returns>解析结果；TOC 加密时条目字段是密文原样解析，见 <see cref="PakArchiveInfo.IsTocEncrypted"/>。</returns>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    /// <exception cref="InvalidDataException">文件过短、魔数不符、版本不支持或条目表越界。</exception>
    public PakArchiveInfo Read(string pakPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        if (!File.Exists(pakPath))
        {
            throw new FileNotFoundException("pak 文件不存在", pakPath);
        }

        using FileStream stream = File.OpenRead(pakPath);
        return Read(stream);
    }

    /// <summary>从已打开的 pak 流读取包头与条目表（预检与解析复用同一句柄）。</summary>
    /// <param name="stream">可读可定位的 pak 文件流；从当前位置开始读取，调用方负责先重置到 0。</param>
    /// <returns>解析结果；TOC 加密时条目字段是密文原样解析，见 <see cref="PakArchiveInfo.IsTocEncrypted"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="InvalidDataException">流过短、魔数不符、版本不支持或条目表越界。</exception>
    public static PakArchiveInfo Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PakPlainHeader header = PakPlainToc.ReadHeader(stream);
        byte[] toc = PakPlainToc.ReadToc(stream, header);
        List<PakEntryInfo> entries = PakPlainToc.ReadEntries(header, toc);
        return new PakArchiveInfo(header.Major, header.Minor, header.Feature, header.Fingerprint, entries);
    }
}
