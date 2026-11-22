using HunterBox.Infrastructure.Logging;

namespace HunterBox.Tests;

public class AppLogTests
{
    [Fact]
    public void Initialize_ThenWrite_CreatesDailyLogFile()
    {
        using TempDir temp = TempDir.Create();
        string directory = Path.Combine(temp.Path, "logs");

        AppLog.Initialize(directory);
        AppLog.Info("测试信息");
        AppLog.Warn("测试警告");
        AppLog.Error("测试错误", new InvalidOperationException("boom"));

        string file = Path.Combine(directory, $"hunterbox-{DateTime.Now:yyyyMMdd}.log");
        Assert.True(File.Exists(file));
        string content = ReadLog(file);
        Assert.Contains("[INF] 测试信息", content);
        Assert.Contains("[WRN] 测试警告", content);
        Assert.Contains("[ERR] 测试错误", content);
        Assert.Contains("boom", content);
    }

    [Fact]
    public void Write_AppendsInsteadOfOverwriting()
    {
        using TempDir temp = TempDir.Create();
        string directory = Path.Combine(temp.Path, "logs");

        AppLog.Initialize(directory);
        AppLog.Info("第一条");
        AppLog.Info("第二条");

        string file = Path.Combine(directory, $"hunterbox-{DateTime.Now:yyyyMMdd}.log");
        string content = ReadLog(file);
        Assert.Contains("第一条", content);
        Assert.Contains("第二条", content);
        Assert.True(content.IndexOf("第一条", StringComparison.Ordinal) < content.IndexOf("第二条", StringComparison.Ordinal));
    }

    /// <summary>WarnBatch 按顺序写入全部条目，空列表不产生任何日志行。</summary>
    [Fact]
    public void WarnBatch_WritesAllMessagesInOrder()
    {
        using TempDir temp = TempDir.Create();
        string directory = Path.Combine(temp.Path, "logs");

        AppLog.Initialize(directory);
        AppLog.WarnBatch(["批量一", "批量二", "批量三"]);

        string file = Path.Combine(directory, $"hunterbox-{DateTime.Now:yyyyMMdd}.log");
        string content = ReadLog(file);
        int first = content.IndexOf("批量一", StringComparison.Ordinal);
        int second = content.IndexOf("批量二", StringComparison.Ordinal);
        int third = content.IndexOf("批量三", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first && third > second, "WarnBatch 必须按顺序写入全部条目");
        // 并行用例可能向同一日志目录写入其它警告，这里只校验本批消息各出现一次。
        Assert.Equal(1, content.Split("批量一").Length - 1);
        Assert.Equal(1, content.Split("批量二").Length - 1);
        Assert.Equal(1, content.Split("批量三").Length - 1);

        // 空批次不写入：并行用例可能同时向同一日志追加，无法用文件长度断言；
        // 这里校验本批消息没有被重复写入，且调用不抛异常。
        AppLog.WarnBatch([]);
        string after = ReadLog(file);
        Assert.Equal(1, after.Split("批量一").Length - 1);
        Assert.Equal(1, after.Split("批量二").Length - 1);
        Assert.Equal(1, after.Split("批量三").Length - 1);
    }

