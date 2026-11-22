using System.Reflection;
using HunterBox.Application.Install;
using HunterBox.Application.Mount;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>多游戏隔离：单 <c>Mods/</c> + 单 index.json，靠 <see cref="ModRecord.GameId"/> 隔离。</summary>
public class MultiGameIsolationTests
{
    private const string LooseFile = "natives/stm/art/model/character/ch03/x.mod3";

    private static ModRecord NewMod(int id, string? gameId, string file = LooseFile) => new()
    {
        Id = id,
        Index = 0,
        GameId = gameId,
        Kind = ModKind.LooseFiles,
        RelativeFiles = [file],
    };

    [Fact]
    public void ModsFor_ReturnsOnlyThatGame_AndUnassignedStaysHidden()
    {
        using TempDir temp = TempDir.Create();
        (_, ModRepository repo) = TestBox.OpenRepository(temp);
        repo.Add(NewMod(1, "mhws"));
        repo.Add(NewMod(2, "mhw"));
        repo.Add(NewMod(3, null));
        repo.Add(NewMod(4, "mhws"));

        Assert.Equal([1, 4], repo.ModsFor("mhws").Select(m => m.Id));
        Assert.Equal([2], repo.ModsFor("mhw").Select(m => m.Id));
        Assert.Equal([3], repo.UnassignedMods.Select(m => m.Id));
        Assert.Empty(repo.ModsFor("unknown-game"));
    }

    [Fact]
    public void ModsFor_AllocatesIndependentIndexesPerGame()
    {
        using TempDir temp = TempDir.Create();
        (_, ModRepository repo) = TestBox.OpenRepository(temp);

        ModRecord wildsFirst = NewMod(1, "mhws");
        wildsFirst.Index = 0;
        repo.Add(wildsFirst);
        ModRecord worldFirst = NewMod(2, "mhw");
        worldFirst.Index = 0;
        repo.Add(worldFirst);
        ModRecord wildsSecond = NewMod(3, "mhws");
        wildsSecond.Index = 0;
        repo.Add(wildsSecond);

        Assert.Equal([1, 2], repo.ModsFor("mhws").Select(m => m.Index));
        Assert.Equal([1], repo.ModsFor("mhw").Select(m => m.Index));
    }

