using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

public class ModFileRescannerTests
{
    private static readonly HunterBox.Application.Common.IFileSystem FileSystem =
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

    [Fact]
    public void Rescan_UpdatesRelativeFilesAfterManualAddAndDelete()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = new()
        {
            Id = 1,
            GameId = "mhws",
            Index = 1,
            RelativeFiles = ["natives/old.dat"],
        };

        string filesDir = paths.FilesDirectory(1);
        Directory.CreateDirectory(Path.Combine(filesDir, "natives", "stm"));
        File.WriteAllText(Path.Combine(filesDir, "natives", "stm", "new.dat"), "x");
        File.WriteAllText(Path.Combine(filesDir, "screenshot.png"), "ignored");

        bool changed = HunterBox.Application.Mods.ModFileRescanner.Rescan(paths, [mod], FileSystem);

        Assert.True(changed);
        Assert.Equal(["natives/stm/new.dat"], mod.RelativeFiles);
    }

    [Fact]
    public void Rescan_LeavesRecordWhenFilesDirMissing()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = new()
        {
            Id = 2,
            GameId = "mhws",
            Index = 1,
            RelativeFiles = ["natives/a.dat"],
        };

        bool changed = HunterBox.Application.Mods.ModFileRescanner.Rescan(paths, [mod], FileSystem);

        Assert.False(changed);
        Assert.Equal(["natives/a.dat"], mod.RelativeFiles);
    }

    [Fact]
    public void Rescan_NoChangeWhenAlreadyInSync()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = new()
        {
            Id = 3,
            GameId = "mhws",
            Index = 1,
            RelativeFiles = ["natives/a.dat"],
        };

        string file = Path.Combine(paths.FilesDirectory(3), "natives", "a.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        Assert.False(HunterBox.Application.Mods.ModFileRescanner.Rescan(paths, [mod], FileSystem));
        Assert.Equal(["natives/a.dat"], mod.RelativeFiles);
    }

    [Fact]
    public void Rescan_RemovesEmptyDirectoriesButKeepsFiles()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = new()
        {
            Id = 3,
            GameId = "mhw",
            Index = 1,
            RelativeFiles = ["nativepc/keep.dat"],
        };

        string filesDir = paths.FilesDirectory(3);
        Directory.CreateDirectory(Path.Combine(filesDir, "nativepc"));
        File.WriteAllText(Path.Combine(filesDir, "nativepc", "keep.dat"), "x");
        string empty = Path.Combine(filesDir, "nativepc", "pl", "f_equip", "pl029_0101", "arm", "mod");
        Directory.CreateDirectory(empty);

        _ = HunterBox.Application.Mods.ModFileRescanner.Rescan(paths, [mod], FileSystem);

        Assert.True(File.Exists(Path.Combine(filesDir, "nativepc", "keep.dat")));
        Assert.False(Directory.Exists(Path.Combine(filesDir, "nativepc", "pl")));
        Assert.True(Directory.Exists(filesDir));
    }
}
