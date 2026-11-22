using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// P-03：PakPacker 改为流式分块读写后，输出必须与旧实现逐字节一致（未压缩路径），
/// 压缩/加密/无压缩收益回退路径必须能还原原内容，流式 checksum 必须与一次性算法完全一致。
/// </summary>
public class PakPackerStreamingTests
{
    private const string RelativeA = "natives/stm/art/model/character/ch03/012/large.bin";
    private const string RelativeB = "natives/stm/art/model/character/ch03/012/small.bin";

    [Fact]
    public void Pack_Uncompressed_MultiMegabyteFile_ProducesByteExactPak()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] large = PakCodecTestTools.CreateBytes(3 * 1024 * 1024, seed: 77);
        byte[] small = PakCodecTestTools.CreateBytes(123, seed: 78);
        PakCodecTestTools.WriteSourceFile(source, RelativeA, large);
        PakCodecTestTools.WriteSourceFile(source, RelativeB, small);
        string pak = Path.Combine(temp.Path, "streamed.pak");

        new PakRepacker().Pack(
            source,
            pak,
            new PakRepackProfile { Major = 4, Minor = 0, Feature = 0, Fingerprint = 0x1234_5678 },
            CancellationToken.None);

        // 独立重建整份 pak 字节：header + 按哈希升序的 TOC + 按文件顺序紧排的数据区。
        byte[] expected = BuildExpectedUncompressedPak(source, 0x1234_5678);
        Assert.Equal(expected, File.ReadAllBytes(pak));

        PakArchiveInfo archive = new RePakReader().Read(pak);
        Assert.Equal(2, archive.Entries.Count);
        // checksum 必须是对原内容算的独立值（流式累加器算错时这里会失败）。
        foreach (PakEntryInfo entry in archive.Entries)
        {
            byte[] expectedContent = entry.Hash == PakPathHasher.HashPath(RelativeA.ToLowerInvariant()) ? large : small;
            Assert.Equal(IndependentXxHash.Checksum(expectedContent), entry.Checksum);
        }
    }

    [Fact]
    public void Pack_Deflate_StoredBytesMatchIndependentOneShotCompression()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(2 * 1024 * 1024);
        PakCodecTestTools.WriteSourceFile(source, RelativeA, content);
        string pak = Path.Combine(temp.Path, "deflate.pak");

        new PakRepacker().Pack(
            source,
            pak,
            new PakRepackProfile { Major = 4, CompressionType = 1 },
            CancellationToken.None);

        PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);
        Assert.Equal(1, entry.CompressionType);
        Assert.Equal(0, entry.EncryptionType);
        Assert.Equal(content.LongLength, entry.DecompressedSize);
        Assert.Equal(IndependentXxHash.Checksum(content), entry.Checksum);

        byte[] stored = PakCodecTestTools.ReadStoredBytes(pak, entry);
        // 流式压缩的字节必须与独立的一次性 DEFLATE 结果一致（同一 CompressionLevel 下是确定的）。
        Assert.Equal(PakCodecTestTools.Deflate(content), stored);

        using TempDir unpacked = TempDir.Create();
        new PakRepacker().Unpack(
            pak,
            unpacked.Dir("out"),
            PakPathHasher.BuildIndex([RelativeA]),
            progress: null,
            CancellationToken.None);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(unpacked.Path, "out", RelativeA.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void Pack_DeflateEncrypted_StoredBytesDecryptAndInflateToOriginal()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(640 * 1024);
        PakCodecTestTools.WriteSourceFile(source, RelativeA, content);
        string pak = Path.Combine(temp.Path, "encrypted.pak");

        new PakRepacker().Pack(
            source,
            pak,
            new PakRepackProfile { Major = 4, CompressionType = 1, EncryptionType = 3 },
            CancellationToken.None);

        PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);
        Assert.Equal(1, entry.CompressionType);
        Assert.Equal(3, entry.EncryptionType);
        Assert.Equal(IndependentXxHash.Checksum(content), entry.Checksum);

        byte[] stored = PakCodecTestTools.ReadStoredBytes(pak, entry);
        byte[] compressed = PakCodecTestTools.DecryptResource(stored);
        Assert.Equal(PakCodecTestTools.Deflate(content), compressed);
        Assert.Equal(content, PakCodecTestTools.Inflate(compressed));

        using TempDir unpacked = TempDir.Create();
        new PakRepacker().Unpack(
            pak,
            unpacked.Dir("out"),
            PakPathHasher.BuildIndex([RelativeA]),
            progress: null,
            CancellationToken.None);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(unpacked.Path, "out", RelativeA.Replace('/', Path.DirectorySeparatorChar))));
    }

    /// <summary>压缩没有收益时按原样存放：数据区必须是原字节，attributes=0，checksum 仍是原内容值。</summary>
    [Fact]
    public void Pack_IncompressibleContent_FallsBackToRawStoredBytes()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] content = PakCodecTestTools.CreateBytes(1024 * 1024, seed: 79);
        PakCodecTestTools.WriteSourceFile(source, RelativeA, content);
        string pak = Path.Combine(temp.Path, "fallback.pak");

        new PakRepacker().Pack(
            source,
            pak,
            new PakRepackProfile { Major = 4, CompressionType = 1 },
            CancellationToken.None);

        PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);
        Assert.Equal(0, entry.CompressionType);
        Assert.Equal(0, entry.EncryptionType);
        Assert.Equal(content.LongLength, entry.CompressedSize);
        Assert.Equal(content.LongLength, entry.DecompressedSize);
        Assert.Equal(IndependentXxHash.Checksum(content), entry.Checksum);
        Assert.Equal(content, PakCodecTestTools.ReadStoredBytes(pak, entry));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1000)]
    [InlineData(100_003)]
    public void ChecksumAccumulator_MatchesOneShotForEveryChunking(int length)
    {
        byte[] data = PakCodecTestTools.CreateBytes(length, seed: length + 3);
        ulong expected = PakChecksum.Compute(data);
        Assert.Equal(IndependentXxHash.Checksum(data), expected);

        foreach (int chunkSize in new[] { 1, 2, 7, 31, 32, 33, 4_093, 65_537 })
        {
            PakChecksumAccumulator accumulator = PakChecksum.CreateAccumulator();
            for (int offset = 0; offset < data.Length; offset += chunkSize)
            {
                accumulator.Append(data.AsSpan(offset, Math.Min(chunkSize, data.Length - offset)));
            }

            Assert.Equal(expected, accumulator.Value);
        }
    }

    /// <summary>按格式独立重建未压缩 v4 pak（header + TOC + 数据区）的期望字节。</summary>
    private static byte[] BuildExpectedUncompressedPak(string sourceRoot, uint fingerprint)
    {
        const int headerSize = 16;
        const int entrySize = 48;
        IReadOnlyList<string> relativePaths =
        [
            RelativeA,
            RelativeB,
        ];
        List<(ulong Hash, long Offset, byte[] Content, ulong Checksum)> entries = [];
        long dataStart = headerSize + ((long)relativePaths.Count * entrySize);
        long position = dataStart;
        foreach (string relativePath in relativePaths)
        {
            byte[] content = File.ReadAllBytes(Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            entries.Add((PakPathHasher.HashPath(relativePath.ToLowerInvariant()), position, content, IndependentXxHash.Checksum(content)));
            position += content.LongLength;
        }

        using MemoryStream memory = new((int)position);
        using BinaryWriter writer = new(memory);
        writer.Write(RePakReader.Magic);
        writer.Write((byte)4);
        writer.Write((byte)0);
        writer.Write((short)0);
        writer.Write(entries.Count);
        writer.Write(fingerprint);

        foreach (var entry in entries.OrderBy(static entry => entry.Hash))
        {
            writer.Write((uint)(entry.Hash & 0xFFFFFFFF));
            writer.Write((uint)(entry.Hash >> 32));
            writer.Write(entry.Offset);
            writer.Write(entry.Content.LongLength);
            writer.Write(entry.Content.LongLength);
            writer.Write(0L);
            writer.Write(entry.Checksum);
        }

        foreach (var entry in entries)
        {
            writer.Write(entry.Content);
        }

        writer.Flush();
        return memory.ToArray();
    }

    /// <summary>测试侧独立实现的 xxHash（格式文档/公开算法），用于交叉验证生产实现的字节结果。</summary>
    private static class IndependentXxHash
    {
        private const ulong P1 = 11400714785074694791ul;
        private const ulong P2 = 14029467366897019727ul;
        private const ulong P3 = 1609587929392839161ul;
        private const ulong P4 = 9650029242287828579ul;
        private const ulong P5 = 2870177450012600261ul;

        // 非 const：lane 初始化里故意溢出 64 位，const 会在编译期报 CS0220。
        [SuppressMessage(
            "Performance",
            "CA1802:Use literals where appropriate",
            Justification = "值参与故意溢出的常量运算，改成 const 会在编译期触发 CS0220。")]
        private static readonly ulong Seed64 = 0xFFFFFFFFul;

        /// <summary>32 位哈希的初始种子；保持运行时字段，避免常量运算在编译期溢出。</summary>
        [SuppressMessage(
            "Performance",
            "CA1802:Use literals where appropriate",
            Justification = "值参与故意溢出的常量运算，改成 const 会在编译期触发 CS0220。")]
        private static readonly uint Seed32 = 0xFFFFFFFFu;

        public static ulong Checksum(ReadOnlySpan<byte> data)
        {
            ulong hash64 = Hash64(data);
            Span<byte> hash64Bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(hash64Bytes, hash64);
            return (hash64 << 32) | Hash32(hash64Bytes);
        }

        private static ulong Hash64(ReadOnlySpan<byte> data)
        {
            int length = data.Length;
            int index = 0;
            ulong hash;
            if (length >= 32)
            {
                ulong lane1 = Seed64 + P1 + P2;
                ulong lane2 = Seed64 + P2;
                ulong lane3 = Seed64;
                ulong lane4 = Seed64 - P1;
                while (index <= length - 32)
                {
                    lane1 = Round64(lane1, BinaryPrimitives.ReadUInt64LittleEndian(data[index..]));
                    lane2 = Round64(lane2, BinaryPrimitives.ReadUInt64LittleEndian(data[(index + 8)..]));
                    lane3 = Round64(lane3, BinaryPrimitives.ReadUInt64LittleEndian(data[(index + 16)..]));
                    lane4 = Round64(lane4, BinaryPrimitives.ReadUInt64LittleEndian(data[(index + 24)..]));
                    index += 32;
                }

                hash = RotateLeft(lane1, 1) + RotateLeft(lane2, 7) + RotateLeft(lane3, 12) + RotateLeft(lane4, 18);
                hash = Merge64(hash, lane1);
                hash = Merge64(hash, lane2);
                hash = Merge64(hash, lane3);
                hash = Merge64(hash, lane4);
            }
            else
            {
                hash = Seed64 + P5;
            }

            hash += (ulong)length;
            for (; index + 8 <= length; index += 8)
            {
                hash ^= Round64(0ul, BinaryPrimitives.ReadUInt64LittleEndian(data[index..]));
                hash = (RotateLeft(hash, 27) * P1) + P4;
            }

            if (index + 4 <= length)
            {
                hash ^= BinaryPrimitives.ReadUInt32LittleEndian(data[index..]) * P1;
                hash = (RotateLeft(hash, 23) * P2) + P3;
                index += 4;
            }

            for (; index < length; index++)
            {
                hash ^= data[index] * P5;
                hash = RotateLeft(hash, 11) * P1;
            }

            hash ^= hash >> 33;
            hash *= P2;
            hash ^= hash >> 29;
            hash *= P3;
            hash ^= hash >> 32;
            return hash;
        }

        private static uint Hash32(ReadOnlySpan<byte> data)
        {
            const uint p1 = 2654435761u;
            const uint p2 = 2246822519u;
            const uint p3 = 3266489917u;
            const uint p4 = 668265263u;
            const uint p5 = 374761393u;
            int length = data.Length;
            int index = 0;
            uint hash;
            if (length >= 16)
            {
                uint lane1 = Seed32 + p1 + p2;
                uint lane2 = Seed32 + p2;
                uint lane3 = Seed32;
                uint lane4 = Seed32 - p1;
                while (index <= length - 16)
                {
                    lane1 = Round32(lane1, BinaryPrimitives.ReadUInt32LittleEndian(data[index..]));
                    lane2 = Round32(lane2, BinaryPrimitives.ReadUInt32LittleEndian(data[(index + 4)..]));
                    lane3 = Round32(lane3, BinaryPrimitives.ReadUInt32LittleEndian(data[(index + 8)..]));
                    lane4 = Round32(lane4, BinaryPrimitives.ReadUInt32LittleEndian(data[(index + 12)..]));
                    index += 16;
                }

                hash = RotateLeft(lane1, 1) + RotateLeft(lane2, 7) + RotateLeft(lane3, 12) + RotateLeft(lane4, 18);
            }
            else
            {
                hash = Seed32 + p5;
            }

            hash += (uint)length;
            for (; index + 4 <= length; index += 4)
            {
                hash += BinaryPrimitives.ReadUInt32LittleEndian(data[index..]) * p3;
                hash = RotateLeft(hash, 17) * p4;
            }

            for (; index < length; index++)
            {
                hash += data[index] * p5;
                hash = RotateLeft(hash, 11) * p1;
            }

            hash ^= hash >> 15;
            hash *= p2;
            hash ^= hash >> 13;
            hash *= p3;
            hash ^= hash >> 16;
            return hash;
        }

        private static ulong Round64(ulong accumulator, ulong value)
        {
            accumulator += value * P2;
            accumulator = RotateLeft(accumulator, 31);
            return accumulator * P1;
        }

        private static ulong Merge64(ulong accumulator, ulong value)
        {
            accumulator ^= Round64(0ul, value);
            return (accumulator * P1) + P4;
        }

        private static uint Round32(uint accumulator, uint value)
        {
            accumulator += value * 2246822519u;
            accumulator = RotateLeft(accumulator, 13);
            return accumulator * 2654435761u;
        }

        private static ulong RotateLeft(ulong value, int bits) => (value << bits) | (value >> (64 - bits));

        private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));
    }
}
