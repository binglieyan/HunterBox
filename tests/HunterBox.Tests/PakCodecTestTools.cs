using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using HunterBox.Application.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// 测试用的 pak 构造工具：按格式文档独立实现 TOC 混淆与资源加密，用来验证生产代码
/// （常量与算法来自格式说明，而不是直接复用生产实现）。
/// </summary>
internal static class PakCodecTestTools
{
    /// <summary>包头长度。</summary>
    public const int HeaderSize = 16;

    private const int TocKeyBlockSize = 128;

    /// <summary>格式文档给出的 TOC 混淆模数（大端十六进制，1024 位）。</summary>
    private const string TocModulusHex =
        "E36B2D353ECF71C61E23F31B95C491B06545CDD3E8E08B35A8B9AFE69A59E3EC" +
        "072074FC26D6624BD2D62DB6C4BA7B1C9D4440C471EAAD908BB78CED23AC2B06" +
        "96671706DFE8EB653613938CC0A23EA1A55D89216D01B63950AE8ADD7B29E789" +
        "1CD9DAEE2CABFC2677BA9334D17CF97110D82133D21675D43BFD237CC1F80B7D";

    /// <summary>格式文档给出的资源加密模数（大端十六进制，256 位）。</summary>
    private const string ResourceModulusHex =
        "0377D865AF025CF0B57736BBD0A0433C7DDF59F8AE78AAD710489188899CD713";

    /// <summary>格式文档给出的资源加密指数（大端十六进制，256 位）。</summary>
    private const string ResourceExponentHex =
        "01209325F43FDF303083EA1613173A163B2B422E85D7D4C7016A345B1F77C2C0";

    private static readonly BigInteger TocModulus = ParseBigInteger(TocModulusHex);
    private static readonly BigInteger ResourceModulus = ParseBigInteger(ResourceModulusHex);
    private static readonly BigInteger ResourceExponent = ParseBigInteger(ResourceExponentHex);

    /// <summary>构造一个固定、可复现的 128 字节 TOC 密钥块。</summary>
    public static byte[] CreateKeyBlock(byte seed = 0x5A)
    {
        byte[] block = new byte[TocKeyBlockSize];
        for (int i = 0; i < block.Length; i++)
        {
            block[i] = (byte)(seed + (i * 37));
        }

        return block;
    }

    /// <summary>独立实现：按格式算法对 TOC 做异或混淆（加密与解密是同一操作）。</summary>
    public static byte[] CryptToc(ReadOnlySpan<byte> toc, ReadOnlySpan<byte> keyBlock)
    {
        BigInteger value = new(keyBlock, isUnsigned: true, isBigEndian: false);
        byte[] key = BigInteger.ModPow(value, 65537, TocModulus).ToByteArray(isUnsigned: true, isBigEndian: false);
        if (key.Length < 32)
        {
            throw new InvalidOperationException("测试密钥块解出的密钥过短");
        }

        byte[] result = toc.ToArray();
        for (int i = 0; i < result.Length; i++)
        {
            result[i] ^= (byte)(i + (key[i % 32] * key[i % 29]));
        }

        return result;
    }

    /// <summary>参考实现：解出资源加密条目承载的压缩数据（含长度/块校验）。</summary>
    public static byte[] DecryptResource(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            throw new InvalidDataException("资源加密条目长度不足 8 字节");
        }

        int cipherLength = data.Length - 8;
        if (cipherLength % 128 != 0)
        {
            throw new InvalidDataException($"资源加密条目数据长度 {data.Length} 不是 128 字节块的整数倍");
        }

        int blockCount = cipherLength / 128;
        long outLength = BinaryPrimitives.ReadInt64LittleEndian(data[..8]);
        if (outLength < 0)
        {
            throw new InvalidDataException($"资源加密条目声明长度异常: {outLength}");
        }

