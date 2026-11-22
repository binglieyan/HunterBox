using HunterBox.Application.Models;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

public class WorldLooseModelRewriteTests
{
    private static readonly HunterBox.Application.Common.IFileSystem Fs =
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

    [Fact]
    public void Rewrite_WorldArmor_MovesModelFilesCleansOldDirsAndKeepsTexture()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        string filesDir = paths.FilesDirectory(7);
        string[] sourceFiles =
        [
            "nativepc/pl/m_equip/pl001_0000/arm/mod/m_arm001_0000.mod3",
            "nativepc/pl/m_equip/pl001_0000/body/mod/m_body001_0000.mod3",
            "nativepc/pl/m_equip/pl001_0000/body/mod/m_body001_0000_ALBD.tex.1",
            "nativepc/wp/bow/bow001/mod/bow001.mdf2",
        ];
        foreach (string relative in sourceFiles)
        {
            string full = Path.Combine(filesDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, relative);
        }

        ModRecord mod = new()
        {
            Id = 7,
            GameId = "mhw",
            RelativeFiles = [.. sourceFiles],
        };

        LooseModelRewriteReport report = new LooseModelRewriteService(paths, Fs)
            .Rewrite(mod, "world:pl/m/001_0000", "world:pl/m/002_0010");

        Assert.Equal(2, report.FilesMoved);
        string targetArm = Path.Combine(filesDir, "nativepc/pl/m_equip/pl002_0010/arm/mod/m_arm002_0010.mod3".Replace('/', Path.DirectorySeparatorChar));
        string targetBody = Path.Combine(filesDir, "nativepc/pl/m_equip/pl002_0010/body/mod/m_body002_0010.mod3".Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(targetArm));
        Assert.True(File.Exists(targetBody));
        Assert.False(File.Exists(Path.Combine(filesDir, "nativepc/pl/m_equip/pl001_0000/arm/mod/m_arm001_0000.mod3".Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(filesDir, "nativepc/pl/m_equip/pl001_0000/body/mod/m_body001_0000.mod3".Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(Path.Combine(filesDir, "nativepc/pl/m_equip/pl001_0000/body/mod/m_body001_0000_ALBD.tex.1".Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(Path.Combine(filesDir, "nativepc/wp/bow/bow001/mod/bow001.mdf2".Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(Directory.Exists(Path.Combine(filesDir, "nativepc/pl/m_equip/pl001_0000/arm".Replace('/', Path.DirectorySeparatorChar))));

        // 相对文件清单必须只包含新路径，不能旧/新两份并存。
        Assert.Contains(mod.RelativeFiles, p => p.Contains("pl002_0010", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mod.RelativeFiles, p => p.Contains("pl001_0000/arm/mod/m_arm001_0000", StringComparison.OrdinalIgnoreCase));
    }
}
