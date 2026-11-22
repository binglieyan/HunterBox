using HunterBox.Application.Install;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>重复安装同一个包必须被拒绝（否则会产生重复记录、文件互相覆盖）。</summary>
public class DuplicateInstallTests
{
    private const string GameId = "mhws";

    private static (BoxPaths Paths, ModRepository Store, ModInstaller Installer) Create(
        TempDir temp,
        string gameId = GameId)
    {
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        return (paths, store, new ModInstaller(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, gameId));
    }

    [Fact]
    public void Install_SamePackageTwice_SecondIsRejected()
    {
        using TempDir temp = TempDir.Create();
        (_, ModRepository store, ModInstaller installer) = Create(temp);
        string zip = ZipBuilder.Create(temp.Path, "mod.zip", ("natives/a.tex", "a"), ("modinfo.ini", "name=重复包"));

        using (InstallStaging staging1 = installer.Stage(zip))
        {
            staging1.Commit();
        }

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using InstallStaging staging2 = installer.Stage(zip);
            staging2.Commit();
        });
        Assert.Contains("重复包", ex.Message);
        Assert.Single(store.Mods);
    }

    [Fact]
    public void Install_SamePackageInDifferentGame_IsAllowed()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller wildsInstaller) = Create(temp, "mhws");
        ModInstaller worldInstaller = new(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhw");
        string zip = ZipBuilder.Create(temp.Path, "mod.zip", ("natives/a.tex", "a"), ("modinfo.ini", "name=跨游戏包"));

        InstallResult first;
        using (InstallStaging staging3 = wildsInstaller.Stage(zip))
        {
            first = staging3.Commit();
        }
        InstallResult second;
        using (InstallStaging staging4 = worldInstaller.Stage(zip))
        {
            second = staging4.Commit();
        }

        Assert.Equal(2, store.Mods.Count);
        Assert.Equal("mhws", first.Record.GameId);
        Assert.Equal("mhw", second.Record.GameId);
        Assert.NotEqual(first.Record.Id, second.Record.Id);
        Assert.Single(store.ModsFor("mhws"));
        Assert.Single(store.ModsFor("mhw"));
    }

    [Fact]
    public void Install_DifferentPackages_AreBothAccepted()
    {
        using TempDir temp = TempDir.Create();
        (_, ModRepository store, ModInstaller installer) = Create(temp);

        using (InstallStaging staging5 = installer.Stage(ZipBuilder.Create(temp.Path, "a.zip", ("natives/a.tex", "a"))))
        {
            staging5.Commit();
        }
        using (InstallStaging staging6 = installer.Stage(ZipBuilder.Create(temp.Path, "b.zip", ("natives/b.tex", "b"))))
        {
            staging6.Commit();
        }

        Assert.Equal(2, store.Mods.Count);
    }

    [Fact]
    public void Install_LegacyFingerprintRecord_SamePackage_IsRejected()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = Create(temp);
        string zip = ZipBuilder.Create(temp.Path, "legacy.zip", ("natives/a.tex", "same"));

        using (InstallStaging staging = installer.Stage(zip))
        {
            staging.Commit();
        }

        // 模拟升级前的记录：没有 v2: 前缀、也没有关键文件内容哈希。
        ModRecord record = Assert.Single(store.Mods);
        string filesDirectory = Path.Combine(paths.ModDirectory(record.Id), "files");
        record.PackageFingerprint = PackageFilters.ComputeLegacyFingerprint(
            filesDirectory,
            record.RelativeFiles,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using InstallStaging staging = installer.Stage(zip);
            staging.Commit();
        });
        Assert.Contains("已经安装过了", ex.Message);
        Assert.Single(store.Mods);
    }

    [Fact]
    public void Stage_ExposesFingerprint_ThatSurvivesRoundTrip()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, _, ModInstaller installer) = Create(temp);
        string zip = ZipBuilder.Create(temp.Path, "mod.zip", ("natives/a.tex", "a"));

        InstallResult result;
        using (InstallStaging staging7 = installer.Stage(zip))
        {
            result = staging7.Commit();
        }
        Assert.False(string.IsNullOrEmpty(result.Record.PackageFingerprint));

        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Equal(result.Record.PackageFingerprint, Assert.Single(reloaded.Mods).PackageFingerprint);
        Assert.Equal(GameId, Assert.Single(reloaded.Mods).GameId);
    }

    [Fact]
    public void Reinstall_OtherGameMod_IsRejected()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller wildsInstaller) = Create(temp, "mhws");
        ModInstaller worldInstaller = new(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhw");
        InstallResult installed;
        using (InstallStaging staging8 = wildsInstaller.Stage(
            ZipBuilder.Create(temp.Path, "v1.zip", ("natives/a.tex", "v1"))))
        {
            installed = staging8.Commit();
        }

        string newZip = ZipBuilder.Create(temp.Path, "v2.zip", ("natives/a.tex", "v2"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using InstallStaging staging9 = worldInstaller.StageReinstall(installed.Record.Id, newZip);
            staging9.Commit();
        });
        Assert.Contains("不属于当前游戏", ex.Message);

        Assert.Throws<InvalidOperationException>(() => worldInstaller.Uninstall(installed.Record.Id));
    }
}
