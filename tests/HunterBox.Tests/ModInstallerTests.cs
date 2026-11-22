using HunterBox.Application.Install;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;
using HunterBox.Application.Mount;

namespace HunterBox.Tests;

public class ModInstallerTests
{
    private const string ModFile = "natives/stm/art/model/character/ch03/x.mod3";
    private const string GameId = "mhws";

    private static (BoxPaths Paths, ModRepository Repository, ModInstaller Installer) CreateBox(TempDir temp)
    {
        (BoxPaths paths, ModRepository repository) = TestBox.OpenRepository(temp);
        return (paths, repository, new ModInstaller(paths, repository, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, GameId));
    }

    [Fact]
    public void Install_ZipWithModInfo_CreatesRecordAndFiles()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repository, ModInstaller installer) = CreateBox(temp);
        string zip = ZipBuilder.Create(
            temp.Path,
            "mod.zip",
            (ModFile, "model-bytes"),
            ("modinfo.ini", "name=太刀模型\nversion=1.0\nauthor=某人"));

        InstallResult result;
        using (InstallStaging staging1 = installer.Stage(zip))
        {
            result = staging1.Commit();
        }

        Assert.Equal(1, result.Record.Id);
        Assert.Equal(GameId, result.Record.GameId);
        Assert.Equal(ModKind.LooseFiles, result.Record.Kind);
        Assert.Equal("太刀模型", result.Record.Metadata.Name);
        Assert.Equal("1.0", result.Record.Metadata.Version);
        Assert.Contains(ModFile, result.Record.RelativeFiles);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1),
            ModFile.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Single(repository.Mods);
    }

    [Fact]
    public void Install_WithoutModInfo_FallsBackToArchiveName()
    {
        using TempDir temp = TempDir.Create();
        (_, _, ModInstaller installer) = CreateBox(temp);
        string zip = ZipBuilder.Create(temp.Path, "Cool Mod.zip", (ModFile, "x"));

        InstallResult result;
        using (InstallStaging staging2 = installer.Stage(zip))
        {
            result = staging2.Commit();
        }

        Assert.Equal("Cool Mod", result.Record.Metadata.Name);
    }

    [Fact]
    public void Install_UnwrapsSingleTopDirectory()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, _, ModInstaller installer) = CreateBox(temp);
        string zip = ZipBuilder.Create(
            temp.Path,
            "wrapped.zip",
            ("CoolMod/" + ModFile, "x"),
            ("CoolMod/modinfo.ini", "name=包了一层"));

        InstallResult result;
        using (InstallStaging staging3 = installer.Stage(zip))
        {
            result = staging3.Commit();
        }

        Assert.Equal("包了一层", result.Record.Metadata.Name);
        Assert.Contains(ModFile, result.Record.RelativeFiles);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1),
            ModFile.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void Install_UnknownPackage_Throws()
    {
        using TempDir temp = TempDir.Create();
        (_, ModRepository repository, ModInstaller installer) = CreateBox(temp);
        string zip = ZipBuilder.Create(temp.Path, "junk.zip", ("readme.txt", "hi"));

        Assert.Throws<InvalidDataException>(() =>
        {
            using InstallStaging staging4 = installer.Stage(zip);
            staging4.Commit();
        });
        Assert.Empty(repository.Mods);
    }

    [Fact]
    public void Install_LuaMod_StaysInPackagePath()
    {
        using TempDir temp = TempDir.Create();
        (_, _, ModInstaller installer) = CreateBox(temp);

        InstallResult result;
        using (InstallStaging staging6 = installer.Stage(
            ZipBuilder.Create(temp.Path, "script.zip", ("tweak.lua", "print(1)"))))
        {
            result = staging6.Commit();
        }

        Assert.Equal(ModKind.RefScripts, result.Record.Kind);
        Assert.Null(result.Record.TargetDirectory);
    }

    [Fact]
    public void Uninstall_RemovesFilesAndRecord()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repository, ModInstaller installer) = CreateBox(temp);
        InstallResult result;
        using (InstallStaging staging7 = installer.Stage(
            ZipBuilder.Create(temp.Path, "mod.zip", (ModFile, "x"))))
        {
            result = staging7.Commit();
        }

        bool removed = installer.Uninstall(result.Record.Id);

        Assert.True(removed);
        Assert.False(Directory.Exists(paths.ModDirectory(result.Record.Id)));
        Assert.Empty(repository.Mods);
    }

    /// <summary>
    /// N-02 回归：索引保存失败时，墓碑目录必须改名回原位、记录加回内存，
    /// 不能出现「记录还在、Mods/&lt;id&gt; 已经没了」。
    /// </summary>
    [Fact]
    public void Uninstall_SaveFailure_RestoresDirectoryAndRecord()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        FakeModStore store = new();
        ModRecord record = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [ModFile],
        };
        string source = Path.Combine(paths.FilesDirectory(1), ModFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "model-bytes");
        store.Add(record);
        ModInstaller installer = new(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, GameId);
        store.FailSave = true;

        Assert.Throws<IOException>(() => installer.Uninstall(record.Id));

        Assert.NotNull(store.Find(record.Id));
        Assert.True(File.Exists(source), "保存失败后 MOD 目录与文件必须恢复");
        Assert.Empty(Directory.GetDirectories(paths.ModsDirectory, "1.old-*"));
    }

    /// <summary>Save 失败且原位被外部重建导致墓碑无法恢复时，异常必须给出墓碑路径。</summary>
    [Fact]
    public void Uninstall_SaveFailureAndDirectoryRestoreFails_ReportsTombstonePath()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        FakeModStore store = new();
        ModRecord record = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [ModFile],
        };
        string source = Path.Combine(paths.FilesDirectory(1), ModFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "model-bytes");
        store.Add(record);
        ModInstaller installer = new(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, GameId);
        store.FailSave = true;
        store.OnSave = () =>
        {
            // Save 期间外部重建原位目录，让墓碑无法改名回去。
            Directory.CreateDirectory(paths.ModDirectory(1));
        };

        IOException failure = Assert.Throws<IOException>(() => installer.Uninstall(record.Id));

        Assert.Contains(".old-", failure.Message);
        Assert.Contains(paths.ModDirectory(1), failure.Message);
        Assert.NotNull(store.Find(record.Id));
    }

    /// <summary>上次失败留下的 .old-* 墓碑在下次卸载时应先恢复再继续。</summary>
    [Fact]
    public void Uninstall_StaleTombstone_IsRecoveredAndRemoved()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        FakeModStore store = new();
        ModRecord record = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [ModFile],
        };
        store.Add(record);
        string tombstone = paths.ModDirectory(1) + ".old-stale";
        Directory.CreateDirectory(Path.Combine(tombstone, "files"));
        File.WriteAllText(Path.Combine(tombstone, "files", "leftover.bin"), "x");
        ModInstaller installer = new(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, GameId);

        Assert.True(installer.Uninstall(record.Id));

        Assert.Empty(store.Mods);
        Assert.False(Directory.Exists(paths.ModDirectory(1)));
        Assert.Empty(Directory.GetDirectories(paths.ModsDirectory, "1.old-*"));
    }

    [Fact]
    public void Uninstall_EnabledMod_Throws()
    {
        using TempDir temp = TempDir.Create();
        (_, _, ModInstaller installer) = CreateBox(temp);
        InstallResult result;
        using (InstallStaging staging8 = installer.Stage(
            ZipBuilder.Create(temp.Path, "mod.zip", (ModFile, "x"))))
        {
            result = staging8.Commit();
        }
        result.Record.Enabled = true;

        Assert.Throws<InvalidOperationException>(() => installer.Uninstall(result.Record.Id));
    }

    [Fact]
    public void Reinstall_ReplacesFilesButKeepsId()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repository, ModInstaller installer) = CreateBox(temp);
        InstallResult first;
        using (InstallStaging staging9 = installer.Stage(
            ZipBuilder.Create(temp.Path, "v1.zip", ("natives/stm/old.tex", "v1"), ("modinfo.ini", "name=旧"))))
        {
            first = staging9.Commit();
        }

        string newZip = ZipBuilder.Create(temp.Path, "v2.zip", ("natives/stm/new.tex", "v2"));
        // 重装走安装向导：对话框把（用户确认后的）元数据显式传给 Commit；这里模拟保留旧名称。
        InstallResult second;
        using (InstallStaging staging5 = installer.StageReinstall(first.Record.Id, newZip))
        {
            second = staging5.Commit(metadata: first.Record.Metadata);
        }

        Assert.Equal(first.Record.Id, second.Record.Id);
        Assert.Equal("旧", second.Record.Metadata.Name);
        Assert.Contains("natives/stm/new.tex", second.Record.RelativeFiles);
        Assert.DoesNotContain("natives/stm/old.tex", second.Record.RelativeFiles);
        Assert.False(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "stm", "old.tex")));
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "stm", "new.tex")));
        Assert.Single(repository.Mods);
    }

    [Fact]
    public void Reinstall_EnabledMod_Throws()
    {
        using TempDir temp = TempDir.Create();
        (_, _, ModInstaller installer) = CreateBox(temp);
        InstallResult result;
        using (InstallStaging staging10 = installer.Stage(
            ZipBuilder.Create(temp.Path, "v1.zip", (ModFile, "v1"))))
        {
            result = staging10.Commit();
        }
        result.Record.Enabled = true;

        Assert.Throws<InvalidOperationException>(() =>
        {
            using InstallStaging staging11 = installer.StageReinstall(
                result.Record.Id, ZipBuilder.Create(temp.Path, "v2.zip", (ModFile, "v2")));
            staging11.Commit();
        });
    }

    [Fact]
    public void Reinstall_UnknownId_Throws()
    {
        using TempDir temp = TempDir.Create();
        (_, _, ModInstaller installer) = CreateBox(temp);

        Assert.Throws<InvalidOperationException>(() =>
        {
            using InstallStaging staging12 = installer.StageReinstall(
                99, ZipBuilder.Create(temp.Path, "v1.zip", (ModFile, "v1")));
            staging12.Commit();
        });
    }

    [Fact]
    public void EndToEnd_InstallEnableDisable_LeavesGameFolderClean()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repository, ModInstaller installer) = CreateBox(temp);
        string gameRoot = temp.Dir("game");

        InstallResult result;
        using (InstallStaging staging13 = installer.Stage(
            ZipBuilder.Create(temp.Path, "mod.zip", (ModFile, "model-bytes"))))
        {
            result = staging13.Commit();
        }

        MountService mount = new(
            paths, gameRoot, GameId, repository.Mods,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());
        MountReport enabled = mount.Enable(result.Record);

        string deployed = Path.Combine(gameRoot, ModFile.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(1, enabled.Copied);
        Assert.Equal("model-bytes", File.ReadAllText(deployed));

        result.Record.Enabled = true;
        MountReport disabled = mount.Disable(result.Record);

        Assert.Equal(1, disabled.Deleted);
        Assert.False(File.Exists(deployed));
        Assert.True(Directory.Exists(gameRoot), "游戏根目录本身要保留");
    }
}
