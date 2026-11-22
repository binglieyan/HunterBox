using System.Buffers.Binary;

namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// pak 条目内容校验和：XXH64 + 由 XXH64 结果再算的 XXH32，组合成 64 位值。
/// </summary>
/// <remarks>
/// 条目表里的 checksum 字段用它填充；种子固定为 <c>0xFFFFFFFF</c>，与格式约定一致。
/// 算法是公开的 xxHash（无密钥），这里只做内容摘要，不涉及任何游戏数据。
/// </remarks>
internal static class PakChecksum
{
    internal const uint Prime32_1 = 2654435761u;
    internal const uint Prime32_2 = 2246822519u;
    internal const uint Prime32_3 = 3266489917u;
    internal const uint Prime32_4 = 668265263u;
    internal const uint Prime32_5 = 374761393u;

    internal const ulong Prime64_1 = 11400714785074694791uL;
    internal const ulong Prime64_2 = 14029467366897019727uL;
    internal const ulong Prime64_3 = 1609587929392839161uL;
    internal const ulong Prime64_4 = 9650029242287828579uL;
    internal const ulong Prime64_5 = 2870177450012600261uL;

    internal const ulong Seed64 = 0xFFFFFFFFuL;
    internal const uint Seed32 = 0xFFFFFFFFu;

    /// <summary>创建一个流式累加器：分块读文件时边读边算，避免整文件进内存。</summary>
    /// <returns>新的累加器。</returns>
    public static PakChecksumAccumulator CreateAccumulator() => new();

    /// <summary>计算条目内容校验和：<c>(XXH64 &lt;&lt; 32) | XXH32(XXH64 的小端字节)</c>。</summary>
    public static ulong Compute(ReadOnlySpan<byte> content)
    {
        ulong hash64 = Hash64(content, Seed64);
        Span<byte> hash64Bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(hash64Bytes, hash64);
        uint hash32 = Hash32(hash64Bytes, Seed32);
        return (hash64 << 32) | hash32;
    }

    /// <summary>XXH64。</summary>
    public static ulong Hash64(ReadOnlySpan<byte> data, ulong seed)
    {
        int index = 0;
        int length = data.Length;
        ulong hash;

        if (length < 32)
        {
            hash = seed + Prime64_5;
        }
        else
        {
            ulong lane1 = seed + Prime64_1 + Prime64_2;
            ulong lane2 = seed + Prime64_2;
            ulong lane3 = seed;
            ulong lane4 = seed - Prime64_1;

            while (index <= length - 32)
            {
                lane1 = Round64(lane1, BinaryPrimitives.ReadUInt64LittleEndian(data[index..]));
                index += 8;
                lane2 = Round64(lane2, BinaryPrimitives.ReadUInt64LittleEndian(data[index..]));
                index += 8;
                lane3 = Round64(lane3, BinaryPrimitives.ReadUInt64LittleEndian(data[index..]));
                index += 8;
                lane4 = Round64(lane4, BinaryPrimitives.ReadUInt64LittleEndian(data[index..]));
                index += 8;
            }

            hash = RotateLeft(lane1, 1) + RotateLeft(lane2, 7) + RotateLeft(lane3, 12) + RotateLeft(lane4, 18);
            hash = Merge64(hash, lane1);
            hash = Merge64(hash, lane2);
            hash = Merge64(hash, lane3);
            hash = Merge64(hash, lane4);
        }

        hash += (ulong)length;

        for (; index + 8 <= length; index += 8)
        {
            hash ^= Round64(0uL, BinaryPrimitives.ReadUInt64LittleEndian(data[index..]));
            hash = (RotateLeft(hash, 27) * Prime64_1) + Prime64_4;
        }

        if (index + 4 <= length)
        {
            hash ^= BinaryPrimitives.ReadUInt32LittleEndian(data[index..]) * Prime64_1;
            hash = (RotateLeft(hash, 23) * Prime64_2) + Prime64_3;
            index += 4;
        }

        for (; index < length; index++)
        {
            hash ^= data[index] * Prime64_5;
            hash = RotateLeft(hash, 11) * Prime64_1;
        }

        hash ^= hash >> 33;
        hash *= Prime64_2;
        hash ^= hash >> 29;
        hash *= Prime64_3;
        hash ^= hash >> 32;
        return hash;
    }

    /// <summary>XXH32。</summary>
    public static uint Hash32(ReadOnlySpan<byte> data, uint seed)
    {
        int index = 0;
        int length = data.Length;
        uint hash;

        if (length < 16)
        {
            hash = seed + Prime32_5;
        }
        else
        {
            uint lane1 = seed + Prime32_1 + Prime32_2;
            uint lane2 = seed + Prime32_2;
            uint lane3 = seed;
            uint lane4 = seed - Prime32_1;

            while (index <= length - 16)
            {
                lane1 = Round32(lane1, BinaryPrimitives.ReadUInt32LittleEndian(data[index..]));
                index += 4;
                lane2 = Round32(lane2, BinaryPrimitives.ReadUInt32LittleEndian(data[index..]));
                index += 4;
                lane3 = Round32(lane3, BinaryPrimitives.ReadUInt32LittleEndian(data[index..]));
                index += 4;
                lane4 = Round32(lane4, BinaryPrimitives.ReadUInt32LittleEndian(data[index..]));
                index += 4;
            }

            hash = RotateLeft(lane1, 1) + RotateLeft(lane2, 7) + RotateLeft(lane3, 12) + RotateLeft(lane4, 18);
        }

        hash += (uint)length;

        for (; index + 4 <= length; index += 4)
        {
            hash += BinaryPrimitives.ReadUInt32LittleEndian(data[index..]) * Prime32_3;
            hash = RotateLeft(hash, 17) * Prime32_4;
        }

        for (; index < length; index++)
        {
            hash += data[index] * Prime32_5;
            hash = RotateLeft(hash, 11) * Prime32_1;
        }

        hash ^= hash >> 15;
        hash *= Prime32_2;
        hash ^= hash >> 13;
        hash *= Prime32_3;
        hash ^= hash >> 16;
        return hash;
    }

    internal static uint Round32(uint accumulator, uint value)
    {
        accumulator += value * Prime32_2;
        accumulator = RotateLeft(accumulator, 13);
        accumulator *= Prime32_1;
        return accumulator;
    }

    internal static ulong Round64(ulong accumulator, ulong value)
    {
        accumulator += value * Prime64_2;
        accumulator = RotateLeft(accumulator, 31);
        accumulator *= Prime64_1;
        return accumulator;
    }

    internal static ulong Merge64(ulong accumulator, ulong value)
    {
        value = Round64(0uL, value);
        accumulator ^= value;
        return (accumulator * Prime64_1) + Prime64_4;
    }

    internal static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    internal static ulong RotateLeft(ulong value, int bits) => (value << bits) | (value >> (64 - bits));
}
