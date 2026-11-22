using HunterBox.Application.Common;
using HunterBox.Infrastructure.IO;

namespace HunterBox.Tests;

/// <summary>空目录清理不得进入链接目标，也不得删除链接本身。</summary>
public class DirectoryPrunerLinkTests
{
    [DirectoryLinkFact]
    public void DeleteEmptyDirectories_DirectoryLink_KeepsLinkAndTarget()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("root");
        string external = temp.Dir("external");
        string externalEmpty = Path.Combine(external, "empty");
        Directory.CreateDirectory(externalEmpty);
        Assert.True(
            LinkTestSupport.TryCreateDirectoryLink(Path.Combine(root, "linked"), external),
            "无法创建目录链接（符号链接与 junction 均失败）");

        DirectoryPruner.DeleteEmptyDirectories(root, SystemFileSystem.Instance);

        Assert.True(Directory.Exists(externalEmpty), "链接目标内的空目录不能被删除");
        Assert.True(Directory.Exists(Path.Combine(root, "linked")));
    }
}