    [Fact]
    public void GameId_SerializesAndRoundTrips()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repo) = TestBox.OpenRepository(temp);
        repo.Add(NewMod(1, "mhws"));
        repo.Add(NewMod(2, "mhw"));
        repo.Save();

        string json = File.ReadAllText(Path.Combine(paths.ModsDirectory, "index.json"));
        Assert.Contains("\"GameId\": \"mhws\"", json);
        Assert.Contains("\"SchemaVersion\": 1", json);

        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Equal("mhws", reloaded.Find(1)!.GameId);
        Assert.Equal("mhw", reloaded.Find(2)!.GameId);
        Assert.Equal(ModRepository.CurrentSchemaVersion, 1);
    }

    [Fact]
    public void Backups_AreIsolatedByGame_EvenForSameRelativePath()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        string wildsRoot = temp.Dir("wilds");
        string worldRoot = temp.Dir("world");

        ModRecord wildsMod = NewMod(1, "mhws");
        ModRecord worldMod = NewMod(2, "mhw");
        WriteModFile(paths, wildsMod.Id, LooseFile, "wilds-mod");
        WriteModFile(paths, worldMod.Id, LooseFile, "world-mod");

        string wildsGameFile = Path.Combine(wildsRoot, LooseFile.Replace('/', Path.DirectorySeparatorChar));
        string worldGameFile = Path.Combine(worldRoot, LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(wildsGameFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(worldGameFile)!);
        File.WriteAllText(wildsGameFile, "wilds-original");
        File.WriteAllText(worldGameFile, "world-original");

        MountService wildsMount = new(
            paths, wildsRoot, "mhws", [wildsMod, worldMod],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        MountService worldMount = new(
            paths, worldRoot, "mhw", [wildsMod, worldMod],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        wildsMount.Enable(wildsMod);
        worldMount.Enable(worldMod);

        string wildsBackup = paths.BackupFile("mhws", LooseFile);
        string worldBackup = paths.BackupFile("mhw", LooseFile);
        Assert.NotEqual(wildsBackup, worldBackup);
        Assert.Equal("wilds-original", File.ReadAllText(wildsBackup));
        Assert.Equal("world-original", File.ReadAllText(worldBackup));

        // 还原一个游戏不能影响另一个游戏的备份/游戏文件。
        wildsMount.Disable(wildsMod);
        Assert.Equal("wilds-original", File.ReadAllText(wildsGameFile));
        Assert.Equal("world-mod", File.ReadAllText(worldGameFile));
        Assert.True(File.Exists(worldBackup));
    }

    [Fact]
    public void Install_WritesGameId_AndSamePackageCanBeInstalledPerGame()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        string zip = ZipBuilder.Create(temp.Path, "mod.zip", ("natives/a.tex", "a"));

        ModInstaller wildsInstaller = new(
            paths, store, new ArchiveExtractor(),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws");
        ModInstaller worldInstaller = new(
            paths, store, new ArchiveExtractor(),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhw");

        InstallResult wilds;
        using (InstallStaging staging1 = wildsInstaller.Stage(zip))
        {
            wilds = staging1.Commit();
        }
        InstallResult world;
        using (InstallStaging staging2 = worldInstaller.Stage(zip))
        {
            world = staging2.Commit();
        }

        Assert.Equal("mhws", wilds.Record.GameId);
        Assert.Equal("mhw", world.Record.GameId);
        Assert.Equal(2, store.Mods.Count);
        Assert.Single(store.ModsFor("mhws"));
        Assert.Single(store.ModsFor("mhw"));
    }

    [Fact]
    public void AssignGame_MovesUnassignedRecordIntoGameList()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repo) = TestBox.OpenRepository(temp);
        repo.Add(NewMod(1, "mhws"));
        repo.Add(NewMod(2, null));

        ModRecord assigned = repo.AssignGame(2, "mhws");
        repo.Save();

        Assert.Equal("mhws", assigned.GameId);
        Assert.Equal([1, 2], repo.ModsFor("mhws").Select(m => m.Id).OrderBy(id => id));
        Assert.Empty(repo.UnassignedMods);

        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Equal("mhws", reloaded.Find(2)!.GameId);
        Assert.Equal([1, 2], reloaded.ModsFor("mhws").Select(m => m.Index));

        Assert.Throws<InvalidOperationException>(() => repo.AssignGame(2, "mhw"));
    }

    [Fact]
    public void AssignGame_PreservesInstallSourceAndEveryPersistentField()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repo) = TestBox.OpenRepository(temp);
        ModRecord legacy = new()
        {
            Id = 7,
            Index = 0,
            Enabled = true,
            Metadata = new ModMetadata { Name = "旧包", Version = "2.0", Author = "作者", Description = "说明" },
            Kind = ModKind.PakFiles,
            TargetDirectory = "natives/stm",
            RelativeFiles = ["natives/stm/a.pak"],
            PackageFingerprint = "fingerprint-abc",
            InstallSource = @"D:\Downloads\old.zip",
            InstalledAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };
        repo.Add(legacy);

        ModRecord assigned = repo.AssignGame(7, "mhw");

        Assert.NotSame(legacy, assigned);
        Assert.Equal("mhw", assigned.GameId);
        Assert.Equal(1, assigned.Index);
        Assert.Equal(@"D:\Downloads\old.zip", assigned.InstallSource);

        // 反射逐属性比对：以后给 ModRecord 加字段而 AssignGame 忘记复制时，这条会失败。
        foreach (PropertyInfo property in typeof(ModRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead
                || property.Name is nameof(ModRecord.GameId) or nameof(ModRecord.Index))
            {
                continue;
            }

            Assert.True(
                Equals(property.GetValue(legacy), property.GetValue(assigned)),
                $"分配游戏后字段 {property.Name} 没有被保留");
        }

        repo.Save();
        ModRepository reloaded = ModRepository.Load(paths);
        ModRecord persisted = Assert.Single(reloaded.Mods);
        Assert.Equal("mhw", persisted.GameId);
        Assert.Equal(@"D:\Downloads\old.zip", persisted.InstallSource);
        Assert.Equal("fingerprint-abc", persisted.PackageFingerprint);
    }

    [Fact]
    public void LegacyIndexWithoutGameId_LoadsAsUnassigned()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(
            Path.Combine(paths.ModsDirectory, "index.json"),
            """
            {
              "NextId": 2,
              "Mods": [
                {
                  "Id": 1,
                  "Index": 1,
                  "Enabled": false,
                  "Metadata": { "Name": "旧数据" },
                  "Kind": 1,
                  "RelativeFiles": ["natives/a.tex"]
                }
              ]
            }
            """);

        ModRepository repo = ModRepository.Load(paths);

        Assert.Null(repo.LoadWarning);
        ModRecord legacy = Assert.Single(repo.Mods);
        Assert.Null(legacy.GameId);
        Assert.Equal(ModKind.LooseFiles, legacy.Kind); // Kind 保持数字反序列化兼容
        Assert.Empty(repo.ModsFor("mhws"));
        Assert.Single(repo.UnassignedMods);
    }

    private static void WriteModFile(BoxPaths paths, int id, string relative, string content)
    {
        string target = Path.Combine(paths.FilesDirectory(id), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
    }
}
