namespace HunterBox.Application.Paks;
using HunterBox.Domain.Paks;

/// <summary>重打包 pak 时保持的属性（版本、feature、条目编码方式与 TOC 加密材料）。</summary>
/// <remarks>
/// 通常由 <see cref="IPakRepacker.Capture"/> 从原 pak 记录，再原样交给
/// <see cref="IPakRepacker.Pack(string, string, PakRepackProfile, CancellationToken)"/>；也可以手工构造来自定义输出形态
/// （例如把 feature 改成 0，输出不加密 TOC 的普通 mod pak）。条目数量以源目录实际文件数为准，
/// <see cref="EntryCount"/> 仅供展示/比较。
/// </remarks>
public sealed record PakRepackProfile
{
    /// <summary>主版本（2 或 4），默认 4。</summary>
    public byte Major { get; init; } = 4;

    /// <summary>次版本（0 或 1）。</summary>
    public byte Minor { get; init; }

    /// <summary>feature 位：0 = 明文 TOC，8 = 加密 TOC，24 = 加密 TOC 且 TOC 与密钥之间有 4 字节附加数据。</summary>
    public short Feature { get; init; }

    /// <summary>包头指纹，原样保留。</summary>
    public uint Fingerprint { get; init; }

    /// <summary>原 pak 的条目数（仅记录，不参与打包）。</summary>
    public int EntryCount { get; init; }

    /// <summary>重打包时写入每个条目 attributes 的默认压缩方式（0 = 原样存放，1 = DEFLATE，2 = ZSTD）。</summary>
    public int CompressionType { get; init; }

    /// <summary>
    /// 重打包时写入每个条目 attributes 的默认加密方式（0 = 不加密，1–5 = 资源加密）。
    /// 只有 <see cref="CompressionType"/> 非 0 时才能使用。
    /// </summary>
    public int EncryptionType { get; init; }

    /// <summary>原 pak 的 128 字节 TOC 加密密钥块；重打包时原样写回，保证解出的 XOR 密钥不变。</summary>
    /// <remarks>为空时打包会生成一个新的密钥块（TOC 加密算法是公开可逆的，不需要私钥）。</remarks>
    public byte[] TocKeyBlob { get; init; } = [];

    /// <summary>feature 含 0x10 位时，TOC 与 TOC 密钥之间的 4 字节附加数据；为空时写 4 个 0。</summary>
    public byte[] Feature24Payload { get; init; } = [];

    /// <summary>TOC 是否加密（feature 含 0x8 位）。</summary>
    public bool IsTocEncrypted => PakFeature.IsTocEncrypted(Feature);

    /// <summary>
    /// Capture 记录属性时产生的降级说明：压缩方式混合时统一为不压缩；
    /// 加密方式混合或「未压缩但加密」时统一降级为明文（不加密）并记录原因。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
