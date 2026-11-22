using HunterBox.Application.Saves;
using HunterBox.Domain.Layout;

namespace HunterBox.Tests;

/// <summary>存档管理（重命名/删除/大小/还原校验）回归测试。</summary>
[Collection("SaveBackupSizeCache")]
public class SaveBackupManagementTests
{
    [Fact]
    public void Rename_ChangesNameAndKeepsContent()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");
        SaveBackup backup = service.Backup("mhws", saveDir);

        SaveBackup renamed = service.Rename(backup, "狩猎前");

        Assert.Equal("狩猎前", renamed.Name);
        Assert.Equal("狩猎前", Path.GetFileName(renamed.Directory));
        Assert.False(Directory.Exists(backup.Directory));
        Assert.Equal("save-1", File.ReadAllText(Path.Combine(renamed.Directory, "slot1.sav")));
        Assert.Contains(service.ListAsync("mhws").GetAwaiter().GetResult(), b => b.Name == "狩猎前");
        Assert.Equal(backup.CreatedAt, renamed.CreatedAt);
    }

    [Fact]
    public void Rename_SameName_IsNoOp()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup backup = BackupWithFile(service, temp);

        SaveBackup renamed = service.Rename(backup, backup.Name);

        Assert.Equal(backup, renamed);
        Assert.True(Directory.Exists(backup.Directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("name.")]
    public void Rename_InvalidName_Throws(string name)
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup backup = BackupWithFile(service, temp);

        Assert.Throws<ArgumentException>(() => service.Rename(backup, name));
        Assert.True(Directory.Exists(backup.Directory));
    }

    [Fact]
    public void Rename_DuplicateName_Throws()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup first = BackupWithFile(service, temp);
        SaveBackup second = BackupWithFile(service, temp);

        Assert.Throws<IOException>(() => service.Rename(second, first.Name));
        Assert.True(Directory.Exists(second.Directory));
    }

    [Fact]
    public void Rename_BackupOutsideSaves_Throws()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup outside = new("x", temp.Dir("outside"), DateTimeOffset.Now);

        Assert.Throws<ArgumentException>(() => service.Rename(outside, "y"));
    }

    [Fact]
    public void Rename_MissingBackup_Throws()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string missing = Path.Combine(service.SavesRoot("mhws"), "20260101-000000");

        Assert.Throws<DirectoryNotFoundException>(() =>
            service.Rename(new SaveBackup("20260101-000000", missing, DateTimeOffset.Now), "x"));
    }

    [Fact]
    public void Delete_RemovesDirectoryAndDisappearsFromList()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup backup = BackupWithFile(service, temp);

        service.Delete(backup);

        Assert.False(Directory.Exists(backup.Directory));
        Assert.Empty(service.ListAsync("mhws").GetAwaiter().GetResult());
    }

    [Fact]
    public void Delete_MissingBackup_Throws()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string missing = Path.Combine(service.SavesRoot("mhws"), "20260101-000000");

        Assert.Throws<DirectoryNotFoundException>(() =>
            service.Delete(new SaveBackup("20260101-000000", missing, DateTimeOffset.Now)));
    }

    [Fact]
    public void Delete_OutsideSaves_Throws()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string outside = temp.Dir("outside");

        Assert.Throws<ArgumentException>(() =>
            service.Delete(new SaveBackup("outside", outside, DateTimeOffset.Now)));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void List_ReportsSizeBytes()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "a.sav"), "12345");
        Directory.CreateDirectory(Path.Combine(saveDir, "sub"));
        File.WriteAllText(Path.Combine(saveDir, "sub", "b.sav"), "678");

        SaveBackup backup = service.Backup("mhws", saveDir);
        SaveBackup listed = Assert.Single(service.ListAsync("mhws").GetAwaiter().GetResult());

        Assert.Equal(8, backup.SizeBytes);
        Assert.Equal(8, listed.SizeBytes);
    }

    [Fact]
    public void List_CustomNamedBackup_KeepsNameAndFallsBackToCreationTime()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string custom = Path.Combine(service.SavesRoot("mhws"), "狩猎前");
        Directory.CreateDirectory(custom);
        File.WriteAllText(Path.Combine(custom, "a.sav"), "x");

        SaveBackup listed = Assert.Single(service.ListAsync("mhws").GetAwaiter().GetResult());

        Assert.Equal("狩猎前", listed.Name);
        Assert.Equal(1, listed.SizeBytes);
        Assert.True(listed.CreatedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public void Restore_CreatesMissingTargetDirectory()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "before");
        SaveBackup backup = service.Backup("mhws", saveDir);

        string target = Path.Combine(temp.Path, "new", "nested", "save");
        service.Restore(backup, target);

        Assert.Equal("before", File.ReadAllText(Path.Combine(target, "slot1.sav")));
    }

    [Fact]
    public void Restore_TargetInsideBackup_Throws()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        SaveBackup backup = BackupWithFile(service, temp);

        Assert.Throws<ArgumentException>(() =>
            service.Restore(backup, Path.Combine(backup.Directory, "nested")));
    }

    /// <summary>空存档目录不再允许备份；管理类用例统一先写一个存档文件。</summary>
    private static SaveBackup BackupWithFile(SaveBackupService service, TempDir temp)
    {
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");
        return service.Backup("mhws", saveDir);
    }

    [Fact]
    public void Backup_FailureRemovesPartialBackup()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "locked.sav"), "x");
        using FileStream locked = new(
            Path.Combine(saveDir, "locked.sav"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        Assert.ThrowsAny<IOException>(() => service.Backup("mhws", saveDir));
        Assert.Empty(service.ListAsync("mhws").GetAwaiter().GetResult());
    }

    [Fact]
    public void CreateSlot_CollisionAppendsSuffix()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        DateTimeOffset time = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));

        SaveBackup first = service.CreateSlot("mhws", time);
        SaveBackup second = service.CreateSlot("mhws", time);

        Assert.Equal("20260102-030405", first.Name);
        Assert.Equal("20260102-030405-1", second.Name);
        Assert.True(Directory.Exists(second.Directory));
    }

    [Fact]
    public void CreateSlot_ConcurrentCalls_GetDistinctSlots()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        DateTimeOffset time = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        const int count = 12;
        SaveBackup[] slots = new SaveBackup[count];

        // 同秒并发创建：没有锁时两个线程会先 Exists 后 Create 到同一个目录。
        Parallel.For(0, count, i => slots[i] = service.CreateSlot("mhws", time));

        Assert.Equal(count, slots.Select(slot => slot.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(slots, slot => Assert.True(Directory.Exists(slot.Directory), $"缺少备份槽目录：{slot.Directory}"));
        Assert.Equal(count, Directory.EnumerateDirectories(service.SavesRoot("mhws")).Count());
    }

    [Fact]
    public void CreateSlot_ExistingFileWithSameTimestamp_AppendsSuffix()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        DateTimeOffset time = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        string root = service.SavesRoot("mhws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "20260102-030405"), "外部程序占用了同名文件");

        SaveBackup slot = service.CreateSlot("mhws", time);

        Assert.Equal("20260102-030405-1", slot.Name);
        Assert.True(Directory.Exists(slot.Directory));
        Assert.True(File.Exists(Path.Combine(root, "20260102-030405")), "不能覆盖外部同名文件");
    }

    [Fact]
    public void CreateSlot_ExistingDirectoryAndSuffixedFile_SkipsToNextFreeSuffix()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        DateTimeOffset time = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        string root = service.SavesRoot("mhws");
        Directory.CreateDirectory(Path.Combine(root, "20260102-030405"));
        File.WriteAllText(Path.Combine(root, "20260102-030405-1"), "外部程序占用");

        SaveBackup slot = service.CreateSlot("mhws", time);

        Assert.Equal("20260102-030405-2", slot.Name);
        Assert.True(Directory.Exists(slot.Directory));
    }

    [Fact]
    public void Backup_SaveDirectoryContainsBoxRoot_ThrowsAndLeavesNoPartialBackup()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(Path.Combine(temp.Path, "box"));
        SaveBackupService service = new(paths, HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string saveDirectory = temp.Path; // HunterBox数据目录位于「存档目录」内部：备份目标会落在源目录里
        File.WriteAllText(Path.Combine(saveDirectory, "slot1.sav"), "current");

        ArgumentException ex = Assert.Throws<ArgumentException>(() => service.Backup("mhws", saveDirectory));

        Assert.Contains("源目录内", ex.Message);
        Assert.Empty(service.ListAsync("mhws").GetAwaiter().GetResult());
        Assert.Equal("current", File.ReadAllText(Path.Combine(saveDirectory, "slot1.sav")));
    }

    [Fact]
    public void Backup_SaveDirectoryIsSavesRoot_ThrowsWithoutDuplicatingExistingBackup()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(new BoxPaths(temp.Path), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        DateTimeOffset time = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        SaveBackup existing = service.CreateSlot("mhws", time);
        File.WriteAllText(Path.Combine(existing.Directory, "slot1.sav"), "x");

        // 存档目录被配置成HunterBox的 Saves/<gameId> 自己：新槽会落在源目录内部。
        Assert.Throws<ArgumentException>(() => service.Backup("mhws", service.SavesRoot("mhws")));

        SaveBackup only = Assert.Single(service.ListAsync("mhws").GetAwaiter().GetResult());
        Assert.Equal(existing.Name, only.Name);
        Assert.Equal("x", File.ReadAllText(Path.Combine(only.Directory, "slot1.sav")));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void FormatSize_FormatsBytes(long bytes, string expected) =>
        Assert.Equal(expected, SaveBackupService.FormatSize(bytes));
}
