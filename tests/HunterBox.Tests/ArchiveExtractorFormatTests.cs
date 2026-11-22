using System.Text;
using HunterBox.Application.Common;
using HunterBox.Application.Install;
using HunterBox.Domain.Archives;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Archives;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>
/// 解析器补齐：魔数探测、GBK、嵌套、密码、裸文件。
/// </summary>
public class ArchiveExtractorFormatTests
{
    private const string GameId = "mhws";

    private static readonly HunterBox.Application.Common.IFileSystem FileSystem =
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

    private static (BoxPaths Paths, ModInstaller Installer) CreateBox(TempDir temp)
    {
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        return (paths, new ModInstaller(paths, store, new ArchiveExtractor(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, GameId));
    }

    [Fact]
    public void Extract_DetectsZipByMagicEvenWithWrongExtension()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "real.zip", ("natives/a.tex", "a"));
        string disguised = Path.Combine(temp.Path, "disguised.dat");
        File.Move(zip, disguised);

        Assert.Equal(ArchiveFormat.Zip, ArchiveFileProbe.Detect(disguised, FileSystem));

        (BoxPaths paths, ModInstaller installer) = CreateBox(temp);
        InstallResult result;
        using (InstallStaging staging1 = installer.Stage(disguised))
        {
            result = staging1.Commit();
        }

