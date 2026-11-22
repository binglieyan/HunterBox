using System.Text;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// 打包路径哈希的大小写约定：引擎低 32 位是 <c>hash(全小写路径)</c>，
/// 磁盘上的文件名大小写不可信，打包时必须先归一为小写（与改写路径的约定一致）。
/// </summary>
public class PakPackerPathHashTests
{
    private const string UpperPath = "Natives/STM/Art/Model/Character/ch03/000/000/Body.MOD3";
    private const string LowerPath = "natives/stm/art/model/character/ch03/000/000/body.mod3";

    [Fact]
    public void Pack_UppercaseFileName_UsesLowercaseHash()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("src");
        byte[] content = Encoding.ASCII.GetBytes("model-content");
        PakCodecTestTools.WriteSourceFile(source, UpperPath, content);
        string pak = Path.Combine(temp.Path, "upper.pak");

        new PakRepacker().Pack(source, pak, new PakRepackProfile { Major = 4 }, CancellationToken.None);

        PakEntryInfo entry = Assert.Single(new PakRepacker().Inspect(pak).Entries);
        Assert.Equal(PakPathHasher.HashPath(LowerPath), entry.Hash);
        Assert.NotEqual(PakPathHasher.HashPath(UpperPath), entry.Hash);

        // 用按引擎约定（小写）构建的清单能认出条目并解包。
        PakNameIndex index = PakPathHasher.BuildIndex([LowerPath]);
        string destination = temp.Dir("out");
        new PakRepacker().Unpack(pak, destination, index, progress: null, CancellationToken.None);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(destination, LowerPath.Replace('/', Path.DirectorySeparatorChar))));
    }
}
