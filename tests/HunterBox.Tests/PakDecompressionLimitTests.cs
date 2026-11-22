using System.Buffers.Binary;
using System.Text;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// 解压输出上限（C-06）：不能等全部解压完再比长度，必须边解压边按
/// 「条目声明的 DecompressedSize + 绝对上限（512MB）」中止，防压缩炸弹 / OOM。
/// </summary>
public class PakDecompressionLimitTests
{
    private const string EntryPath = "natives/stm/bomb.bin";

    /// <summary>绝对上限（512MB）之上：测试按「远大于 512MB」取 600MB。</summary>
    private const long AboveAbsoluteCap = 600L * 1024 * 1024;

    /// <summary>压缩炸弹解压目标大小：32MB 全零，DEFLATE 后只有几十 KB。</summary>
    private const int DeflateBombSize = 32 * 1024 * 1024;

    /// <summary>声明尺寸按 4KB 撒谎，远小于真实解压大小。</summary>
    private const long LyingDeclaredSize = 4096L;

    [Fact]
    public void ReadEntry_DeclaredSizeAboveAbsoluteCap_ThrowsWithoutDecompressing()
    {
        using TempDir temp = TempDir.Create();
        const ulong hash = 0xAAAA_BBBB_CCCC_DDDDuL;
        // 数据本身很小；仅凭条目声明的超大尺寸就应当直接拒绝，不必真的解压。
        PakTestEntry entry = new(
            hash,
            PakCodecTestTools.Deflate(Encoding.ASCII.GetBytes("tiny")),
            AboveAbsoluteCap,
            Attributes: 1L);
        BuiltPak built = PakCodecTestTools.BuildPak(temp.Path, "over-cap.pak", 0, entry);
        PakEntryInfo info = Assert.Single(new RePakReader().Read(built.Path).Entries);

        Assert.Throws<InvalidDataException>(() => new RePakEntryReader().ReadEntry(built.Path, info));
    }

    /// <summary>
    /// 高压缩比 + 声明尺寸撒谎：32MB 全零压成几十 KB，条目却声明只解压 4KB。
    /// 有界复制必须在越过 4KB 时立即中止，而不是把 32MB 全解出来再比长度。
    /// </summary>
    [Fact]
    public void ReadEntry_HighCompressionRatioBomb_AbortsAtDeclaredLimit()
    {
        using TempDir temp = TempDir.Create();
        byte[] content = new byte[DeflateBombSize];
        byte[] compressed = PakCodecTestTools.Deflate(content);
        Assert.True(
            compressed.Length < DeflateBombSize / 100,
            $"压缩后长度应远小于解压后（{compressed.Length} vs {DeflateBombSize}），用于构造压缩炸弹");
        const ulong hash = 0x1234_5678_9ABC_DEF0uL;
        PakTestEntry entry = new(hash, compressed, LyingDeclaredSize, Attributes: 1L);
        BuiltPak built = PakCodecTestTools.BuildPak(temp.Path, "deflate-bomb.pak", 0, entry);
        PakEntryInfo info = Assert.Single(new RePakReader().Read(built.Path).Entries);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => new RePakEntryReader().ReadEntry(built.Path, info));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 旧实现会先解出 32MB（至少分配 32MB）；有界复制应在 4KB 处中止。
        Assert.True(
            allocated < 16L * 1024 * 1024,
            $"解压应在越过声明上限时立即中止，当前线程实际分配 {allocated} 字节（疑似无界解压）");
    }

    /// <summary>同一条压缩炸弹走 <see cref="PakRepacker.Unpack"/> 的流式解包路径也必须有界。</summary>
    [Fact]
    public void Unpack_DeflateHighCompressionRatioBomb_AbortsAtDeclaredLimit()
    {
        using TempDir temp = TempDir.Create();
        ulong hash = PakCodecTestTools.HashPath(EntryPath);
        PakTestEntry entry = new(
            hash,
            PakCodecTestTools.Deflate(new byte[DeflateBombSize]),
            LyingDeclaredSize,
            Attributes: 1L);
        BuiltPak built = PakCodecTestTools.BuildPak(temp.Path, "deflate-bomb.pak", 0, entry);
        string destination = temp.Dir("out");
        PakNameIndex index = PakPathHasher.BuildIndex([EntryPath]);

        Assert.Throws<InvalidDataException>(() =>
            new PakRepacker().Unpack(built.Path, destination, index, progress: null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(temp.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    /// <summary>ZSTD 路径（PakEntryCodec.Unzstd）同样必须有界：真实 pak 由打包器生成后篡改声明尺寸。</summary>
    [Fact]
    public void Unpack_ZstdHighCompressionRatioBomb_AbortsAtDeclaredLimit()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] content = new byte[16 * 1024 * 1024];
        PakCodecTestTools.WriteSourceFile(source, EntryPath, content);
        string pak = Path.Combine(temp.Path, "zstd-bomb.pak");
        new PakRepacker().Pack(
            source,
            pak,
            new PakRepackProfile { CompressionType = 2 },
            CancellationToken.None);

        // v4 条目表：包头 16 字节 + 条目 48 字节；decompressedSize 在条目内 +24。
        byte[] bytes = File.ReadAllBytes(pak);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
        Assert.True(
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16 + 16)) < content.LongLength,
            "ZSTD 压缩后应小于原始大小，用于构造压缩炸弹");
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16 + 24), LyingDeclaredSize);
        File.WriteAllBytes(pak, bytes);

        string destination = temp.Dir("out");
        PakNameIndex index = PakPathHasher.BuildIndex([EntryPath]);
        Assert.Throws<InvalidDataException>(() =>
            new PakRepacker().Unpack(pak, destination, index, progress: null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
    }

    /// <summary>合法的、低于上限的压缩条目不能被误伤。</summary>
    [Fact]
    public void ReadEntry_LegitimateCompressedContent_StillDecompresses()
    {
        using TempDir temp = TempDir.Create();
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(8 * 1024 * 1024);
        const ulong hash = 0x0BAD_F00D_1234_5678uL;
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "legit.pak",
            0,
            PakCodecTestTools.Deflated(hash, content));
        PakEntryInfo info = Assert.Single(new RePakReader().Read(built.Path).Entries);

        Assert.Equal(content, new RePakEntryReader().ReadEntry(built.Path, info));
    }

    /// <summary>声明尺寸比实际解压结果小时同样尽早失败（不是解完再比）。</summary>
    [Fact]
    public void ReadEntry_DeclaredSizeSmallerThanActual_Throws()
    {
        using TempDir temp = TempDir.Create();
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(1024 * 1024);
        const ulong hash = 0x5555_6666_7777_8888uL;
        PakTestEntry entry = new(
            hash,
            PakCodecTestTools.Deflate(content),
            DecompressedSize: 1024L,
            Attributes: 1L);
        BuiltPak built = PakCodecTestTools.BuildPak(temp.Path, "small-declared.pak", 0, entry);
        PakEntryInfo info = Assert.Single(new RePakReader().Read(built.Path).Entries);

        Assert.Throws<InvalidDataException>(() => new RePakEntryReader().ReadEntry(built.Path, info));
    }
}
