using System.Buffers.Binary;
using System.Text;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>解包 / 重打包的失败路径：结构损坏、路径不安全、配置非法、取消与原子写入。</summary>
public class PakRepackerFailureTests
{
    private static byte[] BuildSingleStoredPak(string directory, out ulong hash, out byte[] content)
    {
        hash = PakCodecTestTools.HashPath("natives/stm/entry.bin");
        content = Encoding.ASCII.GetBytes("entry-content");
        return File.ReadAllBytes(PakCodecTestTools.BuildPak(
            directory,
            "valid.pak",
            0,
            PakCodecTestTools.Stored(hash, content)).Path);
    }

    private static string WriteBytes(TempDir temp, string name, byte[] bytes)
    {
        string path = Path.Combine(temp.Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Inspect_WrongMagic_ThrowsInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildSingleStoredPak(temp.Path, out _, out _);
        bytes[0] = 0xFF;
        string pak = WriteBytes(temp, "bad-magic.pak", bytes);

        Assert.Throws<InvalidDataException>(() => new PakRepacker().Inspect(pak));
    }

    [Fact]
    public void Inspect_TruncatedHeader_ThrowsInvalidData()
    {
        using TempDir temp = TempDir.Create();
        string pak = WriteBytes(temp, "short.pak", new byte[10]);

        Assert.Throws<InvalidDataException>(() => new PakRepacker().Inspect(pak));
    }

    [Fact]
    public void Inspect_UnsupportedVersion_ThrowsInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildSingleStoredPak(temp.Path, out _, out _);
        bytes[4] = 9;
        string pak = WriteBytes(temp, "bad-version.pak", bytes);

        Assert.Throws<InvalidDataException>(() => new PakRepacker().Inspect(pak));
    }

