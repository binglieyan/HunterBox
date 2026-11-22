using System.Text;
using HunterBox.Application.Models;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>TOC 加密 pak 的模型改写：解密条目表、改哈希、按同一密钥块加密写回。</summary>
public class PakModelTocEncryptedRewriteTests
{
    [Fact]
    public void RewritePaths_TocEncrypted_RewritesTocInPlace()
    {
        using TempDir temp = TempDir.Create();
        string pak = Path.Combine(temp.Path, "model.pak");
        string[] paths =
        [
            "natives/stm/art/model/character/ch03/012/003/ch03_012_003.mod3",
            "natives/stm/art/model/character/ch03/012/003/ch03_012_003.mdf2.45",
            "natives/stm/art/model/character/ch03/012/003/ch03_012_003.tex.28",
            "natives/stm/art/model/item/it10/03/0000/it10_03_0000.mod3",
        ];
        PakTestEntry[] entries =
        [
            .. paths.Select(p => PakCodecTestTools.Stored(
                PakPathHasher.HashPath(p),
                Encoding.ASCII.GetBytes(p))),
        ];
        PakCodecTestTools.BuildPak(temp.Path, "model.pak", 8, entries);
        string[] targetPaths =
        [
            "natives/stm/art/model/character/ch03/015/004/ch03_015_004.mod3",
            "natives/stm/art/model/character/ch03/015/004/ch03_015_004.mdf2.45",
        ];
        PakNameIndex index = PakPathHasher.BuildIndex([.. paths, .. targetPaths]);

        PakModelRewriteService service = new(
            new RePakReader(),
            new RePakRenamer(),
            new PakPathHashProvider(),
            new PakRepacker());

        ModelRewriteReport report = service.RewritePaths(pak, "ch03/012/003", "ch03/015/004", index, backup: true);

        Assert.Equal(4, report.EntriesScanned);
        Assert.Equal(2, report.EntriesChanged); // .tex 跳过；it10 不受影响
        Assert.True(File.Exists(pak + ".toc.bak"), "加密 TOC 也只保留条目表前像");

        string unpacked = Path.Combine(temp.Path, "unpacked");
        new PakRepacker().Unpack(pak, unpacked, index, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(unpacked, "natives/stm/art/model/character/ch03/015/004/ch03_015_004.mod3")));
        Assert.True(File.Exists(Path.Combine(unpacked, "natives/stm/art/model/character/ch03/015/004/ch03_015_004.mdf2.45")));
        Assert.False(File.Exists(Path.Combine(unpacked, "natives/stm/art/model/character/ch03/012/003/ch03_012_003.mod3")));
        Assert.True(File.Exists(Path.Combine(unpacked, "natives/stm/art/model/character/ch03/012/003/ch03_012_003.tex.28")));

        // 仍保持加密 TOC：用包内同一密钥块重新加密，不改变 pak 格式属性。
        byte[] bytes = File.ReadAllBytes(pak);
        (_, _, _, short feature, _, _) = PakCodecTestTools.ReadHeader(bytes);
        Assert.Equal((short)8, feature);
    }

    /// <summary>
    /// TOC 加密 pak 的多 change 在一次 TOC 写入里全部落地（不再逐 change 重打包）。
    /// </summary>
    [Fact]
    public void RewritePaths_TocEncrypted_MultipleChanges_AllApplied()
    {
        using TempDir temp = TempDir.Create();
        string pak = Path.Combine(temp.Path, "multi.pak");
        string[] paths =
        [
            "natives/stm/art/model/character/ch03/012/003/ch03_012_003.mod3",
            "natives/stm/art/model/character/ch03/012/004/ch03_012_004.mod3",
            "natives/stm/art/model/character/ch03/012/003/ch03_012_003.tex.28",
        ];
        PakTestEntry[] entries =
        [
            .. paths.Select(p => PakCodecTestTools.Stored(
                PakPathHasher.HashPath(p),
                Encoding.ASCII.GetBytes(p))),
        ];
        PakCodecTestTools.BuildPak(temp.Path, "multi.pak", 8, entries);
        string[] targetPaths =
        [
            "natives/stm/art/model/character/ch03/015/007/ch03_015_007.mod3",
            "natives/stm/art/model/character/ch03/015/008/ch03_015_008.mod3",
            "natives/stm/art/model/character/ch03/015/007/ch03_015_007.tex.28",
        ];
        PakNameIndex index = PakPathHasher.BuildIndex([.. paths, .. targetPaths]);
        PakModelRewriteService service = new(
            new RePakReader(),
            new RePakRenamer(),
            new PakPathHashProvider(),
            new PakRepacker());

        ModelRewriteReport report = service.RewritePaths(
            pak,
            [("ch03/012/003", "ch03/015/007"), ("ch03/012/004", "ch03/015/008")],
            index,
            backup: true);

        Assert.Equal(3, report.EntriesScanned);
        Assert.Equal(2, report.EntriesChanged);
        Assert.True(File.Exists(pak + ".toc.bak"));
        Assert.Equal((short)8, PakCodecTestTools.ReadHeader(File.ReadAllBytes(pak)).Feature);

        string unpacked = Path.Combine(temp.Path, "unpacked");
        new PakRepacker().Unpack(pak, unpacked, index, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(unpacked, "natives/stm/art/model/character/ch03/015/007/ch03_015_007.mod3")));
        Assert.True(File.Exists(Path.Combine(unpacked, "natives/stm/art/model/character/ch03/015/008/ch03_015_008.mod3")));
        // 贴图不随身份改名（规则同明文路径），仍留在原路径。
        Assert.True(File.Exists(Path.Combine(unpacked, "natives/stm/art/model/character/ch03/012/003/ch03_012_003.tex.28")));
    }
}
