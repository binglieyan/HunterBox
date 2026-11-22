using System.Text;
using HunterBox.Application.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

public class RePakReaderTests
{
    private static byte[] BuildV4Pak((uint Lower, uint Upper, long Offset, long Compressed, long Decompressed, long Attributes, ulong Checksum)[] entries)
    {
        using MemoryStream memory = new();
        using BinaryWriter writer = new(memory);

        writer.Write(RePakReader.Magic);
        writer.Write((byte)4);
        writer.Write((byte)0);
        writer.Write((short)0);
        writer.Write(entries.Length);
        writer.Write(0x12345678u);

        foreach ((uint lower, uint upper, long offset, long compressed, long decompressed, long attributes, ulong checksum) in entries)
        {
            writer.Write(lower);
            writer.Write(upper);
            writer.Write(offset);
            writer.Write(compressed);
            writer.Write(decompressed);
            writer.Write(attributes);
            writer.Write(checksum);
        }

        writer.Write(Encoding.ASCII.GetBytes("payload"));
        return memory.ToArray();
    }

    [Fact]
    public void Read_ParsesHeaderAndEntries()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildV4Pak([(0x1111u, 0x2222u, 16L + 48, 7L, 7L, 0L, 0xABCDuL)]);
        string path = Path.Combine(temp.Path, "a.pak");
        File.WriteAllBytes(path, bytes);

        PakArchiveInfo archive = new RePakReader().Read(path);

        Assert.Equal(4, archive.Major);
        Assert.Equal(0, archive.Minor);
        Assert.Equal(0x12345678u, archive.Fingerprint);
        PakEntryInfo entry = Assert.Single(archive.Entries);
        Assert.Equal(0x1111u, entry.HashLower);
        Assert.Equal(0x2222_0000_1111uL, entry.Hash);
        Assert.Equal(16L + 48, entry.Offset);
        Assert.Equal(7, entry.CompressedSize);
        Assert.Equal(0, entry.CompressionType);
        Assert.True(PakArchiveInfo.IsSupportedMajorMinor(archive.Major, archive.Minor) && PakArchiveInfo.IsSupportedFeature(archive.Feature));
    }

    [Fact]
    public void Read_DecodesCompressionAndEncryptionFromAttributes()
    {
        using TempDir temp = TempDir.Create();
        // 压缩方式 1，加密方式 2（0x20000）
        byte[] bytes = BuildV4Pak([(1u, 2u, 0L, 10L, 20L, 0x20001L, 0uL)]);
        string path = Path.Combine(temp.Path, "a.pak");
        File.WriteAllBytes(path, bytes);

        PakEntryInfo entry = Assert.Single(new RePakReader().Read(path).Entries);
        Assert.Equal(1, entry.CompressionType);
        Assert.Equal(2, entry.EncryptionType);
    }

    [Fact]
    public void Read_RejectsWrongMagic()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "a.pak");
        File.WriteAllBytes(path, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]);

        Assert.Throws<InvalidDataException>(() => new RePakReader().Read(path));
    }

    [Fact]
    public void Read_RejectsUnsupportedVersion()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildV4Pak([]);
        bytes[4] = 9;
        string path = Path.Combine(temp.Path, "a.pak");
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => new RePakReader().Read(path));
    }

    [Fact]
    public void Read_PlainToc_IsNotTocEncrypted()
    {
        using TempDir temp = TempDir.Create();
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "plain.pak",
            0,
            PakCodecTestTools.Stored(0x1111_2222_3333_4444uL, Encoding.ASCII.GetBytes("x")));

        PakArchiveInfo archive = new RePakReader().Read(built.Path);

        Assert.False(archive.IsTocEncrypted);
    }

    /// <summary>TOC 加密时 RePakReader 返回的是密文原样解析结果，必须通过 IsTocEncrypted 提示调用方。</summary>
    [Fact]
    public void Read_TocEncrypted_ReportsIsTocEncrypted()
    {
        using TempDir temp = TempDir.Create();
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "encrypted.pak",
            8,
            PakCodecTestTools.Stored(0x1111_2222_3333_4444uL, Encoding.ASCII.GetBytes("x")));

        PakArchiveInfo archive = new RePakReader().Read(built.Path);

        Assert.True(archive.IsTocEncrypted);
        Assert.Equal((short)8, archive.Feature);
        Assert.True(PakArchiveInfo.IsSupportedMajorMinor(archive.Major, archive.Minor) && PakArchiveInfo.IsSupportedFeature(archive.Feature));
    }

    [Fact]
    public void Read_RealGamePak_WhenAvailable()
    {
        // 仅在本机存在游戏 pak 时校验；CI 上直接跳过
        const string gamePath = @"E:\SteamLibrary\steamapps\common\MonsterHunterWilds\re_chunk_000.pak";
        if (!File.Exists(gamePath))
        {
            return;
        }

        PakArchiveInfo archive = new RePakReader().Read(gamePath);

        Assert.Equal(4, archive.Major);
        Assert.True(PakArchiveInfo.IsSupportedMajorMinor(archive.Major, archive.Minor) && PakArchiveInfo.IsSupportedFeature(archive.Feature));
        Assert.NotEmpty(archive.Entries);
    }
}
