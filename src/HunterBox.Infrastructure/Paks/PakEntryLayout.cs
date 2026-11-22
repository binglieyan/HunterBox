using System.Buffers.Binary;
using HunterBox.Application.Paks;

namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// 条目表（TOC）中单个条目的磁盘布局，解析与写回共用这一份字段偏移定义。
/// </summary>
/// <remarks>
/// <para>
/// v2 条目共 24 字节：<c>offset(8) / decompressedSize(8) / hashLower(4) / hashUpper(4)</c>；
/// v4 条目共 48 字节：<c>hashLower(4) / hashUpper(4) / offset(8) / compressedSize(8) /
/// decompressedSize(8) / attributes(8) / checksum(8)</c>。多字节整数一律小端。
/// </para>
/// <para>
/// 哈希字段的位置随版本不同：v4 在条目起始（+0），v2 在 <c>offset/decompressedSize</c>
/// 之后（+16）。任何「只改哈希」的写回都必须先按 <see cref="HashFieldOffset"/> 定位，
/// 不能假设哈希在条目开头（曾因此把 v2 的 offset 字段写坏）。
/// </para>
/// </remarks>
internal static class PakEntryLayout
{
    /// <summary>v2 单个条目长度。</summary>
    public const int V2EntrySize = 24;

    /// <summary>v4 单个条目长度。</summary>
    public const int V4EntrySize = 48;

    /// <summary>v2 条目内哈希字段偏移（<c>offset + decompressedSize</c> 之后）。</summary>
    public const int V2HashFieldOffset = 16;

    /// <summary>v4 条目内哈希字段偏移（条目起始）。</summary>
    public const int V4HashFieldOffset = 0;

    /// <summary>v4 条目内 checksum 字段偏移（v2 没有该字段）。</summary>
    public const int V4ChecksumFieldOffset = 40;

    /// <summary>返回指定主版本的条目长度。</summary>
    /// <param name="major">pak 主版本（2 / 4）。</param>
    /// <returns>单个条目的磁盘长度。</returns>
    /// <exception cref="NotSupportedException">主版本不是 2 / 4。</exception>
    public static int EntrySize(byte major) => major switch
    {
        2 => V2EntrySize,
        4 => V4EntrySize,
        _ => throw new NotSupportedException($"不支持的 pak 版本 {major}（仅支持 2 / 4）"),
    };

    /// <summary>返回哈希字段在条目内的偏移。</summary>
    /// <param name="major">pak 主版本（2 / 4）。</param>
    /// <returns>v4 为 0，v2 为 16。</returns>
    /// <exception cref="NotSupportedException">主版本不是 2 / 4。</exception>
    public static int HashFieldOffset(byte major) => major switch
    {
        2 => V2HashFieldOffset,
        4 => V4HashFieldOffset,
        _ => throw new NotSupportedException($"不支持的 pak 版本 {major}（仅支持 2 / 4）"),
    };

    /// <summary>返回 checksum 字段在条目内的偏移。</summary>
    /// <param name="major">pak 主版本（2 / 4）。</param>
    /// <returns>v4 为 40；v2 没有该字段，返回 -1。</returns>
    /// <exception cref="NotSupportedException">主版本不是 2 / 4。</exception>
    public static int ChecksumFieldOffset(byte major) => major switch
    {
        2 => -1,
        4 => V4ChecksumFieldOffset,
        _ => throw new NotSupportedException($"不支持的 pak 版本 {major}（仅支持 2 / 4）"),
    };

