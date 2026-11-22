using HunterBox.Application.Install;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>
/// 安装源归档 + 安装选项 + 重装走向导。
/// </summary>
public class InstallSourceTests
{
    private const string GameId = "mhws";

    private static readonly HunterBox.Application.Common.IFileSystem Fs =
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

    private static (BoxPaths Paths, ModRepository Store, ModInstaller Installer) CreateBox(TempDir temp)
    {
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        return (paths, store, new ModInstaller(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, GameId));
    }

    private static string CreatePackage(TempDir temp, string fileName, string name, string file = "natives/a.tex", string content = "a")
    {
        string zip = ZipBuilder.Create(
            temp.Path,
            fileName,
            (file, content),
            ("modinfo.ini", "name=" + name));
        return zip;
    }

    [Theory]
    [InlineData(null, InstallSourceMode.None)]
    [InlineData("", InstallSourceMode.None)]
    [InlineData("0", InstallSourceMode.None)]
    [InlineData("1", InstallSourceMode.Copy)]
    [InlineData("2", InstallSourceMode.Move)]
    [InlineData("9", InstallSourceMode.None)]
    [InlineData("x", InstallSourceMode.None)]
    public void ParseInstallOption_ThreeStates(string? raw, InstallSourceMode expected) =>
        Assert.Equal(expected, InstallSourceSettings.Parse(raw));

    [Fact]
    public void NoneMode_RecordsOriginalSourceWithoutArchiving()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = CreateBox(temp);
        string zip = CreatePackage(temp, "mod.zip", "无操作包");

        InstallResult result;
        using (InstallStaging staging1 = installer.Stage(zip))
        {
            result = staging1.Commit(archiveMode: InstallSourceMode.None);
        }