        Assert.Contains("natives/a.tex", result.Record.RelativeFiles);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "a.tex")));
    }

    [Fact]
    public void Install_NestedArchive_IsRecursivelyExtracted()
    {
        using TempDir temp = TempDir.Create();
        string innerZip = ZipBuilder.Create(temp.Path, "inner.zip", ("natives/inner.tex", "inner"));
        byte[] innerBytes = File.ReadAllBytes(innerZip);
        string outer = ZipTestTools.CreateFromBytes(
            temp.Path,
            "outer.zip",
            ("inner.zip", innerBytes));

        (BoxPaths paths, ModInstaller installer) = CreateBox(temp);
        InstallResult result;
        using (InstallStaging staging2 = installer.Stage(outer))
        {
            result = staging2.Commit();
        }

        Assert.Contains("natives/inner.tex", result.Record.RelativeFiles);
        Assert.DoesNotContain(result.Record.RelativeFiles, f => f.EndsWith(".zip"));
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "natives", "inner.tex")));
    }

    [Fact]
    public void Extract_ReadsLegacyGbkFileNames()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "gbk.zip", ("AAAA.txt", "gbk-content"));
        ZipTestTools.RewriteEntryNameAsGbk(zip, "AAAA.txt", "测试.txt");

        string destination = temp.Dir("out");
        IReadOnlyList<string> files = new ArchiveExtractor().Extract(zip, destination);

        Assert.Contains("测试.txt", files);
        string extracted = Path.Combine(destination, "测试.txt");
        Assert.True(File.Exists(extracted), "GBK 文件名应被正确解码");
        Assert.Equal("gbk-content", File.ReadAllText(extracted));
    }

    [Fact]
    public void Extract_GuessesPasswordFromFileName()
    {
        using TempDir temp = TempDir.Create();
        byte[] content = Encoding.UTF8.GetBytes("secret-mod-data");
        string zip = ZipTestTools.CreateEncrypted(temp.Path, "mod密码abc123.zip", "natives/secret.tex", content, "abc123");

        Assert.Equal("abc123", ArchiveFormatDetector.GuessPasswordFromFileName("mod密码abc123.zip"));

        string destination = temp.Dir("out");
        IReadOnlyList<string> files = new ArchiveExtractor().Extract(zip, destination);

        Assert.Contains("natives/secret.tex", files);
        Assert.Equal("secret-mod-data", File.ReadAllText(Path.Combine(destination, "natives", "secret.tex")));
    }

    [Fact]
    public void Extract_EncryptedWithoutPasswordHint_ThrowsFriendlyError()
    {
        using TempDir temp = TempDir.Create();
        byte[] content = Encoding.UTF8.GetBytes("secret");
        string zip = ZipTestTools.CreateEncrypted(temp.Path, "encrypted.zip", "natives/secret.tex", content, "abc123");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => new ArchiveExtractor().Extract(zip, temp.Dir("out")));

        Assert.Contains("加密", ex.Message);
    }

    [Theory]
    [InlineData("mod解压码123.zip", "123")]
    [InlineData("mod密码hello.zip", "hello")]
    [InlineData("mod密码abc(1).zip", "abc")]
    [InlineData("mod：密码xyz.zip", "xyz")]
    [InlineData("密码abc.zip", "abc")] // 关键字在文件名开头也合法
    [InlineData("解压码987.zip", "987")]
    [InlineData("普通包.zip", null)]
    [InlineData("密码.zip", null)]
    [InlineData(null, null)]
    public void GuessPasswordFromFileName_Theory(string? fileName, string? expected) =>
        Assert.Equal(expected, ArchiveFormatDetector.GuessPasswordFromFileName(fileName));

    [Fact]
    public void Extract_ReportsProgressAndHonorsCancellation()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "big.zip", ("natives/a.tex", new string('x', 300_000)));

        List<ArchiveProgress> reports = [];
        using CancellationTokenSource cancellation = new();
        ProgressCollector collector = new(progress =>
        {
            reports.Add(progress);
            if (reports.Count >= 2)
            {
                cancellation.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => new ArchiveExtractor().Extract(
            zip,
            temp.Dir("out"),
            new ArchiveExtractionOptions { Progress = collector, CancellationToken = cancellation.Token }));

        Assert.NotEmpty(reports);
        Assert.Contains(reports, r => r.BytesTransferred > 0);
    }

    [Fact]
    public void Install_BarePakFile_IsInstalledAsPakMod()
    {
        using TempDir temp = TempDir.Create();
        string pak = temp.File("cool.pak", "pak-bytes");
        (BoxPaths paths, ModInstaller installer) = CreateBox(temp);

        InstallResult result;
        using (InstallStaging staging3 = installer.Stage(pak))
        {
            result = staging3.Commit();
        }

        Assert.Equal(ModKind.PakFiles, result.Record.Kind);
        Assert.Contains("cool.pak", result.Record.RelativeFiles);
        Assert.Equal("pak-bytes", File.ReadAllText(Path.Combine(paths.FilesDirectory(1), "cool.pak")));
    }

    [Fact]
    public void Install_BareLuaFile_StaysInPackagePath()
    {
        using TempDir temp = TempDir.Create();
        string lua = temp.File("tweak.lua", "print(1)");
        (BoxPaths paths, ModInstaller installer) = CreateBox(temp);

        InstallResult result;
        using (InstallStaging staging4 = installer.Stage(lua))
        {
            result = staging4.Commit();
        }

        Assert.Equal(ModKind.RefScripts, result.Record.Kind);
        Assert.Null(result.Record.TargetDirectory);
        Assert.True(File.Exists(Path.Combine(paths.FilesDirectory(1), "tweak.lua")));
    }

    [Fact]
    public void Install_UnsupportedSingleFile_ThrowsFriendlyError()
    {
        using TempDir temp = TempDir.Create();
        string text = temp.File("readme.txt", "hello");
        (_, ModInstaller installer) = CreateBox(temp);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
        {
            using InstallStaging staging5 = installer.Stage(text);
            staging5.Commit();
        });
        Assert.Contains("无法识别", ex.Message);
    }

    [Fact]
    public void Detect_RecognizesMagicOfCommonFormats()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "a.zip", ("x", "y"));

        Assert.Equal(ArchiveFormat.Zip, ArchiveFileProbe.Detect(zip, FileSystem));
        Assert.False(ArchiveFileProbe.IsSupportedArchive(temp.File("plain.txt", "x"), FileSystem));
        Assert.Equal(
            ArchiveFormat.Unknown,
            ArchiveFileProbe.Detect(Path.Combine(temp.Path, "missing.zip"), FileSystem));
    }

    /// <summary>把同步回调包装成 IProgress。</summary>
    private sealed class ProgressCollector : IProgress<ArchiveProgress>
    {
        private readonly Action<ArchiveProgress> _callback;

        public ProgressCollector(Action<ArchiveProgress> callback) => _callback = callback;

        public void Report(ArchiveProgress value) => _callback(value);
    }
}