    /// <summary>按版本解析条目表里的一个条目。</summary>
    /// <param name="major">pak 主版本（2 / 4）。</param>
    /// <param name="raw">该条目的原始字节（至少 <see cref="EntrySize"/> 字节）。</param>
    /// <returns>解析出的条目。</returns>
    /// <exception cref="ArgumentException"><paramref name="raw"/> 长度不足。</exception>
    /// <exception cref="NotSupportedException">主版本不是 2 / 4。</exception>
    public static PakEntryInfo Read(byte major, ReadOnlySpan<byte> raw)
    {
        int entrySize = EntrySize(major);
        if (raw.Length < entrySize)
        {
            throw new ArgumentException($"条目字节长度不足：{raw.Length} < {entrySize}", nameof(raw));
        }

        if (major == 2)
        {
            return new PakEntryInfo(
                HashLower: BinaryPrimitives.ReadUInt32LittleEndian(raw[V2HashFieldOffset..]),
                HashUpper: BinaryPrimitives.ReadUInt32LittleEndian(raw[(V2HashFieldOffset + 4)..]),
                Offset: BinaryPrimitives.ReadInt64LittleEndian(raw),
                CompressedSize: 0L,
                DecompressedSize: BinaryPrimitives.ReadInt64LittleEndian(raw[8..]),
                Attributes: 0L,
                Checksum: 0uL);
        }

        return new PakEntryInfo(
            HashLower: BinaryPrimitives.ReadUInt32LittleEndian(raw),
            HashUpper: BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]),
            Offset: BinaryPrimitives.ReadInt64LittleEndian(raw[8..]),
            CompressedSize: BinaryPrimitives.ReadInt64LittleEndian(raw[16..]),
            DecompressedSize: BinaryPrimitives.ReadInt64LittleEndian(raw[24..]),
            Attributes: BinaryPrimitives.ReadInt64LittleEndian(raw[32..]),
            Checksum: BinaryPrimitives.ReadUInt64LittleEndian(raw[V4ChecksumFieldOffset..]));
    }

    /// <summary>按版本把一个条目写进条目表缓冲区。</summary>
    /// <param name="major">pak 主版本（2 / 4）。</param>
    /// <param name="raw">该条目的目标字节（至少 <see cref="EntrySize"/> 字节）。</param>
    /// <param name="hash">8 字节组合哈希。</param>
    /// <param name="offset">数据在 pak 内的偏移。</param>
    /// <param name="compressedSize">压缩后大小（v2 忽略，写 0 语义）。</param>
    /// <param name="decompressedSize">解压后大小。</param>
    /// <param name="attributes">压缩/加密属性（v2 忽略）。</param>
    /// <param name="checksum">条目内容校验和（v2 忽略）。</param>
    /// <exception cref="ArgumentException"><paramref name="raw"/> 长度不足。</exception>
    /// <exception cref="NotSupportedException">主版本不是 2 / 4。</exception>
    public static void Write(
        byte major,
        Span<byte> raw,
        ulong hash,
        long offset,
        long compressedSize,
        long decompressedSize,
        long attributes,
        ulong checksum)
    {
        int entrySize = EntrySize(major);
        if (raw.Length < entrySize)
        {
            throw new ArgumentException($"条目字节长度不足：{raw.Length} < {entrySize}", nameof(raw));
        }

        if (major == 2)
        {
            BinaryPrimitives.WriteInt64LittleEndian(raw, offset);
            BinaryPrimitives.WriteInt64LittleEndian(raw[8..], decompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(raw[V2HashFieldOffset..], (uint)(hash & 0xFFFFFFFF));
            BinaryPrimitives.WriteUInt32LittleEndian(raw[(V2HashFieldOffset + 4)..], (uint)(hash >> 32));
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(raw, (uint)(hash & 0xFFFFFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(raw[4..], (uint)(hash >> 32));
        BinaryPrimitives.WriteInt64LittleEndian(raw[8..], offset);
        BinaryPrimitives.WriteInt64LittleEndian(raw[16..], compressedSize);
        BinaryPrimitives.WriteInt64LittleEndian(raw[24..], decompressedSize);
        BinaryPrimitives.WriteInt64LittleEndian(raw[32..], attributes);
        BinaryPrimitives.WriteUInt64LittleEndian(raw[V4ChecksumFieldOffset..], checksum);
    }
}
