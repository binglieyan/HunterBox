using System.Buffers.Binary;
using System.Text;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>整包解包 / 重打包的正向路径：明文 TOC、加密 TOC、压缩、资源加密与未知条目。</summary>
public class PakRepackerTests
{
    private const string ArmorPath = "natives/stm/art/model/character/ch02/012/003/ch02_012_003.mdf2.45";
    private const string ArmorNewPath = "natives/stm/art/model/character/ch02/015/003/ch02_012_003.mdf2.45";

    private static PakNameIndex IndexOf(params string[] paths) => PakPathHasher.BuildIndex(paths);

    private static byte[] ReadExtracted(string root, string relativePath) =>
        File.ReadAllBytes(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Pack_Unencrypted_Roundtrip()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] first = PakCodecTestTools.CreateBytes(4096, seed: 7);
        byte[] second = Encoding.UTF8.GetBytes("hello pak");
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/first.bin", first);
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/sub/second.txt", second);
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/empty.bin", []);
        string pak = Path.Combine(temp.Path, "mod.pak");

        PakRepackProfile profile = new()
        {
            Major = 4,
            Minor = 0,
            Feature = 0,
            Fingerprint = 0xAABBCCDD,
        };
        PakRepacker repacker = new();
        repacker.Pack(source, pak, profile, CancellationToken.None);

        PakArchiveInfo archive = repacker.Inspect(pak);
        Assert.Equal(4, archive.Major);
        Assert.Equal(0, archive.Feature);
        Assert.Equal(0xAABBCCDDu, archive.Fingerprint);
        Assert.Equal(3, archive.Entries.Count);
        Assert.Equal(archive.Entries.Select(static entry => entry.Hash).Order(), archive.Entries.Select(static entry => entry.Hash));
        HashSet<ulong> hashes = [.. archive.Entries.Select(static entry => entry.Hash)];
        Assert.Contains(PakCodecTestTools.HashPath("natives/stm/first.bin"), hashes);
        Assert.Contains(PakCodecTestTools.HashPath("natives/stm/sub/second.txt"), hashes);
        Assert.Contains(PakCodecTestTools.HashPath("natives/stm/empty.bin"), hashes);
        foreach (PakEntryInfo entry in archive.Entries)
        {
            Assert.Equal(0, entry.CompressionType);
            Assert.Equal(0, entry.EncryptionType);
            Assert.True(entry.Offset >= 16 + (48 * 3));
        }

        string destination = temp.Dir("out");
        repacker.Unpack(
            pak,
            destination,
            IndexOf("natives/stm/first.bin", "natives/stm/sub/second.txt", "natives/stm/empty.bin"),
            progress: null,
            CancellationToken.None);

