using System.Text;
using HunterBox.Application.Models;
using HunterBox.Domain.Models;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>TOC 加密 pak 改写后，目标路径必须能按游戏路径清单重新识别。</summary>
public class EncryptedPakModelRewriteIntegrationTests
{
    [Fact]
    public void RewritePaths_TocEncrypted_ToExistingTarget_IsResolvable()
    {
        using TempDir temp = TempDir.Create();
        string pak = Path.Combine(temp.Path, "model.pak");
        string[] sourcePaths =
        [
            "natives/STM/Art/Model/Character/ch03/000/000/1/ch03_000_0001.mdf2.45",
            "natives/STM/Art/Model/Character/ch03/000/000/1/ch03_000_0001.mesh.241111606",
        ];
        string[] targetPaths =
        [
            "natives/STM/Art/Model/Character/ch03/002/000/1/ch03_002_0001.mdf2.45",
            "natives/STM/Art/Model/Character/ch03/002/000/1/ch03_002_0001.mesh.241111606",
        ];
        PakTestEntry[] entries =
        [
            .. sourcePaths.Select(p => PakCodecTestTools.Stored(PakPathHasher.HashPath(p), Encoding.ASCII.GetBytes(p))),
        ];
        PakCodecTestTools.BuildPak(temp.Path, "model.pak", 8, entries);
        PakNameIndex index = PakPathHasher.BuildIndex([.. sourcePaths, .. targetPaths]);

        PakModelRewriteService service = new(
            new RePakReader(),
            new RePakRenamer(),
            new PakPathHashProvider(),
            new PakRepacker());
        ModelRewriteReport report = service.RewritePaths(pak, "ch03/000/000", "ch03/002/000", index);
        Assert.Equal(2, report.EntriesChanged);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("不在游戏路径清单"));

        string unpacked = Path.Combine(temp.Path, "unpacked");
        new PakRepacker().Unpack(pak, unpacked, index, null, CancellationToken.None);
        foreach (string target in targetPaths)
        {
            string file = Path.Combine(unpacked, target.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), target);
            Assert.True(ModelPathRules.TryParsePath(target, out ModelIdentity identity));
            Assert.Equal(ModelIdentity.Armor(3, 2, 0), identity);
        }
    }
}
