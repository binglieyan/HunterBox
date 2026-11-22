using HunterBox.Application.Install;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>安装提交阶段的取消必须保持原子性（无记录、无半成品文件）。</summary>
public class InstallStagingCancellationTests
{
    private const string FileA = "natives/stm/a.tex";
    private const string FileB = "natives/stm/b.tex";

    private static (BoxPaths Paths, ModRepository Repository, ModInstaller Installer) CreateBox(TempDir temp)
    {
        (BoxPaths paths, ModRepository repository) = TestBox.OpenRepository(temp);
        return (paths, repository, new ModInstaller(paths, repository, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws"));
    }

    [Fact]
    public void Commit_PreCanceledToken_LeavesNoRecordOrFiles_AndStagingStillUsable()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repository, ModInstaller installer) = CreateBox(temp);
        string zip = ZipBuilder.Create(temp.Path, "mod.zip", (FileA, "a"), (FileB, "b"));
        using InstallStaging staging = installer.Stage(zip);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => staging.Commit(cancellationToken: cts.Token));

        Assert.Empty(repository.Mods);
        Assert.False(Directory.Exists(paths.FilesDirectory(1)), "取消不得创建 MOD 目录");
        Assert.False(Directory.Exists(paths.FilesDirectory(0)));
        Assert.True(Directory.Exists(staging.WorkDirectory), "取消不应提前销毁暂存目录");

        // 取消不是失败状态：同一个暂存可以重新提交并完成安装。
        InstallResult result = staging.Commit();
        Assert.Single(repository.Mods);
        Assert.Equal(result.Record.Id, repository.Mods[0].Id);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(result.Record.Id),
            FileA.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(Directory.Exists(paths.StagingDirectory) && Directory.EnumerateDirectories(paths.StagingDirectory).Any(),
            "提交成功后暂存目录必须清理");
    }

    [Fact]
    public void Commit_ReinstallPreCanceledToken_KeepsOldFilesAndRecord()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repository, ModInstaller installer) = CreateBox(temp);
        InstallResult original;
        using (InstallStaging staging1 = installer.Stage(
            ZipBuilder.Create(temp.Path, "old.zip", (FileA, "old"))))
        {
            original = staging1.Commit();
        }
        string filesDirectory = paths.FilesDirectory(original.Record.Id);

        using InstallStaging staging = installer.StageReinstall(
            original.Record.Id,
            ZipBuilder.Create(temp.Path, "new.zip", (FileB, "new")));
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => staging.Commit(cancellationToken: cts.Token));

        Assert.True(File.Exists(Path.Combine(filesDirectory, FileA.Replace('/', Path.DirectorySeparatorChar))),
            "取消重装后旧文件必须原样保留");
        Assert.Equal(original.Record.Id, Assert.Single(repository.Mods).Id);
        Assert.Equal([FileA], Assert.Single(repository.Mods).RelativeFiles);
    }
}
