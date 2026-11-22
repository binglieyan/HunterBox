using System.Globalization;
using HunterBox.Application.Saves;
using HunterBox.Domain.Layout;

namespace HunterBox.Tests;

[Collection("SaveBackupSizeCache")]
public class SaveBackupServiceTests
{
    /// <summary>
    /// 无清单的备份写入负缓存；缓存为进程级，跨 Store 实例（对话框重复打开）
    /// 连续刷新只扫描一次目录；过期后重新扫描。
    /// </summary>
    [Fact]
    public void List_BackupWithoutManifest_ScansDirectoryOnceAcrossStoreInstances()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        SaveBackupStore.ResetSizeCache();
        string backupDir = Path.Combine(paths.Root, "Saves", "mhws", "20260101-120000");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "slot1.sav"), "save-1");

        SaveBackupStore first = new(
            paths,
            clock,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup backup = Assert.Single(first.List("mhws"));
        Assert.True(backup.SizeBytes > 0);
        Assert.Equal(1, first.BackupSizeDirectoryScanCount);

        SaveBackupStore second = new(
            paths,
            clock,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup cached = Assert.Single(second.List("mhws"));
        Assert.Equal(backup.SizeBytes, cached.SizeBytes);
        Assert.Equal(0, second.BackupSizeDirectoryScanCount);

        clock.Advance(SaveBackupStore.NegativeSizeCacheLifetime + TimeSpan.FromSeconds(1));
        SaveBackupStore third = new(
            paths,
            clock,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        _ = third.List("mhws");
        Assert.Equal(1, third.BackupSizeDirectoryScanCount);
    }

    /// <summary>重命名备份后体积缓存键随目录迁移，新路径不重新扫描目录。</summary>
    [Fact]
    public void Rename_MigratesSizeCacheKeyWithoutRescanning()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        SaveBackupStore.ResetSizeCache();
        SaveBackupStore store = new(
            paths,
            clock,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string backupDir = Path.Combine(paths.Root, "Saves", "mhws", "20260101-120000");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "slot1.sav"), "save-1");

        SaveBackup backup = Assert.Single(store.List("mhws"));
        Assert.Equal(1, store.BackupSizeDirectoryScanCount);

        SaveBackup renamed = store.Rename(backup, "改名后的备份");
        SaveBackup listed = Assert.Single(store.List("mhws"));
        Assert.Equal("改名后的备份", listed.Name);
        Assert.Equal(backup.SizeBytes, listed.SizeBytes);
        Assert.Equal(1, store.BackupSizeDirectoryScanCount);
        Assert.Equal(renamed.Directory, listed.Directory);
    }

    /// <summary>
    /// 清单损坏时走负缓存（连续刷新不重扫）；
    /// FinalizeImportedBackup 写入清单后直接用写入体积刷新缓存，不回读整份清单。
    /// </summary>
    [Fact]
    public void List_CorruptManifest_UsesNegativeCacheAndFinalizeRefreshesSize()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        SaveBackupStore.ResetSizeCache();
        SaveBackupStore store = new(
            paths,
            clock,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string backupDir = Path.Combine(paths.Root, "Saves", "mhws", "20260101-120000");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "slot1.sav"), "save-1");
        File.WriteAllText(
            Path.Combine(backupDir, SaveBackupManifest.FileName),
            "{ 这不是合法清单 }");

        SaveBackup backup = Assert.Single(store.List("mhws"));
        Assert.Equal(6, backup.SizeBytes);
        Assert.Equal(1, store.BackupSizeDirectoryScanCount);
        Assert.Equal(1, store.BackupSizeManifestReadCount);
        Assert.Single(store.List("mhws"));
        Assert.Equal(1, store.BackupSizeDirectoryScanCount);
        Assert.Equal(1, store.BackupSizeManifestReadCount);

        // 新增第二个文件，让「清单体积」与「此前扫描体积」可区分（6 → 12）。
        File.WriteAllText(Path.Combine(backupDir, "slot2.sav"), "save-2");
        BackupManifestEntry slot1 = new("slot1.sav", 6, new string('A', 64));
        BackupManifestEntry slot2 = new("slot2.sav", 6, new string('B', 64));
        SaveBackup finalized = store.FinalizeImportedBackup(backup, [slot1, slot2], 12);
        Assert.Equal(12, finalized.SizeBytes);

        // 写入清单后直接使用返回体积，不读清单、不扫目录。
        Assert.Equal(1, store.BackupSizeManifestReadCount);
        Assert.Equal(1, store.BackupSizeDirectoryScanCount);

        SaveBackup listed = Assert.Single(store.List("mhws"));
        Assert.Equal(12, listed.SizeBytes);
        Assert.Equal(1, store.BackupSizeManifestReadCount);
        Assert.Equal(1, store.BackupSizeDirectoryScanCount);
    }

    /// <summary>时钟回拨后负缓存不能继续命中，必须重新扫描而不是无限延长。</summary>
    [Fact]
    public void List_ClockRollback_TreatsNegativeCacheAsExpired()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        SaveBackupStore.ResetSizeCache();
        string backupDir = Path.Combine(paths.Root, "Saves", "mhws", "20260101-120000");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "slot1.sav"), "save-1");

        SaveBackupStore first = new(
            paths,
            clock,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        _ = Assert.Single(first.List("mhws"));
        Assert.Equal(1, first.BackupSizeDirectoryScanCount);

        clock.Advance(TimeSpan.FromMinutes(-5));
        SaveBackupStore second = new(
            paths,
            clock,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        _ = second.List("mhws");
        Assert.Equal(1, second.BackupSizeDirectoryScanCount);
    }

    /// <summary>进程级体积缓存有上界，备份目录远超上限时条目数不会无界增长。</summary>
    [Fact]
    public void List_SizeCache_IsBoundedByMaxEntries()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        try
        {
            SaveBackupStore.ResetSizeCache();
            string root = Path.Combine(paths.Root, "Saves", "mhws");
            Directory.CreateDirectory(root);
            int count = SaveBackupStore.MaxSizeCacheEntries + 4;
            for (int index = 0; index < count; index++)
            {
                string directory = Path.Combine(root, "20260101-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "slot1.sav"), "x");
            }

            SaveBackupStore store = new(
                paths,
                clock,
                HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
                new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
            Assert.Equal(count, store.List("mhws").Count);
            Assert.True(SaveBackupStore.SizeCacheEntryCount <= SaveBackupStore.MaxSizeCacheEntries);
        }
        finally
        {
            SaveBackupStore.ResetSizeCache();
        }
    }

    /// <summary>扫描失败不能被当成 0 字节负缓存，下一次调用必须重新尝试。</summary>
    [Fact]
    public void GetBackupSize_ScanFailure_IsNotCached()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        SaveBackupStore store = new(
            paths,
            clock,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string filePath = Path.Combine(paths.Root, "Saves", "mhws", "not-a-directory");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "x");

        Assert.Equal(0, store.GetBackupSize(filePath, clock.UtcNow));
        Assert.Equal(0, store.GetBackupSize(filePath, clock.UtcNow));
        Assert.Equal(2, store.BackupSizeDirectoryScanCount);
    }

    /// <summary>超大清单按不可读处理，回退目录扫描且不读取清单内容。</summary>
    [Fact]
    public void List_OversizedManifest_FallsBackToDirectoryScan()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        SaveBackupStore.ResetSizeCache();
        string backupDir = Path.Combine(paths.Root, "Saves", "mhws", "20260101-120000");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "slot1.sav"), "save-1");
        string manifestPath = Path.Combine(backupDir, SaveBackupManifest.FileName);
        using (FileStream stream = new(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(SaveBackupManifest.MaxManifestBytes + 1);
        }

        SaveBackupStore store = new(
            paths,
            clock,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup backup = Assert.Single(store.List("mhws"));
        Assert.Equal(6, backup.SizeBytes);
        Assert.Equal(1, store.BackupSizeDirectoryScanCount);
        Assert.Equal(0, store.BackupSizeManifestReadCount);
    }

    /// <summary>子目录里的同名清单文件是用户文件，扫描体积必须计入。</summary>
    [Fact]
    public void List_NestedManifestNamedFile_IsCountedInSize()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        SaveBackupStore.ResetSizeCache();
        SaveBackupStore store = new(
            paths,
            clock,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string backupDir = Path.Combine(paths.Root, "Saves", "mhws", "20260101-120000");
        Directory.CreateDirectory(Path.Combine(backupDir, "sub"));
        File.WriteAllText(Path.Combine(backupDir, "slot1.sav"), "save-1");
        File.WriteAllText(Path.Combine(backupDir, "sub", SaveBackupManifest.FileName), "nested");

        SaveBackup backup = Assert.Single(store.List("mhws"));

        Assert.Equal(12, backup.SizeBytes);
    }

    /// <summary>超过容量时按 LRU 淘汰最久未使用项，最近写入项必须保留。</summary>
    [Fact]
    public void SizeCache_OverCapacity_EvictsOldestAndKeepsNewest()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        try
        {
            SaveBackupStore.ResetSizeCache();
            SaveBackupStore store = new(
                paths,
                clock,
                HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
                new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
            string root = Path.Combine(paths.Root, "Saves", "mhws");
            Directory.CreateDirectory(root);
            string first = string.Empty;
            string last = string.Empty;
            for (int index = 0; index <= SaveBackupStore.MaxSizeCacheEntries; index++)
            {
                string directory = Path.Combine(
                    root,
                    "20260101-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "slot1.sav"), "x");
                first = index == 0 ? directory : first;
                last = directory;
                _ = store.GetBackupSize(directory, clock.UtcNow);
            }

            Assert.False(SaveBackupStore.SizeCacheContains(first));
            Assert.True(SaveBackupStore.SizeCacheContains(last));
            Assert.True(SaveBackupStore.SizeCacheEntryCount <= SaveBackupStore.MaxSizeCacheEntries);
        }
        finally
        {
            SaveBackupStore.ResetSizeCache();
        }
    }

    /// <summary>单游戏超过上限只淘汰本游戏最久项，不影响其它游戏的缓存。</summary>
    [Fact]
    public void SizeCache_PerGameLimit_DoesNotEvictOtherGames()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        try
        {
            SaveBackupStore.ResetSizeCache();
            SaveBackupStore store = new(
                paths,
                clock,
                HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
                new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
            string otherRoot = Path.Combine(paths.Root, "Saves", "mhr");
            Directory.CreateDirectory(otherRoot);
            string other = Path.Combine(otherRoot, "20260101-000000");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "slot1.sav"), "x");
            _ = store.GetBackupSize(other, clock.UtcNow);

            string root = Path.Combine(paths.Root, "Saves", "mhws");
            Directory.CreateDirectory(root);
            string first = string.Empty;
            string last = string.Empty;
            for (int index = 0; index <= SaveBackupStore.MaxSizeCacheEntriesPerGame; index++)
            {
                string directory = Path.Combine(
                    root,
                    "20260101-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "slot1.sav"), "x");
                if (index == 0)
                {
                    first = directory;
                }

                last = directory;
                _ = store.GetBackupSize(directory, clock.UtcNow);
            }

            Assert.False(
                SaveBackupStore.SizeCacheContains(first),
                "单游戏超限应淘汰本游戏最久未使用项，否则限额没有生效");
            Assert.True(SaveBackupStore.SizeCacheContains(last));
            Assert.True(
                SaveBackupStore.SizeCacheContains(other),
                "单游戏超限不应淘汰其它游戏的缓存条目");
            Assert.True(SaveBackupStore.SizeCacheEntryCount <= SaveBackupStore.MaxSizeCacheEntries);
        }
        finally
        {
            SaveBackupStore.ResetSizeCache();
        }
    }

    /// <summary>普通备份直接使用复制时的大小，不再为读体积而回读清单。</summary>
    [Fact]
    public void Backup_UsesCopyTotalsWithoutReadingManifest()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        SaveBackupStore store = new(
            paths,
            new MutableClock(),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");

        SaveBackup backup = store.Backup("mhws", saveDir);

        Assert.Equal(6, backup.SizeBytes);
        Assert.Equal(0, store.BackupSizeManifestReadCount);
    }

    /// <summary>命中缓存与读取清单两种成功路径都只做一次元数据探测。</summary>
    [Fact]
    public void List_ManifestMetadataProbedOncePerRefresh()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        try
        {
            SaveBackupStore.ResetSizeCache();
            SaveBackupStore store = new(
                paths,
                clock,
                HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
                new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
            string saveDir = temp.Dir("save");
            File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");
            SaveBackup backup = store.Backup("mhws", saveDir);
            Assert.Equal(0, store.BackupSizeMetadataProbeCount);
            Assert.Equal(0, store.BackupSizeManifestReadCount);

            SaveBackup listed = Assert.Single(store.List("mhws"));
            Assert.Equal(backup.SizeBytes, listed.SizeBytes);
            Assert.Equal(1, store.BackupSizeMetadataProbeCount);
            Assert.Equal(0, store.BackupSizeManifestReadCount);

            SaveBackupStore.ResetSizeCache();
            SaveBackupStore fresh = new(
                paths,
                clock,
                HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
                new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
            _ = Assert.Single(fresh.List("mhws"));
            Assert.Equal(1, fresh.BackupSizeMetadataProbeCount);
            Assert.Equal(1, fresh.BackupSizeManifestReadCount);
        }
        finally
        {
            SaveBackupStore.ResetSizeCache();
        }
    }

    /// <summary>写侧超限必须在落盘前失败，不能生成之后必然不可读的清单。</summary>
    [Fact]
    public void WriteManifest_OversizedText_ThrowsBeforeWriting()
    {
        using TempDir temp = TempDir.Create();
        string backupDir = temp.Dir("backup");
        List<BackupManifestEntry> files =
        [
            new("slot1.sav", 6, new string('A', 64)),
        ];

        Assert.Throws<InvalidDataException>(() => SaveBackupManifest.WriteManifest(
            backupDir,
            files,
            new MutableClock(),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new OversizedManifestStore()));
        Assert.False(File.Exists(Path.Combine(backupDir, SaveBackupManifest.FileName)));
    }

    /// <summary>还原时清单超限要与损坏分开报告，便于用户判断原因。</summary>
    [Fact]
    public void Restore_OversizedManifest_ReportsManifestTooLarge()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(
            new BoxPaths(temp.Path),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");
        SaveBackup backup = service.Backup("mhws", saveDir);
        string manifestPath = Path.Combine(backup.Directory, SaveBackupManifest.FileName);
        using (FileStream stream = new(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(SaveBackupManifest.MaxManifestBytes + 1);
        }

        SaveBackupVerificationException ex = Assert.Throws<SaveBackupVerificationException>(
            () => service.Restore(backup, saveDir));

        Assert.Contains(ex.Verification.Issues, issue => issue.Kind == SaveBackupIssueKind.ManifestTooLarge);
        Assert.Equal("save-1", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
    }

    /// <summary>导入清单不能包含清单文件自身，避免写入后自引用。</summary>
    [Fact]
    public void FinalizeImportedBackup_ManifestSelfReference_Throws()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(
            new BoxPaths(temp.Path),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup slot = service.CreateSlot("mhws", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        BackupManifestEntry entry = new(
            SaveBackupManifest.FileName,
            6,
            new string('A', 64));

        Assert.Throws<InvalidDataException>(() => service.FinalizeImportedBackup(slot, [entry]));
        Assert.False(File.Exists(Path.Combine(slot.Directory, SaveBackupManifest.FileName)));
    }

    /// <summary>写侧超限用例的清单编解码桩，只返回超长文本。</summary>
    private sealed class OversizedManifestStore : IBackupManifestStore
    {
        /// <inheritdoc />
        public string Serialize(DateTimeOffset createdAt, IReadOnlyList<BackupManifestEntry> entries) =>
            new string('x', checked((int)SaveBackupManifest.MaxManifestBytes + 1));

        /// <inheritdoc />
        public IReadOnlyList<BackupManifestEntry>? TryDeserialize(string json) => null;
    }

    [Fact]
    public void Backup_CopiesWholeDirectoryTree()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");
        Directory.CreateDirectory(Path.Combine(saveDir, "backup"));
        File.WriteAllText(Path.Combine(saveDir, "backup", "old.sav"), "old");

        SaveBackup backup = service.Backup("mhws", saveDir);

        Assert.True(Directory.Exists(backup.Directory));
        Assert.Equal("save-1", File.ReadAllText(Path.Combine(backup.Directory, "slot1.sav")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(backup.Directory, "backup", "old.sav")));
    }

    [Fact]
    public async Task ListAsync_PreCanceled_ThrowsBeforeEnumerating()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string root = service.SavesRoot("mhws");
        Directory.CreateDirectory(Path.Combine(root, "20260101-120000"));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        // C-19：取消令牌必须传进目录遍历，而不是只阻止排队。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ListAsync("mhws", cancellation.Token));
    }

    [Fact]
    public void PurgeStaleSnapshots_RemovesOldSnapshotsAndKeepsNewest()
    {
        using TempDir temp = TempDir.Create();
        string saveDir = temp.Dir("userdata", "save");
        string parent = Path.GetDirectoryName(saveDir)!;
        string oldest = Path.Combine(parent, ".restore-undo-oldest");
        string older = Path.Combine(parent, ".restore-undo-old");
        string recent = Path.Combine(parent, ".restore-undo-recent");
        Directory.CreateDirectory(oldest);
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(recent);
        DateTimeOffset now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        Directory.SetLastWriteTimeUtc(oldest, now.UtcDateTime.AddDays(-40));
        Directory.SetLastWriteTimeUtc(older, now.UtcDateTime.AddDays(-30));
        Directory.SetLastWriteTimeUtc(recent, now.UtcDateTime.AddDays(-1));

        int removed = SaveBackupService.PurgeStaleSnapshots(
            saveDir, TimeSpan.FromDays(7), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, now);

        // 过期快照按保留期清理，只保留最近一份作为手工恢复来源。
        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(oldest));
        Assert.False(Directory.Exists(older));
        Assert.True(Directory.Exists(recent), "最近一份快照必须保留");
    }

    [Fact]
    public void PurgeStaleSnapshots_SingleOldSnapshot_IsKept()
    {
        using TempDir temp = TempDir.Create();
        string saveDir = temp.Dir("userdata", "save");
        string snapshot = Path.Combine(Path.GetDirectoryName(saveDir)!, ".restore-undo-kept");
        Directory.CreateDirectory(snapshot);
        DateTimeOffset now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        Directory.SetLastWriteTimeUtc(snapshot, now.UtcDateTime.AddDays(-40));

        Assert.Equal(0, SaveBackupService.PurgeStaleSnapshots(
            saveDir, TimeSpan.FromDays(7), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, now));
        Assert.True(Directory.Exists(snapshot));
    }

    [Fact]
    public void List_ReturnsNewestFirst()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string root = service.SavesRoot("mhws");
        Directory.CreateDirectory(Path.Combine(root, "20260101-120000"));
        Directory.CreateDirectory(Path.Combine(root, "20260201-120000"));

        IReadOnlyList<SaveBackup> backups = service.ListAsync("mhws").GetAwaiter().GetResult();

        Assert.Equal(2, backups.Count);
        Assert.Equal("20260201-120000", backups[0].Name);
    }

    [Fact]
    public async Task ListAsync_ReturnsBackupsWithManifestSize()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");
        Directory.CreateDirectory(Path.Combine(saveDir, "backup"));
        File.WriteAllText(Path.Combine(saveDir, "backup", "old.sav"), "old");

        SaveBackup created = service.Backup("mhws", saveDir);
        IReadOnlyList<SaveBackup> backups = await service.ListAsync("mhws");

        SaveBackup listed = Assert.Single(backups);
        Assert.Equal(created.Name, listed.Name);
        Assert.Equal(created.SizeBytes, listed.SizeBytes);
        Assert.Equal(9, listed.SizeBytes);
    }

    [Fact]
    public void Backup_UsesInjectedClockForSlotName()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore(), new FixedClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save");

        SaveBackup backup = service.Backup("mhws", saveDir);

        // N-03：目录名与列表/解析统一使用本地时间。
        string expected = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
            .ToLocalTime()
            .ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        Assert.Equal(expected, backup.Name);
    }

    private sealed class FixedClock(DateTimeOffset now) : HunterBox.Application.Common.IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    [Fact]
    public void Backup_MissingSaveDirectory_Throws()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());

        Assert.Throws<DirectoryNotFoundException>(() =>
            service.Backup("mhws", Path.Combine(temp.Path, "nope")));
    }

    [Fact]
    public void Restore_OverwritesFilesFromBackup()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "before");
        SaveBackup backup = service.Backup("mhws", saveDir);

        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "after");
        service.Restore(backup, saveDir);

        Assert.Equal("before", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
    }

    [Fact]
    public void Restore_MissingBackup_Throws()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());

        Assert.Throws<DirectoryNotFoundException>(() =>
            service.Restore(new SaveBackup("x", Path.Combine(temp.Path, "nope"), DateTimeOffset.Now), temp.Path));
    }

    [Fact]
    public void Backup_WritesHashManifest_AndVerifyPasses()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");
        Directory.CreateDirectory(Path.Combine(saveDir, "nested"));
        File.WriteAllText(Path.Combine(saveDir, "nested", "slot2.sav"), "save-2");

        SaveBackup backup = service.Backup("mhws", saveDir);
        Assert.True(File.Exists(Path.Combine(backup.Directory, SaveBackupManifest.FileName)));

        SaveBackupVerification verification = Verify(backup);
        Assert.True(verification.HasManifest);
        Assert.True(verification.IsValid);
        Assert.Equal(2, verification.FileCount);
    }

    [Fact]
    public void Restore_TamperedBackup_ThrowsInvalidData_AndDoesNotOverwrite()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "original");
        SaveBackup backup = service.Backup("mhws", saveDir);

        File.WriteAllText(Path.Combine(backup.Directory, "slot1.sav"), "tampered");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "current");

        SaveBackupVerificationException ex = Assert.Throws<SaveBackupVerificationException>(
            () => service.Restore(backup, saveDir));
        Assert.Contains(
            ex.Verification.Issues,
            issue => issue.Kind == SaveBackupIssueKind.HashMismatch && issue.RelativePath == "slot1.sav");
        Assert.Equal("current", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
    }

    [Fact]
    public void Restore_MissingHashManifest_StillRestoresLegacyBackup()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "legacy");
        SaveBackup backup = service.Backup("mhws", saveDir);

        File.Delete(Path.Combine(backup.Directory, SaveBackupManifest.FileName));
        Assert.False(Verify(backup).HasManifest);

        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "changed");
        SaveBackupVerification verification = service.Restore(backup, saveDir);
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
        Assert.False(verification.HasManifest, "旧备份无清单时应如实返回未校验");
    }

    /// <summary>
    /// P-12：还原只产生一次校验结果 —— <see cref="SaveBackupService.Restore"/> 返回它实际使用的校验结果，
    /// 界面不需要（也不应）在调用前再 <see cref="SaveBackupService.Verify"/> 一遍做全量哈希。
    /// </summary>
    [Fact]
    public void Restore_ReturnsVerificationResult_ReusableByUi()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");
        Directory.CreateDirectory(Path.Combine(saveDir, "nested"));
        File.WriteAllText(Path.Combine(saveDir, "nested", "slot2.sav"), "save-2");
        SaveBackup backup = service.Backup("mhws", saveDir);

        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "changed");
        SaveBackupVerification verification = service.Restore(backup, saveDir);

        Assert.True(verification.HasManifest);
        Assert.True(verification.IsValid);
        Assert.Equal(2, verification.FileCount);
        Assert.Empty(verification.Issues);
        Assert.Equal("save-1", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
    }

    /// <summary>P-12：源校验与写影子目录合并后仍必须拒绝「清单里有、备份里缺失」的备份，且不改动当前存档。</summary>
    [Fact]
    public void Restore_BackupFileMissing_ThrowsInvalidData_AndDoesNotOverwrite()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "original");
        File.WriteAllText(Path.Combine(saveDir, "slot2.sav"), "original-2");
        SaveBackup backup = service.Backup("mhws", saveDir);

        File.Delete(Path.Combine(backup.Directory, "slot2.sav"));
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "current");

        SaveBackupVerificationException ex = Assert.Throws<SaveBackupVerificationException>(
            () => service.Restore(backup, saveDir));

        Assert.Contains(
            ex.Verification.Issues,
            issue => issue.Kind == SaveBackupIssueKind.MissingFile && issue.RelativePath == "slot2.sav");
        Assert.Equal("current", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
        AssertNoRestoreDirectories(temp.Path);
    }

    /// <summary>P-12：清单含 <c>..</c> 非法路径时必须在改动存档前拒绝（不再依赖外部先调 Verify）。</summary>
    [Fact]
    public void Restore_ManifestWithIllegalPath_ThrowsInvalidData_AndDoesNotOverwrite()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "current");
        SaveBackup backup = service.Backup("mhws", saveDir);
        File.WriteAllText(
            Path.Combine(backup.Directory, SaveBackupManifest.FileName),
            """
            {
              "version": 1,
              "createdAt": "2026-01-01T00:00:00+08:00",
              "files": [
                { "path": "../evil.sav", "size": 1, "sha256": "0000000000000000000000000000000000000000000000000000000000000000" }
              ]
            }
            """);

        SaveBackupVerificationException ex = Assert.Throws<SaveBackupVerificationException>(
            () => service.Restore(backup, saveDir));

        Assert.Contains(
            ex.Verification.Issues,
            issue => issue.Kind == SaveBackupIssueKind.InvalidPath && issue.RelativePath == "../evil.sav");
        Assert.Equal("current", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
        AssertNoRestoreDirectories(temp.Path);
    }

    [Fact]
    public void Restore_DoesNotCopyManifestIntoGameDirectory()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "x");
        SaveBackup backup = service.Backup("mhws", saveDir);

        service.Restore(backup, saveDir);

        Assert.False(File.Exists(Path.Combine(saveDir, SaveBackupManifest.FileName)));
        Assert.Equal("x", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
    }

    /// <summary>备份目录里旧的清单临时文件不能被还原进游戏存档。</summary>
    [Fact]
    public void Restore_DoesNotCopyManifestTemporaryFileIntoGameDirectory()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "x");
        SaveBackup backup = service.Backup("mhws", saveDir);
        File.WriteAllText(Path.Combine(backup.Directory, SaveBackupManifest.FileName + ".tmp"), "{}");

        service.Restore(backup, saveDir);

        Assert.False(File.Exists(Path.Combine(saveDir, SaveBackupManifest.FileName + ".tmp")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
    }

    /// <summary>空存档目录没有可还原内容，备份阶段直接拒绝且不留下半成品。</summary>
    [Fact]
    public void Backup_EmptySaveDirectory_ThrowsAndLeavesNoBackup()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");

        Assert.Throws<InvalidDataException>(() => service.Backup("mhws", saveDir));
        Assert.Empty(service.ListAsync("mhws").GetAwaiter().GetResult());
    }

    /// <summary>备份失败删除半成品后必须失效复制阶段写入的 0 字节正缓存。</summary>
    [Fact]
    public void Backup_EmptySaveDirectory_InvalidatesSizeCache()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        MutableClock clock = new();
        SaveBackupStore store = new(
            paths,
            clock,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        string expectedSlot = Path.Combine(
            paths.Root,
            "Saves",
            "mhws",
            clock.UtcNow.ToLocalTime().ToString(
                SaveBackupFormatter.TimestampFormat,
                System.Globalization.CultureInfo.InvariantCulture));

        Assert.Throws<InvalidDataException>(() => store.Backup("mhws", saveDir));
        Assert.False(SaveBackupStore.SizeCacheContains(expectedSlot));
    }

    /// <summary>空导入清单没有任何可校验条目，收尾必须拒绝且不写清单。</summary>
    [Fact]
    public void FinalizeImportedBackup_EmptyEntries_Throws()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup slot = service.CreateSlot("mhws", DateTimeOffset.Now);

        Assert.Throws<InvalidDataException>(() => service.FinalizeImportedBackup(slot, []));
        Assert.False(File.Exists(Path.Combine(slot.Directory, SaveBackupManifest.FileName)));
    }

    /// <summary>
    /// 调用方取得的长度可能过期；读取后必须按实际内容复核上限，
    /// 超过 16MB 的清单按不可读处理。
    /// </summary>
    [Fact]
    public void ReadManifest_StaleKnownLengthAndOversizedContent_ReturnsNull()
    {
        using TempDir temp = TempDir.Create();
        string manifestPath = Path.Combine(temp.Path, "manifest.json");
        using (FileStream stream = new(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(SaveBackupManifest.MaxManifestBytes + 1);
        }

        SaveBackupManifest.HashManifest? manifest = SaveBackupManifest.Read(
            manifestPath,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Saves.JsonBackupManifestStore(),
            out bool contentRead,
            knownLength: 1);

        Assert.Null(manifest);
        Assert.True(contentRead);
    }

    /// <summary>同一路径的不同写法（大小写、空段、<c>.</c> 段、尾随分隔符）视为重复并整份拒绝。</summary>
    [Theory]
    [InlineData("slot1.sav", "slot1.sav")]
    [InlineData("slot1.sav", "SLOT1.sav")]
    [InlineData("natives/a.bin", "natives//a.bin")]
    [InlineData("natives/a.bin", "natives/a.bin/")]
    [InlineData("natives/a.bin", "natives/./a.bin")]
    [InlineData("natives/a.bin", "natives/../natives/a.bin")]
    [InlineData("natives/a.bin", "natives/a.bin ")]
    [InlineData("natives/a.bin", "natives/a.bin.")]
    [InlineData("natives\\a.bin", "natives/a.bin")]
    public void ReadManifest_DuplicatePathVariants_ReturnsNull(string first, string second)
    {
        using TempDir temp = TempDir.Create();
        string manifestPath = Path.Combine(temp.Path, "manifest.json");
        File.WriteAllText(
            manifestPath,
            "{\"version\":1,\"files\":["
            + "{\"path\":\"" + first + "\",\"size\":1,\"sha256\":\"" + new string('0', 64) + "\"},"
            + "{\"path\":\"" + second + "\",\"size\":2,\"sha256\":\"" + new string('1', 64) + "\"}]}");

        SaveBackupManifest.HashManifest? manifest = SaveBackupManifest.Read(
            manifestPath,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore(), out _, new FileInfo(manifestPath).Length);

        Assert.Null(manifest);
    }

    /// <summary>前导空格是 Windows 文件名的一部分，不能被重复检测归一掉。</summary>
    [Fact]
    public void ReadManifest_LeadingSpaceNames_AreNotDuplicates()
    {
        using TempDir temp = TempDir.Create();
        string manifestPath = Path.Combine(temp.Path, "manifest.json");
        File.WriteAllText(
            manifestPath,
            "{\"version\":1,\"files\":["
            + "{\"path\":\" a.sav\",\"size\":1,\"sha256\":\"" + new string('0', 64) + "\"},"
            + "{\"path\":\"a.sav\",\"size\":2,\"sha256\":\"" + new string('1', 64) + "\"}]}");

        SaveBackupManifest.HashManifest? manifest = SaveBackupManifest.Read(
            manifestPath,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore(), out _, new FileInfo(manifestPath).Length);

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.Files.Count);
    }

    /// <summary>SHA-256 长度或字符不合法时按清单损坏处理，而不是等到还原后报哈希不一致。</summary>
    [Fact]
    public void ReadManifest_InvalidSha256_ReturnsNull()
    {
        using TempDir temp = TempDir.Create();
        string manifestPath = Path.Combine(temp.Path, "manifest.json");
        foreach (string sha in new[] { "AB", new string('Z', 64) })
        {
            File.WriteAllText(
                manifestPath,
                "{\"version\":1,\"files\":[{\"path\":\"slot1.sav\",\"size\":1,\"sha256\":\"" + sha + "\"}]}");

            SaveBackupManifest.HashManifest? manifest = SaveBackupManifest.Read(
                manifestPath,
                HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore(), out _, new FileInfo(manifestPath).Length);

            Assert.Null(manifest);
        }
    }

    [Fact]
    public void FinalizeImportedBackup_WritesManifestForSlot()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup slot = service.CreateSlot("mhws", DateTimeOffset.Now);
        File.WriteAllText(Path.Combine(slot.Directory, "slot1.sav"), "imported");

        BackupManifestEntry entry = new(
            "slot1.sav",
            8,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("imported"))));
        SaveBackup finalized = service.FinalizeImportedBackup(slot, [entry]);
        SaveBackupVerification verification = Verify(finalized);
        Assert.True(verification.HasManifest);
        Assert.True(verification.IsValid);
        Assert.Equal(1, verification.FileCount);
    }

    [Fact]
    public void Restore_MissingBackupFile_ReturnsStructuredMissingFileIssue()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");
        SaveBackup backup = service.Backup("mhws", saveDir);
        File.Delete(Path.Combine(backup.Directory, "slot1.sav"));

        SaveBackupVerificationException ex = Assert.Throws<SaveBackupVerificationException>(() => Verify(backup));

        Assert.True(ex.Verification.HasManifest);
        Assert.False(ex.Verification.IsValid);
        Assert.Contains(
            ex.Verification.Issues,
            issue => issue.Kind == SaveBackupIssueKind.MissingFile && issue.RelativePath == "slot1.sav");
    }

    [Fact]
    public void Restore_LeavesNoStagingDirectories()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "current");
        SaveBackup backup = service.Backup("test", saveDir);
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "changed");

        service.Restore(backup, saveDir);

        Assert.Equal("current", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
        string parent = Path.GetDirectoryName(Path.GetFullPath(saveDir))!;
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(parent),
            path => Path.GetFileName(path).StartsWith(".restore-", StringComparison.Ordinal));
    }

    [Fact]
    public void Restore_KeepsFilesNotPresentInBackup()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "backup-version");
        SaveBackup backup = service.Backup("test", saveDir);

        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "changed");
        File.WriteAllText(Path.Combine(saveDir, "extra.sav"), "extra");

        service.Restore(backup, saveDir);

        Assert.Equal("backup-version", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
        Assert.Equal("extra", File.ReadAllText(Path.Combine(saveDir, "extra.sav")));
    }

    [Fact]
    public void ReadManifest_NullFiles_ReturnsNull()
    {
        using TempDir temp = TempDir.Create();
        string manifestPath = Path.Combine(temp.Path, "manifest.json");
        File.WriteAllText(
            manifestPath,
            "{\"version\":1,\"createdAt\":\"2026-11-17T00:00:00+00:00\",\"files\":null}");

        SaveBackupManifest.HashManifest? manifest = SaveBackupManifest.Read(
            manifestPath,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore(), out _, new FileInfo(manifestPath).Length);

        // 没有任何有效条目的清单不能当成“有效空清单”，否则还原会未校验覆盖存档。
        Assert.Null(manifest);
    }

    [Fact]
    public void Restore_ManifestWithNullFiles_RejectsAndKeepsCurrentSave()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "backup-version");
        SaveBackup backup = service.Backup("mhws", saveDir);
        File.WriteAllText(
            Path.Combine(backup.Directory, SaveBackupManifest.FileName),
            "{\"version\":1,\"files\":null}");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "current");

        SaveBackupVerificationException ex = Assert.Throws<SaveBackupVerificationException>(
            () => service.Restore(backup, saveDir));

        // 清单损坏时必须在改动存档之前拒绝，且当前存档保持原样、不留下还原临时目录。
        Assert.False(ex.RolledBack);
        Assert.Contains(ex.Verification.Issues, issue => issue.Kind == SaveBackupIssueKind.ManifestUnreadable);
        Assert.Equal("current", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
        AssertNoRestoreDirectories(Path.GetDirectoryName(Path.GetFullPath(saveDir))!);
    }

    [Fact]
    public void ReadManifest_NullEntry_ReturnsNull()
    {
        using TempDir temp = TempDir.Create();
        string manifestPath = Path.Combine(temp.Path, "manifest.json");
        File.WriteAllText(
            manifestPath,
            "{\"version\":1,\"files\":[null,{\"path\":\"slot1.sav\",\"size\":1,\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}]}");

        SaveBackupManifest.HashManifest? manifest = SaveBackupManifest.Read(
            manifestPath,
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore(), out _, new FileInfo(manifestPath).Length);

        // 混入 null 项的清单不能被过滤后放行，否则未列出的文件不会参与校验。
        Assert.Null(manifest);
    }

    /// <summary>一条有效 + 一条 null 的部分损坏清单必须在改动存档前拒绝还原。</summary>
    [Fact]
    public void Restore_ManifestWithPartiallyCorruptEntries_RejectsAndKeepsCurrentSave()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "backup-version");
        SaveBackup backup = service.Backup("mhws", saveDir);
        File.WriteAllText(
            Path.Combine(backup.Directory, SaveBackupManifest.FileName),
            "{\"version\":1,\"files\":[{\"path\":\"slot1.sav\",\"size\":14,\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"},null]}");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "current");

        SaveBackupVerificationException ex = Assert.Throws<SaveBackupVerificationException>(
            () => service.Restore(backup, saveDir));

        Assert.False(ex.RolledBack);
        Assert.Contains(ex.Verification.Issues, issue => issue.Kind == SaveBackupIssueKind.ManifestUnreadable);
        Assert.Equal("current", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
        AssertNoRestoreDirectories(Path.GetDirectoryName(Path.GetFullPath(saveDir))!);
    }

    /// <summary>备份目录出现清单外文件时必须在改动存档前失败，而不是显示“校验通过”。</summary>
    [Fact]
    public void Restore_BackupWithFileOutsideManifest_RejectsAndKeepsCurrentSave()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "original");
        SaveBackup backup = service.Backup("mhws", saveDir);
        File.WriteAllText(Path.Combine(backup.Directory, "extra.sav"), "unexpected");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "current");

        SaveBackupVerificationException ex = Assert.Throws<SaveBackupVerificationException>(
            () => service.Restore(backup, saveDir));

        Assert.False(ex.RolledBack);
        Assert.Contains(ex.Verification.Issues, issue =>
            issue.Kind == SaveBackupIssueKind.UnexpectedFile && issue.RelativePath == "extra.sav");
        Assert.Equal("current", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
        Assert.False(File.Exists(Path.Combine(saveDir, "extra.sav")));
        AssertNoRestoreDirectories(Path.GetDirectoryName(Path.GetFullPath(saveDir))!);
    }

    /// <summary>用还原路径取得校验结果：生产入口只有 Restore，测试不再依赖已删除的 Verify。</summary>
    private static SaveBackupVerification Verify(SaveBackup backup)
    {
        string target = Path.Combine(Path.GetTempPath(), "hunterbox-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            SaveBackupService service = new(
                new BoxPaths(Path.Combine(Path.GetTempPath(), "hunterbox-verify-box")),
                HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
                new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
            return service.Restore(backup, target);
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    private static void AssertNoRestoreDirectories(string parent)
    {
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(parent),
            path => Path.GetFileName(path).StartsWith(".restore-", StringComparison.Ordinal));
    }
}

/// <summary>
/// 共享进程级备份体积缓存的测试集合：集合内串行，避免 <c>ResetSizeCache</c> 与全局计数断言
/// 被并发用例干扰；不禁止集合与其它测试类并行。
/// </summary>
[CollectionDefinition("SaveBackupSizeCache")]
public sealed class SaveBackupSizeCacheDefinition
{
}
