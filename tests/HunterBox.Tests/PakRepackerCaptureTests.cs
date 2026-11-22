using System.Text;
using HunterBox.Application.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// <see cref="PakRepacker.Capture"/> 对「整包属性不一致」的降级行为：
/// 单一 profile 表达不了逐条目属性时降级为不压缩/不加密（并写日志），不能抛异常也不能装作一致。
/// </summary>
public class PakRepackerCaptureTests
{
    private const string FirstPath = "natives/stm/a.bin";
    private const string SecondPath = "natives/stm/b.bin";
    private const string ThirdPath = "natives/stm/c.bin";

    private static BuiltPak BuildMixedCompressionPak(TempDir temp)
    {
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(4096);
        return PakCodecTestTools.BuildPak(
            temp.Path,
            "mixed.pak",
            0,
            PakCodecTestTools.Stored(PakPathHasher.HashPath(FirstPath), Encoding.ASCII.GetBytes("stored")),
            PakCodecTestTools.Deflated(PakPathHasher.HashPath(SecondPath), content));
    }

    [Fact]
    public void Capture_MixedCompression_DegradesToUncompressed()
    {
        using TempDir temp = TempDir.Create();
        BuiltPak built = BuildMixedCompressionPak(temp);
        PakRepacker repacker = new();

        PakArchiveInfo archive = repacker.Inspect(built.Path);
        Assert.Contains(archive.Entries, static entry => entry.CompressionType == 0);
        Assert.Contains(archive.Entries, static entry => entry.CompressionType == 1);

        PakRepackProfile captured = repacker.Capture(built.Path);

        Assert.Equal(4, captured.Major);
        Assert.Equal(0, captured.CompressionType);
        Assert.Equal(0, captured.EncryptionType);
        Assert.Equal(2, captured.EntryCount);
        Assert.Contains(captured.Warnings, warning => warning.Contains("压缩方式不一致"));
    }

    /// <summary>原样存放 + 加密无法保持该组合，必须明确告警降级为明文。</summary>
    [Fact]
    public void Capture_StoredEncrypted_WarnsPlaintextDowngrade()
    {
        using TempDir temp = TempDir.Create();
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(1024);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "stored-enc.pak",
            0,
            PakCodecTestTools.StoredEncrypted(PakPathHasher.HashPath(FirstPath), content));

        PakRepackProfile captured = new PakRepacker().Capture(built.Path);

        Assert.Equal(0, captured.CompressionType);
        Assert.Contains(captured.Warnings, warning => warning.Contains("降级为明文", StringComparison.Ordinal));
    }

    [Fact]
    public void Capture_UniformCompression_KeepsCompressionType()
    {
        using TempDir temp = TempDir.Create();
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(4096);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "deflate.pak",
            0,
            PakCodecTestTools.Deflated(PakPathHasher.HashPath(FirstPath), content),
            PakCodecTestTools.Deflated(PakPathHasher.HashPath(SecondPath), content));

        PakRepackProfile captured = new PakRepacker().Capture(built.Path);

        Assert.Equal(1, captured.CompressionType);
        Assert.Equal(0, captured.EncryptionType);
        Assert.Empty(captured.Warnings);
    }

    [Fact]
    public void Capture_MixedEncryption_DegradesToPlain()
    {
        using TempDir temp = TempDir.Create();
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(4096);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "mixed-enc.pak",
            0,
            PakCodecTestTools.DeflatedEncrypted(PakPathHasher.HashPath(FirstPath), content, encryptionType: 1),
            PakCodecTestTools.Deflated(PakPathHasher.HashPath(SecondPath), content));

        PakArchiveInfo archive = new PakRepacker().Inspect(built.Path);
        Assert.Contains(archive.Entries, static entry => entry.EncryptionType == 1);
        Assert.Contains(archive.Entries, static entry => entry.EncryptionType == 0);

        PakRepackProfile captured = new PakRepacker().Capture(built.Path);

        // 压缩方式一致（1），但加密方式混合：profile 只能记录不加密。
        Assert.Equal(1, captured.CompressionType);
        Assert.Equal(0, captured.EncryptionType);
        Assert.Contains(captured.Warnings, warning => warning.Contains("加密方式不一致"));
    }

    /// <summary>压缩与加密同时混合时，两条降级说明都必须上报。</summary>
    [Fact]
    public void Capture_MixedCompressionAndEncryption_ReportsBothWarnings()
    {
        using TempDir temp = TempDir.Create();
        byte[] content = PakCodecTestTools.CreateCompressibleBytes(4096);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "mixed-both.pak",
            0,
            PakCodecTestTools.Stored(PakPathHasher.HashPath(FirstPath), Encoding.ASCII.GetBytes("stored")),
            PakCodecTestTools.Deflated(PakPathHasher.HashPath(SecondPath), content),
            PakCodecTestTools.DeflatedEncrypted(PakPathHasher.HashPath(ThirdPath), content, encryptionType: 1));

        PakRepackProfile captured = new PakRepacker().Capture(built.Path);

        Assert.Equal(0, captured.CompressionType);
        Assert.Equal(0, captured.EncryptionType);
        Assert.Contains(captured.Warnings, warning => warning.Contains("压缩方式不一致"));
        Assert.Contains(captured.Warnings, warning => warning.Contains("加密方式不一致"));
    }
}
