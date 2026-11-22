using System.Text;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// 解包路径安全：Windows 保留设备名、尾随点/空格等「路径合法但写入目标不是普通文件」的情况必须拒绝。
/// 这些路径来自外部清单（不可信），只靠 <see cref="Path.GetInvalidFileNameChars"/> 拦不住。
/// </summary>
public class PakUnpackerPathSafetyTests
{
    private const ulong EntryHash = 0x1111_2222_3333_4444uL;

    private static void AssertRejected(string unsafePath)
    {
        using TempDir temp = TempDir.Create();
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "path.pak",
            0,
            PakCodecTestTools.Stored(EntryHash, Encoding.ASCII.GetBytes("payload")));
        PakNameIndex index = new();
        index.Add(EntryHash, unsafePath);
        string destination = temp.Dir("out");

        Assert.Throws<InvalidDataException>(() =>
            new PakRepacker().Unpack(built.Path, destination, index, progress: null, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    [Theory]
    [InlineData("natives/CON")]
    [InlineData("natives/con.bin")]
    [InlineData("natives/NUL")]
    [InlineData("natives/AUX.txt")]
    [InlineData("natives/PRN")]
    [InlineData("natives/COM1")]
    [InlineData("natives/LPT9.dat")]
    [InlineData("natives/stm/COM3.tex")]
    public void Unpack_ReservedDeviceName_ThrowsInvalidData(string unsafePath) => AssertRejected(unsafePath);

    [Theory]
    [InlineData("natives/stm/file.")]
    [InlineData("natives/stm/file ")]
    [InlineData("natives/stm/file. ")]
    [InlineData("natives/stm/dir./file.bin")]
    public void Unpack_TrailingDotOrSpace_ThrowsInvalidData(string unsafePath) => AssertRejected(unsafePath);

    /// <summary>空段与前导 <c>./</c> 是同一位置的另一写法，必须拒绝而不是静默折叠。</summary>
    [Theory]
    [InlineData("natives//stm/file.bin")]
    [InlineData("natives/stm//file.bin")]
    [InlineData("./natives/stm/file.bin")]
    [InlineData("natives\\stm\\\\file.bin")]
    public void Unpack_EmptySegmentOrDotPrefix_ThrowsInvalidData(string unsafePath) => AssertRejected(unsafePath);

    [Fact]
    public void Unpack_NormalPath_StillWorks()
    {
        using TempDir temp = TempDir.Create();
        const string relativePath = "natives/stm/art/model/character/ch03/000/000/body.mod3";
        byte[] content = PakCodecTestTools.CreateBytes(128, seed: 31);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "path.pak",
            0,
            PakCodecTestTools.Stored(EntryHash, content));
        PakNameIndex index = new();
        index.Add(EntryHash, relativePath);
        string destination = temp.Dir("out");

        new PakRepacker().Unpack(built.Path, destination, index, progress: null, CancellationToken.None);

        string file = Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(content, File.ReadAllBytes(file));
    }
}
