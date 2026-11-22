using HunterBox.Application.Install;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>
/// 安装/重装/卸载的原子性与数据一致性：
/// 失败不留半成品、Id 可复用、重装失败保留旧文件、卸载严格删除。
/// </summary>
public class InstallAtomicityTests
{
    private const string GameId = "mhws";
    private const string ModFile = "natives/stm/art/model/character/ch03/x.mod3";

    private static (BoxPaths Paths, ModRepository Store, ModInstaller Installer) CreateRealBox(TempDir temp)
    {
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        return (paths, store, new ModInstaller(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, GameId));
    }

    private static (BoxPaths Paths, FakeModStore Store, ModInstaller Installer) CreateFlakyBox(TempDir temp)
    {
        BoxPaths paths = new(temp.Path);
        FakeModStore store = new();
        return (paths, store, new ModInstaller(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, GameId));
    }

    [Fact]
    public void Commit_StagingCopyFailure_LeavesNoHalfProductAndReusesId()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = CreateRealBox(temp);
        string zip = ZipBuilder.Create(temp.Path, "mod.zip", ("natives/a.tex", "a"));

        using InstallStaging staging = installer.Stage(zip);
        string extracted = Directory.EnumerateFiles(staging.WorkDirectory, "a.tex", SearchOption.AllDirectories).Single();

        using (FileStream hold = new(extracted, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => staging.Commit());
        }

        Assert.Empty(store.Mods);
        Assert.False(Directory.Exists(paths.ModDirectory(1)), "安装失败不能留下 Mods/<id> 半成品");
        Assert.False(Directory.Exists(paths.FilesDirectory(1)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.StagingDirectory));

        // 失败时没有写记录，NextId 未推进：重试仍复用 Id=1。
        InstallResult result = staging.Commit();

