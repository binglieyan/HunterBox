using HunterBox.Application.Install;
using HunterBox.Application.Saves;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Saves;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>备份、文件夹安装与还原不得复制或写入符号链接/junction 的目标。</summary>
[Collection("SaveBackupSizeCache")]
public class SaveBackupLinkSafetyTests
{
    private static SaveBackupService CreateService(TempDir temp) =>
        new(new BoxPaths(temp.Path), SystemFileSystem.Instance, new JsonBackupManifestStore());

    /// <summary>无链接环境下也验证安全枚举本身：真实文件一个不少、普通目录不会被误判为链接。</summary>
    [Fact]
    public void SafeFileTree_PlainDirectory_EnumeratesAllFilesAndReportsNoLink()
    {
        using TempDir temp = TempDir.Create();
        temp.File("tree/a.sav", "a");
        temp.File("tree/sub/b.sav", "b");
        temp.File("tree/sub/deep/c.sav", "c");

        string[] files = HunterBox.Application.Common.SafeFileTree
            .EnumerateFiles(SystemFileSystem.Instance, Path.Combine(temp.Path, "tree"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(3, files.Length);
        Assert.EndsWith("a.sav", files[0], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("b.sav", files[1], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("c.sav", files[2], StringComparison.OrdinalIgnoreCase);
        Assert.False(HunterBox.Application.Common.SafeFileTree
            .ContainsReparsePoint(SystemFileSystem.Instance, Path.Combine(temp.Path, "tree")));
    }

    /// <summary>不依赖系统链接权限：用注入的目录树验证「带 ReparsePoint 的条目被跳过且不进入链接目录」。</summary>
    [Fact]
    public void SafeFileTree_EntryMarkedAsReparsePoint_IsSkippedWithoutEnteringLink()
    {
        string root = Path.Combine(Path.GetTempPath(), "hb-safe-tree-root");
        string realFile = Path.Combine(root, "a.sav");
        string realDirectory = Path.Combine(root, "sub");
        string nestedFile = Path.Combine(realDirectory, "b.sav");
        string linkDirectory = Path.Combine(root, "linked");
        string linkFile = Path.Combine(root, "linked.sav");

        Dictionary<string, string[]> entries = new(StringComparer.OrdinalIgnoreCase)
        {
            [root] = [realFile, realDirectory, linkDirectory, linkFile],
            [realDirectory] = [nestedFile],
        };

        string[] Enumerate(string directory) => directory.Equals(linkDirectory, StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException("不应进入链接目录")
            : entries.TryGetValue(directory, out string[]? list) ? list : [];

        static FileAttributes Attributes(string path) => Path.GetFileName(path) switch
        {
            "linked" => FileAttributes.Directory | FileAttributes.ReparsePoint,
            "linked.sav" => FileAttributes.ReparsePoint,
            "sub" => FileAttributes.Directory,
            _ => FileAttributes.Normal,
        };

        string[] files = HunterBox.Application.Common.SafeFileTree
            .EnumerateFiles(root, Enumerate, Attributes)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal([realFile, nestedFile], files);
        Assert.True(HunterBox.Application.Common.SafeFileTree.ContainsReparsePoint(root, Enumerate, Attributes));
    }

    /// <summary>
    /// 带 ReparsePoint 但不是链接的 OneDrive 占位文件必须按普通文件处理，
    /// 否则安装/迁移会漏拷并生成空包。
    /// </summary>
    [Fact]
    public void SafeFileTree_ReparsePointWithoutLinkTarget_IsTreatedAsRegularEntry()
    {
        string root = Path.Combine(Path.GetTempPath(), "hb-safe-tree-cloud");
        string cloudDirectory = Path.Combine(root, "cloud-dir");
        string cloudFile = Path.Combine(root, "cloud.sav");
        string nestedFile = Path.Combine(cloudDirectory, "nested.sav");

        Dictionary<string, string[]> entries = new(StringComparer.OrdinalIgnoreCase)
        {
            [root] = [cloudDirectory, cloudFile],
            [cloudDirectory] = [nestedFile],
        };

        string[] Enumerate(string directory) =>
            entries.TryGetValue(directory, out string[]? list) ? list : [];

        static FileAttributes Attributes(string path) =>
            path.EndsWith("cloud-dir", StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.ReparsePoint;

        static bool NotLink(string path, FileAttributes attributes) => false;

        string[] files = HunterBox.Application.Common.SafeFileTree
            .EnumerateFiles(root, Enumerate, Attributes, NotLink)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(2, files.Length);
        Assert.Contains(cloudFile, files);
        Assert.Contains(nestedFile, files);
        Assert.False(
            HunterBox.Application.Common.SafeFileTree.ContainsReparsePoint(root, Enumerate, Attributes, NotLink));
    }

    [DirectoryLinkFact]
    public void Backup_SourceContainsDirectoryLink_DoesNotCopyLinkTarget()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = CreateService(temp);
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");
        string external = temp.Dir("external");
        File.WriteAllText(Path.Combine(external, "secret.sav"), "secret");
        Assert.True(
            LinkTestSupport.TryCreateDirectoryLink(Path.Combine(saveDir, "linked"), external),
            "无法创建目录链接（符号链接与 junction 均失败）");

        SaveBackup backup = service.Backup("mhws", saveDir);

        Assert.True(File.Exists(Path.Combine(backup.Directory, "slot1.sav")));
        Assert.False(File.Exists(Path.Combine(backup.Directory, "linked", "secret.sav")));
        string manifest = File.ReadAllText(
            Path.Combine(backup.Directory, SaveBackupManifest.FileName));
        Assert.DoesNotContain("secret", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [FileSymlinkFact]
    public void Backup_SourceContainsFileLink_DoesNotCopyLinkTarget()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = CreateService(temp);
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "save-1");
        string target = Path.Combine(temp.Path, "external.sav");
        File.WriteAllText(target, "external");
        Assert.True(
            LinkTestSupport.TryCreateFileLink(Path.Combine(saveDir, "linked.sav"), target),
            "文件符号链接创建失败");

        SaveBackup backup = service.Backup("mhws", saveDir);

        Assert.True(File.Exists(Path.Combine(backup.Directory, "slot1.sav")));
        Assert.False(File.Exists(Path.Combine(backup.Directory, "linked.sav")));
    }

    [DirectoryLinkFact]
    public void Restore_TargetContainsDirectoryLink_RejectsAndWritesNothingOutside()
    {
        using TempDir temp = TempDir.Create();
        SaveBackupService service = CreateService(temp);
        string saveDir = temp.Dir("save");
        File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "backup-content");
        SaveBackup backup = service.Backup("mhws", saveDir);

        string target = temp.Dir("target");
        File.WriteAllText(Path.Combine(target, "slot1.sav"), "current");
        string external = temp.Dir("external");
        File.WriteAllText(Path.Combine(external, "keep.sav"), "keep");
        Assert.True(
            LinkTestSupport.TryCreateDirectoryLink(Path.Combine(target, "linked"), external),
            "无法创建目录链接（符号链接与 junction 均失败）");

        IOException ex = Assert.Throws<IOException>(() => service.Restore(backup, target));

        Assert.Contains("链接", ex.Message);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(external, "keep.sav")));
        Assert.False(File.Exists(Path.Combine(external, "slot1.sav")), "绝不能写进链接目标");
        Assert.Equal("current", File.ReadAllText(Path.Combine(target, "slot1.sav")));
    }

    [DirectoryLinkFact]
    public void Stage_FolderWithDirectoryLink_DoesNotCopyLinkTarget()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repository) = TestBox.OpenRepository(temp);
        ModInstaller installer = new(paths, repository, new ArchiveExtractor(), SystemFileSystem.Instance, "mhws");
        temp.File("mod/natives/a.tex", "a");
        string external = temp.Dir("external");
        File.WriteAllText(Path.Combine(external, "secret.tex"), "secret");
        Assert.True(
            LinkTestSupport.TryCreateDirectoryLink(Path.Combine(temp.Path, "mod", "natives", "linked"), external),
            "无法创建目录链接（符号链接与 junction 均失败）");

        InstallResult result;
        using (InstallStaging staging = installer.Stage(Path.Combine(temp.Path, "mod")))
        {
            result = staging.Commit();
        }

        string[] installed = [.. Directory.EnumerateFiles(
            paths.FilesDirectory(result.Record.Id), "*", SearchOption.AllDirectories)];
        Assert.Contains(installed, file => file.EndsWith("a.tex", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(installed, file => file.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }
}
