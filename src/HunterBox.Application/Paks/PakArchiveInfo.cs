namespace HunterBox.Application.Paks;
using HunterBox.Domain.Paks;

/// <summary>pak 内的一个文件条目（RE Engine的 pak 不保存文件名，只有哈希）。</summary>
/// <param name="HashLower">路径哈希低 32 位。</param>
/// <param name="HashUpper">路径哈希高 32 位。</param>
/// <param name="Offset">数据在 pak 内的偏移。</param>
/// <param name="CompressedSize">压缩后大小。</param>
/// <param name="DecompressedSize">解压后大小。</param>
/// <param name="Attributes">属性位：低 4 位是压缩方式，16–23 位是加密方式。</param>
/// <param name="Checksum">条目校验和。</param>
public sealed record PakEntryInfo(
    uint HashLower,
    uint HashUpper,
    long Offset,
    long CompressedSize,
    long DecompressedSize,
    long Attributes,
    ulong Checksum)
{
    /// <summary>压缩方式（<c>attributes &amp; 0xF</c>）。</summary>
    public int CompressionType => (int)(Attributes & 0xF);

    /// <summary>加密方式（<c>(attributes &amp; 0xFF0000) &gt;&gt; 16</c>）。</summary>
    public int EncryptionType => (int)((Attributes & 0xFF0000) >> 16);

    /// <summary>路径哈希（高 32 位在前）。</summary>
    public ulong Hash => ((ulong)HashUpper << 32) | HashLower;

    /// <summary>
    /// 路径哈希是否全为 0（清零型半写 TOC 的判据）。
    /// </summary>
    /// <remarks>
    /// pak 不保存文件名，条目哈希 = 全小写/全大写路径的 Murmur3（见 <c>docs/PAK.md</c>）。
    /// 按格式约定，路径哈希为 0 只会出现在未初始化/被清零的数据中；真实路径碰撞到 0 的概率约 2^-64，
    /// 可忽略但非零，因此这里按「疑似损坏」处理并交由前像恢复/明确失败兜底。
    /// 该判定由 <c>PakEntryPathReader</c> 与 <c>PakTocPreImage</c> 共用，避免两处各写一份。
    /// </remarks>
    public bool HasEmptyPathHash => HashLower == 0 && HashUpper == 0;
}

/// <summary>解析后的 pak 头部信息。</summary>
/// <param name="Major">主版本（2 / 4）。</param>
/// <param name="Minor">次版本。</param>
/// <param name="Feature">feature 位；非 0 表示带混淆/加密。</param>
/// <param name="Fingerprint">pak 指纹。</param>
/// <param name="Entries">条目表。</param>
public sealed record PakArchiveInfo(
    byte Major,
    byte Minor,
    short Feature,
    uint Fingerprint,
    IReadOnlyList<PakEntryInfo> Entries)
{
    /// <summary>判断主/次版本是否受支持（唯一规则：2.0/2.1/4.0/4.1）。</summary>
    /// <param name="major">主版本。</param>
    /// <param name="minor">次版本。</param>
    /// <returns>受支持时返回 <see langword="true"/>。</returns>
    public static bool IsSupportedMajorMinor(byte major, byte minor) =>
        major is 2 or 4 && minor is 0 or 1;

    /// <summary>判断 feature 是否受支持（唯一规则：0 / 8 / 24）。</summary>
    /// <param name="feature">feature 位。</param>
    /// <returns>受支持时返回 <see langword="true"/>。</returns>
    public static bool IsSupportedFeature(short feature) => feature is 0 or 8 or 24;

    /// <summary>TOC（条目表）是否已加密（feature 含 0x8 位，即 feature 8 / 24）。</summary>
    /// <remarks>
    /// TOC 加密时 <c>RePakReader</c> 这类「只解析明文结构」的读取器拿到的是密文原样解析结果，
    /// 其中的 offset / 大小 / 哈希字段都不可信；调用方必须先检查本属性，改走
    /// <c>PakRepacker.Inspect</c>（会先解密）或整包解包→重打包路径，绝不能再把结果写回。
    /// </remarks>
    public bool IsTocEncrypted => PakFeature.IsTocEncrypted(Feature);
}