    [Fact]
    public void CurrentFile_IsUnderInitializedDirectory()
    {
        using TempDir temp = TempDir.Create();
        string directory = Path.Combine(temp.Path, "logs");

        AppLog.Initialize(directory);

        Assert.StartsWith(directory, AppLog.CurrentFile, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".log", AppLog.CurrentFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Initialize_UnwritableDirectory_DoesNotThrow()
    {
        using TempDir temp = TempDir.Create();
        string blocked = temp.File("blocked", "x");

        // 不抛异常即通过；写失败原因可在 LastError 里看到（可能为空，取决于平台）。
        Exception? error = Record.Exception(() =>
        {
            AppLog.Initialize(Path.Combine(blocked, "logs"));
            AppLog.Info("写不进去也不能影响主流程");
            AppLog.Error("同样静默", new InvalidOperationException("boom"));
        });
        Assert.Null(error);
        _ = AppLog.LastError;
    }

    [Fact]
    public void ResolveWritableDirectory_PrefersPreferred()
    {
        using TempDir temp = TempDir.Create();
        string preferred = Path.Combine(temp.Path, "preferred-logs");
        string fallback = Path.Combine(temp.Path, "fallback-logs");

        Assert.Equal(preferred, AppLog.ResolveWritableDirectory(preferred, fallback));
        Assert.True(Directory.Exists(preferred));
    }

    [Fact]
    public void ResolveWritableDirectory_FallsBackWhenPreferredUnwritable()
    {
        using TempDir temp = TempDir.Create();
        string blocked = temp.File("blocked", "x");
        string fallback = Path.Combine(temp.Path, "fallback-logs");

        Assert.Equal(fallback, AppLog.ResolveWritableDirectory(Path.Combine(blocked, "logs"), fallback));
        Assert.True(Directory.Exists(fallback));
    }

    [Fact]
    public void FallbackDirectory_IsUnderLocalAppData()
    {
        Assert.Contains("logs", AppLog.FallbackDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HunterBox", AppLog.FallbackDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultDirectory_IsUnderBaseDirectory()
    {
        Assert.StartsWith(AppContext.BaseDirectory, AppLog.DefaultDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("logs", AppLog.DefaultDirectory, StringComparison.OrdinalIgnoreCase);
    }

    // ---- P-06：常驻 writer 的并发、轮转与句柄切换 ----

    [Fact]
    public async Task Write_ConcurrentThreads_DoesNotLoseLines()
    {
        using TempDir temp = TempDir.Create();
        string directory = Path.Combine(temp.Path, "logs");
        AppLog.Initialize(directory);

        const int writers = 6;
        const int linesPerWriter = 40;
        using ManualResetEventSlim start = new(false);
        Task[] tasks = [.. Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            start.Wait();
            for (int line = 0; line < linesPerWriter; line++)
            {
                AppLog.Info($"并发-{writer}-{line}");
            }
        }))];
        start.Set();
        await Task.WhenAll(tasks);
        AppLog.Flush();

        string content = ReadLog(Path.Combine(directory, $"hunterbox-{DateTime.Now:yyyyMMdd}.log"));
        for (int writer = 0; writer < writers; writer++)
        {
            for (int line = 0; line < linesPerWriter; line++)
            {
                Assert.Contains($"并发-{writer}-{line}" + Environment.NewLine, content);
            }
        }
    }

    [Fact]
    public void Write_ExceedsSizeLimit_RotatesToNumberedFileAndKeepsWriting()
    {
        using TempDir temp = TempDir.Create();
        string directory = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(directory);
        string date = DateTime.Now.ToString("yyyyMMdd");
        string current = Path.Combine(directory, $"hunterbox-{date}.log");
        File.WriteAllText(current, new string('x', (int)AppLog.MaxLogFileBytes + 1024));

        AppLog.Initialize(directory);
        AppLog.Info("轮转后的新日志");

        string archived = Path.Combine(directory, $"hunterbox-{date}.1.log");
        Assert.True(File.Exists(archived), "超限日志应滚动为带序号的历史文件，不能无限增长");
        Assert.True(new FileInfo(archived).Length > AppLog.MaxLogFileBytes);
        Assert.Contains("轮转后的新日志", ReadLog(current));
        Assert.DoesNotContain("轮转后的新日志", ReadLog(archived));
    }

    [Fact]
    public void Initialize_SwitchingDirectory_ClosesPreviousWriterHandle()
    {
        using TempDir temp = TempDir.Create();
        string first = Path.Combine(temp.Path, "logs-a");
        string second = Path.Combine(temp.Path, "logs-b");
        AppLog.Initialize(first);
        AppLog.Info("第一条");
        AppLog.Initialize(second);
        AppLog.Info("第二条");

        string firstFile = Path.Combine(first, $"hunterbox-{DateTime.Now:yyyyMMdd}.log");
        using (FileStream exclusive = new(firstFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(exclusive.Length > 0);
        }

        string secondFile = Path.Combine(second, $"hunterbox-{DateTime.Now:yyyyMMdd}.log");
        Assert.Contains("第二条", ReadLog(secondFile));
    }

    /// <summary>
    /// 读取日志文件：常驻 writer 在进程内保持写句柄（P-06），因此读取方必须显式允许共享写入，
    /// 否则 <see cref="File.ReadAllText(string)"/> 在 Windows 上会因共享冲突失败。
    /// </summary>
    private static string ReadLog(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
