using HunterBox.Application.Paks;

namespace HunterBox.Infrastructure.Paks;

/// <summary>按条目信息从 pak 里取出文件内容（含 DEFLATE 解压）。</summary>
/// <remarks>
/// <para>只支持未加密条目（加密方式 0）。压缩方式：0 = 原样存放，1 = DEFLATE，其它暂不支持。</para>
/// <para>测试校验工具：生产链路不实例化，仅测试用构造/读取 pak 条目做断言。</para>
/// </remarks>
public sealed class RePakEntryReader : IPakEntryReader
{
    /// <summary>读取一个条目的原始内容。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <param name="entry">由 <see cref="RePakReader"/> 解析出的条目。</param>
    /// <returns>解压后的字节。</returns>
    /// <exception cref="FileNotFoundException">pak 不存在。</exception>
    /// <exception cref="InvalidDataException">条目越界或长度不符。</exception>
    /// <exception cref="NotSupportedException">条目加密或使用了不支持的压缩方式。</exception>
    public byte[] ReadEntry(string pakPath, PakEntryInfo entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.EncryptionType != 0)
        {
            throw new NotSupportedException($"不支持的加密方式: {entry.EncryptionType}");
        }

        using FileStream stream = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = entry.CompressedSize > 0 ? entry.CompressedSize : entry.DecompressedSize;

        // 先拒绝超过 int 上限的尺寸（new byte[] 会抛 Overflow/OOM，不是约定的 InvalidDataException），
        // 再用减法做范围检查，避免 entry.Offset + size 的 long 溢出绕过校验。
        if (entry.Offset < 0 || size < 0 || size > int.MaxValue || entry.Offset > stream.Length - size)
        {
            throw new InvalidDataException("条目超出 pak 文件范围");
        }

        stream.Seek(entry.Offset, SeekOrigin.Begin);
        byte[] raw = new byte[(int)size];
        stream.ReadExactly(raw);

        return entry.CompressionType switch
        {
            0 => raw,
            1 => Inflate(raw, entry.DecompressedSize),
            var other => throw new NotSupportedException($"不支持的压缩方式: {other}"),
        };
    }

    /// <summary>解压 DEFLATE 数据：按声明尺寸 + 绝对上限做有界复制（防压缩炸弹）。</summary>
    /// <param name="raw">压缩数据。</param>
    /// <param name="expectedSize">条目表声明的解压后大小；为 0 表示未知。</param>
    /// <returns>解压后的数据。</returns>
    /// <exception cref="InvalidDataException">解压失败、输出超限或长度与声明不符。</exception>
    private static byte[] Inflate(byte[] raw, long expectedSize)
    {
        // 复用生产解压实现：声明尺寸大于 512MB 时会直接拒绝，不做无界解压。
        byte[] result = PakEntryCodec.Decompress(PakEntryCodec.CompressionDeflate, raw, expectedSize);
        if (expectedSize > 0 && result.LongLength != expectedSize)
        {
            throw new InvalidDataException($"解压后大小不符：{result.LongLength} != {expectedSize}");
        }

        return result;
    }
}