    [Fact]
    public void Inspect_UnsupportedFeature_ThrowsNotSupported()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildSingleStoredPak(temp.Path, out _, out _);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), 16);
        string pak = WriteBytes(temp, "bad-feature.pak", bytes);

        Assert.Throws<NotSupportedException>(() => new PakRepacker().Inspect(pak));
    }

    [Fact]
    public void Inspect_EntryOutOfRange_ThrowsInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildSingleStoredPak(temp.Path, out _, out _);
        // 条目 0 的 offset 在 16 + 8 处；改成远超文件长度的值。
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16 + 8), bytes.LongLength + 4096);
        string pak = WriteBytes(temp, "bad-offset.pak", bytes);

        Assert.Throws<InvalidDataException>(() => new PakRepacker().Inspect(pak));
    }

    [Fact]
    public void Inspect_TruncatedToc_ThrowsInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildSingleStoredPak(temp.Path, out _, out _);
        string pak = WriteBytes(temp, "truncated.pak", bytes[..20]);

        Assert.Throws<InvalidDataException>(() => new PakRepacker().Inspect(pak));
    }

    [Fact]
    public void Inspect_TruncatedData_ThrowsInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildSingleStoredPak(temp.Path, out _, out _);
        string pak = WriteBytes(temp, "truncated-data.pak", bytes[..^2]);

        Assert.Throws<InvalidDataException>(() => new PakRepacker().Inspect(pak));
    }

    [Fact]
    public void Inspect_EncryptedTocMissingKeyBlock_ThrowsInvalidData()
    {
        using TempDir temp = TempDir.Create();
        BuiltPak built = PakCodecTestTools.BuildPak(temp.Path, "enc.pak", 8);
        byte[] bytes = File.ReadAllBytes(built.Path);
        string pak = WriteBytes(temp, "truncated-key.pak", bytes[..100]);

        Assert.Throws<InvalidDataException>(() => new PakRepacker().Inspect(pak));
    }

    [Fact]
    public void Unpack_PathTraversal_ThrowsInvalidDataAndWritesNothing()
    {
        using TempDir temp = TempDir.Create();
        const ulong hash = 0xDEAD_BEEF_1234_5678uL;
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "path.pak",
            0,
            PakCodecTestTools.Stored(hash, Encoding.ASCII.GetBytes("payload")));
        PakRepacker repacker = new();
        string destination = temp.Dir("root");

        PakNameIndex traversal = new();
        traversal.Add(hash, "../escaped.bin");
        Assert.Throws<InvalidDataException>(() =>
            repacker.Unpack(built.Path, destination, traversal, progress: null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(temp.Path, "escaped.bin")));

        PakNameIndex nested = new();
        nested.Add(hash, "natives/../../escaped.bin");
        Assert.Throws<InvalidDataException>(() =>
            repacker.Unpack(built.Path, destination, nested, progress: null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(temp.Path, "escaped.bin")));

        PakNameIndex absolute = new();
        absolute.Add(hash, "C:/escaped.bin");
        Assert.Throws<InvalidDataException>(() =>
            repacker.Unpack(built.Path, destination, absolute, progress: null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(temp.Path, "escaped.bin", SearchOption.AllDirectories));
    }

    [Fact]
    public void Unpack_Cancelled_ThrowsOperationCanceled()
    {
        using TempDir temp = TempDir.Create();
        const ulong hash = 0x1111_2222_3333_4444uL;
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "cancel.pak",
            0,
            PakCodecTestTools.Stored(hash, Encoding.ASCII.GetBytes("payload")));
        string destination = Path.Combine(temp.Path, "out");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new PakRepacker().Unpack(built.Path, destination, cancellation.Token));
        if (Directory.Exists(destination))
        {
            Assert.DoesNotContain(
                Directory.GetFiles(destination, "*", SearchOption.AllDirectories),
                path => path.StartsWith(destination, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Pack_Cancelled_LeavesTargetUntouched()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/a.bin", Encoding.ASCII.GetBytes("data"));
        string pak = Path.Combine(temp.Path, "target.pak");
        byte[] sentinel = Encoding.ASCII.GetBytes("sentinel-content");
        File.WriteAllBytes(pak, sentinel);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new PakRepacker().Pack(source, pak, new PakRepackProfile(), cancellation.Token));

        Assert.Equal(sentinel, File.ReadAllBytes(pak));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Pack_InvalidUnknownFileName_LeavesTargetUntouched()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        PakCodecTestTools.WriteSourceFile(source, "__Unknown/not-a-hex-hash.bin", Encoding.ASCII.GetBytes("x"));
        string pak = Path.Combine(temp.Path, "target.pak");
        byte[] sentinel = Encoding.ASCII.GetBytes("sentinel-content");
        File.WriteAllBytes(pak, sentinel);

        Assert.Throws<InvalidDataException>(() =>
            new PakRepacker().Pack(source, pak, new PakRepackProfile(), CancellationToken.None));

        Assert.Equal(sentinel, File.ReadAllBytes(pak));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Pack_DuplicateHashes_LeavesTargetUntouched()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        PakCodecTestTools.WriteSourceFile(source, "__Unknown/0000000000000001", Encoding.ASCII.GetBytes("first"));
        PakCodecTestTools.WriteSourceFile(source, "__Unknown/0000000000000001.dup", Encoding.ASCII.GetBytes("second"));
        string pak = Path.Combine(temp.Path, "target.pak");
        byte[] sentinel = Encoding.ASCII.GetBytes("sentinel-content");
        File.WriteAllBytes(pak, sentinel);

        Assert.Throws<InvalidDataException>(() =>
            new PakRepacker().Pack(source, pak, new PakRepackProfile(), CancellationToken.None));

        Assert.Equal(sentinel, File.ReadAllBytes(pak));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Pack_InvalidProfile_ThrowsArgumentException()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/a.bin", Encoding.ASCII.GetBytes("data"));
        string pak = Path.Combine(temp.Path, "out.pak");
        PakRepacker repacker = new();

        void AssertRejected(PakRepackProfile profile) =>
            Assert.Throws<ArgumentException>(() => repacker.Pack(source, pak, profile, CancellationToken.None));

        AssertRejected(new PakRepackProfile { Major = 3 });
        AssertRejected(new PakRepackProfile { Feature = 16 });
        AssertRejected(new PakRepackProfile { CompressionType = 7 });
        AssertRejected(new PakRepackProfile { EncryptionType = 6 });
        AssertRejected(new PakRepackProfile { Major = 2, CompressionType = 1 });
        AssertRejected(new PakRepackProfile { CompressionType = 0, EncryptionType = 2 });
        AssertRejected(new PakRepackProfile { Feature = 8, TocKeyBlob = new byte[5] });
        AssertRejected(new PakRepackProfile { Feature = 8, Feature24Payload = new byte[5] });
        Assert.False(File.Exists(pak));
    }

    [Fact]
    public void Pack_MissingSourceDirectory_ThrowsDirectoryNotFound()
    {
        using TempDir temp = TempDir.Create();
        string source = Path.Combine(temp.Path, "missing");
        string pak = Path.Combine(temp.Path, "out.pak");

        Assert.Throws<DirectoryNotFoundException>(() =>
            new PakRepacker().Pack(source, pak, new PakRepackProfile(), CancellationToken.None));
    }

    [Fact]
    public void Unpack_DecompressedSizeMismatch_ThrowsInvalidData()
    {
        using TempDir temp = TempDir.Create();
        ulong hash = PakCodecTestTools.HashPath("natives/stm/entry.bin");
        byte[] content = Encoding.ASCII.GetBytes("deflated-entry");
        PakTestEntry deflated = PakCodecTestTools.Deflated(hash, content);
        PakTestEntry wrongSize = deflated with { DecompressedSize = content.Length + 5L };
        BuiltPak built = PakCodecTestTools.BuildPak(temp.Path, "bad-size.pak", 0, wrongSize);
        string destination = temp.Dir("out");
        PakNameIndex index = PakPathHasher.BuildIndex(["natives/stm/entry.bin"]);

        Assert.Throws<InvalidDataException>(() =>
            new PakRepacker().Unpack(built.Path, destination, index, progress: null, CancellationToken.None));
    }

    [Fact]
    public void Unpack_UnknownCompression_ThrowsNotSupported()
    {
        using TempDir temp = TempDir.Create();
        ulong hash = PakCodecTestTools.HashPath("natives/stm/entry.bin");
        PakTestEntry entry = new(hash, Encoding.ASCII.GetBytes("data"), 4L, 9L);
        BuiltPak built = PakCodecTestTools.BuildPak(temp.Path, "bad-compression.pak", 0, entry);
        string destination = temp.Dir("out");
        PakNameIndex index = PakPathHasher.BuildIndex(["natives/stm/entry.bin"]);

        Assert.Throws<NotSupportedException>(() =>
            new PakRepacker().Unpack(built.Path, destination, index, progress: null, CancellationToken.None));
    }

    [Fact]
    public void Pack_SkipsUnpackMetadataFile()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        PakCodecTestTools.WriteSourceFile(source, "natives/stm/a.bin", Encoding.ASCII.GetBytes("data"));
        File.WriteAllLines(Path.Combine(source, "file.list"), ["natives/stm/a.bin"]);
        string pak = Path.Combine(temp.Path, "out.pak");

        new PakRepacker().Pack(source, pak, new PakRepackProfile(), CancellationToken.None);

        Assert.Equal(PakCodecTestTools.HashPath("natives/stm/a.bin"), Assert.Single(new PakRepacker().Inspect(pak).Entries).Hash);
    }
}