        long payloadLength = Math.Min(outLength, (long)blockCount * 8);
        byte[] output = new byte[blockCount * 8];
        for (int block = 0; block < blockCount; block++)
        {
            ReadOnlySpan<byte> blockData = data.Slice(8 + (block * 128), 128);
            BigInteger value = new(blockData[..64], isUnsigned: true, isBigEndian: false);
            BigInteger dividend = new(blockData[64..], isUnsigned: true, isBigEndian: false);
            BigInteger divisor = BigInteger.ModPow(value, ResourceExponent, ResourceModulus);
            byte[] quotient = BigInteger.Divide(dividend, divisor).ToByteArray(isUnsigned: true, isBigEndian: false);
            quotient.CopyTo(output, block * 8);
        }

        return output.AsSpan(0, (int)payloadLength).ToArray();
    }

    /// <summary>独立实现：把压缩数据加密成资源加密条目格式。</summary>
    public static byte[] EncryptResource(ReadOnlySpan<byte> payload)
    {
        int blockCount = (payload.Length + 7) / 8;
        byte[] result = new byte[8 + (blockCount * 128)];
        BinaryPrimitives.WriteInt64LittleEndian(result, payload.Length);
        Random random = new(20261115);
        Span<byte> valueBytes = stackalloc byte[64];
        for (int block = 0; block < blockCount; block++)
        {
            int sourceOffset = block * 8;
            int chunkLength = Math.Min(8, payload.Length - sourceOffset);
            ulong chunk = 0;
            for (int i = 0; i < chunkLength; i++)
            {
                chunk |= (ulong)payload[sourceOffset + i] << (i * 8);
            }

            valueBytes.Clear();
            random.NextBytes(valueBytes);
            valueBytes[^1] &= 0x7F;
            BigInteger value = new(valueBytes, isUnsigned: false, isBigEndian: false);
            BigInteger divisor = BigInteger.ModPow(value, ResourceExponent, ResourceModulus);
            BigInteger dividend = divisor * chunk;
            Span<byte> target = result.AsSpan(8 + (block * 128), 128);
            target.Clear();
            value.TryWriteBytes(target[..64], out _, isUnsigned: true, isBigEndian: false);
            dividend.TryWriteBytes(target[64..], out _, isUnsigned: true, isBigEndian: false);
        }

        return result;
    }

    /// <summary>
    /// 独立实现：用「最高位为 1」的 64 字节密钥构造资源加密块。
    /// 这是格式允许、游戏/其它打包工具会生成的数据：按有符号解析时该大数为负，
    /// <c>ModPow</c> 结果与商都会带符号，解出的明文错位。用来守住「无符号解析」这条互操作约定。
    /// </summary>
    public static byte[] EncryptResourceWithHighBitKey(ReadOnlySpan<byte> payload)
    {
        int blockCount = (payload.Length + 7) / 8;
        byte[] result = new byte[8 + (blockCount * 128)];
        BinaryPrimitives.WriteInt64LittleEndian(result, payload.Length);
        for (int block = 0; block < blockCount; block++)
        {
            // 固定、可复现的密钥，第 63 字节最高位必为 1（等价其它工具生成的随机密钥）。
            byte[] valueBytes = new byte[64];
            for (int i = 0; i < valueBytes.Length; i++)
            {
                valueBytes[i] = (byte)(0x81 + ((i * 7) % 0x40));
            }

            BigInteger value = new(valueBytes, isUnsigned: true, isBigEndian: false);
            BigInteger divisor = BigInteger.ModPow(value, ResourceExponent, ResourceModulus);

            int sourceOffset = block * 8;
            int chunkLength = Math.Min(8, payload.Length - sourceOffset);
            ulong chunk = 0;
            for (int i = 0; i < chunkLength; i++)
            {
                chunk |= (ulong)payload[sourceOffset + i] << (i * 8);
            }

            BigInteger dividend = divisor * chunk;
            Span<byte> target = result.AsSpan(8 + (block * 128), 128);
            target.Clear();
            value.TryWriteBytes(target[..64], out _, isUnsigned: true, isBigEndian: false);
            dividend.TryWriteBytes(target[64..], out _, isUnsigned: true, isBigEndian: false);
        }

        return result;
    }

    /// <summary>独立实现：裸 DEFLATE 压缩。</summary>
    public static byte[] Deflate(ReadOnlySpan<byte> content)
    {
        using MemoryStream output = new();
        using (DeflateStream deflate = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(content);
        }

        return output.ToArray();
    }

    /// <summary>独立实现：裸 DEFLATE 解压。</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> compressed)
    {
        using MemoryStream input = new(compressed.ToArray());
        using DeflateStream deflate = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>读出 pak 内某个条目的原始字节（不处理压缩/加密）。</summary>
    public static byte[] ReadStoredBytes(string pakPath, PakEntryInfo entry)
    {
        byte[] all = File.ReadAllBytes(pakPath);
        int length = (int)(entry.CompressedSize > 0 ? entry.CompressedSize : entry.DecompressedSize);
        return all.AsSpan((int)entry.Offset, length).ToArray();
    }

    /// <summary>构造一个条目：原样存放。</summary>
    public static PakTestEntry Stored(ulong hash, byte[] content) =>
        new(hash, content, content.LongLength, 0L);

    /// <summary>构造一个条目：DEFLATE 压缩。</summary>
    public static PakTestEntry Deflated(ulong hash, byte[] content) =>
        new(hash, Deflate(content), content.LongLength, 1L);

    /// <summary>构造一个条目：原样存放 + 资源加密（测试用）。</summary>
    public static PakTestEntry StoredEncrypted(ulong hash, byte[] content, int encryptionType = 1)
    {
        byte[] encrypted = EncryptResource(content);
        return new PakTestEntry(hash, encrypted, content.LongLength, (long)encryptionType << 16);
    }

    /// <summary>构造一个条目：DEFLATE 压缩 + 资源加密。</summary>
    public static PakTestEntry DeflatedEncrypted(ulong hash, byte[] content, int encryptionType = 1)
    {
        byte[] compressed = Deflate(content);
        byte[] encrypted = EncryptResource(compressed);
        long attributes = 1L | ((long)encryptionType << 16);
        return new PakTestEntry(hash, encrypted, content.LongLength, attributes);
    }

    /// <summary>构造 v4.0 pak（feature 含 8 时 TOC 用独立实现加密）。</summary>
    public static BuiltPak BuildPak(
        string directory,
        string fileName,
        short feature,
        params PakTestEntry[] entries)
    {
        byte major = 4;
        byte minor = 0;
        int entrySize = 48;
        int featurePayloadLength = (feature & 0x10) != 0 ? 4 : 0;
        int keyBlockLength = (feature & 8) != 0 ? TocKeyBlockSize : 0;
        long dataStart = HeaderSize + ((long)entries.Length * entrySize) + featurePayloadLength + keyBlockLength;

        byte[] toc = new byte[entries.Length * entrySize];
        long offset = dataStart;
        for (int i = 0; i < entries.Length; i++)
        {
            PakTestEntry entry = entries[i];
            Span<byte> raw = toc.AsSpan(i * entrySize, entrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(raw, (uint)(entry.Hash & 0xFFFFFFFF));
            BinaryPrimitives.WriteUInt32LittleEndian(raw[4..], (uint)(entry.Hash >> 32));
            BinaryPrimitives.WriteInt64LittleEndian(raw[8..], offset);
            BinaryPrimitives.WriteInt64LittleEndian(raw[16..], entry.Stored.LongLength);
            BinaryPrimitives.WriteInt64LittleEndian(raw[24..], entry.DecompressedSize);
            BinaryPrimitives.WriteUInt64LittleEndian(raw[32..], (ulong)entry.Attributes);
            BinaryPrimitives.WriteUInt64LittleEndian(raw[40..], 0uL);
            offset += entry.Stored.LongLength;
        }

        byte[] keyBlock = (feature & 8) != 0 ? CreateKeyBlock() : [];
        byte[] featurePayload = (feature & 0x10) != 0 ? [0x11, 0x22, 0x33, 0x44] : [];
        byte[] storedToc = keyBlock.Length > 0 ? CryptToc(toc, keyBlock) : toc;

        string path = Path.Combine(directory, fileName);
        using FileStream file = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter writer = new(file);
        writer.Write(RePakReader.Magic);
        writer.Write(major);
        writer.Write(minor);
        writer.Write(feature);
        writer.Write(entries.Length);
        writer.Write(0x1234_5678u);
        writer.Write(storedToc);
        writer.Write(featurePayload);
        writer.Write(keyBlock);
        foreach (PakTestEntry entry in entries)
        {
            writer.Write(entry.Stored);
        }

        return new BuiltPak(path, keyBlock, featurePayload);
    }

    /// <summary>构造 v2.0 pak（条目只有偏移/大小/哈希，数据原样存放）。</summary>
    public static string BuildV2Pak(string directory, string fileName, params (ulong Hash, byte[] Content)[] entries)
    {
        const int entrySize = 24;
        long dataStart = HeaderSize + ((long)entries.Length * entrySize);
        byte[] toc = new byte[entries.Length * entrySize];
        long offset = dataStart;
        for (int i = 0; i < entries.Length; i++)
        {
            Span<byte> raw = toc.AsSpan(i * entrySize, entrySize);
            BinaryPrimitives.WriteInt64LittleEndian(raw, offset);
            BinaryPrimitives.WriteInt64LittleEndian(raw[8..], entries[i].Content.LongLength);
            BinaryPrimitives.WriteUInt32LittleEndian(raw[16..], (uint)(entries[i].Hash & 0xFFFFFFFF));
            BinaryPrimitives.WriteUInt32LittleEndian(raw[20..], (uint)(entries[i].Hash >> 32));
            offset += entries[i].Content.LongLength;
        }

        string path = Path.Combine(directory, fileName);
        using FileStream file = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter writer = new(file);
        writer.Write(RePakReader.Magic);
        writer.Write((byte)2);
        writer.Write((byte)0);
        writer.Write((short)0);
        writer.Write(entries.Length);
        writer.Write(0x1234_5678u);
        writer.Write(toc);
        foreach ((_, byte[] content) in entries)
        {
            writer.Write(content);
        }

        return path;
    }

    /// <summary>解析包头字段。</summary>
    public static (uint Magic, byte Major, byte Minor, short Feature, int Count, uint Fingerprint) ReadHeader(byte[] bytes) =>
        (
            BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            bytes[4],
            bytes[5],
            BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(6)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));

    /// <summary>按引擎规则计算组合哈希。</summary>
    public static ulong HashPath(string path) => PakPathHasher.HashPath(path);

    /// <summary>写入一个源目录文件。</summary>
    public static void WriteSourceFile(string root, string relativePath, byte[] content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, content);
    }

    /// <summary>生成可重复的伪随机内容。</summary>
    public static byte[] CreateBytes(int length, int seed = 1234)
    {
        byte[] data = new byte[length];
        Random random = new(seed);
        random.NextBytes(data);
        return data;
    }

    /// <summary>生成可压缩的内容。</summary>
    public static byte[] CreateCompressibleBytes(int length)
    {
        StringBuilder builder = new();
        builder.Append('A', length);
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static BigInteger ParseBigInteger(string hex) =>
        new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);
}

/// <summary>测试用 pak 条目。</summary>
internal sealed record PakTestEntry(ulong Hash, byte[] Stored, long DecompressedSize, long Attributes);

/// <summary>测试用 pak 构造结果。</summary>
internal sealed record BuiltPak(string Path, byte[] TocKeyBlock, byte[] FeaturePayload);
