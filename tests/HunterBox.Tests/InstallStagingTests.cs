using HunterBox.Application.Install;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>安装暂存（对应「安装 MOD」弹窗）的行为：先暂存预览，再按勾选提交。</summary>
public class InstallStagingTests
{
    private static (BoxPaths Paths, ModRepository Store, ModInstaller Installer) CreateBox(TempDir temp)
    {
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        return (paths, store, new ModInstaller(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhws"));
    }

    [Fact]
    public void Stage_ThenCommitSubset_OnlyInstallsSelectedFiles()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = CreateBox(temp);
        string zip = ZipBuilder.Create(
            temp.Path,
            "mod.zip",
            ("natives/stm/keep.tex", "keep"),
            ("natives/stm/skip.tex", "skip"),
            ("modinfo.ini", "name=选择性安装"));

        using InstallStaging staging = installer.Stage(zip);
        Assert.Equal(3, staging.Files.Count);
        Assert.Equal("选择性安装", staging.SuggestedMetadata.Name);

        InstallResult result = staging.Commit(["natives/stm/keep.tex"]);

        Assert.Equal(["natives/stm/keep.tex"], result.Record.RelativeFiles);
        Assert.Equal("mhws", result.Record.GameId);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "stm", "keep.tex")));
        Assert.False(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "stm", "skip.tex")));
        Assert.Single(store.Mods);
    }

    [Fact]
    public void Stage_DisposedWithoutCommit_InstallsNothing()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = CreateBox(temp);
        string zip = ZipBuilder.Create(temp.Path, "mod.zip", ("natives/a.tex", "a"));

        string workDir;
        using (InstallStaging staging = installer.Stage(zip))
        {
            workDir = staging.WorkDirectory;
        }

        Assert.False(Directory.Exists(workDir));
        Assert.Empty(store.Mods);
        Assert.False(Directory.Exists(paths.FilesDirectory(1)));
    }

    [Fact]
    public void Stage_CommitWithNoSelection_Throws()
    {
        using TempDir temp = TempDir.Create();
        (_, _, ModInstaller installer) = CreateBox(temp);
        using InstallStaging staging = installer.Stage(
            ZipBuilder.Create(temp.Path, "mod.zip", ("natives/a.tex", "a")));

        Assert.Throws<InvalidOperationException>(() => staging.Commit([]));
    }

    [Fact]
    public void Stage_CommitAll_InstallsEveryFile()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, _, ModInstaller installer) = CreateBox(temp);

        using InstallStaging staging = installer.Stage(
            ZipBuilder.Create(temp.Path, "mod.zip", ("natives/a.tex", "a"), ("natives/b.tex", "b")));
        InstallResult result = staging.Commit();

        Assert.Equal(2, result.Record.RelativeFiles.Count);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "a.tex")));
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "b.tex")));
    }

    [Fact]
    public void ValidateName_RequiresNonEmptyName()
    {
        using TempDir temp = TempDir.Create();
        (_, _, ModInstaller installer) = CreateBox(temp);
        using InstallStaging staging = installer.Stage(
            ZipBuilder.Create(temp.Path, "mod.zip", ("natives/a.tex", "a")));

        Assert.Equal("请填写 MOD 名称", staging.ValidateName(null));
        Assert.Equal("请填写 MOD 名称", staging.ValidateName("   "));
        Assert.Null(staging.ValidateName("正常名字"));
    }

    [Fact]
    public void ValidateName_RejectsDuplicateInSameGameOnly()
    {
        using TempDir temp = TempDir.Create();
        (_, ModRepository store, ModInstaller installer) = CreateBox(temp);
        using (InstallStaging staging1 = installer.Stage(ZipBuilder.Create(
            temp.Path,
            "first.zip",
            ("natives/a.tex", "a"),
            ("modinfo.ini", "name=重复名字"))))
        {
            staging1.Commit();
        }

        using InstallStaging staging = installer.Stage(
            ZipBuilder.Create(temp.Path, "second.zip", ("natives/b.tex", "b")));

        Assert.Equal("名称已被使用：重复名字", staging.ValidateName("重复名字"));

        ModInstaller otherGame = new(new BoxPaths(temp.Path), store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, "mhw");
        using InstallStaging otherStaging = otherGame.Stage(
            ZipBuilder.Create(temp.Path, "third.zip", ("natives/c.tex", "c")));
        Assert.Null(otherStaging.ValidateName("重复名字"));
    }

    [Fact]
    public void Stage_CommitExplicitPreview_InstallsUserSelectedImage()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, _, ModInstaller installer) = CreateBox(temp);
        string zip = ZipBuilder.Create(
            temp.Path, "mod.zip",
            ("natives/stm/a.tex", "a"),
            ("preview.png", "png"));

        using InstallStaging staging = installer.Stage(zip, null);
        InstallResult result = staging.Commit(["preview.png"]);

        Assert.Equal(["preview.png"], result.Record.RelativeFiles);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "preview.png")));
    }

    // ---- P-04：提交阶段同卷改名进暂存，消除「workDir → staging」的二次写入 ----

    [Fact]
    public void Commit_DirectorySource_MovesIntoStagingInsteadOfCopyingTwice()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = CreateBox(temp);
        string sourceDirectory = temp.Dir("source-tree");
        string sourceFile = Path.Combine(sourceDirectory, "natives", "stm", "a.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllText(sourceFile, "directory-source");

        using InstallStaging staging = installer.Stage(sourceDirectory);
        string workFile = Assert.Single(
            Directory.EnumerateFiles(staging.WorkDirectory, "a.tex", SearchOption.AllDirectories));

        InstallResult result = staging.Commit();

        Assert.False(File.Exists(workFile), "提交阶段应把暂存文件改名换入，而不是复制第二遍（P-04）");
        Assert.True(File.Exists(Path.Combine(
            paths.FilesDirectory(result.Record.Id), "natives", "stm", "a.tex")));
        Assert.Equal("directory-source", File.ReadAllText(sourceFile));
        Assert.Single(store.Mods);
    }

    [Fact]
    public void Commit_DirectorySource_LockedFileFailure_KeepsStagingRetryableAndAtomic()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store, ModInstaller installer) = CreateBox(temp);
        string sourceDirectory = temp.Dir("source-tree");
        string fileA = Path.Combine(sourceDirectory, "natives", "stm", "a.tex");
        string fileB = Path.Combine(sourceDirectory, "natives", "stm", "b.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(fileA)!);
        File.WriteAllText(fileA, "a");
        File.WriteAllText(fileB, "b");

        using InstallStaging staging = installer.Stage(sourceDirectory);
        string workFileA = Assert.Single(
            Directory.EnumerateFiles(staging.WorkDirectory, "a.tex", SearchOption.AllDirectories));
        string workFileB = Assert.Single(
            Directory.EnumerateFiles(staging.WorkDirectory, "b.tex", SearchOption.AllDirectories));

        using (FileStream hold = new(workFileB, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => staging.Commit());
        }

        // 失败必须回移已改名的文件，才能重试；也不得留下记录/半成品目录。
        Assert.True(File.Exists(workFileA), "失败后已移动的暂存文件必须回移，保证可重试");
        Assert.True(File.Exists(workFileB));
        Assert.Empty(store.Mods);
        Assert.False(Directory.Exists(paths.ModDirectory(1)));
        Assert.False(Directory.Exists(paths.FilesDirectory(1)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.StagingDirectory));

        InstallResult result = staging.Commit();

        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(result.Record.Id), "natives", "stm", "a.tex")));
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(result.Record.Id), "natives", "stm", "b.tex")));
        Assert.Equal("a", File.ReadAllText(fileA));
        Assert.Equal("b", File.ReadAllText(fileB));
    }
}
