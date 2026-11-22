using System.Buffers.Binary;
using System.Text;
using HunterBox.Application.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// D-03：明文 TOC 解析层合并前的字节级 / 异常行为回归基线。
/// 钉住两处读取器各自的容忍度：
/// <list type="bullet">
/// <item><see cref="RePakReader"/>：密文原样解析、不校验 feature、不校验条目；</item>
/// <item><see cref="PakArchiveCodec"/>（经 <see cref="PakRepacker.Inspect"/>）：校验 feature、解密 TOC、校验条目。</item>
/// </list>
/// 重构后本文件必须继续绿——两处可以共用「明文头 + TOC 读取 + 条目解码」，但不能把上述差异抹平。
/// </summary>
public class PakPlainTocParsingRegressionTests
{
    private static PakArchiveInfo ReadViaCodec(string pakPath)
    {
        using FileStream stream = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PakArchiveCodec.Read(stream).Archive;
    }

    private static string WritePak(TempDir temp, string name, byte[] bytes)
    {
        string path = Path.Combine(temp.Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>独立按 v4 条目布局解析 TOC 内的一项（不依赖生产实现的布局定义）。</summary>
    private static PakEntryInfo ParseV4TocEntry(ReadOnlySpan<byte> raw) => new(
        HashLower: BinaryPrimitives.ReadUInt32LittleEndian(raw),
        HashUpper: BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]),
        Offset: BinaryPrimitives.ReadInt64LittleEndian(raw[8..]),
        CompressedSize: BinaryPrimitives.ReadInt64LittleEndian(raw[16..]),
        DecompressedSize: BinaryPrimitives.ReadInt64LittleEndian(raw[24..]),
        Attributes: BinaryPrimitives.ReadInt64LittleEndian(raw[32..]),
        Checksum: BinaryPrimitives.ReadUInt64LittleEndian(raw[40..]));

    [Fact]
    public void PlainV4_ReaderAndCodec_AgreeByteForByte()
    {
        using TempDir temp = TempDir.Create();
        byte[] stored = Encoding.ASCII.GetBytes("stored-content");
        PakTestEntry[] entries =
        [
            PakCodecTestTools.Stored(0x1111_2222_3333_4444uL, stored),
            PakCodecTestTools.Deflated(0x5555_6666_7777_8888uL, Encoding.ASCII.GetBytes("deflated-content")),
            PakCodecTestTools.DeflatedEncrypted(0x9999_AAAA_BBBB_CCCCuL, Encoding.ASCII.GetBytes("encrypted-content")),
        ];
        BuiltPak built = PakCodecTestTools.BuildPak(temp.Path, "plain-v4.pak", 0, entries);

        PakArchiveInfo reader = new RePakReader().Read(built.Path);
        PakArchiveInfo codec = ReadViaCodec(built.Path);

        Assert.Equal(codec.Major, reader.Major);
        Assert.Equal(codec.Minor, reader.Minor);
        Assert.Equal(codec.Feature, reader.Feature);
        Assert.Equal(codec.Fingerprint, reader.Fingerprint);
        Assert.False(reader.IsTocEncrypted);
        Assert.Equal(codec.Entries, reader.Entries);

        // 字节级：条目 offset 必须指向磁盘上真实数据区，读出的原始字节与构造输入一致。
        long expectedOffset = PakCodecTestTools.HeaderSize + (entries.Length * 48L);
        for (int i = 0; i < entries.Length; i++)
        {
            Assert.Equal(expectedOffset, reader.Entries[i].Offset);
            Assert.Equal(entries[i].DecompressedSize, reader.Entries[i].DecompressedSize);
            Assert.Equal(entries[i].Attributes, reader.Entries[i].Attributes);
            Assert.Equal(entries[i].Stored, PakCodecTestTools.ReadStoredBytes(built.Path, reader.Entries[i]));
            expectedOffset += entries[i].Stored.LongLength;
        }
    }

    [Fact]
    public void PlainV2_ReaderAndCodec_AgreeByteForByte()
    {
        using TempDir temp = TempDir.Create();
        string pak = PakCodecTestTools.BuildV2Pak(
            temp.Path,
            "plain-v2.pak",
            (0x1111_2222_3333_4444uL, Encoding.ASCII.GetBytes("one")),
            (0x5555_6666_7777_8888uL, Encoding.ASCII.GetBytes("two-two")));

        PakArchiveInfo reader = new RePakReader().Read(pak);
        PakArchiveInfo codec = ReadViaCodec(pak);

        Assert.Equal(2, reader.Major);
        Assert.Equal(codec.Entries, reader.Entries);

        long expectedOffset = PakCodecTestTools.HeaderSize + (2 * 24L);
        foreach (PakEntryInfo entry in reader.Entries)
        {
            Assert.Equal(expectedOffset, entry.Offset);
            Assert.Equal(entry.DecompressedSize, PakCodecTestTools.ReadStoredBytes(pak, entry).LongLength);
            expectedOffset += entry.DecompressedSize;
        }
    }