        Assert.Equal(first, ReadExtracted(destination, "natives/stm/first.bin"));
        Assert.Equal(second, ReadExtracted(destination, "natives/stm/sub/second.txt"));
        Assert.Empty(ReadExtracted(destination, "natives/stm/empty.bin"));
    }

    [Fact]
    public void Pack_EmptyDirectory_ProducesEmptyPak()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("empty");
        string pak = Path.Combine(temp.Path, "empty.pak");
        PakRepacker repacker = new();

        repacker.Pack(source, pak, new PakRepackProfile(), CancellationToken.None);

        Assert.Empty(repacker.Inspect(pak).Entries);
        string destination = temp.Dir("out");
        repacker.Unpack(pak, destination, CancellationToken.None);
        Assert.Empty(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Pack_V2Pak_Roundtrip()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] content = PakCodecTestTools.CreateBytes(512, seed: 3);
        PakCodecTestTools.WriteSourceFile(source, "nativepc/pl/one.bin", content);
        string pak = Path.Combine(temp.Path, "v2.pak");
        PakRepacker repacker = new();

        repacker.Pack(
            source,
            pak,
            new PakRepackProfile { Major = 2, Minor = 0, Feature = 0 },
            CancellationToken.None);

        PakArchiveInfo archive = repacker.Inspect(pak);
        Assert.Equal(2, archive.Major);
        PakEntryInfo entry = Assert.Single(archive.Entries);
        Assert.Equal(0, entry.CompressedSize);
        Assert.Equal(content.LongLength, entry.DecompressedSize);
        Assert.Equal(PakCodecTestTools.HashPath("nativepc/pl/one.bin"), entry.Hash);

        string destination = temp.Dir("out");
        repacker.Unpack(pak, destination, IndexOf("nativepc/pl/one.bin"), progress: null, CancellationToken.None);
        Assert.Equal(content, ReadExtracted(destination, "nativepc/pl/one.bin"));

        PakRepackProfile captured = repacker.Capture(pak);
        Assert.Equal(2, captured.Major);
        Assert.Equal(0, captured.CompressionType);
        Assert.Equal(0, captured.EncryptionType);
    }

    [Fact]
    public void Pack_ThenUnpack_UnknownEntriesRoundtrip()
    {
        using TempDir temp = TempDir.Create();
        const ulong unknownHash = 0x1234_5678_9ABC_DEF0uL;
        byte[] content = Encoding.ASCII.GetBytes("unknown-entry-payload");
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "in.pak",
            0,
            PakCodecTestTools.Stored(unknownHash, content));
        PakRepacker repacker = new();

        string destination = temp.Dir("unpacked");
        repacker.Unpack(built.Path, destination, CancellationToken.None);

        string unknownPath = Path.Combine(destination, "__Unknown", unknownHash.ToString("X16"));
        Assert.True(File.Exists(unknownPath), "路径清单未命中时应写成 __Unknown/<哈希>");
        Assert.Equal(content, File.ReadAllBytes(unknownPath));

        string repacked = Path.Combine(temp.Path, "repacked.pak");
        repacker.Pack(destination, repacked, repacker.Capture(built.Path), CancellationToken.None);

        Assert.Equal(unknownHash, Assert.Single(repacker.Inspect(repacked).Entries).Hash);
        string secondDestination = temp.Dir("unpacked2");
        repacker.Unpack(repacked, secondDestination, CancellationToken.None);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(secondDestination, "__Unknown", unknownHash.ToString("X16"))));
    }

    [Fact]
    public void Unpack_RenameFiles_Repack_UsesNewPaths()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] content = Encoding.ASCII.GetBytes("armor-model-bytes");
        PakCodecTestTools.WriteSourceFile(source, ArmorPath, content);
        string pak = Path.Combine(temp.Path, "mod.pak");
        PakRepacker repacker = new();
        PakRepackProfile profile = new() { Feature = 0, Fingerprint = 0x12345678 };
        repacker.Pack(source, pak, profile, CancellationToken.None);
        PakRepackProfile captured = repacker.Capture(pak);

        string working = temp.Dir("working");
        repacker.Unpack(pak, working, IndexOf(ArmorPath), progress: null, CancellationToken.None);
        string oldFile = Path.Combine(working, ArmorPath.Replace('/', Path.DirectorySeparatorChar));
        string newFile = Path.Combine(working, ArmorNewPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
        File.Move(oldFile, newFile);

        string repacked = Path.Combine(temp.Path, "repacked.pak");
        repacker.Pack(working, repacked, captured, CancellationToken.None);

        PakArchiveInfo archive = repacker.Inspect(repacked);
        ulong newHash = PakCodecTestTools.HashPath(ArmorNewPath);
        Assert.Equal(newHash, Assert.Single(archive.Entries).Hash);
        Assert.Equal(0, archive.Feature);

        string destination = temp.Dir("out");
        repacker.Unpack(repacked, destination, IndexOf(ArmorNewPath), progress: null, CancellationToken.None);
        Assert.Equal(content, ReadExtracted(destination, ArmorNewPath));
        Assert.False(File.Exists(Path.Combine(destination, ArmorPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void InspectAndUnpack_EncryptedTocFixture_UsesFormatAlgorithm()
    {
        using TempDir temp = TempDir.Create();
        byte[] storedContent = Encoding.ASCII.GetBytes("stored-model-bytes");
        byte[] deflatedContent = PakCodecTestTools.CreateCompressibleBytes(4096);
        ulong storedHash = PakCodecTestTools.HashPath("natives/stm/a.mdf2.45");
        ulong deflatedHash = PakCodecTestTools.HashPath("natives/stm/b.mod3");
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "encrypted.pak",
            8,
            PakCodecTestTools.Stored(storedHash, storedContent),
            PakCodecTestTools.Deflated(deflatedHash, deflatedContent));

        PakRepacker repacker = new();
        PakArchiveInfo archive = repacker.Inspect(built.Path);
        Assert.Equal(8, archive.Feature);
        Assert.Equal(2, archive.Entries.Count);
        HashSet<ulong> hashes = [.. archive.Entries.Select(static entry => entry.Hash)];
        Assert.Contains(storedHash, hashes);
        Assert.Contains(deflatedHash, hashes);

        PakRepackProfile captured = repacker.Capture(built.Path);
        Assert.True(captured.IsTocEncrypted);
        Assert.Equal(8, captured.Feature);
        Assert.Equal(built.TocKeyBlock, captured.TocKeyBlob);
        Assert.Equal(2, captured.EntryCount);

        string destination = temp.Dir("out");
        repacker.Unpack(built.Path, destination, IndexOf("natives/stm/a.mdf2.45", "natives/stm/b.mod3"), progress: null, CancellationToken.None);
        Assert.Equal(storedContent, ReadExtracted(destination, "natives/stm/a.mdf2.45"));
        Assert.Equal(deflatedContent, ReadExtracted(destination, "natives/stm/b.mod3"));
    }

    [Fact]
    public void InspectAndCapture_Feature24_PreservesExtraWordAndKeyBlock()
    {
        using TempDir temp = TempDir.Create();
        byte[] content = Encoding.ASCII.GetBytes("feature-24-entry");
        ulong hash = PakCodecTestTools.HashPath("natives/stm/c.tex.0");
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "encrypted24.pak",
            24,
            PakCodecTestTools.Stored(hash, content));
        PakRepacker repacker = new();

        PakArchiveInfo archive = repacker.Inspect(built.Path);
        Assert.Equal(24, archive.Feature);
        Assert.Equal(hash, Assert.Single(archive.Entries).Hash);

        PakRepackProfile captured = repacker.Capture(built.Path);
        Assert.True(captured.IsTocEncrypted);
        Assert.Equal(built.TocKeyBlock, captured.TocKeyBlob);
        Assert.Equal(built.FeaturePayload, captured.Feature24Payload);

        string destination = temp.Dir("out");
        repacker.Unpack(built.Path, destination, IndexOf("natives/stm/c.tex.0"), progress: null, CancellationToken.None);
        Assert.Equal(content, ReadExtracted(destination, "natives/stm/c.tex.0"));
    }

    [Fact]
    public void Pack_EncryptedToc_ReencryptsWithOriginalAlgorithm()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] first = PakCodecTestTools.CreateBytes(1000, seed: 11);
        byte[] second = Encoding.ASCII.GetBytes("second-entry");
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/one.bin", first);
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/two.bin", second);
        string pak = Path.Combine(temp.Path, "enc.pak");
        byte[] keyBlock = PakCodecTestTools.CreateKeyBlock(seed: 0x33);
        PakRepacker repacker = new();

        repacker.Pack(
            source,
            pak,
            new PakRepackProfile { Feature = 8, Fingerprint = 0x0BADF00D, TocKeyBlob = keyBlock },
            CancellationToken.None);

        byte[] bytes = File.ReadAllBytes(pak);
        (uint magic, byte major, byte minor, short feature, int count, uint fingerprint) = PakCodecTestTools.ReadHeader(bytes);
        Assert.Equal(RePakReader.Magic, magic);
        Assert.Equal((byte)4, major);
        Assert.Equal((byte)0, minor);
        Assert.Equal((short)8, feature);
        Assert.Equal(2, count);
        Assert.Equal(0x0BADF00Du, fingerprint);

        int tocLength = count * 48;
        byte[] storedKeyBlock = bytes.AsSpan(16 + tocLength, 128).ToArray();
        Assert.Equal(keyBlock, storedKeyBlock);

        // 用按格式独立实现的算法解密密文 TOC，确认条目表结构与哈希正确。
        // 用按格式独立实现的算法解密密文 TOC，确认条目表结构与哈希正确。
        byte[] plainToc = PakCodecTestTools.CryptToc(bytes.AsSpan(16, tocLength), storedKeyBlock);
        List<(ulong Hash, long Offset, long Compressed, long Decompressed)> parsed = [];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> raw = plainToc.AsSpan(i * 48, 48);
            ulong hash = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]) << 32)
                | BinaryPrimitives.ReadUInt32LittleEndian(raw);
            parsed.Add((
                hash,
                BinaryPrimitives.ReadInt64LittleEndian(raw[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(raw[16..]),
                BinaryPrimitives.ReadInt64LittleEndian(raw[24..])));
        }

        Assert.Equal(parsed.Select(static item => item.Hash).Order(), parsed.Select(static item => item.Hash));
        Assert.Contains(parsed, item => item.Hash == PakCodecTestTools.HashPath("natives/stm/one.bin")
            && item.Decompressed == first.Length
            && item.Offset >= 16 + tocLength + 128);
        Assert.Contains(parsed, item => item.Hash == PakCodecTestTools.HashPath("natives/stm/two.bin")
            && item.Decompressed == second.Length);

        PakArchiveInfo archive = repacker.Inspect(pak);
        Assert.Equal(8, archive.Feature);
        Assert.Equal(2, archive.Entries.Count);

        string destination = temp.Dir("out");
        repacker.Unpack(pak, destination, IndexOf("natives/stm/one.bin", "natives/stm/two.bin"), progress: null, CancellationToken.None);
        Assert.Equal(first, ReadExtracted(destination, "natives/stm/one.bin"));
        Assert.Equal(second, ReadExtracted(destination, "natives/stm/two.bin"));

        PakRepackProfile recaptured = repacker.Capture(pak);
        Assert.Equal(keyBlock, recaptured.TocKeyBlob);
    }

    [Fact]
    public void Pack_EncryptedToc_GeneratesKeyBlockWhenMissing()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/one.bin", Encoding.ASCII.GetBytes("payload"));
        string pak = Path.Combine(temp.Path, "enc.pak");
        PakRepacker repacker = new();

        repacker.Pack(source, pak, new PakRepackProfile { Feature = 8 }, CancellationToken.None);

        byte[] bytes = File.ReadAllBytes(pak);
        int tocLength = 48;
        byte[] keyBlock = bytes.AsSpan(16 + tocLength, 128).ToArray();
        Assert.Equal(128, keyBlock.Length);
        Assert.Contains(keyBlock, static value => value != 0);
        Assert.Equal((short)8, repacker.Inspect(pak).Feature);
        Assert.Equal(PakCodecTestTools.HashPath("natives/stm/one.bin"), Assert.Single(repacker.Inspect(pak).Entries).Hash);
    }

    [Fact]
    public void Pack_RepackEncryptedTocFixture_FullFallbackFlow()
    {
        using TempDir temp = TempDir.Create();
        byte[] modelContent = PakCodecTestTools.CreateCompressibleBytes(8192);
        byte[] otherContent = Encoding.ASCII.GetBytes("other-entry");
        ulong armorHash = PakCodecTestTools.HashPath(ArmorPath);
        ulong otherHash = PakCodecTestTools.HashPath("natives/stm/other.bin");
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "source.pak",
            8,
            PakCodecTestTools.Deflated(armorHash, modelContent),
            PakCodecTestTools.Stored(otherHash, otherContent));
        PakRepacker repacker = new();

        PakRepackProfile captured = repacker.Capture(built.Path);
        Assert.True(captured.IsTocEncrypted);

        string working = temp.Dir("working");
        repacker.Unpack(
            built.Path,
            working,
            IndexOf(ArmorPath, "natives/stm/other.bin"),
            progress: null,
            CancellationToken.None);

        string oldFile = Path.Combine(working, ArmorPath.Replace('/', Path.DirectorySeparatorChar));
        string newFile = Path.Combine(working, ArmorNewPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
        File.Move(oldFile, newFile);

        string repacked = Path.Combine(temp.Path, "repacked.pak");
        repacker.Pack(working, repacked, captured, CancellationToken.None);

        PakArchiveInfo archive = repacker.Inspect(repacked);
        Assert.Equal(8, archive.Feature);
        Assert.Equal(2, archive.Entries.Count);
        HashSet<ulong> hashes = [.. archive.Entries.Select(static entry => entry.Hash)];
        Assert.Contains(PakCodecTestTools.HashPath(ArmorNewPath), hashes);
        Assert.Contains(otherHash, hashes);
        Assert.DoesNotContain(armorHash, hashes);
        Assert.Equal(built.TocKeyBlock, repacker.Capture(repacked).TocKeyBlob);

        string destination = temp.Dir("out");
        repacker.Unpack(
            repacked,
            destination,
            IndexOf(ArmorNewPath, "natives/stm/other.bin"),
            progress: null,
            CancellationToken.None);
        Assert.Equal(modelContent, ReadExtracted(destination, ArmorNewPath));
        Assert.Equal(otherContent, ReadExtracted(destination, "natives/stm/other.bin"));
    }

    [Fact]
    public void Pack_FromEncryptedFixture_CanEmitPlainTocByClearingFeature()
    {
        using TempDir temp = TempDir.Create();
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(1024);
        ulong hash = PakCodecTestTools.HashPath("natives/stm/plain.bin");
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "encrypted.pak",
            8,
            PakCodecTestTools.Deflated(hash, content));
        PakRepacker repacker = new();
        PakRepackProfile captured = repacker.Capture(built.Path);

        string working = temp.Dir("working");
        repacker.Unpack(built.Path, working, IndexOf("natives/stm/plain.bin"), progress: null, CancellationToken.None);
        string plainPak = Path.Combine(temp.Path, "plain.pak");
        repacker.Pack(working, plainPak, captured with { Feature = 0 }, CancellationToken.None);

        Assert.Equal(0, repacker.Inspect(plainPak).Feature);
        Assert.Equal(hash, Assert.Single(repacker.Inspect(plainPak).Entries).Hash);
        string destination = temp.Dir("out");
        repacker.Unpack(plainPak, destination, IndexOf("natives/stm/plain.bin"), progress: null, CancellationToken.None);
        Assert.Equal(content, ReadExtracted(destination, "natives/stm/plain.bin"));
    }

    [Fact]
    public void Pack_Feature24_RoundtripsThroughInspectAndUnpack()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] content = PakCodecTestTools.CreateBytes(777, seed: 5);
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/x.bin", content);
        string pak = Path.Combine(temp.Path, "f24.pak");
        PakRepacker repacker = new();
        byte[] keyBlock = PakCodecTestTools.CreateKeyBlock(seed: 0x77);

        repacker.Pack(
            source,
            pak,
            new PakRepackProfile
            {
                Feature = 24,
                TocKeyBlob = keyBlock,
                Feature24Payload = [0xCA, 0xFE, 0xBA, 0xBE],
            },
            CancellationToken.None);

        byte[] bytes = File.ReadAllBytes(pak);
        int tocLength = 48;
        Assert.Equal((short)24, PakCodecTestTools.ReadHeader(bytes).Feature);
        Assert.Equal(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, bytes.AsSpan(16 + tocLength, 4).ToArray());
        Assert.Equal(keyBlock, bytes.AsSpan(16 + tocLength + 4, 128).ToArray());

        PakRepackProfile captured = repacker.Capture(pak);
        Assert.Equal(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, captured.Feature24Payload);

        string destination = temp.Dir("out");
        repacker.Unpack(pak, destination, IndexOf("natives/stm/x.bin"), progress: null, CancellationToken.None);
        Assert.Equal(content, ReadExtracted(destination, "natives/stm/x.bin"));
    }

    [Fact]
    public void Pack_DeflateCompression_WritesCompressedAttributes()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] compressible = PakCodecTestTools.CreateCompressibleBytes(16 * 1024);
        byte[] tiny = Encoding.ASCII.GetBytes("x");
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/big.bin", compressible);
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/tiny.bin", tiny);
        string pak = Path.Combine(temp.Path, "deflate.pak");
        PakRepacker repacker = new();

        repacker.Pack(source, pak, new PakRepackProfile { CompressionType = 1 }, CancellationToken.None);

        PakArchiveInfo archive = repacker.Inspect(pak);
        PakEntryInfo bigEntry = Assert.Single(archive.Entries, entry =>
            entry.Hash == PakCodecTestTools.HashPath("natives/stm/big.bin"));
        Assert.Equal(1, bigEntry.CompressionType);
        Assert.True(bigEntry.CompressedSize < bigEntry.DecompressedSize);

        PakEntryInfo tinyEntry = Assert.Single(archive.Entries, entry =>
            entry.Hash == PakCodecTestTools.HashPath("natives/stm/tiny.bin"));
        Assert.Equal(0, tinyEntry.CompressionType);
        Assert.Equal(tiny.LongLength, tinyEntry.CompressedSize);

        string destination = temp.Dir("out");
        repacker.Unpack(pak, destination, IndexOf("natives/stm/big.bin", "natives/stm/tiny.bin"), progress: null, CancellationToken.None);
        Assert.Equal(compressible, ReadExtracted(destination, "natives/stm/big.bin"));
        Assert.Equal(tiny, ReadExtracted(destination, "natives/stm/tiny.bin"));
    }

    [Fact]
    public void Pack_ZstdCompression_WritesCompressedAttributes()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] compressible = PakCodecTestTools.CreateCompressibleBytes(32 * 1024);
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/big.bin", compressible);
        string pak = Path.Combine(temp.Path, "zstd.pak");
        PakRepacker repacker = new();

        repacker.Pack(source, pak, new PakRepackProfile { CompressionType = 2 }, CancellationToken.None);

        PakEntryInfo entry = Assert.Single(repacker.Inspect(pak).Entries);
        Assert.Equal(2, entry.CompressionType);
        Assert.True(entry.CompressedSize < entry.DecompressedSize);

        string destination = temp.Dir("out");
        repacker.Unpack(pak, destination, IndexOf("natives/stm/big.bin"), progress: null, CancellationToken.None);
        Assert.Equal(compressible, ReadExtracted(destination, "natives/stm/big.bin"));
    }

    [Fact]
    public void Pack_ResourceEncryption_Roundtrip()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(4096);
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/secret.bin", content);
        string pak = Path.Combine(temp.Path, "enc-entry.pak");
        PakRepacker repacker = new();

        repacker.Pack(
            source,
            pak,
            new PakRepackProfile { CompressionType = 1, EncryptionType = 3 },
            CancellationToken.None);

        PakEntryInfo entry = Assert.Single(repacker.Inspect(pak).Entries);
        Assert.Equal(1, entry.CompressionType);
        Assert.Equal(3, entry.EncryptionType);

        // 用按格式文档独立实现的算法解开条目，确认生产侧加密可被原始算法还原。
        // 用按格式文档独立实现的算法解开条目，确认生产侧加密可被原始算法还原。
        byte[] stored = PakCodecTestTools.ReadStoredBytes(pak, entry);
        byte[] compressed = PakCodecTestTools.DecryptResource(stored);
        Assert.Equal(content, PakCodecTestTools.Inflate(compressed));

        string destination = temp.Dir("out");
        repacker.Unpack(pak, destination, IndexOf("natives/stm/secret.bin"), progress: null, CancellationToken.None);
        Assert.Equal(content, ReadExtracted(destination, "natives/stm/secret.bin"));
    }

    [Fact]
    public void Pack_ResourceEncryption_WithZstd_Roundtrip()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(5000);
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/secret.bin", content);
        string pak = Path.Combine(temp.Path, "zstd-encrypted.pak");
        PakRepacker repacker = new();

        repacker.Pack(
            source,
            pak,
            new PakRepackProfile { CompressionType = 2, EncryptionType = 1 },
            CancellationToken.None);

        PakEntryInfo entry = Assert.Single(repacker.Inspect(pak).Entries);
        Assert.Equal(2, entry.CompressionType);
        Assert.Equal(1, entry.EncryptionType);
        Assert.Equal(content.LongLength, entry.DecompressedSize);

        string destination = temp.Dir("out");
        repacker.Unpack(pak, destination, IndexOf("natives/stm/secret.bin"), progress: null, CancellationToken.None);
        Assert.Equal(content, ReadExtracted(destination, "natives/stm/secret.bin"));
    }

    [Fact]
    public void Unpack_ResourceEncryptedFixture_WithDecompressedSizeInHeader_StillDecodes()
    {
        using TempDir temp = TempDir.Create();
        ulong hash = PakCodecTestTools.HashPath("natives/stm/legacy.bin");
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(1500);
        PakTestEntry baseEntry = PakCodecTestTools.DeflatedEncrypted(hash, content, encryptionType: 1);
        byte[] stored = (byte[])baseEntry.Stored.Clone();
        // 老实现把 outLen 写成解压后大小（而不是承载的压缩数据长度），解包应当以块数为准并忽略填充。
        BinaryPrimitives.WriteInt64LittleEndian(stored, content.LongLength);
        PakTestEntry entry = baseEntry with { Stored = stored, DecompressedSize = content.LongLength };
        BuiltPak built = PakCodecTestTools.BuildPak(temp.Path, "legacy-encrypted.pak", 0, entry);

        string destination = temp.Dir("out");
        new PakRepacker().Unpack(built.Path, destination, IndexOf("natives/stm/legacy.bin"), progress: null, CancellationToken.None);
        Assert.Equal(content, ReadExtracted(destination, "natives/stm/legacy.bin"));
    }

    [Fact]
    public void Capture_PlainCompressedPak_RecordsPropertiesAndRoundtrips()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] compressible = PakCodecTestTools.CreateCompressibleBytes(8192);
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/big.bin", compressible);
        string pak = Path.Combine(temp.Path, "deflate.pak");
        PakRepacker repacker = new();
        repacker.Pack(source, pak, new PakRepackProfile { CompressionType = 1, Fingerprint = 0x5A5A5A5A }, CancellationToken.None);

        PakRepackProfile captured = repacker.Capture(pak);
        Assert.Equal(4, captured.Major);
        Assert.Equal(0, captured.Minor);
        Assert.Equal(0, captured.Feature);
        Assert.False(captured.IsTocEncrypted);
        Assert.Equal(0x5A5A5A5Au, captured.Fingerprint);
        Assert.Equal(1, captured.EntryCount);
        Assert.Equal(1, captured.CompressionType);
        Assert.Equal(0, captured.EncryptionType);
        Assert.Empty(captured.TocKeyBlob);

        string working = temp.Dir("working");
        repacker.Unpack(pak, working, IndexOf("natives/stm/big.bin"), progress: null, CancellationToken.None);
        string repacked = Path.Combine(temp.Path, "repacked.pak");
        repacker.Pack(working, repacked, captured, CancellationToken.None);

        PakEntryInfo entry = Assert.Single(repacker.Inspect(repacked).Entries);
        Assert.Equal(1, entry.CompressionType);
        Assert.Equal(0x5A5A5A5Au, repacker.Inspect(repacked).Fingerprint);

        string destination = temp.Dir("out");
        repacker.Unpack(repacked, destination, IndexOf("natives/stm/big.bin"), progress: null, CancellationToken.None);
        Assert.Equal(compressible, ReadExtracted(destination, "natives/stm/big.bin"));
    }

    [Fact]
    public void Progress_ReportsEveryEntryForPackAndUnpack()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        string[] paths = ["natives/stm/a.bin", "natives/stm/b.bin", "natives/stm/c.bin"];
        for (int i = 0; i < paths.Length; i++)
        {
            PakCodecTestTools.WriteSourceFile(source, paths[i], PakCodecTestTools.CreateBytes(64, seed: i));
        }

        string pak = Path.Combine(temp.Path, "progress.pak");
        PakRepacker repacker = new();
        RecordingProgress packProgress = new();
        repacker.Pack(source, pak, new PakRepackProfile(), packProgress, CancellationToken.None);

        Assert.Equal(paths.Length, packProgress.Reports.Count);
        Assert.Equal(paths.Length, packProgress.Reports[^1].TotalEntries);
        Assert.Equal(paths.Length, packProgress.Reports[^1].CompletedEntries);
        Assert.All(packProgress.Reports, report => Assert.False(string.IsNullOrEmpty(report.CurrentPath)));

        string destination = temp.Dir("out");
        RecordingProgress unpackProgress = new();
        repacker.Unpack(pak, destination, IndexOf(paths), unpackProgress, CancellationToken.None);

        Assert.Equal(paths.Length, unpackProgress.Reports.Count);
        Assert.Equal(paths.Length, unpackProgress.Reports[^1].TotalEntries);
        Assert.Equal(paths.Length, unpackProgress.Reports[^1].CompletedEntries);
    }

    [Fact]
    public void Pack_AtomicReplace_OverwritesExistingTarget()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] content = Encoding.ASCII.GetBytes("atomic");
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/a.bin", content);
        string pak = Path.Combine(temp.Path, "target.pak");
        File.WriteAllText(pak, "old-content");
        PakRepacker repacker = new();

        repacker.Pack(source, pak, new PakRepackProfile(), CancellationToken.None);

        PakArchiveInfo archive = repacker.Inspect(pak);
        Assert.Equal(PakCodecTestTools.HashPath("natives/stm/a.bin"), Assert.Single(archive.Entries).Hash);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void Unpack_ResourceEncryptedFixture_UsesFormatAlgorithm_AndCapturePreservesAttributes()
    {
        using TempDir temp = TempDir.Create();
        ulong hash = PakCodecTestTools.HashPath("natives/stm/encrypted.bin");
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(2048);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "enc-entry.pak",
            0,
            PakCodecTestTools.DeflatedEncrypted(hash, content, encryptionType: 2));
        PakRepacker repacker = new();

        PakEntryInfo sourceEntry = Assert.Single(repacker.Inspect(built.Path).Entries);
        Assert.Equal(1, sourceEntry.CompressionType);
        Assert.Equal(2, sourceEntry.EncryptionType);

        PakRepackProfile captured = repacker.Capture(built.Path);
        Assert.Equal(1, captured.CompressionType);
        Assert.Equal(2, captured.EncryptionType);

        string destination = temp.Dir("out");
        repacker.Unpack(built.Path, destination, IndexOf("natives/stm/encrypted.bin"), progress: null, CancellationToken.None);
        Assert.Equal(content, ReadExtracted(destination, "natives/stm/encrypted.bin"));

        string repacked = Path.Combine(temp.Path, "repacked.pak");
        repacker.Pack(destination, repacked, captured, CancellationToken.None);
        PakEntryInfo repackedEntry = Assert.Single(repacker.Inspect(repacked).Entries);
        Assert.Equal(1, repackedEntry.CompressionType);
        Assert.Equal(2, repackedEntry.EncryptionType);

        string secondDestination = temp.Dir("out2");
        repacker.Unpack(repacked, secondDestination, IndexOf("natives/stm/encrypted.bin"), progress: null, CancellationToken.None);
        Assert.Equal(content, ReadExtracted(secondDestination, "natives/stm/encrypted.bin"));
    }

    private sealed class RecordingProgress : IProgress<PakRepackProgress>
    {
        public List<PakRepackProgress> Reports { get; } = [];

        public void Report(PakRepackProgress value) => Reports.Add(value);
    }
}
