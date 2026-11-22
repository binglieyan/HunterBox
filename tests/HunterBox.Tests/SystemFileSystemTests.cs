using System.Runtime.Versioning;
using HunterBox.Infrastructure.IO;

namespace HunterBox.Tests;

/// <summary>SystemFileSystem 删除语义：空目录删除必须非递归，绝不吞掉判空后新增的文件。</summary>
public class SystemFileSystemTests
{
    /// <summary>端口必须能区分真正的链接与普通文件，并复用已读属性。</summary>
    [DirectoryLinkFact]
    public void IsReparseLink_JunctionAndPlainFile_DistinguishesLinks()
    {
        using TempDir temp = TempDir.Create();
        string target = temp.Dir("target");
        string link = Path.Combine(temp.Path, "link");
        Assert.True(
            LinkTestSupport.TryCreateDirectoryLink(link, target),
            "无法创建目录链接，用例前置条件不成立");
        string plain = temp.File("plain.txt", "x");

        Assert.True(SystemFileSystem.Instance.IsReparseLink(link, File.GetAttributes(link)));
        Assert.False(SystemFileSystem.Instance.IsReparseLink(plain, File.GetAttributes(plain)));
    }

    [Fact]
    public void DeleteEmptyDirectory_EmptyDirectory_DeletesDirectory()
    {
        using TempDir temp = TempDir.Create();
        string directory = temp.Dir("empty");
        Directory.CreateDirectory(directory);

        SystemFileSystem.Instance.DeleteEmptyDirectory(directory);

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void DeleteEmptyDirectory_FileWrittenAfterEmptyCheck_ThrowsAndKeepsFile()
    {
        using TempDir temp = TempDir.Create();
        string directory = temp.Dir("raced");
        Directory.CreateDirectory(directory);
        Assert.Empty(SystemFileSystem.Instance.EnumerateFileSystemEntries(directory));

        // 模拟「判空之后、删除之前」游戏或用户写入的新文件。
        string written = Path.Combine(directory, "late.txt");
        File.WriteAllText(written, "x");

        Assert.ThrowsAny<IOException>(() => SystemFileSystem.Instance.DeleteEmptyDirectory(directory));
        Assert.True(File.Exists(written));
        Assert.True(Directory.Exists(directory));
    }

    /// <summary>一次查询返回目录存在性与最后写入时间；缺失返回 false。</summary>
    [Fact]
    public void GetDirectoryExistenceAndLastWriteTimeUtc_ExistingAndMissingDirectories()
    {
        using TempDir temp = TempDir.Create();
        string directory = temp.Dir("probe");
        Directory.CreateDirectory(directory);
        DateTime expected = DateTime.UtcNow.AddHours(-1);
        Directory.SetLastWriteTimeUtc(directory, expected);

        (bool exists, DateTime lastWrite) = SystemFileSystem.Instance.GetDirectoryExistenceAndLastWriteTimeUtc(directory);
        (bool missing, DateTime missingWrite) = SystemFileSystem.Instance.GetDirectoryExistenceAndLastWriteTimeUtc(
            Path.Combine(temp.Path, "missing"));

        Assert.True(exists);
        Assert.Equal(expected, lastWrite, TimeSpan.FromSeconds(1));
        Assert.False(missing);
        Assert.Equal(default, missingWrite);
    }

    /// <summary>路径是文件（不是目录）时按不存在处理。</summary>
    [Fact]
    public void GetDirectoryExistenceAndLastWriteTimeUtc_FilePath_ReturnsFalse()
    {
        using TempDir temp = TempDir.Create();
        string file = temp.File("not-a-dir.txt", "x");

        (bool exists, _) = SystemFileSystem.Instance.GetDirectoryExistenceAndLastWriteTimeUtc(file);

        Assert.False(exists);
    }

    /// <summary>无权限读取目录属性时抛 <see cref="UnauthorizedAccessException"/>，不伪装成缺失。</summary>
    [DirectoryAttributeAclFact]
    [SupportedOSPlatform("windows")]
    public void GetDirectoryExistenceAndLastWriteTimeUtc_DeniedDirectory_ThrowsUnauthorized()
    {
        using TempDir temp = TempDir.Create();
        string blocked = temp.Dir("blocked");
        using DirectoryReadAttributesDeny deny = DirectoryReadAttributesDeny.Apply(blocked);

        Assert.Throws<UnauthorizedAccessException>(
            () => SystemFileSystem.Instance.GetDirectoryExistenceAndLastWriteTimeUtc(blocked));
    }

    /// <summary>超过 260 字符的目录也能正确读取存在性与最后写入时间。</summary>
    [LongPathFact]
    public void GetDirectoryExistenceAndLastWriteTimeUtc_LongPath_ReadsMetadata()
    {
        using TempDir temp = TempDir.Create();
        string deep = temp.Path;
        for (int index = 0; index < 12; index++)
        {
            deep = Path.Combine(deep, "long-directory-name-" + index);
        }

        Assert.True(deep.Length > 260, "用例需要超过传统路径长度上限");
        string extended = @"\\?\" + deep;
        Directory.CreateDirectory(extended);
        DateTime expected = new(2026, 11, 18, 5, 6, 7, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(extended, expected);

        (bool exists, DateTime lastWrite) =
            SystemFileSystem.Instance.GetDirectoryExistenceAndLastWriteTimeUtc(deep);

        Assert.True(exists);
        Assert.Equal(expected, lastWrite, TimeSpan.FromSeconds(2));
    }

    /// <summary>超长相对路径有确定行为，抛带上下文的 <see cref="IOException"/>。</summary>
    [Fact]
    public void GetDirectoryExistenceAndLastWriteTimeUtc_TooLongRelativePath_ThrowsIOException()
    {
        string relative = new('a', 40_000);

        IOException error = Assert.Throws<IOException>(
            () => SystemFileSystem.Instance.GetDirectoryExistenceAndLastWriteTimeUtc(relative));

        Assert.Contains("路径", error.Message, StringComparison.Ordinal);
    }

    /// <summary>设备路径被明确拒绝，不会拼出非法扩展前缀。</summary>
    [Fact]
    public void GetDirectoryExistenceAndLastWriteTimeUtc_DevicePath_ThrowsIOException()
    {
        IOException error = Assert.Throws<IOException>(
            () => SystemFileSystem.Instance.GetDirectoryExistenceAndLastWriteTimeUtc(@"\\.\PhysicalDrive0"));

        Assert.Contains("设备路径", error.Message, StringComparison.Ordinal);
    }

    /// <summary>长路径下的文件不是目录，按不存在处理。</summary>
    [LongPathFact]
    public void GetDirectoryExistenceAndLastWriteTimeUtc_LongFilePath_ReturnsFalse()
    {
        using TempDir temp = TempDir.Create();
        string deep = temp.Path;
        for (int index = 0; index < 12; index++)
        {
            deep = Path.Combine(deep, "long-directory-name-" + index);
        }

        string extendedDirectory = @"\\?\" + deep;
        Directory.CreateDirectory(extendedDirectory);
        File.WriteAllText(Path.Combine(extendedDirectory, "file.txt"), "x");
        string normalFile = Path.Combine(deep, "file.txt");

        Assert.False(SystemFileSystem.Instance.GetDirectoryExistenceAndLastWriteTimeUtc(normalFile).Exists);
    }
}
