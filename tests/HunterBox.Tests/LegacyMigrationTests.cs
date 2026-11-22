using System.Runtime.Versioning;
using System.Text.Json;
using HunterBox.Application.Common;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Migration;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

public class LegacyMigrationTests
{
    /// <summary>只记录警告的内存日志，用于验证静态迁移入口的日志端口（A-23）。</summary>
    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message)
        {
        }

        public void Warn(string message) => Warnings.Add(message);

        public void WarnBatch(IReadOnlyList<string> messages) => Warnings.AddRange(messages);

        public void Error(string message, Exception? exception = null)
        {
        }
    }

    private const string InfoXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <ModInfo>
          <moduleName>Legacy Mod</moduleName>
          <name>旧测试 MOD</name>
          <version>1.2.3</version>
          <author>tester</author>
          <nexusId>777</nexusId>
          <modType>装备</modType>
          <HomeUrl>https://example.com/mod</HomeUrl>
          <enable>true</enable>
          <index>1</index>
        </ModInfo>
        """;

    [Fact]
    public void MigrateSettings_ReadOnlySettings_LogsWarningThroughInjectedLogger()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        FakeSettings settings = new() { IsReadOnly = true };
        RecordingLogger logger = new();

        bool migrated = LegacyMigrationService.MigrateSettings(paths, settings, logger);

        Assert.False(migrated);
        Assert.Contains(logger.Warnings, warning => warning.Contains("只读降级"));
    }

    [Fact]
    public void Detect_FindsLegacyLayouts()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 1001, "natives/stm/a.dat", "content");
        Directory.CreateDirectory(Path.Combine(temp.Path, "Backup_2246340", "natives", "stm"));
        File.WriteAllText(Path.Combine(temp.Path, "Backup_2246340", "natives", "stm", "orig.dat"), "GAME-ORIGINAL");
        Directory.CreateDirectory(Path.Combine(temp.Path, "Saves_2246340"));
        File.WriteAllText(Path.Combine(temp.Path, "sjmodbox.ini"), "[settings]\ngame=2246340\ncheck_game_running=true\n[2246340]\ngamepath=D:\\Game\\Wilds\n");

        LegacyInventory inventory = new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Detect();

        Assert.Single(inventory.Games);
        Assert.Equal("mhws", inventory.Games[0].GameId);
        Assert.Equal(1, inventory.Games[0].ModCount);
        Assert.True(inventory.Games[0].HasBackup);
        Assert.NotNull(inventory.LegacyIniPath);
        Assert.Single(inventory.LegacySaveDirectories);
        Assert.True(inventory.HasAnything);
    }

    [Fact]
    public void Import_CopiesModsAndDoesNotMoveLegacyData()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 1001, "natives/stm/a.dat", "content");
        string legacyModDir = Path.Combine(temp.Path, "Mods_2246340", "1001");

        LegacyInventory inventory = new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Detect();
        LegacyImportResult result = new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Import(inventory);

        Assert.Equal(1, result.ImportedMods);
        Assert.Equal(0, result.SkippedMods);
        Assert.True(Directory.Exists(legacyModDir), "旧数据必须保留（只读复制）");

        var mod = Assert.Single(store.Mods);
        Assert.Equal("mhws", mod.GameId);
        Assert.False(mod.Enabled, "旧启用状态不沿用，导入后应为未启用");
        Assert.Equal("旧测试 MOD", mod.Metadata.Name);
        Assert.Equal("1.2.3", mod.Metadata.Version);
        Assert.Equal("tester", mod.Metadata.Author);
        Assert.Equal(777, mod.Metadata.NexusId);
        Assert.Equal("装备", mod.Metadata.Category);
        Assert.Equal("https://example.com/mod", mod.Metadata.HomeUrl);
        Assert.Contains("natives/stm/a.dat", mod.RelativeFiles);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(mod.Id), "natives", "stm", "a.dat")));

        // 索引已落盘，重新加载仍能看到归属与元数据。
        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Single(reloaded.ModsFor("mhws"));
    }

    [Fact]
    public void Import_MigratesLegacyBackupDirectory()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 1003, "natives/stm/a.dat", "content");
        string backupFile = Path.Combine(temp.Path, "Backup_2246340", "natives", "stm", "orig.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.WriteAllText(backupFile, "GAME-ORIGINAL");

        LegacyInventory inventory = new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Detect();
        new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Import(inventory);

        Assert.True(File.Exists(paths.BackupFile("mhws", "natives/stm/orig.dat")));
        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(paths.BackupFile("mhws", "natives/stm/orig.dat")));
    }

    [Fact]
    public void Import_BackupOnlyLegacyGame_SkipsModsStageAndMigratesBackup()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        // 只有 Backup_<appid>、没有 Mods_<appid>：MOD 阶段跳过并记 warning，备份仍要迁移（C-02）。
        string backupFile = Path.Combine(temp.Path, "Backup_2246340", "natives", "stm", "orig.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.WriteAllText(backupFile, "GAME-ORIGINAL");

        LegacyInventory inventory = new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Detect();
        LegacyImportResult result = new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Import(inventory);

        Assert.Equal(0, result.ImportedMods);
        Assert.Equal(1, result.ImportedBackupFiles);
        Assert.True(File.Exists(paths.BackupFile("mhws", "natives/stm/orig.dat")));
        Assert.Contains(result.Warnings, warning => warning.Contains("旧 MOD 目录不存在"));
    }

    [AclFact]
    [SupportedOSPlatform("windows")]
    public void Import_UnreadableModsDirectory_SkipsModsAndStillImportsBackup()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        string modsDir = Path.Combine(temp.Path, "Mods_2246340");
        Directory.CreateDirectory(Path.Combine(modsDir, "1001"));
        string backupFile = Path.Combine(temp.Path, "Backup_2246340", "natives", "stm", "orig.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.WriteAllText(backupFile, "GAME-ORIGINAL");
        using DirectoryListDeny deny = DirectoryListDeny.Apply(modsDir);

        // 目录存在但无列举权限时跳过 MOD 阶段并记 warning，备份照常迁移。
        LegacyInventory inventory = new(
            [new LegacyGameInventory(2246340, "mhws", modsDir, ModCount: 1, HasBackup: true)],
            null,
            [],
            [],
            null);
        LegacyImportResult result = new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Import(inventory);

        Assert.Equal(0, result.ImportedMods);
        Assert.Equal(1, result.ImportedBackupFiles);
        Assert.True(File.Exists(paths.BackupFile("mhws", "natives/stm/orig.dat")));
        Assert.Contains(result.Warnings, warning => warning.Contains("无法枚举"));
    }

    /// <summary>Detect 枚举失败时标记残缺，Import 必须提示可能不完整。</summary>
    [AclFact]
    [SupportedOSPlatform("windows")]
    public void Detect_UnreadableModsDirectory_MarksIncompleteAndImportWarns()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        string modsDir = Path.Combine(temp.Path, "Mods_2246340");
        Directory.CreateDirectory(Path.Combine(modsDir, "1001"));
        using DirectoryListDeny deny = DirectoryListDeny.Apply(modsDir);

        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyInventory inventory = service.Detect();

        Assert.Contains(modsDir, inventory.UnreadablePaths);
        // 只有不可读路径时也必须算「有数据」，否则对话框不会弹出。
        Assert.True(inventory.HasAnything);

        LegacyImportResult result = service.Import(inventory);
        Assert.Contains(result.Warnings, warning => warning.Contains("未能完整读取", StringComparison.Ordinal));
    }

    /// <summary>mhr 回退到可读的 <c>Mods</c> 时，不再报告原 <c>Mods_&lt;appid&gt;</c> 不可读。</summary>
    [AclFact]
    [SupportedOSPlatform("windows")]
    public void Detect_MhrFallbackReadable_DoesNotReportOriginalUnreadable()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        int riseAppId = GameCatalog.Rise.SteamAppId;
        string appModsDir = Path.Combine(temp.Path, $"Mods_{riseAppId}");
        Directory.CreateDirectory(Path.Combine(appModsDir, "1001"));
        using DirectoryListDeny deny = DirectoryListDeny.Apply(appModsDir);

        string fallbackMod = Path.Combine(temp.Path, "Mods", "1001");
        Directory.CreateDirectory(Path.Combine(fallbackMod, "files"));
        File.WriteAllText(Path.Combine(fallbackMod, "files", "a.dat"), "content");
        File.WriteAllText(Path.Combine(fallbackMod, "info.xml"), InfoXml);

        LegacyInventory inventory = new LegacyMigrationService(
            paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Detect();

        Assert.True(
            inventory.UnreadablePaths.Count == 0,
            "不可读路径：" + string.Join(" | ", inventory.UnreadablePaths)
            + "；games=" + string.Join(",", inventory.Games.Select(g => g.GameId + ":" + g.ModCount + ":" + g.ModsDirectory)));
        Assert.Contains(inventory.Games, game => game.GameId == "mhr" && game.ModCount == 1);
    }

    /// <summary>原 <c>Mods_&lt;appid&gt;</c> 与回退 <c>Mods</c> 都不可读时，两条路径都要进入告警。</summary>
    [AclFact]
    [SupportedOSPlatform("windows")]
    public void Detect_MhrFallbackBothUnreadable_ReportsBothPaths()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        int riseAppId = GameCatalog.Rise.SteamAppId;
        string appMods = Path.Combine(temp.Path, $"Mods_{riseAppId}");
        Directory.CreateDirectory(Path.Combine(appMods, "1001"));
        using DirectoryListDeny appDeny = DirectoryListDeny.Apply(appMods);
        string fallback = Path.Combine(temp.Path, "Mods");
        Directory.CreateDirectory(fallback);
        using DirectoryListDeny fallbackDeny = DirectoryListDeny.Apply(fallback);

        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyInventory inventory = service.Detect();

        Assert.Contains(appMods, inventory.UnreadablePaths);
        Assert.Contains(fallback, inventory.UnreadablePaths);
        Assert.True(inventory.HasAnything);
        LegacyImportResult result = service.Import(inventory);
        Assert.Contains(result.Warnings, warning => warning.Contains("未能完整读取", StringComparison.Ordinal));
    }

    /// <summary>mhr 回退目标 <c>Mods</c> 本身也不可读时，必须记为不可读并提示可能不完整。</summary>
    [AclFact]
    [SupportedOSPlatform("windows")]
    public void Detect_MhrFallbackUnreadable_MarksIncomplete()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        string fallback = Path.Combine(temp.Path, "Mods");
        Directory.CreateDirectory(fallback);
        using DirectoryListDeny deny = DirectoryListDeny.Apply(fallback);

        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyInventory inventory = service.Detect();

        Assert.Contains(fallback, inventory.UnreadablePaths);
        Assert.True(inventory.HasAnything);
        LegacyImportResult result = service.Import(inventory);
        Assert.Contains(result.Warnings, warning => warning.Contains("未能完整读取", StringComparison.Ordinal));
    }

    /// <summary>只有 info.xml 内容不可读时，检测摘要也必须提示可能不完整。</summary>
    [FileAclFact]
    [SupportedOSPlatform("windows")]
    public void Detect_UnreadableInfoXml_MarksIncomplete()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        string modDir = Path.Combine(temp.Path, "Mods_2246340", "1001");
        Directory.CreateDirectory(Path.Combine(modDir, "files"));
        string infoPath = Path.Combine(modDir, "info.xml");
        File.WriteAllText(infoPath, InfoXml);
        using FileReadDeny deny = FileReadDeny.Apply(infoPath);

        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyInventory inventory = service.Detect();

        Assert.Contains(infoPath, inventory.UnreadablePaths);
        Assert.True(inventory.HasAnything);
        LegacyImportResult result = service.Import(inventory);
        Assert.Contains(result.Warnings, warning => warning.Contains("未能完整读取", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("无法读取（权限不足）", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_BackupCount_OnlyCountsThisGamesBackups()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 1004, "natives/stm/a.dat", "content");
        string legacyBackup = Path.Combine(temp.Path, "Backup_2246340", "natives", "stm", "orig.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyBackup)!);
        File.WriteAllText(legacyBackup, "GAME-ORIGINAL");

        string otherGameBackup = paths.BackupFile("mhw", "natives/stm/other.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(otherGameBackup)!);
        File.WriteAllText(otherGameBackup, "OTHER");

        LegacyInventory inventory = new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Detect();
        LegacyImportResult result = new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Import(inventory);

        Assert.Equal(1, result.ImportedBackupFiles);
        Assert.True(File.Exists(otherGameBackup));
    }

    /// <summary>无 info.xml 的目录不参与进度总数，进度必须能到达 100%。</summary>
    [Fact]
    public void Import_SkipsModWithoutInfoXml_AndProgressReachesTotal()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        string dir = Path.Combine(temp.Path, "Mods_2246340", "999");
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        File.WriteAllText(Path.Combine(dir, "files", "x.dat"), "x");
        WriteLegacyMod(temp, 2246340, 1001, "natives/stm/a.dat", "a");

        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyInventory inventory = service.Detect();
        List<(int Done, int Total)> reports = [];
        LegacyImportResult result = service.Import(
            inventory,
            default,
            new InlineProgress<(int Done, int Total)>(reports.Add));

        Assert.Equal(1, result.ImportedMods);
        Assert.Single(store.Mods);
        Assert.NotEmpty(reports);
        Assert.Equal(reports[^1].Done, reports[^1].Total);
    }

    [DirectoryLinkFact]
    public void Import_LegacyFilesContainDirectoryLink_SkipsLinkTarget()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 1001, "natives/stm/a.dat", "content");
        string external = temp.Dir("external");
        File.WriteAllText(Path.Combine(external, "secret.dat"), "secret");
        string filesDir = Path.Combine(temp.Path, "Mods_2246340", "1001", "files");
        Assert.True(
            LinkTestSupport.TryCreateDirectoryLink(Path.Combine(filesDir, "linked"), external),
            "无法创建目录链接（符号链接与 junction 均失败）");

        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyImportResult result = service.Import(service.Detect());

        ModRecord mod = Assert.Single(store.Mods);
        Assert.Contains("natives/stm/a.dat", mod.RelativeFiles);
        Assert.DoesNotContain(mod.RelativeFiles, file => file.Contains("linked", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("符号链接", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(paths.FilesDirectory(mod.Id), "linked", "secret.dat")));
    }

    /// <summary>files 根本身是链接时整条记录跳过，不复制链接目标。</summary>
    [DirectoryLinkFact]
    public void Import_FilesRootIsDirectoryLink_SkipsWholeMod()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        string external = temp.Dir("external-root");
        File.WriteAllText(Path.Combine(external, "secret.dat"), "secret");
        string modRoot = Path.Combine(temp.Path, "Mods_2246340", "1001");
        Directory.CreateDirectory(modRoot);
        File.WriteAllText(Path.Combine(modRoot, "info.xml"), InfoXml);
        Assert.True(
            LinkTestSupport.TryCreateDirectoryLink(Path.Combine(modRoot, "files"), external),
            "无法创建目录链接（符号链接与 junction 均失败）");

        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyImportResult result = service.Import(service.Detect());

        Assert.Empty(store.Mods);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("files 目录是符号链接", StringComparison.Ordinal));
    }

    private static void WriteLegacyMod(TempDir temp, int appId, int id, string relativeFile, string content)
    {
        string root = Path.Combine(temp.Path, $"Mods_{appId}", id.ToString());
        string file = Path.Combine(root, "files", relativeFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        File.WriteAllText(Path.Combine(root, "info.xml"), InfoXml);
    }

    [Fact]
    public void Import_CanceledToken_ThrowsWithoutCreatingMods()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 1001, "natives/stm/a.dat", "content");
        LegacyInventory inventory = new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Detect();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new LegacyMigrationService(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Import(inventory, cts.Token));
        Assert.Empty(store.Mods);
    }

    [Fact]
    public void Import_SameLegacyDirectoryTwice_SecondSkipsDuplicatesAndDoesNotCopyAgain()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 1001, "natives/stm/a.dat", "content");
        string legacyModDir = Path.Combine(temp.Path, "Mods_2246340", "1001");
        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyInventory inventory = service.Detect();

        LegacyImportResult first = service.Import(inventory);

        Assert.Equal(1, first.ImportedMods);
        Assert.Equal(0, first.SkippedDuplicates);
        Assert.Single(store.Mods);

        LegacyImportResult second = service.Import(inventory);

        Assert.Equal(0, second.ImportedMods);
        Assert.Equal(1, second.SkippedMods);
        Assert.Equal(1, second.SkippedDuplicates);
        Assert.Contains(second.Warnings, w => w.Contains("已跳过", StringComparison.Ordinal));

        // 记录与磁盘都没有翻倍：只有一条 MOD、一个 files 目录；旧数据保持原样。
        Assert.Single(store.Mods);
        Assert.Single(Directory.EnumerateDirectories(paths.ModsDirectory));
        Assert.True(Directory.Exists(legacyModDir), "旧数据必须保留（只读复制）");
        Assert.Equal("content", File.ReadAllText(
            Path.Combine(paths.FilesDirectory(store.Mods[0].Id), "natives", "stm", "a.dat")));
    }

    /// <summary>N-24：用户删除已导入的 MOD 后，去重标记不能阻止再次导入。</summary>
    [Fact]
    public void Import_AfterUserDeletesImportedMod_AllowsReimport()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 1001, "natives/stm/a.dat", "content");
        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        LegacyImportResult first = service.Import(service.Detect());
        int importedId = Assert.Single(store.Mods).Id;
        Assert.Equal(1, first.ImportedMods);

        // 模拟用户卸载：记录与 Mods/<id> 目录都删除，并落盘。
        Assert.True(store.Remove(importedId));
        store.Save();
        if (Directory.Exists(paths.ModDirectory(importedId)))
        {
            Directory.Delete(paths.ModDirectory(importedId), recursive: true);
        }

        LegacyImportResult second = service.Import(service.Detect());

        Assert.Equal(1, second.ImportedMods);
        ModRecord reimported = Assert.Single(store.Mods);
        Assert.NotEqual(importedId, reimported.Id);

        // 重启后再导入：去重标记必须已经落盘为新的 ModId，不能再复制一份。
        ModRepository reloaded = ModRepository.Load(paths);
        LegacyMigrationService restarted = new(paths, reloaded, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyImportResult third = restarted.Import(restarted.Detect());

        Assert.Equal(0, third.ImportedMods);
        Assert.Equal(1, third.SkippedDuplicates);
        Assert.Single(reloaded.Mods);
    }

    [Fact]
    public void Import_PersistsDiagnosableMarkerWithSourceAndModId()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 1001, "natives/stm/a.dat", "content");
        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        service.Import(service.Detect());

        Assert.True(File.Exists(service.ImportMarkerPath), "去重记录必须随导入落盘");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(service.ImportMarkerPath));
        JsonElement importedMods = document.RootElement.GetProperty("importedMods");
        JsonElement entry = Assert.Single(importedMods.EnumerateArray());
        Assert.Equal(
            Path.GetFullPath(Path.Combine(temp.Path, "Mods_2246340", "1001")),
            entry.GetProperty("source").GetString(),
            ignoreCase: true);
        Assert.Equal(store.Mods[0].Id, entry.GetProperty("modId").GetInt32());

        // 记录随索引一起被重新加载：新实例（模拟重启）导入同一旧目录同样会跳过。
        ModRepository reloaded = ModRepository.Load(paths);
        LegacyMigrationService restarted = new(paths, reloaded, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyImportResult second = restarted.Import(restarted.Detect());
        Assert.Equal(0, second.ImportedMods);
        Assert.Equal(1, second.SkippedDuplicates);
        Assert.Single(reloaded.Mods);
    }

    [Fact]
    public void Import_PartialFailureRetry_DoesNotDuplicateCompletedItems()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 2001, "natives/stm/a.dat", "A");
        WriteLegacyMod(temp, 2246340, 2002, "natives/stm/b.dat", "B");
        string lockedSource = Path.Combine(
            temp.Path, "Mods_2246340", "2002", "files", "natives", "stm", "b.dat");
        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyInventory inventory = service.Detect();

        LegacyImportResult first;
        using (FileStream locked = new(lockedSource, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // 其中一个旧 MOD 的源文件被占用：该 MOD 失败，另一个成功，属于「部分成功」。
            first = service.Import(inventory);
        }

        Assert.Equal(1, first.ImportedMods);
        Assert.Equal(1, first.SkippedMods);
        Assert.Equal(0, first.SkippedDuplicates);

        LegacyImportResult second = service.Import(inventory);

        Assert.Equal(1, second.ImportedMods);
        // 重试只能补导失败项，不能重复已完成项。
        Assert.Equal(1, second.SkippedDuplicates);
        Assert.Equal(2, store.Mods.Count);
        Assert.Contains(store.Mods, mod => File.Exists(
            Path.Combine(paths.FilesDirectory(mod.Id), "natives", "stm", "a.dat")));
        Assert.Contains(store.Mods, mod => File.Exists(
            Path.Combine(paths.FilesDirectory(mod.Id), "natives", "stm", "b.dat")));
        Assert.Equal(
            2,
            Directory.EnumerateDirectories(paths.ModsDirectory)
                .Count(dir => int.TryParse(Path.GetFileName(dir), out _)));
    }

    [Fact]
    public void Import_CanceledMidway_PersistsCompletedItemsAndRetrySkipsThem()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 3001, "natives/stm/a.dat", "A");
        WriteLegacyMod(temp, 2246340, 3002, "natives/stm/b.dat", "B");
        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyInventory inventory = service.Detect();
        using CancellationTokenSource cts = new();
        int reports = 0;
        IProgress<(int Done, int Total)> progress = new InlineProgress<(int, int)>(_ =>
        {
            if (++reports == 1)
            {
                cts.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() => service.Import(inventory, cts.Token, progress));

        // 取消也落盘：已完成的那一个 MOD 与去重记录都必须能在「重启」后读到。
        Assert.Single(store.Mods);
        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Single(reloaded.Mods);
        Assert.True(File.Exists(service.ImportMarkerPath));

        LegacyMigrationService restarted = new(paths, reloaded, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyImportResult retry = restarted.Import(restarted.Detect());

        Assert.Equal(1, retry.ImportedMods);
        Assert.Equal(1, retry.SkippedDuplicates);
        Assert.Equal(2, reloaded.Mods.Count);
    }

    [Fact]
    public void Import_CorruptMarker_IsPreservedWithWarningAndImportContinues()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        WriteLegacyMod(temp, 2246340, 4001, "natives/stm/a.dat", "content");
        Directory.CreateDirectory(paths.ModsDirectory);
        string markerPath = Path.Combine(paths.ModsDirectory, LegacyMigrationService.ImportMarkerFileName);
        File.WriteAllText(markerPath, "{ 这不是合法 JSON");

        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyImportResult result = service.Import(service.Detect());

        Assert.Equal(1, result.ImportedMods);
        Assert.Contains(result.Warnings, w => w.Contains("去重记录", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(paths.ModsDirectory, LegacyMigrationService.ImportMarkerFileName + ".bad-*"));
        Assert.True(File.Exists(service.ImportMarkerPath), "损坏记录保留后应重新写一份可用的去重记录");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(service.ImportMarkerPath));
        Assert.Single(document.RootElement.GetProperty("importedMods").EnumerateArray());
    }

    [Fact]
    public void Import_MarkerWithNullEntry_IsSkippedWithoutThrowing()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        Directory.CreateDirectory(paths.ModsDirectory);
        string markerPath = Path.Combine(paths.ModsDirectory, LegacyMigrationService.ImportMarkerFileName);
        File.WriteAllText(markerPath, "{\"version\":1,\"importedMods\":[null]}");

        // 去重记录里的 null 元素必须跳过，不能每次导入都 NRE。
        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        LegacyImportResult result = service.Import(new LegacyInventory([], null, [], [], null));

        Assert.Equal(0, result.ImportedMods);
    }

    /// <summary>导入前清理历史遗留的多余去重记录备份（≤3）。</summary>
    [Fact]
    public void Import_StaleMarkerBackups_ArePrunedAtStartup()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        Directory.CreateDirectory(paths.ModsDirectory);
        string markerPath = Path.Combine(paths.ModsDirectory, LegacyMigrationService.ImportMarkerFileName);
        for (int index = 0; index < 5; index++)
        {
            File.WriteAllText($"{markerPath}.bad-2026010100000{index}", "old");
        }

        LegacyMigrationService service = new(paths, store, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);
        _ = service.Import(new LegacyInventory([], null, [], [], null));

        Assert.True(
            Directory.GetFiles(paths.ModsDirectory, LegacyMigrationService.ImportMarkerFileName + ".bad-*").Length
                <= CorruptFileQuarantine.DefaultMaxCopies,
            "导入前未清理多余的去重记录备份");
    }

    /// <summary>同步回调的进度实现（<see cref="Progress{T}"/> 会切线程，测试里取消时机不可控）。</summary>
    private sealed class InlineProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
