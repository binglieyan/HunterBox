namespace HunterBox.Infrastructure.Paks;

/// <summary>MurmurHash3 的 32 位实现（x86 变体），用于 RE Engine 的路径哈希。</summary>
/// <remarks>算法公开、与具体游戏数据无关；这里只实现哈希核心，不涉及任何路径表。</remarks>
public static class Murmur3
{
    private const uint C1 = 0xcc9e2d51u;
    private const uint C2 = 0x1b873593u;

    /// <summary>计算 32 位 MurmurHash3。</summary>
    /// <param name="data">待哈希字节。</param>
    /// <param name="seed">种子，默认 0。</param>
    /// <returns>32 位哈希值。</returns>
    public static uint Hash32(ReadOnlySpan<byte> data, uint seed = 0)
    {
        uint hash = seed;
        int blockCount = data.Length / 4;
        for (int i = 0; i < blockCount; i++)
        {
            uint k1 = (uint)(data[i * 4] | (data[(i * 4) + 1] << 8) | (data[(i * 4) + 2] << 16) | (data[(i * 4) + 3] << 24));
            k1 *= C1;
            k1 = RotateLeft(k1, 15);
            k1 *= C2;

            hash ^= k1;
            hash = RotateLeft(hash, 13);
            hash = (hash * 5) + 0xe6546b64u;
        }

        uint tail = 0;
        int tailIndex = blockCount * 4;
        switch (data.Length & 3)
        {
            case 3:
                tail ^= (uint)data[tailIndex + 2] << 16;
                goto case 2;
            case 2:
                tail ^= (uint)data[tailIndex + 1] << 8;
                goto case 1;
            case 1:
                tail ^= data[tailIndex];
                tail *= C1;
                tail = RotateLeft(tail, 15);
                tail *= C2;
                hash ^= tail;
                break;
        }

        hash ^= (uint)data.Length;
        hash ^= hash >> 16;
        hash *= 0x85ebca6bu;
        hash ^= hash >> 13;
        hash *= 0xc2b2ae35u;
        hash ^= hash >> 16;
        return hash;
    }

    /// <summary>
    /// 对 UTF-16 字符串按「UTF-16LE 字节」计算 32 位 MurmurHash3，与
    /// <see cref="Hash32(ReadOnlySpan{byte}, uint)"/> 作用于 <c>Encoding.Unicode.GetBytes(text)</c> 时逐字节等价。
    /// </summary>
    /// <remarks>直接读 char 单元，避免为大路径清单逐行分配临时 byte[]；算法结构逐段对应字节版。</remarks>
    /// <param name="data">待哈希字符（每个 char 按 2 字节小端计入）。</param>
    /// <param name="seed">种子，默认 0。</param>
    /// <returns>32 位哈希值。</returns>
    public static uint Hash32(ReadOnlySpan<char> data, uint seed = 0)
    {
        uint hash = seed;
        int blockCount = data.Length / 2;
        for (int i = 0; i < blockCount; i++)
        {
            // 2 个 char = 4 字节 UTF-16LE：先低字节后高字节，与 Encoding.Unicode 输出一致。
            uint k1 = (uint)data[i * 2] | ((uint)data[(i * 2) + 1] << 16);
            k1 *= C1;
            k1 = RotateLeft(k1, 15);
            k1 *= C2;

            hash ^= k1;
            hash = RotateLeft(hash, 13);
            hash = (hash * 5) + 0xe6546b64u;
        }

        if ((data.Length & 1) != 0)
        {
            // 剩余 1 个 char = 2 字节尾部：字节版依次并入低字节、高字节后整体混合一次。
            uint tail = data[^1];
            tail *= C1;
            tail = RotateLeft(tail, 15);
            tail *= C2;
            hash ^= tail;
        }

        hash ^= (uint)(data.Length * 2);
        hash ^= hash >> 16;
        hash *= 0x85ebca6bu;
        hash ^= hash >> 13;
        hash *= 0xc2b2ae35u;
        hash ^= hash >> 16;
        return hash;
    }

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));
}
