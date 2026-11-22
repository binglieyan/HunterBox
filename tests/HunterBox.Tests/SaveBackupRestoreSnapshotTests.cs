using HunterBox.Application.Saves;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Saves;

namespace HunterBox.Tests;

/// <summary>源校验失败时不得创建还原快照，也不得改动当前存档。</summary>
[Collection("SaveBackupSizeCache")]
public class SaveBackupRestoreSnapshotTests
{
    [Fact]
    public void Restore_SourceHashMismatch_DoesNotCreateSnapshot()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        SaveBackupService service = new(paths, SystemFileSystem.Instance, new JsonBackupManifestStore());
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "current");
        SaveBackup backup = service.Backup("mhws", saveDir);
        File.WriteAllText(Path.Combine(backup.Directory, "slot1.sav"), "tampered");

        Assert.Throws<SaveBackupVerificationException>(() => service.Restore(backup, saveDir));

        string parent = Path.GetDirectoryName(Path.GetFullPath(saveDir))!;
        Assert.DoesNotContain(
            Directory.GetDirectories(parent),
            path => Path.GetFileName(path).StartsWith(".restore-undo-", StringComparison.Ordinal));
        Assert.Equal("current", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
    }
}
