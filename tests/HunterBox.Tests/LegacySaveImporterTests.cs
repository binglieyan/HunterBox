using System.Security.Cryptography;
using System.Text;
using HunterBox.Application.Saves;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Saves;

namespace HunterBox.Tests;

/// <summary> <c>Saves_&lt;appid&gt;/*.zip</c> 导入为目录备份的回归测试。</summary>
[Collection("SaveBackupSizeCache")]
public class LegacySaveImporterTests
{
    /// <summary>用注入的文件系统与存档服务装配导入器，不触碰真实游戏目录。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <returns>待测导入器。</returns>
    private static SharpCompressSaveImporter NewImporter(BoxPaths paths) =>
        new(paths, SystemFileSystem.Instance, new SaveBackupService(paths, SystemFileSystem.Instance, new JsonBackupManifestStore()));

    [Fact]
    public void LegacySaveDirectory_UsesAppIdSuffix_ExceptRise()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "Saves_2246340"), SharpCompressSaveImporter.LegacySaveDirectory(paths, TestGames.Wilds));
        Assert.Equal(Path.Combine(temp.Path, "Saves"), SharpCompressSaveImporter.LegacySaveDirectory(paths, TestGames.Rise));
    }

    [Fact]
    public void List_MissingDirectory_ReturnsEmpty()
    {
        using TempDir temp = TempDir.Create();
        SharpCompressSaveImporter importer = NewImporter(new BoxPaths(temp.Path));

        Assert.Empty(importer.List(TestGames.Wilds));
    }

    [Fact]
    public void List_ReturnsZipsNewestFirst_AndOnlyTopLevel()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        SharpCompressSaveImporter importer = NewImporter(paths);
        string legacy = SharpCompressSaveImporter.LegacySaveDirectory(paths, TestGames.Wilds);
        string older = ZipBuilder.CreateAt(Path.Combine(legacy, "旧.zip"), ("a.sav", "1"));
        string newer = ZipBuilder.CreateAt(Path.Combine(legacy, "新.zip"), ("b.sav", "2"));
        File.SetLastWriteTime(older, new DateTime(2026, 1, 1, 10, 0, 0));
        File.SetLastWriteTime(newer, new DateTime(2026, 2, 1, 10, 0, 0));
        ZipBuilder.CreateAt(Path.Combine(legacy, "sub", "忽略.zip"), ("c.sav", "3"));
        File.WriteAllText(Path.Combine(legacy, "不是zip.txt"), "x");

        IReadOnlyList<LegacySaveArchive> archives = importer.List(TestGames.Wilds);

        Assert.Equal(2, archives.Count);
        Assert.Equal("新.zip", archives[0].FileName);
        Assert.Equal("旧.zip", archives[1].FileName);
        Assert.True(archives[0].SizeBytes > 0);
    }

    [Fact]
    public void List_Rise_ReadsTopLevelZipsFromSavesDirectory()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        SharpCompressSaveImporter importer = NewImporter(paths);
        ZipBuilder.CreateAt(Path.Combine(temp.Path, "Saves", "崛起备份.zip"), ("slot1.sav", "1"));
        // 新格式目录备份（Saves/mhr/<时间戳>）不应被当成旧 zip。
        Directory.CreateDirectory(Path.Combine(temp.Path, "Saves", "mhr", "20260101-000000"));

        LegacySaveArchive archive = Assert.Single(importer.List(TestGames.Rise));

        Assert.Equal("崛起备份.zip", archive.FileName);
    }

    [Fact]
    public void Import_ExtractsDirectoryBackup_AndAppearsInList()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        SharpCompressSaveImporter importer = NewImporter(paths);
        SaveBackupService saves = new(paths, HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string zip = ZipBuilder.CreateAt(
            Path.Combine(SharpCompressSaveImporter.LegacySaveDirectory(paths, TestGames.Wilds), "存档.zip"),
            ("slot1.sav", "save-1"),
            ("sub/deep.sav", "deep"));
        File.SetLastWriteTime(zip, new DateTime(2026, 3, 4, 5, 6, 7));
        LegacySaveArchive source = importer.List(TestGames.Wilds).Single();

        SaveBackup imported = importer.Import(TestGames.Wilds, source);

        Assert.Equal("20260304-050607", imported.Name);
        Assert.True(Directory.Exists(imported.Directory));
        Assert.Equal("save-1", File.ReadAllText(Path.Combine(imported.Directory, "slot1.sav")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(imported.Directory, "sub", "deep.sav")));
        SaveBackup listed = Assert.Single(saves.ListAsync(TestGames.Wilds.Id).GetAwaiter().GetResult());
        Assert.Equal(imported.Name, listed.Name);
        Assert.True(listed.SizeBytes > 0);
        Assert.True(saves.Restore(imported, Path.Combine(temp.Path, "restored")).IsValid, "导入的旧存档也应生成哈希清单");
        Assert.True(File.Exists(zip), "导入不应移动/删除旧 zip");
    }

    [Fact]
    public void Import_Twice_SecondGetsSuffixAndBothVisible()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        SharpCompressSaveImporter importer = NewImporter(paths);
        SaveBackupService saves = new(paths, HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string zip = ZipBuilder.CreateAt(
            Path.Combine(SharpCompressSaveImporter.LegacySaveDirectory(paths, TestGames.Wilds), "存档.zip"),
            ("slot1.sav", "x"));
        DateTime set = new(2026, 5, 6, 7, 8, 9);
        File.SetLastWriteTime(zip, set);
        LegacySaveArchive archive = Assert.Single(importer.List(TestGames.Wilds));

        SaveBackup first = importer.Import(TestGames.Wilds, archive);
        SaveBackup second = importer.Import(TestGames.Wilds, archive);

        Assert.Equal("20260506-070809", first.Name);
        Assert.Equal("20260506-070809-1", second.Name);
        Assert.Equal(2, saves.ListAsync(TestGames.Wilds.Id).GetAwaiter().GetResult().Count);
    }

    [Fact]
    public void Import_RenamedThenListShowsCustomName()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        SharpCompressSaveImporter importer = NewImporter(paths);
        SaveBackupService saves = new(paths, HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        ZipBuilder.CreateAt(Path.Combine(SharpCompressSaveImporter.LegacySaveDirectory(paths, TestGames.Wilds), "a.zip"), ("a.sav", "x"));
        SaveBackup imported = importer.Import(TestGames.Wilds, importer.List(TestGames.Wilds)[0]);

        SaveBackup renamed = saves.Rename(imported, "导入后的备份");

        Assert.Equal("导入后的备份", Assert.Single(saves.ListAsync(TestGames.Wilds.Id).GetAwaiter().GetResult()).Name);
        Assert.True(Directory.Exists(renamed.Directory));
    }

    [Fact]
    public void Import_ZipSlipEntry_ThrowsAndLeavesNoBackup()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        SharpCompressSaveImporter importer = NewImporter(paths);
        SaveBackupService saves = new(paths, HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string zip = Path.Combine(SharpCompressSaveImporter.LegacySaveDirectory(paths, TestGames.Wilds), "evil.zip");
        ZipBuilder.CreateAt(zip, ("../evil.sav", "bad"));
        LegacySaveArchive archive = Assert.Single(importer.List(TestGames.Wilds));

        Assert.Throws<InvalidDataException>(() => importer.Import(TestGames.Wilds, archive));
        Assert.False(File.Exists(Path.Combine(temp.Path, "evil.sav")));
        Assert.Empty(saves.ListAsync(TestGames.Wilds.Id).GetAwaiter().GetResult());
    }

    [Fact]
    public void Import_EmptyZip_Throws()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        SharpCompressSaveImporter importer = NewImporter(paths);
        SaveBackupService saves = new(paths, HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string zip = Path.Combine(SharpCompressSaveImporter.LegacySaveDirectory(paths, TestGames.Wilds), "empty.zip");
        ZipBuilder.CreateAt(zip);
        LegacySaveArchive archive = Assert.Single(importer.List(TestGames.Wilds));

        Assert.Throws<InvalidDataException>(() => importer.Import(TestGames.Wilds, archive));
        Assert.Empty(saves.ListAsync(TestGames.Wilds.Id).GetAwaiter().GetResult());
    }

    [Fact]
    public void Import_CorruptZip_ThrowsInvalidDataException()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        SharpCompressSaveImporter importer = NewImporter(paths);
        SaveBackupService saves = new(paths, HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        string legacy = SharpCompressSaveImporter.LegacySaveDirectory(paths, TestGames.Wilds);
        Directory.CreateDirectory(legacy);
        string zip = Path.Combine(legacy, "broken.zip");
        File.WriteAllText(zip, "这不是一个 zip 文件");
        LegacySaveArchive archive = Assert.Single(importer.List(TestGames.Wilds));

        Assert.Throws<InvalidDataException>(() => importer.Import(TestGames.Wilds, archive));
        Assert.Empty(saves.ListAsync(TestGames.Wilds.Id).GetAwaiter().GetResult());
    }

    [Fact]
    public void Import_MissingFile_ThrowsFileNotFound()
    {
        using TempDir temp = TempDir.Create();
        SharpCompressSaveImporter importer = NewImporter(new BoxPaths(temp.Path));
        LegacySaveArchive archive = new(
            Path.Combine(temp.Path, "Saves_2246340", "nope.zip"),
            "nope.zip",
            DateTimeOffset.Now,
            0);

        Assert.Throws<FileNotFoundException>(() => importer.Import(TestGames.Wilds, archive));
    }

    [Fact]
    public void Import_PreCanceled_ThrowsAndLeavesNoBackup()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        SharpCompressSaveImporter importer = NewImporter(paths);
        SaveBackupService saves = new(paths, HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Saves.JsonBackupManifestStore());
        ZipBuilder.CreateAt(
            Path.Combine(SharpCompressSaveImporter.LegacySaveDirectory(paths, TestGames.Wilds), "a.zip"),
            ("a.sav", "x"));
        LegacySaveArchive archive = Assert.Single(importer.List(TestGames.Wilds));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        // 取消必须在创建备份槽之前生效，不能在列表里留下空备份。
        Assert.Throws<OperationCanceledException>(
            () => importer.Import(TestGames.Wilds, archive, cancellation.Token));
        Assert.Empty(saves.ListAsync(TestGames.Wilds.Id).GetAwaiter().GetResult());
    }

    [Fact]
    public void ExtractZip_DeclaredEntrySizeExceedsLimit_ThrowsWithoutWritingFile()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "a.zip", ("a.sav", "0123456789"));
        string destination = Path.Combine(temp.Path, "out");

        // 按中央目录声明的尺寸预检，超限立即拒绝。
        Assert.Throws<InvalidDataException>(() => SharpCompressSaveImporter.ExtractZipWithHashes(
            zip, destination, password: null, maxEntryBytes: 1, maxTotalBytes: 1024));
        Assert.False(File.Exists(Path.Combine(destination, "a.sav")));
    }

    [Fact]
    public void ExtractZip_TotalSizeExceedsLimit_Throws()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "a.zip", ("a.sav", "0123456789"));
        string destination = Path.Combine(temp.Path, "out");

        Assert.Throws<InvalidDataException>(() => SharpCompressSaveImporter.ExtractZipWithHashes(
            zip, destination, password: null, maxEntryBytes: 1024, maxTotalBytes: 5));
    }

    [Fact]
    public void ExtractZip_CanceledToken_StopsAtEntryBoundary()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "a.zip", ("a.sav", "x"));
        string destination = Path.Combine(temp.Path, "out");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SharpCompressSaveImporter.ExtractZipWithHashes(
            zip, destination, password: null, cancellationToken: cancellation.Token));
        Assert.Empty(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories));
    }

    /// <summary>解压时同步计算 SHA-256，清单条目不依赖再次读盘。</summary>
    [Fact]
    public void ExtractZipWithHashes_ComputesEntryHashWhileWriting()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "a.zip", ("a.sav", "save-1"), ("sub/b.sav", "deep"));
        string destination = Path.Combine(temp.Path, "out");

        (int count, List<BackupManifestEntry> entries) = SharpCompressSaveImporter.ExtractZipWithHashes(
            zip,
            destination,
            password: null);

        Assert.Equal(2, count);
        BackupManifestEntry first = Assert.Single(entries, entry => entry.Path == "a.sav");
        Assert.Equal(6, first.Size);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("save-1"))),
            first.Sha256);
        Assert.Contains(entries, entry => entry.Path == "sub/b.sav" && entry.Size == 4);
    }

    /// <summary>传入预计算清单时收尾不再读取备份文件（文件被独占也能成功）。</summary>
    [Fact]
    public void FinalizeImportedBackup_WithPrecomputedEntries_DoesNotReReadFiles()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = new(
            new BoxPaths(temp.Path),
            SystemFileSystem.Instance,
            new JsonBackupManifestStore());
        SaveBackup slot = service.CreateSlot("mhws", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        string file = Path.Combine(slot.Directory, "slot1.sav");
        File.WriteAllText(file, "save-1");
        BackupManifestEntry entry = new(
            "slot1.sav",
            6,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("save-1"))));

        using FileStream locked = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        SaveBackup finalized = service.FinalizeImportedBackup(slot, [entry]);

        Assert.Equal(6, finalized.SizeBytes);
        Assert.True(File.Exists(Path.Combine(slot.Directory, SaveBackupManifest.FileName)));
    }

    [Theory]
    [InlineData("存档 解压码abc.zip", "abc")]
    [InlineData("存档_密码：xyz.zip", "xyz")]
    [InlineData("密码 1234(群内获取).zip", "1234")]
    [InlineData("普通备份.zip", null)]
    [InlineData("解压码.zip", null)]
    [InlineData("", null)]
    public void PasswordFromFileName_MatchesLegacyRule(string fileName, string? expected) =>
        Assert.Equal(expected, SharpCompressSaveImporter.PasswordFromFileName(fileName));
}
