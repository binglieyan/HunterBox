using System.IO.Compression;
using System.Text;
using HunterBox.Application.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

public class RePakEntryReaderTests
{
    /// <summary>构造一个只含单条目的 v4 pak：包头 16 + 条目 48 = 数据从 64 开始。</summary>
    private static string BuildPak(byte[] payload, int compressionType, ushort encryptionType = 0, long? decompressedSize = null)
    {
        byte[] stored = payload;
        if (compressionType == 1)
        {
            using MemoryStream output = new();
            using (DeflateStream deflate = new(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(payload);
            }

            stored = output.ToArray();
        }

        long attributes = compressionType | (encryptionType << 16);
        using MemoryStream memory = new();
        using BinaryWriter writer = new(memory);

        writer.Write(RePakReader.Magic);
        writer.Write((byte)4);
        writer.Write((byte)0);
        writer.Write((short)0);
        writer.Write(1);
        writer.Write(0u);

        writer.Write(0x1u);                       // hash lower
        writer.Write(0x2u);                       // hash upper
        writer.Write(64L);                        // offset
        writer.Write((long)stored.Length);        // compressed
        writer.Write(decompressedSize ?? payload.Length); // decompressed
        writer.Write(attributes);
        writer.Write(0uL);                        // checksum
        writer.Write(stored);

        string path = Path.Combine(Path.GetTempPath(), "hb-entry-" + Guid.NewGuid().ToString("N") + ".pak");
        File.WriteAllBytes(path, memory.ToArray());
        return path;
    }

    [Fact]
    public void ReadEntry_Uncompressed_ReturnsPayload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("model-bytes");
        string path = BuildPak(payload, compressionType: 0);
        try
        {
            PakEntryInfo entry = Assert.Single(new RePakReader().Read(path).Entries);
            Assert.Equal(payload, new RePakEntryReader().ReadEntry(path, entry));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadEntry_Deflate_Decompresses()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('A', 4096));
        string path = BuildPak(payload, compressionType: 1);
        try
        {
            PakEntryInfo entry = Assert.Single(new RePakReader().Read(path).Entries);
            byte[] result = new RePakEntryReader().ReadEntry(path, entry);
            Assert.Equal(payload, result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadEntry_Encrypted_Throws()
    {
        byte[] payload = Encoding.UTF8.GetBytes("secret");
        string path = BuildPak(payload, compressionType: 0, encryptionType: 2);
        try
        {
            PakEntryInfo entry = Assert.Single(new RePakReader().Read(path).Entries);
            Assert.Throws<NotSupportedException>(() => new RePakEntryReader().ReadEntry(path, entry));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>损坏 pak 声明的超大 CompressedSize 必须被拒绝，不能抛 Overflow/OOM。</summary>
    [Fact]
    public void ReadEntry_CompressedSizeOverflow_ThrowsInvalidDataException()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("overflow.pak", "x");
        PakEntryInfo entry = new(
            HashLower: 0,
            HashUpper: 0,
            Offset: 1,
            CompressedSize: long.MaxValue,
            DecompressedSize: 0,
            Attributes: 0,
            Checksum: 0);

        Assert.Throws<InvalidDataException>(() => new RePakEntryReader().ReadEntry(path, entry));
    }

    [Fact]
    public void ReadEntry_TooLargeDecompressedSize_Throws()
    {
        byte[] payload = Encoding.UTF8.GetBytes("abc");
        string path = BuildPak(payload, compressionType: 1, decompressedSize: 999);
        try
        {
            PakEntryInfo entry = Assert.Single(new RePakReader().Read(path).Entries);
            Assert.Throws<InvalidDataException>(() => new RePakEntryReader().ReadEntry(path, entry));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
