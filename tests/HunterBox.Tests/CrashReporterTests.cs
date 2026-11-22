using HunterBox.Infrastructure.Logging;

namespace HunterBox.Tests;

/// <summary>崩溃报告落盘（内容、降级目录、彻底失败静默）的回归测试。</summary>
public class CrashReporterTests
{
    [Fact]
    public void WriteReport_WritesFileWithContextAndExceptionChain()
    {
        using TempDir temp = TempDir.Create();
        string logs = Path.Combine(temp.Path, "logs");
        CrashReporter reporter = new(logs, Path.Combine(temp.Path, "fallback"));
        CrashContext context = new("9.9.9", "怪物猎人崛起", ["--crash-probe", "参数"], "启动阶段");
        Exception exception = new InvalidOperationException(
            "外层崩溃",
            new ArgumentException("内层崩溃"));

        string? path = reporter.WriteReport("XamlUnhandled", exception, context);

        Assert.NotNull(path);
        Assert.Equal(path, reporter.LastWrittenPath);
        Assert.Null(reporter.LastError);
        Assert.StartsWith(logs, path!, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"crash-\d{8}-\d{6}\.txt$", Path.GetFileName(path!));
        string content = File.ReadAllText(path!);
        Assert.Contains("HunterBox 崩溃报告", content);
        Assert.Contains("来源: XamlUnhandled", content);
        Assert.Contains("版本: 9.9.9", content);
        Assert.Contains("游戏: 怪物猎人崛起", content);
        Assert.Contains("--crash-probe", content);
        Assert.Contains("启动阶段", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("外层崩溃", content);
        Assert.Contains("内部异常链", content);
        Assert.Contains("ArgumentException", content);
        Assert.Contains("内层崩溃", content);
        Assert.Contains("程序目录", content);
        Assert.Contains("报告结束", content);
    }

    [Fact]
    public void WriteReport_PrimaryUnwritable_FallsBackToLocalAppDataStyleDirectory()
    {
        using TempDir temp = TempDir.Create();
        string blocked = temp.File("blocked", "x");
        string fallback = Path.Combine(temp.Path, "fallback-logs");
        CrashReporter reporter = new(Path.Combine(blocked, "logs"), fallback);

        string? path = reporter.WriteReport("AppDomain", new Exception("炸"), new CrashContext("1.0.0"));

        Assert.NotNull(path);
        Assert.StartsWith(fallback, path!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
        Assert.Contains("炸", File.ReadAllText(path!));
    }

    [Fact]
    public void WriteReport_AllDirectoriesUnwritable_ReturnsNullWithoutThrowing()
    {
        using TempDir temp = TempDir.Create();
        string blocked = temp.File("blocked", "x");
        CrashReporter reporter = new(Path.Combine(blocked, "logs"), Path.Combine(blocked, "fallback"));

        string? path = reporter.WriteReport("UnobservedTask", new Exception("炸"), new CrashContext("1.0.0"));

        Assert.Null(path);
        Assert.NotNull(reporter.LastError);
        Assert.Null(reporter.LastWrittenPath);
    }

    [Fact]
    public void WriteReport_SameSecond_DoesNotOverwriteExistingReport()
    {
        using TempDir temp = TempDir.Create();
        string logs = Path.Combine(temp.Path, "logs");
        CrashReporter reporter = new(logs, Path.Combine(temp.Path, "fallback"));

        string? first = reporter.WriteReport("XamlUnhandled", new Exception("一"), new CrashContext("1.0.0"));
        string? second = reporter.WriteReport("XamlUnhandled", new Exception("二"), new CrashContext("1.0.0"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Contains("一", File.ReadAllText(first!));
        Assert.Contains("二", File.ReadAllText(second!));
    }

    [Fact]
    public void WriteReport_NullException_StillWritesReport()
    {
        using TempDir temp = TempDir.Create();
        CrashReporter reporter = new(Path.Combine(temp.Path, "logs"), Path.Combine(temp.Path, "fallback"));

        string? path = reporter.WriteReport("AppDomain", null, new CrashContext("1.0.0"));

        Assert.NotNull(path);
        Assert.Contains("没有携带托管异常对象", File.ReadAllText(path!));
    }

    [Fact]
    public void WriteReport_NullContext_Throws()
    {
        CrashReporter reporter = new();

        Assert.Throws<ArgumentNullException>(() => reporter.WriteReport("x", null, null!));
    }

    [Fact]
    public void Defaults_UseAppLogDirectories()
    {
        CrashReporter reporter = new();

        Assert.Equal(AppLog.DefaultDirectory, reporter.LogDirectory);
        Assert.Equal(AppLog.FallbackDirectory, reporter.FallbackDirectory);
        Assert.Contains("logs", reporter.LogDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HunterBox", reporter.FallbackDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
