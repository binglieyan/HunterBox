using System.Buffers.Binary;

namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// 流式条目校验和累加器：分块喂入内容，最终结果与 <see cref="PakChecksum.Compute(ReadOnlySpan{byte})"/> 完全一致。
/// </summary>
/// <remarks>
/// 打包时需要边读文件边算 checksum（P-03：不再 <c>File.ReadAllBytes</c> 后再全量遍历），
/// 因此把 XXH64 写成「保留 4 条 lane + 未满 32 字节尾巴」的流式形态；
/// XXH32 只对最终 8 字节 XXH64 结果计算一次。
/// </remarks>
internal sealed class PakChecksumAccumulator
{
    private const int StripeSize = 32;

    private readonly byte[] _pending = new byte[StripeSize];
    private int _pendingCount;
    private long _totalLength;
    private ulong _lane1;
    private ulong _lane2;
    private ulong _lane3;
    private ulong _lane4;
    private bool _hasLanes;

    /// <summary>追加一块内容。</summary>
    /// <param name="data">任意长度的一块内容。</param>
    public void Append(ReadOnlySpan<byte> data)
    {
        _totalLength += data.Length;
        if (data.IsEmpty)
        {
            return;
        }

        if (!_hasLanes)
        {
            int take = Math.Min(StripeSize - _pendingCount, data.Length);
            data[..take].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += take;
            data = data[take..];
            if (_pendingCount < StripeSize)
            {
                return;
            }

            // 首次凑满 32 字节：初始化 4 条 lane 并吃掉这一条（与一次性实现 length≥32 分支一致）。
            ulong seed = PakChecksum.Seed64;
            _lane1 = seed + PakChecksum.Prime64_1 + PakChecksum.Prime64_2;
            _lane2 = seed + PakChecksum.Prime64_2;
            _lane3 = seed;
            _lane4 = seed - PakChecksum.Prime64_1;
            ProcessStripe(_pending);
            _hasLanes = true;
            _pendingCount = 0;
        }
        else if (_pendingCount > 0)
        {
            // 先把上一次没凑满的那一条补满，保证字节顺序。
            int take = Math.Min(StripeSize - _pendingCount, data.Length);
            data[..take].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += take;
            data = data[take..];
            if (_pendingCount == StripeSize)
            {
                ProcessStripe(_pending);
                _pendingCount = 0;
            }

            if (data.IsEmpty)
            {
                return;
            }
        }

        while (data.Length >= StripeSize)
        {
            ProcessStripe(data[..StripeSize]);
            data = data[StripeSize..];
        }

        if (!data.IsEmpty)
        {
            data.CopyTo(_pending);
            _pendingCount = data.Length;
        }
    }

    /// <summary>结束累加并返回 <c>(XXH64 &lt;&lt; 32) | XXH32(XXH64 小端)</c>。</summary>
    public ulong Value
    {
        get
        {
            ReadOnlySpan<byte> tail = _pending.AsSpan(0, _pendingCount);
            ulong hash;
            if (_hasLanes)
            {
                hash = PakChecksum.RotateLeft(_lane1, 1)
                    + PakChecksum.RotateLeft(_lane2, 7)
                    + PakChecksum.RotateLeft(_lane3, 12)
                    + PakChecksum.RotateLeft(_lane4, 18);
                hash = PakChecksum.Merge64(hash, _lane1);
                hash = PakChecksum.Merge64(hash, _lane2);
                hash = PakChecksum.Merge64(hash, _lane3);
                hash = PakChecksum.Merge64(hash, _lane4);
            }
            else
            {
                hash = PakChecksum.Seed64 + PakChecksum.Prime64_5;
            }

            hash += (ulong)_totalLength;

            int index = 0;
            while (index + 8 <= tail.Length)
            {
                hash ^= PakChecksum.Round64(0uL, BinaryPrimitives.ReadUInt64LittleEndian(tail[index..]));
                hash = (PakChecksum.RotateLeft(hash, 27) * PakChecksum.Prime64_1) + PakChecksum.Prime64_4;
                index += 8;
            }

            if (index + 4 <= tail.Length)
            {
                hash ^= BinaryPrimitives.ReadUInt32LittleEndian(tail[index..]) * PakChecksum.Prime64_1;
                hash = (PakChecksum.RotateLeft(hash, 23) * PakChecksum.Prime64_2) + PakChecksum.Prime64_3;
                index += 4;
            }

            for (; index < tail.Length; index++)
            {
                hash ^= tail[index] * PakChecksum.Prime64_5;
                hash = PakChecksum.RotateLeft(hash, 11) * PakChecksum.Prime64_1;
            }

            hash ^= hash >> 33;
            hash *= PakChecksum.Prime64_2;
            hash ^= hash >> 29;
            hash *= PakChecksum.Prime64_3;
            hash ^= hash >> 32;

            Span<byte> hash64Bytes = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(hash64Bytes, hash);
            uint hash32 = PakChecksum.Hash32(hash64Bytes, PakChecksum.Seed32);
            return (hash << 32) | hash32;
        }
    }

    private void ProcessStripe(ReadOnlySpan<byte> stripe)
    {
        _lane1 = PakChecksum.Round64(_lane1, BinaryPrimitives.ReadUInt64LittleEndian(stripe));
        _lane2 = PakChecksum.Round64(_lane2, BinaryPrimitives.ReadUInt64LittleEndian(stripe[8..]));
        _lane3 = PakChecksum.Round64(_lane3, BinaryPrimitives.ReadUInt64LittleEndian(stripe[16..]));
        _lane4 = PakChecksum.Round64(_lane4, BinaryPrimitives.ReadUInt64LittleEndian(stripe[24..]));
    }
}