        Assert.False(Directory.Exists(paths.DownloadDirectory));
        Assert.Equal(zip, result.Record.InstallSource);
        Assert.Single(store.Mods);
    }

    [Fact]
    public void CopyMode_ArchivesToDownloadAndKeepsOriginal()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = CreateBox(temp);
        string zip = CreatePackage(temp, "mod.zip", "复制模式包");

        InstallResult result;
        using (InstallStaging staging2 = installer.Stage(zip))
        {
            result = staging2.Commit(archiveMode: InstallSourceMode.Copy);
        }

        string expected = Path.Combine(paths.DownloadDirectory, "复制模式包.zip");
        Assert.True(File.Exists(expected), "原始包应归档到 Download/<显示名>.zip");
        Assert.True(File.Exists(zip), "复制模式不删除原包");
        Assert.Equal(expected, result.Record.InstallSource);
        Assert.Single(store.Mods);
    }

    [Fact]
    public void MoveMode_ArchivesToDownloadAndDeletesOriginal()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, _, ModInstaller installer) = CreateBox(temp);
        string zip = CreatePackage(temp, "mod.zip", "移动模式包");

        InstallResult result;
        using (InstallStaging staging3 = installer.Stage(zip))
        {
            result = staging3.Commit(archiveMode: InstallSourceMode.Move);
        }

        string expected = Path.Combine(paths.DownloadDirectory, "移动模式包.zip");
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(zip), "移动模式应删除原包");
        Assert.Equal(expected, result.Record.InstallSource);
    }

    [Fact]
    public void DuplicateArchiveName_GetsNumericSuffix()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, _, ModInstaller installer) = CreateBox(temp);
        string first = CreatePackage(temp, "v1.zip", "同名包", content: "v1");
        string second = CreatePackage(temp, "v2.zip", "同名包", file: "natives/b.tex", content: "v2");

        InstallResult r1;
        using (InstallStaging staging4 = installer.Stage(first))
        {
            r1 = staging4.Commit(archiveMode: InstallSourceMode.Copy);
        }
        InstallResult r2;
        using (InstallStaging staging5 = installer.Stage(second))
        {
            r2 = staging5.Commit(archiveMode: InstallSourceMode.Copy);
        }

        Assert.True(File.Exists(r1.Record.InstallSource));
        Assert.True(File.Exists(r2.Record.InstallSource));
        Assert.NotEqual(r1.Record.InstallSource, r2.Record.InstallSource);
        Assert.Contains("(2)", Path.GetFileName(r2.Record.InstallSource!));
        Assert.Equal(2, Directory.GetFiles(paths.DownloadDirectory, "*.zip").Length);
    }

    [Fact]
    public void InstallSource_SurvivesRepositoryRoundTrip()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, _, ModInstaller installer) = CreateBox(temp);
        string zip = CreatePackage(temp, "mod.zip", "往返包");

        InstallResult result;
        using (InstallStaging staging6 = installer.Stage(zip))
        {
            result = staging6.Commit(archiveMode: InstallSourceMode.Copy);
        }
        ModRepository reloaded = ModRepository.Load(paths);

        ModRecord record = Assert.Single(reloaded.Mods);
        Assert.Equal(result.Record.InstallSource, record.InstallSource);
        Assert.Equal(GameId, record.GameId);
    }

    [Fact]
    public void StageReinstall_PreservesIdIndexGameIdAndReplacesFiles()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = CreateBox(temp);
        InstallResult first;
        using (InstallStaging staging7 = installer.Stage(CreatePackage(temp, "v1.zip", "重装包", content: "v1")))
        {
            first = staging7.Commit(archiveMode: InstallSourceMode.Copy);
        }
        first.Record.Index = 7;
        first.Record.InstalledAt = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset oldInstalledAt = first.Record.InstalledAt;
        store.Save();

        string v2 = CreatePackage(temp, "v2.zip", "重装包", file: "natives/b.tex", content: "v2");
        using (InstallStaging staging = installer.StageReinstall(first.Record.Id, v2))
        {
            Assert.True(staging.IsReinstall);

            InstallResult second = staging.Commit(archiveMode: InstallSourceMode.Copy);

            Assert.Equal(first.Record.Id, second.Record.Id);
            Assert.Equal(7, second.Record.Index);
            Assert.Equal(GameId, second.Record.GameId);
            Assert.Contains("natives/b.tex", second.Record.RelativeFiles);
            Assert.DoesNotContain("natives/a.tex", second.Record.RelativeFiles);
            Assert.True(second.Record.InstalledAt > oldInstalledAt);
            Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(first.Record.Id), "natives", "b.tex")));
            Assert.False(File.Exists(Path.Combine(paths.FilesDirectory(first.Record.Id), "natives", "a.tex")));
            Assert.True(File.Exists(second.Record.InstallSource));
        }

        Assert.Single(store.Mods);
    }

    [Fact]
    public void StageReinstall_EnabledMod_ThrowsBeforeTouchingFiles()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, _, ModInstaller installer) = CreateBox(temp);
        InstallResult installed;
        using (InstallStaging staging8 = installer.Stage(CreatePackage(temp, "v1.zip", "启用中")))
        {
            installed = staging8.Commit();
        }
        installed.Record.Enabled = true;

        string v2 = CreatePackage(temp, "v2.zip", "启用中", file: "natives/b.tex");
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => installer.StageReinstall(installed.Record.Id, v2));
        Assert.Contains("禁用", ex.Message);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(installed.Record.Id), "natives", "a.tex")));
    }

    [Fact]
    public void StageReinstall_UnknownOrOtherGame_Throws()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = CreateBox(temp);
        Assert.Throws<InvalidOperationException>(() => installer.StageReinstall(99, "x.zip"));

        InstallResult installed;
        using (InstallStaging staging9 = installer.Stage(CreatePackage(temp, "v1.zip", "跨游戏")))
        {
            installed = staging9.Commit();
        }
        ModInstaller other = new(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhw");
        Assert.Throws<InvalidOperationException>(() =>
            other.StageReinstall(installed.Record.Id, CreatePackage(temp, "v2.zip", "跨游戏")));
    }

    [Fact]
    public void ArchiveFailure_LeavesNoHalfArchiveAndKeepsOriginalSource()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, _, ModInstaller installer) = CreateBox(temp);
        // 用一个同名文件占位 Download 目录，让归档必然失败。
        File.WriteAllText(paths.DownloadDirectory, "占位文件");

        string zip = CreatePackage(temp, "mod.zip", "归档失败包");
        InstallResult result;
        using (InstallStaging staging10 = installer.Stage(zip))
        {
            result = staging10.Commit(archiveMode: InstallSourceMode.Copy);
        }

        Assert.Equal(zip, result.Record.InstallSource);
        Assert.Contains(result.Warnings, w => w.Contains("归档失败"));
        Assert.Equal("占位文件", File.ReadAllText(paths.DownloadDirectory));
    }

    [Fact]
    public void InstallSourceLocator_PrefersExistingSource()
    {
        using TempDir temp = TempDir.Create();
        string archived = temp.File("Download/包.zip", "x");
        ModRecord record = new() { Id = 1, GameId = GameId, InstallSource = archived };

        Assert.Equal(archived, InstallSourceLocator.FindExisting(record, Fs));
        Assert.Equal(archived, InstallSourceLocator.GetRecorded(record));

        record.InstallSource = Path.Combine(temp.Path, "no-such.zip");
        Assert.Null(InstallSourceLocator.FindExisting(record, Fs));
        Assert.NotNull(InstallSourceLocator.GetRecorded(record));
    }

    [Fact]
    public void Archiver_SanitizesDisplayName()
    {
        Assert.Equal("MOD", InstallSourceArchiver.SanitizeFileName(null));
        Assert.Equal("a_b_c", InstallSourceArchiver.SanitizeFileName("a/b:c"));
        Assert.DoesNotContain('?', InstallSourceArchiver.SanitizeFileName("a?b"));
    }

    [Fact]
    public void Archiver_UsesUniquePathOnConflict()
    {
        using TempDir temp = TempDir.Create();
        string first = temp.File("Download/x.zip", "1");

        string second = InstallSourceArchiver.BuildUniquePath(
            Path.Combine(temp.Path, "Download"), "x.zip", Fs);

        Assert.NotEqual(first, second);
        Assert.EndsWith("x (2).zip", second);
    }

    [Fact]
    public void StageReinstall_Commit_RecordsNewInstallSource()
    {
        using TempDir temp = TempDir.Create();
        (_, _, ModInstaller installer) = CreateBox(temp);
        InstallResult first;
        using (InstallStaging staging11 = installer.Stage(CreatePackage(temp, "v1.zip", "直连重装")))
        {
            first = staging11.Commit(archiveMode: InstallSourceMode.Copy);
        }
        string v2 = CreatePackage(temp, "v2.zip", "直连重装", file: "natives/b.tex");

        InstallResult second;
        using (InstallStaging staging12 = installer.StageReinstall(first.Record.Id, v2))
        {
            second = staging12.Commit(archiveMode: InstallSourceMode.Copy);
        }

        Assert.Equal(first.Record.Id, second.Record.Id);
        Assert.True(File.Exists(second.Record.InstallSource));
        Assert.Contains("(2)", Path.GetFileName(second.Record.InstallSource!));
        Assert.Equal("直连重装", second.Record.Metadata.Name);
    }
}