        Assert.Equal(1, result.Record.Id);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "a.tex")));
        Assert.Single(store.Mods);
    }

    [Fact]
    public void Install_SaveFailure_DoesNotLeaveDirectoryOrRecord()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, FakeModStore store, ModInstaller installer) = CreateFlakyBox(temp);
        string zip = ZipBuilder.Create(temp.Path, "mod.zip", (ModFile, "bytes"));
        store.FailSave = true;

        Assert.ThrowsAny<IOException>(() =>
        {
            using InstallStaging staging1 = installer.Stage(zip);
            staging1.Commit();
        });

        Assert.Empty(store.Mods);
        Assert.False(Directory.Exists(paths.ModDirectory(1)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.StagingDirectory));

        store.FailSave = false;
        InstallResult result;
        using (InstallStaging staging2 = installer.Stage(zip))
        {
            result = staging2.Commit();
        }

        // 与真实仓库一致：NextId 是高水位，失败/删除都不会回退复用。
        Assert.Equal(2, result.Record.Id);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(2),
            ModFile.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void Commit_ExcludesModInfoFomodConfigAndPreview()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, _, ModInstaller installer) = CreateRealBox(temp);
        string zip = ZipBuilder.Create(
            temp.Path,
            "mod.zip",
            ("natives/a.tex", "a"),
            ("modinfo.ini", "name=测试包"),
            ("preview.png", "png-bytes"),
            ("fomod/ModuleConfig.xml", "<config />"));

        using InstallStaging staging = installer.Stage(zip);
        InstallResult result = staging.Commit();

        Assert.Equal(["natives/a.tex"], result.Record.RelativeFiles);
        string filesDir = paths.FilesDirectory(result.Record.Id);
        Assert.False(File.Exists(Path.Combine(filesDir, "modinfo.ini")));
        Assert.False(File.Exists(Path.Combine(filesDir, "preview.png")));
        Assert.False(File.Exists(Path.Combine(filesDir, "fomod", "ModuleConfig.xml")));
    }

    [Fact]
    public void Reinstall_FailureKeepsOldFilesAndRecordThenSucceeds()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, FakeModStore store, ModInstaller installer) = CreateFlakyBox(temp);
        InstallResult first;
        using (InstallStaging staging4 = installer.Stage(ZipBuilder.Create(
            temp.Path,
            "v1.zip",
            ("natives/stm/old.tex", "v1"),
            ("modinfo.ini", "name=旧"))))
        {
            first = staging4.Commit();
        }
        int id = first.Record.Id;
        string oldFingerprint = first.Record.PackageFingerprint!;
        Assert.False(string.IsNullOrEmpty(oldFingerprint));

        string v2 = ZipBuilder.Create(
            temp.Path,
            "v2.zip",
            ("natives/stm/new.tex", "v2"),
            ("modinfo.ini", "name=新版"),
            ("preview.png", "png-bytes"));

        store.FailSave = true;
        IOException saveFailure = Assert.ThrowsAny<IOException>(() =>
        {
            using InstallStaging staging5 = installer.StageReinstall(id, v2);
            staging5.Commit();
        });
        // C-18：恢复成功时必须如实说明「已恢复旧文件与旧记录」，不能只报笼统失败。
        Assert.Contains("已恢复旧文件与旧记录", saveFailure.Message);

        Assert.Equal(["natives/stm/old.tex"], first.Record.RelativeFiles);
        Assert.Equal(oldFingerprint, first.Record.PackageFingerprint);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(id), "natives", "stm", "old.tex")),
            "重装失败必须保留旧文件");
        Assert.False(File.Exists(Path.Combine(paths.FilesDirectory(id), "natives", "stm", "new.tex")));
        Assert.Equal(id, Assert.Single(store.Mods).Id);

        store.FailSave = false;
        InstallResult second;
        using (InstallStaging staging3 = installer.StageReinstall(id, v2))
        {
            second = staging3.Commit();
        }

        Assert.Equal(id, second.Record.Id);
        Assert.Equal(["natives/stm/new.tex"], second.Record.RelativeFiles);
        Assert.NotEqual(oldFingerprint, second.Record.PackageFingerprint);
        string filesDir = paths.FilesDirectory(id);
        Assert.True(File.Exists(Path.Combine(filesDir, "natives", "stm", "new.tex")));
        Assert.False(File.Exists(Path.Combine(filesDir, "natives", "stm", "old.tex")));
        // 重装同样排除 modinfo.ini / 预览图。
        Assert.False(File.Exists(Path.Combine(filesDir, "modinfo.ini")));
        Assert.False(File.Exists(Path.Combine(filesDir, "preview.png")));
        Assert.Single(Directory.EnumerateDirectories(paths.ModDirectory(id)));
    }

    [Fact]
    public void Reinstall_NonIoSaveFailure_StillRestoresOldFilesAndRecord()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, FakeModStore store, ModInstaller installer) = CreateFlakyBox(temp);
        InstallResult first;
        using (InstallStaging staging = installer.Stage(ZipBuilder.Create(
            temp.Path,
            "v1.zip",
            ("natives/stm/old.tex", "v1"),
            ("modinfo.ini", "name=旧"))))
        {
            first = staging.Commit();
        }
        int id = first.Record.Id;
        string oldFingerprint = first.Record.PackageFingerprint!;
        string v2 = ZipBuilder.Create(temp.Path, "v2.zip", ("natives/stm/new.tex", "v2"));

        // 非 IO 的保存异常同样必须回滚（旧 API 的 catch-all 语义），不能留下半套重装结果。
        store.SaveFailure = new InvalidOperationException("模拟非 IO 的索引写失败");
        IOException ex = Assert.Throws<IOException>(() =>
        {
            using InstallStaging staging = installer.StageReinstall(id, v2);
            staging.Commit();
        });

        Assert.Contains("已恢复旧文件与旧记录", ex.Message);
        Assert.Equal(["natives/stm/old.tex"], first.Record.RelativeFiles);
        Assert.Equal(oldFingerprint, first.Record.PackageFingerprint);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(id), "natives", "stm", "old.tex")),
            "非 IO 保存失败也必须保留旧文件");
        Assert.False(File.Exists(Path.Combine(paths.FilesDirectory(id), "natives", "stm", "new.tex")));
        Assert.Equal(id, Assert.Single(store.Mods).Id);
    }

    [Fact]
    public void Reinstall_RollbackFailure_ReportsIncompleteInsteadOfClaimingRestored()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, FakeModStore store, ModInstaller installer) = CreateFlakyBox(temp);
        InstallResult first;
        using (InstallStaging seed = installer.Stage(ZipBuilder.Create(
            temp.Path,
            "v1.zip",
            ("natives/stm/old.tex", "v1"),
            ("modinfo.ini", "name=旧"))))
        {
            first = seed.Commit();
        }
        int id = first.Record.Id;
        string oldFingerprint = first.Record.PackageFingerprint!;
        string v2 = ZipBuilder.Create(temp.Path, "v2.zip", ("natives/stm/new.tex", "v2"));

        using InstallStaging staging = installer.StageReinstall(id, v2);
        string extracted = Directory.EnumerateFiles(
            staging.WorkDirectory, "new.tex", SearchOption.AllDirectories).Single();
        try
        {
            // 换入后的新文件只读 → save 失败时回滚既删不掉新目录也挪不回旧目录，恢复必然失败。
            File.SetAttributes(extracted, FileAttributes.ReadOnly);
            store.FailSave = true;

            // C-18：回滚失败必须如实报告「回滚不完整」，不能宣称已恢复旧文件与旧记录。
            IOException ex = Assert.Throws<IOException>(() => staging.Commit());
            Assert.Contains("回滚不完整", ex.Message);
            Assert.DoesNotContain("已恢复旧文件与旧记录", ex.Message);

            // 内存记录已恢复到旧值；旧目录仍保留在 .old-* 下，供用户手工恢复。
            Assert.Equal(["natives/stm/old.tex"], first.Record.RelativeFiles);
            Assert.Equal(oldFingerprint, first.Record.PackageFingerprint);
            string oldDirectory = Assert.Single(
                Directory.EnumerateDirectories(paths.ModDirectory(id), ".old-*"));
            Assert.True(File.Exists(Path.Combine(oldDirectory, "natives", "stm", "old.tex")),
                "回滚失败时旧文件必须保留在 .old-* 下，不能丢失");
        }
        finally
        {
            store.FailSave = false;
            // 清掉只读属性，保证暂存目录与测试临时目录能正常清理。
            foreach (string file in Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void CommitFomod_WritesPackageFingerprint()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = CreateRealBox(temp);
        const string config = """
            <config>
              <moduleName>组件包</moduleName>
              <installSteps>
                <installStep name="主体">
                  <optionalFileGroups>
                    <group name="体型" type="SelectExactlyOne">
                      <plugins>
                        <plugin name="大型"><files><file source="data/large.mod3" destination="natives/large.mod3" /></files></plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;
        string zip = ZipBuilder.Create(
            temp.Path,
            "fomod.zip",
            ("fomod/ModuleConfig.xml", config),
            ("data/large.mod3", "large"));

        using InstallStaging staging = installer.Stage(zip);
        InstallResult result = FomodSelectionTestTools.CommitByName(staging, ["大型"]);

        Assert.False(string.IsNullOrEmpty(result.Record.PackageFingerprint), "FOMOD 安装也要写包指纹");
        Assert.Equal(result.Record.PackageFingerprint,
            Assert.Single(ModRepository.Load(paths).Mods).PackageFingerprint);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "large.mod3")));
        Assert.Single(store.Mods);
    }

    [Fact]
    public void Uninstall_LockedFile_KeepsRecordAndDirectoryThenSucceeds()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = CreateRealBox(temp);
        InstallResult installed;
        using (InstallStaging staging6 = installer.Stage(
            ZipBuilder.Create(temp.Path, "mod.zip", (ModFile, "bytes"))))
        {
            installed = staging6.Commit();
        }
        int id = installed.Record.Id;
        string lockedFile = Path.Combine(
            paths.FilesDirectory(id),
            ModFile.Replace('/', Path.DirectorySeparatorChar));

        using (FileStream hold = new(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            IOException ex = Assert.Throws<IOException>(() => installer.Uninstall(id));
            Assert.Contains(paths.ModDirectory(id), ex.Message);
        }

        Assert.True(Directory.Exists(paths.ModDirectory(id)), "删除失败必须保留目录（可重试）");
        Assert.NotNull(store.Find(id));
        Assert.Single(store.Mods);

        // 释放占用后可以正常卸载。
        Assert.True(installer.Uninstall(id));
        Assert.False(Directory.Exists(paths.ModDirectory(id)));
        Assert.Empty(store.Mods);
    }

}
