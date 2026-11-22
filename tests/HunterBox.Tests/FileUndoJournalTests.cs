using HunterBox.Application.Common;

namespace HunterBox.Tests;

/// <summary>
/// A-07：<see cref="FileUndoJournal"/> 的文件事务脚手架语义——
/// 备份还原、新建删除、移入撤销目录后移回、单项回滚失败继续、Dispose 清理撤销目录。
/// </summary>
/// <remarks>
/// 这里只验证 helper 自身的文件语义；<see cref="MountServiceTests"/>、
/// <see cref="PakMountServiceTests"/>、<see cref="ActivationSafetyTests"/> 覆盖两个挂载服务
/// 在真实启用/禁用失败路径上通过该 helper 回滚的行为。
/// </remarks>
public class FileUndoJournalTests
{
    [Fact]
    public void BackupFile_Rollback_RestoresOriginalContent()
    {
        using TempDir temp = TempDir.Create();
        string destination = temp.File("game/a.txt", "original");
        string undoRoot = Path.Combine(temp.Path, "undo");
        using FileUndoJournal journal = new(undoRoot, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        // 覆盖前登记：目标文件被复制进撤销目录；之后用新内容覆盖。
        journal.BackupFile(destination);
        Assert.True(Directory.Exists(undoRoot), "登记前像时应创建撤销目录");
        File.WriteAllText(destination, "modified");

        List<string> warnings = [];
        journal.Rollback(warnings);

        Assert.Empty(warnings);
        Assert.Equal("original", File.ReadAllText(destination));
    }

    [Fact]
    public void Rollback_CallbackThrows_ContinuesWithRemainingEntries()
    {
        using TempDir temp = TempDir.Create();
        string first = temp.File("game/first.txt", "1");
        string second = temp.File("game/second.txt", "2");
        bool thrown = false;
        using FileUndoJournal journal = new(
            Path.Combine(temp.Path, "undo"),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            _ =>
            {
                if (!thrown)
                {
                    thrown = true;
                    throw new InvalidOperationException("模拟回调失败");
                }
            });
        journal.RecordCreated(first);
        journal.RecordCreated(second);

        List<string> warnings = [];
        journal.Rollback(warnings);

        // 回调异常不能让回滚中断：两条新建文件都必须删除，失败项转成告警。
        Assert.Single(warnings);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void RecordCreated_Rollback_DeletesNewFile()
    {
        using TempDir temp = TempDir.Create();
        string destination = Path.Combine(temp.Path, "game", "new.txt");
        using FileUndoJournal journal = new(Path.Combine(temp.Path, "undo"), HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        // 新建文件：本来不存在，回滚时删除。
        journal.RecordCreated(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "brand-new");

        List<string> warnings = [];
        journal.Rollback(warnings);

        Assert.Empty(warnings);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void MoveAside_Rollback_MovesFileBack()
    {
        using TempDir temp = TempDir.Create();
        string original = temp.File("pak_mods/cool.pak", "pak-bytes");
        string undoRoot = Path.Combine(temp.Path, "pak_mods", ".hb-undo-test");
        using FileUndoJournal journal = new(undoRoot, HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        journal.MoveAside(original);

        Assert.False(File.Exists(original), "移入撤销目录后原路径不应再有文件");
        Assert.True(Directory.Exists(undoRoot));

        List<string> warnings = [];
        journal.Rollback(warnings);

        Assert.Empty(warnings);
        Assert.Equal("pak-bytes", File.ReadAllText(original));
    }

    [Fact]
    public void RecordRestore_Rollback_RestoresFromExistingPreImageAndKeepsIt()
    {
        using TempDir temp = TempDir.Create();
        string destination = temp.File("game/a.txt", "game-original");
        string backup = temp.File("backup/a.txt", "game-original");
        using FileUndoJournal journal = new(Path.Combine(temp.Path, "undo"), HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        // 覆盖前已把游戏原文件落盘到持久备份：回滚从该备份还原，备份本身保留。
        journal.RecordRestore(destination, backup);
        File.WriteAllText(destination, "mod-content");

        List<string> warnings = [];
        journal.Rollback(warnings);

        Assert.Empty(warnings);
        Assert.Equal("game-original", File.ReadAllText(destination));
        Assert.Equal("game-original", File.ReadAllText(backup));
    }

    [Fact]
    public void RecordRestore_WithDeletePreImageOnRollback_RemovesNewBackupAfterRestore()
    {
        using TempDir temp = TempDir.Create();
        string destination = temp.File("game/a.txt", "game-original");
        string backup = temp.File("backup/a.txt", "game-original");
        using FileUndoJournal journal = new(Path.Combine(temp.Path, "undo"), HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        // 备份是本次启用新建的：回滚还原后要删除它，回到「没有备份」的启用前状态。
        journal.RecordRestore(destination, backup, deletePreImageOnRollback: true);
        File.WriteAllText(destination, "mod-content");

        List<string> warnings = [];
        journal.Rollback(warnings);

        Assert.Empty(warnings);
        Assert.Equal("game-original", File.ReadAllText(destination));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void Rollback_OneEntryFails_ContinuesWithRemainingEntriesAndWarns()
    {
        using TempDir temp = TempDir.Create();
        string first = temp.File("a.txt", "A0");
        string second = temp.File("b.txt", "B0");
        using FileUndoJournal journal = new(Path.Combine(temp.Path, "undo"), HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        journal.BackupFile(first);
        journal.BackupFile(second);
        File.WriteAllText(first, "A1");
        File.WriteAllText(second, "B1");

        List<string> warnings = [];
        // 独占打开后一个文件（逆序回滚时先处理它）：还原复制失败只告警，不能中断前面的条目。
        using (new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            journal.Rollback(warnings);
        }

        string warning = Assert.Single(warnings);
        Assert.Contains("回滚文件失败", warning);
        Assert.Contains(second, warning);
        Assert.Equal("A0", File.ReadAllText(first));
        Assert.Equal("B1", File.ReadAllText(second));

        // N-03：回滚有失败项时，Dispose 必须保留撤销目录（里面可能是唯一前像）。
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "undo")), "回滚失败后撤销目录不能删除");
    }

    /// <summary>N-03：目标被外部重建导致 MoveAside 无法移回时，必须告警并保留撤销目录。</summary>
    [Fact]
    public void Rollback_MoveAsideTargetRecreated_WarnsAndPreservesUndoDirectory()
    {
        using TempDir temp = TempDir.Create();
        string original = temp.File("pak_mods/cool.pak", "pak-bytes");
        string undoRoot = Path.Combine(temp.Path, "pak_mods", ".hb-undo-recreated");
        using (FileUndoJournal journal = new(undoRoot, HunterBox.Infrastructure.IO.SystemFileSystem.Instance))
        {
            journal.MoveAside(original);
            File.WriteAllText(original, "created-again");

            List<string> warnings = [];
            journal.Rollback(warnings);

            string warning = Assert.Single(warnings);
            Assert.Contains("重新创建", warning);
            Assert.Contains(undoRoot, warning);
            Assert.Equal("created-again", File.ReadAllText(original));
        }

        Assert.True(Directory.Exists(undoRoot), "无法移回的前像必须保留");
        Assert.Contains(
            Directory.EnumerateFiles(undoRoot),
            file => File.ReadAllText(file) == "pak-bytes");
    }

    [Fact]
    public void Commit_ThenDispose_CleansUndoRoot()
    {
        using TempDir temp = TempDir.Create();
        string destination = temp.File("game/a.txt", "original");
        string undoRoot = Path.Combine(temp.Path, "undo-clean");

        using (FileUndoJournal journal = new(undoRoot, HunterBox.Infrastructure.IO.SystemFileSystem.Instance))
        {
            journal.BackupFile(destination);
            File.WriteAllText(destination, "modified");
            journal.Commit();
            Assert.True(Directory.Exists(undoRoot));
        }

        Assert.False(Directory.Exists(undoRoot), "提交成功后 Dispose 应清理撤销目录");
        Assert.Equal("modified", File.ReadAllText(destination));
    }

    /// <summary>既未提交也未回滚（异常退出）时保留唯一前像。</summary>
    [Fact]
    public void Dispose_WithoutCommitOrRollback_PreservesUndoRoot()
    {
        using TempDir temp = TempDir.Create();
        string destination = temp.File("game/a.txt", "original");
        string undoRoot = Path.Combine(temp.Path, "undo-abandoned");

        using (FileUndoJournal journal = new(undoRoot, HunterBox.Infrastructure.IO.SystemFileSystem.Instance))
        {
            journal.BackupFile(destination);
            File.WriteAllText(destination, "modified");
        }

        Assert.True(Directory.Exists(undoRoot), "未提交/未回滚的撤销目录必须保留");
        Assert.Contains(
            Directory.EnumerateFiles(undoRoot),
            file => File.ReadAllText(file) == "original");
    }

    [Fact]
    public void Dispose_WithoutJournalEntries_DoesNotCreateUndoRoot()
    {
        using TempDir temp = TempDir.Create();
        string undoRoot = Path.Combine(temp.Path, "undo-lazy");

        using (FileUndoJournal journal = new(undoRoot, HunterBox.Infrastructure.IO.SystemFileSystem.Instance))
        {
            journal.RecordCreated(Path.Combine(temp.Path, "not-created.txt"));
        }

        Assert.False(Directory.Exists(undoRoot), "只需要删除记录的日志不必创建撤销目录");
    }
}
