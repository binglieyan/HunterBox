using HunterBox.Application.Install;

namespace HunterBox.Tests;

/// <summary> 拖拽/入口分类：错类型提示而不是崩溃，多文件保留逐个安装。</summary>
public class InstallEntryTests
{
    private static readonly HunterBox.Application.Common.IFileSystem FileSystem =
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

    [Fact]
    public void Classify_Directory_IsDirectory()
    {
        using TempDir temp = TempDir.Create();
        string directory = temp.Dir("mod-folder");

        Assert.Equal(InstallEntryKind.Directory, InstallEntryClassifier.Classify(directory, FileSystem));
    }

    [Fact]
    public void Classify_ArchiveByContent_EvenWithWrongExtension()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "real.zip", ("natives/a.tex", "a"));
        string disguised = Path.Combine(temp.Path, "mod.bin");
        File.Move(zip, disguised);

        Assert.Equal(InstallEntryKind.Archive, InstallEntryClassifier.Classify(disguised, FileSystem));
    }

    [Theory]
    [InlineData("cool.pak")]
    [InlineData("tweak.lua")]
    [InlineData("plugin.dll")]
    public void Classify_BareInstallableFiles_AreSingleFile(string fileName)
    {
        using TempDir temp = TempDir.Create();

        Assert.Equal(
            InstallEntryKind.SingleFile,
            InstallEntryClassifier.Classify(temp.File(fileName, "x"), FileSystem));
    }

    [Fact]
    public void Classify_UnsupportedTypes_AreRejectedWithoutThrowing()
    {
        using TempDir temp = TempDir.Create();

        Assert.Equal(
            InstallEntryKind.Unsupported,
            InstallEntryClassifier.Classify(temp.File("readme.txt", "x"), FileSystem));
        Assert.Equal(
            InstallEntryKind.Unsupported,
            InstallEntryClassifier.Classify(temp.File("image.png", "x"), FileSystem));
        Assert.Equal(InstallEntryKind.Unsupported, InstallEntryClassifier.Classify(null, FileSystem));
        Assert.Equal(InstallEntryKind.Unsupported, InstallEntryClassifier.Classify(string.Empty, FileSystem));
        Assert.Equal(
            InstallEntryKind.Unsupported,
            InstallEntryClassifier.Classify(Path.Combine(temp.Path, "missing.zip"), FileSystem));
    }

    /// <summary>任意层级的 modinfo.ini 与说明/预览图默认不勾选，规则与提交阶段一致。</summary>
    [Theory]
    [InlineData("modinfo.ini", true)]
    [InlineData("sub/modinfo.ini", true)]
    [InlineData("readme.txt", true)]
    [InlineData("ModuleConfig.xml", true)]
    [InlineData("fomod/ModuleConfig.xml", true)]
    [InlineData("preview.JPG", true)]
    [InlineData("natives/stm/a.pak", false)]
    public void ShouldDefaultUncheck_AnyLevelModInfoAndDocumentation(string path, bool expected) =>
        Assert.Equal(expected, InstallFileRules.ShouldDefaultUncheck(path));

    [Fact]
    public void Classify_LockedFile_IsUnreadableInsteadOfUnsupported()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("locked.pak", "x");
        using FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(InstallEntryKind.Unreadable, InstallEntryClassifier.Classify(path, FileSystem));
        Assert.False(
            HunterBox.Application.Common.ArchiveFileProbe.Probe(path, FileSystem).Readable,
            "探测结果必须携带读取失败原因");
    }

    [Fact]
    public void ClassifyAll_MultiFile_PreservesOrderAndKinds()
    {
        using TempDir temp = TempDir.Create();
        string archive = ZipBuilder.Create(temp.Path, "a.zip", ("natives/a.tex", "a"));
        string pak = temp.File("b.pak", "b");
        string text = temp.File("c.txt", "c");

        IReadOnlyList<InstallEntryDecision> decisions = InstallEntryClassifier.ClassifyAll(
            [archive, pak, text],
            FileSystem);

        Assert.Equal(3, decisions.Count);
        Assert.Equal(InstallEntryKind.Archive, decisions[0].Kind);
        Assert.Equal(InstallEntryKind.SingleFile, decisions[1].Kind);
        Assert.Equal(InstallEntryKind.Unsupported, decisions[2].Kind);
        Assert.Equal(archive, decisions[0].Path);
    }

    [Fact]
    public void ClassifyAll_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(InstallEntryClassifier.ClassifyAll([], FileSystem));
        Assert.Empty(InstallEntryClassifier.ClassifyAll(null, FileSystem));
        Assert.Empty(InstallEntryClassifier.ClassifyAll(["", "   "], FileSystem));
    }
}