    /// <summary>加密 TOC：明文读取器必须返回密文原样解析，codec 才解密。</summary>
    [Fact]
    public void EncryptedToc_ReaderKeepsCiphertext_CodecDecrypts()
    {
        using TempDir temp = TempDir.Create();
        const ulong hash = 0x1111_2222_3333_4444uL;
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "encrypted.pak",
            8,
            PakCodecTestTools.Stored(hash, Encoding.ASCII.GetBytes("x")));

        byte[] bytes = File.ReadAllBytes(built.Path);
        PakEntryInfo ciphertextEntry = ParseV4TocEntry(bytes.AsSpan(PakCodecTestTools.HeaderSize, 48));
        PakArchiveInfo reader = new RePakReader().Read(built.Path);
        Assert.True(reader.IsTocEncrypted);
        Assert.Equal((short)8, reader.Feature);
        Assert.Equal(ciphertextEntry, Assert.Single(reader.Entries));

        PakArchiveInfo codec = ReadViaCodec(built.Path);
        Assert.True(codec.IsTocEncrypted);
        PakEntryInfo decrypted = Assert.Single(codec.Entries);
        Assert.Equal(hash, decrypted.Hash);
        // dataStart = 16 字节头 + 48 字节条目 + 128 字节密钥块。
        Assert.Equal(192L, decrypted.Offset);
        Assert.NotEqual(ciphertextEntry.Offset, decrypted.Offset);
    }

    /// <summary>feature 24 = 加密 TOC + 4 字节附加数据：数据区起点必须算对（16 + TOC + 4 + 128）。</summary>
    [Fact]
    public void EncryptedTocWithExtraWord_CodecFindsDataStart()
    {
        using TempDir temp = TempDir.Create();
        const ulong hash = 0x1111_2222_3333_4444uL;
        byte[] content = Encoding.ASCII.GetBytes("payload");
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "encrypted24.pak",
            24,
            PakCodecTestTools.Stored(hash, content));

        PakArchiveInfo reader = new RePakReader().Read(built.Path);
        Assert.Equal((short)24, reader.Feature);
        Assert.True(reader.IsTocEncrypted);
        Assert.Single(reader.Entries);

        PakArchiveInfo codec = ReadViaCodec(built.Path);
        PakEntryInfo decrypted = Assert.Single(codec.Entries);
        Assert.Equal(hash, decrypted.Hash);
        Assert.Equal(content.LongLength, decrypted.DecompressedSize);
        Assert.Equal(196L, decrypted.Offset);
        Assert.Equal(content, PakCodecTestTools.ReadStoredBytes(built.Path, decrypted));
    }

    /// <summary>容忍度差异：越界条目 RePakReader 原样返回，codec 必须拒绝。</summary>
    [Fact]
    public void OutOfRangeEntry_ReaderTolerates_CodecRejects()
    {
        using TempDir temp = TempDir.Create();
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "bad-offset.pak",
            0,
            PakCodecTestTools.Stored(0x1111_2222_3333_4444uL, Encoding.ASCII.GetBytes("payload")));
        byte[] bytes = File.ReadAllBytes(built.Path);
        long badOffset = bytes.LongLength + 4096;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(PakCodecTestTools.HeaderSize + 8), badOffset);
        File.WriteAllBytes(built.Path, bytes);

        PakEntryInfo entry = Assert.Single(new RePakReader().Read(built.Path).Entries);
        Assert.Equal(badOffset, entry.Offset);
        Assert.Throws<InvalidDataException>(() => new PakRepacker().Inspect(built.Path));
    }

    /// <summary>容忍度差异：未知 feature RePakReader 照读（只按位判断 TOC 是否加密），codec 明确拒绝。</summary>
    [Fact]
    public void UnknownFeature_ReaderParses_CodecRejects()
    {
        using TempDir temp = TempDir.Create();
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "bad-feature.pak",
            0,
            PakCodecTestTools.Stored(0x1111_2222_3333_4444uL, Encoding.ASCII.GetBytes("payload")));
        byte[] bytes = File.ReadAllBytes(built.Path);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), 16);
        File.WriteAllBytes(built.Path, bytes);

        PakArchiveInfo archive = new RePakReader().Read(built.Path);
        Assert.Equal((short)16, archive.Feature);
        Assert.False(archive.IsTocEncrypted);
        Assert.False(PakArchiveInfo.IsSupportedMajorMinor(archive.Major, archive.Minor) && PakArchiveInfo.IsSupportedFeature(archive.Feature));
        Assert.Single(archive.Entries);
        Assert.Throws<NotSupportedException>(() => new PakRepacker().Inspect(built.Path));
    }

    /// <summary>容忍度差异：加密 TOC 缺密钥块时 RePakReader 只读头+TOC 仍成功，codec 必须拒绝。</summary>
    [Fact]
    public void EncryptedTocMissingKeyBlock_ReaderTolerates_CodecRejects()
    {
        using TempDir temp = TempDir.Create();
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "missing-key.pak",
            8,
            PakCodecTestTools.Stored(0x1111_2222_3333_4444uL, Encoding.ASCII.GetBytes("payload")));
        byte[] bytes = File.ReadAllBytes(built.Path);
        File.WriteAllBytes(built.Path, bytes[..(PakCodecTestTools.HeaderSize + 48)]);

        PakArchiveInfo archive = new RePakReader().Read(built.Path);
        Assert.True(archive.IsTocEncrypted);
        Assert.Single(archive.Entries);
        Assert.Throws<InvalidDataException>(() => new PakRepacker().Inspect(built.Path));
    }

    // ---- 畸形头/TOC：两个读取器都必须抛 InvalidDataException（异常类型是行为契约） ----

    [Fact]
    public void Malformed_EmptyFile_BothReadersThrowInvalidData()
    {
        using TempDir temp = TempDir.Create();
        AssertBothReject(temp, "empty.pak", []);
    }

    [Fact]
    public void Malformed_ShortHeader_BothReadersThrowInvalidData()
    {
        using TempDir temp = TempDir.Create();
        AssertBothReject(temp, "short-header.pak", new byte[10]);
    }

    [Fact]
    public void Malformed_WrongMagic_BothReadersThrowInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildValidPak(temp);
        bytes[0] = 0xFF;
        AssertBothReject(temp, "wrong-magic.pak", bytes);
    }

    [Fact]
    public void Malformed_UnsupportedMajor_BothReadersThrowInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildValidPak(temp);
        bytes[4] = 9;
        AssertBothReject(temp, "wrong-major.pak", bytes);
    }

    [Fact]
    public void Malformed_UnsupportedMinor_BothReadersThrowInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildValidPak(temp);
        bytes[5] = 2;
        AssertBothReject(temp, "wrong-minor.pak", bytes);
    }

    [Fact]
    public void Malformed_NegativeCount_BothReadersThrowInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildValidPak(temp);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), -1);
        AssertBothReject(temp, "negative-count.pak", bytes);
    }

    [Fact]
    public void Malformed_HugeCount_BothReadersThrowInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildValidPak(temp);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 5_000_001);
        AssertBothReject(temp, "huge-count.pak", bytes);
    }

    [Fact]
    public void Malformed_TruncatedToc_BothReadersThrowInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildValidPak(temp)[..(PakCodecTestTools.HeaderSize + 16)];
        AssertBothReject(temp, "truncated-toc.pak", bytes);
    }

    private static byte[] BuildValidPak(TempDir temp) =>
        File.ReadAllBytes(PakCodecTestTools.BuildPak(
            temp.Path,
            "valid-" + Guid.NewGuid().ToString("N") + ".pak",
            0,
            PakCodecTestTools.Stored(0x1111_2222_3333_4444uL, Encoding.ASCII.GetBytes("payload"))).Path);

    /// <summary>
    /// 合并后的统一语义：TOC 读到一半提前 EOF（Length 已报告完整、读取却短读，等价并发截断）
    /// 两个读取器都抛 <see cref="InvalidDataException"/>。
    /// 合并前 codec 走 <c>ReadExactly</c> 抛 <see cref="EndOfStreamException"/>、RePakReader 抛
    /// <see cref="InvalidDataException"/>；统一后与两者文档里的异常契约一致。
    /// </summary>
    [Fact]
    public void Codec_TocReadTruncatedByStream_SurfacesInvalidData()
    {
        using TempDir temp = TempDir.Create();
        byte[] bytes = BuildValidPak(temp);
        using TruncatedReadStream stream = new(bytes, readableLength: PakCodecTestTools.HeaderSize + 8);

        Assert.Throws<InvalidDataException>(() => PakArchiveCodec.Read(stream));
    }

    private static void AssertBothReject(TempDir temp, string name, byte[] bytes)
    {
        string path = WritePak(temp, name, bytes);
        Assert.Throws<InvalidDataException>(() => new RePakReader().Read(path));
        Assert.Throws<InvalidDataException>(() => new PakRepacker().Inspect(path));
    }

    /// <summary>模拟「长度已报告完整、实际读取中途被截断」的数据流（并发截断/延迟分配）。</summary>
    private sealed class TruncatedReadStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _readableLength;
        private long _position;

        public TruncatedReadStream(byte[] bytes, int readableLength)
        {
            _bytes = bytes;
            _readableLength = readableLength;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _bytes.LongLength;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = (int)Math.Clamp(_readableLength - _position, 0, count);
            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(_bytes, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _bytes.LongLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
