using HunterBox.Application.Mods;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

public class LegacyPluginPathRepairTests
{
    private static readonly HunterBox.Application.Common.IFileSystem FileSystem =
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

    [Fact]
    public void Repair_WorldNativePcPlugins_DropsReframeworkPrefix()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = WorldMod(["nativepc/plugins/mhw-dlss4.dll", "nativepc/plugins/mhw-dlss4.ini"]);

        LegacyPluginPathRepairResult result = LegacyPluginPathRepair.Repair([mod], paths, _ => null, FileSystem);

        Assert.True(result.Changed);
        Assert.Null(mod.TargetDirectory);
        Assert.Equal(0, result.FilesMoved);
    }

    [Fact]
    public void Repair_WorldBarePlugin_GoesToNativePcPlugins()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = WorldMod(["mhw-dlss4.dll"]);

        LegacyPluginPathRepairResult result = LegacyPluginPathRepair.Repair([mod], paths, _ => null, FileSystem);

        Assert.True(result.Changed);
        Assert.Null(mod.TargetDirectory);
    }

    [Fact]
    public void Repair_EnabledMod_MovesDeployedFilesAndRemovesEmptyReframework()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        string gameRoot = Path.Combine(temp.Path, "game");
        string oldFile = Path.Combine(
            gameRoot,
            "reframework", "plugins", "nativepc", "plugins", "mhw-dlss4.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
        File.WriteAllText(oldFile, "plugin");

        ModRecord mod = WorldMod(["nativepc/plugins/mhw-dlss4.dll"]);
        mod.Enabled = true;

        LegacyPluginPathRepairResult result = LegacyPluginPathRepair.Repair([mod], paths, _ => gameRoot, FileSystem);

        Assert.True(result.Changed);
        Assert.Equal(1, result.FilesMoved);
        Assert.Null(mod.TargetDirectory);
        Assert.True(File.Exists(Path.Combine(gameRoot, "nativePC", "plugins", "mhw-dlss4.dll")));
        Assert.False(File.Exists(oldFile));
        Assert.False(Directory.Exists(Path.Combine(gameRoot, "reframework")));
    }

    [Fact]
    public void Repair_NonWorldReframeworkTarget_AlsoUsesPackagePath()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = new()
        {
            Id = 8,
            GameId = "mhws",
            Enabled = false,
            TargetDirectory = "reframework/plugins",
            RelativeFiles = ["my-plugin.dll"],
        };

        LegacyPluginPathRepairResult result = LegacyPluginPathRepair.Repair([mod], paths, _ => null, FileSystem);

        Assert.True(result.Changed);
        Assert.Null(mod.TargetDirectory);
    }

    private static ModRecord WorldMod(List<string> files) => new()
    {
        Id = 7,
        GameId = "mhw",
        Enabled = false,
        TargetDirectory = "reframework/plugins",
        RelativeFiles = files,
    };
}
