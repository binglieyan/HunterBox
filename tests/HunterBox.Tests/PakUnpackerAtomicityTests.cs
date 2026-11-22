using System.Text;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// 解包原子性（C-05）：先写同卷暂存目录，全部成功后才整体替换目标。
/// 中途失败 / 取消时目标目录必须保持原样，且不留下暂存或回滚目录。
/// </summary>
public class PakUnpackerAtomicityTests
{
    private const string FirstPath = "natives/stm/first.bin";
    private const string SecondPath = "natives/stm/second.bin";
    private const string ThirdPath = "natives/stm/third.bin";

    private static byte[] Content(string value) => Encoding.ASCII.GetBytes(value);

    private static PakNameIndex IndexOf(params string[] paths) => PakPathHasher.BuildIndex(paths);

    private static void AssertNoStagingArtifacts(TempDir temp)
    {
        Assert.Empty(Directory.GetDirectories(temp.Path, "*.tmp", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.GetDirectories(temp.Path, "*.old", SearchOption.TopDirectoryOnly));
    }

    private static string[] FilesUnder(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories);

    /// <summary>第 N 个条目解压失败时，已经解出的前 N-1 个条目不能落到目标目录里。</summary>
    [Fact]
    public void Unpack_MidwayFailure_LeavesDestinationUntouched()
    {
        using TempDir temp = TempDir.Create();
        ulong firstHash = PakCodecTestTools.HashPath(FirstPath);
        ulong secondHash = PakCodecTestTools.HashPath(SecondPath);
        PakTestEntry second = PakCodecTestTools.Deflated(secondHash, Content("second-content")) with
        {
            // 声明尺寸与实际解压长度不符：第一个条目写完后，第二个条目必然失败。
            DecompressedSize = Content("second-content").LongLength + 5L,
        };
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "midway.pak",
            0,
            PakCodecTestTools.Stored(firstHash, Content("first-content")),
            second);

        string destination = temp.Dir("out");
        string sentinel = Path.Combine(destination, "keep.txt");
        File.WriteAllText(sentinel, "GAME-ORIGINAL");
        Directory.CreateDirectory(Path.Combine(destination, "nested"));
        File.WriteAllText(Path.Combine(destination, "nested", "keep2.txt"), "GAME-ORIGINAL-2");

        Assert.Throws<InvalidDataException>(() =>
            new PakRepacker().Unpack(
                built.Path,
                destination,
                IndexOf(FirstPath, SecondPath),
                progress: null,
                CancellationToken.None));

        // 目标目录原样保留：既有文件不能丢，也不能出现半套新文件。
        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(sentinel));
        Assert.Equal("GAME-ORIGINAL-2", File.ReadAllText(Path.Combine(destination, "nested", "keep2.txt")));
        Assert.Equal(2, FilesUnder(destination).Length);
        AssertNoStagingArtifacts(temp);
    }

    /// <summary>用户中途取消：不能留下半套文件，目标目录保持原样。</summary>
    [Fact]
    public void Unpack_CancelledMidway_LeavesDestinationUntouched()
    {
        using TempDir temp = TempDir.Create();
        ulong firstHash = PakCodecTestTools.HashPath(FirstPath);
        ulong secondHash = PakCodecTestTools.HashPath(SecondPath);
        ulong thirdHash = PakCodecTestTools.HashPath(ThirdPath);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "cancel.pak",
            0,
            PakCodecTestTools.Stored(firstHash, Content("first")),
            PakCodecTestTools.Stored(secondHash, Content("second")),
            PakCodecTestTools.Stored(thirdHash, Content("third")));

        string destination = temp.Dir("out");
        string sentinel = Path.Combine(destination, "keep.txt");
        File.WriteAllText(sentinel, "GAME-ORIGINAL");

        using CancellationTokenSource cancellation = new();
        CancelAfterFirstReport progress = new(cancellation);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new PakRepacker().Unpack(
                built.Path,
                destination,
                IndexOf(FirstPath, SecondPath, ThirdPath),
                progress,
                cancellation.Token));

        Assert.Equal(1, progress.ReportCount);
        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(sentinel));
        Assert.Equal(new[] { sentinel }, FilesUnder(destination));
        AssertNoStagingArtifacts(temp);
    }

    /// <summary>目标已存在（非空）时整体替换：旧内容不残留，新内容完整。</summary>
    [Fact]
    public void Unpack_Success_ReplacesExistingDestination()
    {
        using TempDir temp = TempDir.Create();
        ulong firstHash = PakCodecTestTools.HashPath(FirstPath);
        ulong secondHash = PakCodecTestTools.HashPath(SecondPath);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "replace.pak",
            0,
            PakCodecTestTools.Stored(firstHash, Content("new-first")),
            PakCodecTestTools.Stored(secondHash, Content("new-second")));

        string destination = temp.Dir("out");
        File.WriteAllText(Path.Combine(destination, "stale.txt"), "STALE");
        Directory.CreateDirectory(Path.Combine(destination, "stale-dir"));
        File.WriteAllText(Path.Combine(destination, "stale-dir", "stale2.txt"), "STALE-2");

        new PakRepacker().Unpack(
            built.Path,
            destination,
            IndexOf(FirstPath, SecondPath),
            progress: null,
            CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(destination, "stale.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "stale-dir")));
        Assert.Equal("new-first", File.ReadAllText(Path.Combine(destination, FirstPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal("new-second", File.ReadAllText(Path.Combine(destination, SecondPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(2, FilesUnder(destination).Length);
        AssertNoStagingArtifacts(temp);
    }

    /// <summary>目标不存在时也要先写暂存目录再改名，成功后正常出现。</summary>
    [Fact]
    public void Unpack_NewDestination_CreatedAfterAllEntriesSucceed()
    {
        using TempDir temp = TempDir.Create();
        ulong firstHash = PakCodecTestTools.HashPath(FirstPath);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "create.pak",
            0,
            PakCodecTestTools.Stored(firstHash, Content("payload")));
        string destination = Path.Combine(temp.Path, "brand-new", "out");

        new PakRepacker().Unpack(
            built.Path,
            destination,
            IndexOf(FirstPath),
            progress: null,
            CancellationToken.None);

        string file = Path.Combine(destination, FirstPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal("payload", File.ReadAllText(file));
        Assert.Single(FilesUnder(destination));
        AssertNoStagingArtifacts(temp);
    }

    [Fact]
    public void Unpack_DestinationIsExistingFile_ThrowsArgumentException()
    {
        using TempDir temp = TempDir.Create();
        ulong hash = PakCodecTestTools.HashPath(FirstPath);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "file-dest.pak",
            0,
            PakCodecTestTools.Stored(hash, Content("payload")));
        string destination = temp.File("destination-file.txt", "not-a-directory");

        Assert.Throws<ArgumentException>(() =>
            new PakRepacker().Unpack(
                built.Path,
                destination,
                IndexOf(FirstPath),
                progress: null,
                CancellationToken.None));
        Assert.Equal("not-a-directory", File.ReadAllText(destination));
        AssertNoStagingArtifacts(temp);
    }

    /// <summary>在第一次进度回调时取消：后续条目不再解，直接走取消清理路径。</summary>
    private sealed class CancelAfterFirstReport(CancellationTokenSource cancellation) : IProgress<PakRepackProgress>
    {
        public int ReportCount { get; private set; }

        public void Report(PakRepackProgress value)
        {
            ReportCount++;
            cancellation.Cancel();
        }
    }
}
