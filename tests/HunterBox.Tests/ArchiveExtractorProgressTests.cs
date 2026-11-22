using System.IO.Compression;
using HunterBox.Domain.Archives;
using HunterBox.Infrastructure.Archives;

namespace HunterBox.Tests;

/// <summary>P-05：解压进度按「百分比变化 / ≥100ms」节流，不再每 80KB 派发一次。</summary>
public class ArchiveExtractorProgressTests
{
    private const int LargeSize = 16 * 1024 * 1024;
    private const string LargeEntryPath = "natives/big.bin";

    [Fact]
    public void Extract_LargeEntry_ThrottlesProgressReports()
    {
        using TempDir temp = TempDir.Create();
        string zip = CreateLargeZip(temp.Path, "big.zip", LargeEntryPath, LargeSize);
        ProgressCollector progress = new();

        new ArchiveExtractor().Extract(
            zip,
            temp.Dir("out"),
            new ArchiveExtractionOptions { Progress = progress });

        Assert.NotEmpty(progress.Reports);

        // 旧实现每 80KB 回调一次（16MB ≈ 205 次）；节流后上限约为百分比变化次数（≤101）。
        Assert.True(
            progress.Reports.Count <= 110,
            $"进度回调 {progress.Reports.Count} 次，应按百分比/时间节流到 ≤110 次");

        ArchiveProgress last = progress.Reports[^1];
        Assert.Equal(LargeSize, last.BytesTransferred);
        Assert.Equal(LargeSize, last.TotalBytes);
        Assert.Equal(100, last.Percent);
        Assert.Equal(LargeEntryPath, last.EntryPath);

        // 百分比单调非递减，并且有中间进度（不是只在最后跳 100%）。
        int previous = -1;
        foreach (ArchiveProgress report in progress.Reports)
        {
            Assert.True(report.Percent >= previous, $"进度百分比回退：{previous} → {report.Percent}");
            previous = report.Percent;
        }

        Assert.Contains(progress.Reports, report => report.Percent is > 0 and < 100);
        Assert.True(File.Exists(Path.Combine(temp.Path, "out", "natives", "big.bin")));
    }

    [Fact]
    public void Extract_SmallArchive_StillEndsWithFullProgress()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(
            temp.Path,
            "small.zip",
            ("natives/a.tex", "A"),
            ("natives/b.tex", "B"));
        ProgressCollector progress = new();

        new ArchiveExtractor().Extract(
            zip,
            temp.Dir("out"),
            new ArchiveExtractionOptions { Progress = progress });

        Assert.NotEmpty(progress.Reports);
        Assert.Equal(100, progress.Reports[^1].Percent);
        Assert.Equal(progress.Reports[^1].TotalBytes, progress.Reports[^1].BytesTransferred);
    }

    /// <summary>N-12：数万小文件不能每个条目都派发一次进度，统一走时间闸门。</summary>
    [Fact]
    public void Extract_ManySmallEntries_ThrottlesProgressReports()
    {
        using TempDir temp = TempDir.Create();
        string zipPath = Path.Combine(temp.Path, "many-small.zip");
        const int entryCount = 2000;
        using (FileStream file = File.Create(zipPath))
        using (ZipArchive zip = new(file, ZipArchiveMode.Create))
        {
            for (int i = 0; i < entryCount; i++)
            {
                ZipArchiveEntry entry = zip.CreateEntry($"natives/small/{i:D5}.tex");
                using Stream stream = entry.Open();
                stream.Write([1, 2, 3, 4]);
            }
        }

        ProgressCollector progress = new();
        new ArchiveExtractor().Extract(
            zipPath,
            temp.Dir("out-small"),
            new ArchiveExtractionOptions { Progress = progress });

        Assert.NotEmpty(progress.Reports);
        Assert.True(
            progress.Reports.Count < 200,
            $"2000 个小文件派发了 {progress.Reports.Count} 次进度，应统一按时间闸门限流");
        Assert.Equal(100, progress.Reports[^1].Percent);
    }

    /// <summary>条目大小未知/全为空文件（total=0）时，结束时仍要上报一次 100%。</summary>
    [Fact]
    public void Extract_ZeroTotalEntries_ReportsCompletion()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(
            temp.Path,
            "empty.zip",
            ("natives/empty-a.tex", ""),
            ("natives/empty-b.tex", ""));
        ProgressCollector progress = new();

        new ArchiveExtractor().Extract(
            zip,
            temp.Dir("out-empty"),
            new ArchiveExtractionOptions { Progress = progress });

        Assert.NotEmpty(progress.Reports);
        Assert.Equal(100, progress.Reports[^1].Percent);
        Assert.Null(progress.Reports[^1].TotalBytes);
    }

    private static string CreateLargeZip(string directory, string fileName, string entryPath, int size)
    {
        string zipPath = Path.Combine(directory, fileName);
        using FileStream file = File.Create(zipPath);
        using ZipArchive zip = new(file, ZipArchiveMode.Create);
        ZipArchiveEntry entry = zip.CreateEntry(entryPath, CompressionLevel.Fastest);
        using Stream stream = entry.Open();
        byte[] chunk = new byte[1024 * 1024];
        int remaining = size;
        while (remaining > 0)
        {
            int count = Math.Min(chunk.Length, remaining);
            stream.Write(chunk, 0, count);
            remaining -= count;
        }

        return zipPath;
    }

    private sealed class ProgressCollector : IProgress<ArchiveProgress>
    {
        public List<ArchiveProgress> Reports { get; } = [];

        public void Report(ArchiveProgress value) => Reports.Add(value);
    }
}
