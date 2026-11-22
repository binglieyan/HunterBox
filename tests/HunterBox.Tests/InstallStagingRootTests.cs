using HunterBox.Application.Install;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>「设为根」与「安装前缀」的行为（对应安装对话框的对应控件）。</summary>
public class InstallStagingRootTests
{
    private static (BoxPaths Paths, ModInstaller Installer) Create(TempDir temp)
    {
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        return (paths, new ModInstaller(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws"));
    }

    [Fact]
    public void Commit_WithRoot_StripsRootFromInstalledPaths()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModInstaller installer) = Create(temp);

        using InstallStaging staging = installer.Stage(
            ZipBuilder.Create(temp.Path, "wrapped.zip", ("CoolMod/natives/a.tex", "a"), ("readme.txt", "r")));

        InstallResult result = staging.Commit(null, null, "coolmod");

        Assert.Equal(["natives/a.tex"], result.Record.RelativeFiles);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "a.tex")));
    }

    [Fact]
    public void Commit_WithPrefix_PutsFilesUnderPrefix()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModInstaller installer) = Create(temp);

        using InstallStaging staging = installer.Stage(
            ZipBuilder.Create(temp.Path, "mod.zip", ("natives/a.tex", "a")));

        InstallResult result = staging.Commit(null, null, null, "reframework/autorun");

        Assert.Equal(["reframework/autorun/natives/a.tex"], result.Record.RelativeFiles);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "reframework", "autorun", "natives", "a.tex")));
    }

    [Fact]
    public void Commit_RootWithoutMatchingFiles_Throws()
    {
        using TempDir temp = TempDir.Create();
        (_, ModInstaller installer) = Create(temp);

        using InstallStaging staging = installer.Stage(
            ZipBuilder.Create(temp.Path, "mod.zip", ("natives/a.tex", "a")));

        Assert.Throws<InvalidOperationException>(() => staging.Commit(null, null, "no-such-root"));
    }

    [Fact]
    public void Commit_WithMultipleRoots_StripsEachRootAndSkipsOutsideFiles()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModInstaller installer) = Create(temp);

        using InstallStaging staging = installer.Stage(
            ZipBuilder.Create(
                temp.Path,
                "multiroot.zip",
                ("pack-a/natives/a.tex", "a"),
                ("pack-b/natives/b.tex", "b"),
                ("outside/readme.txt", "r")));

        InstallResult result = staging.Commit(
            null,
            null,
            rootDirectories: ["pack-a", "pack-b"]);

        Assert.Equal(["natives/a.tex", "natives/b.tex"], result.Record.RelativeFiles.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "a.tex")));
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "b.tex")));
    }
}
