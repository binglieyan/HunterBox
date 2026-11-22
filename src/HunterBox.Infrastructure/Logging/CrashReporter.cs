using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using HunterBox.Application.Common;

namespace HunterBox.Infrastructure.Logging;

/// <summary>崩溃报告的上下文信息（版本、当前游戏、启动参数等）。</summary>
/// <param name="AppVersion">程序版本号。</param>
/// <param name="Game">当前游戏显示名；未知时为 <see langword="null"/>。</param>
/// <param name="Arguments">启动参数；<see langword="null"/> 时读 <see cref="Environment.GetCommandLineArgs"/>。</param>
/// <param name="Note">附加说明（如「启动阶段失败」）。</param>
public sealed record CrashContext(
    string AppVersion,
    string? Game = null,
    IReadOnlyList<string>? Arguments = null,
    string? Note = null);

/// <summary>
/// 崩溃报告落盘：把未处理异常写成
/// <c>logs/crash-&lt;yyyyMMdd-HHmmss&gt;.txt</c>，含版本、操作系统、程序/当前目录、启动参数、异常与内部异常堆栈。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>写 <c>&lt;程序目录&gt;/logs</c> 失败时降级到 <c>%LOCALAPPDATA%/HunterBox/logs</c>。</item>
/// <item>写盘失败不抛异常：全部失败时返回 <see langword="null"/> 并把异常放进 <see cref="LastError"/>；参数非法仍抛 <see cref="ArgumentNullException"/>。
/// 调用方（App.xaml.cs）据此提示用户。</item>
/// <item>文件名为秒级时间戳，同一秒多次崩溃自动追加 <c>-1</c>、<c>-2</c>，绝不覆盖已有报告。</item>
/// </list>
/// </remarks>
public sealed class CrashReporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _logDirectory;
    private readonly string _fallbackDirectory;

    /// <summary>装配崩溃报告器。</summary>
    /// <param name="logDirectory">首选日志目录；<see langword="null"/> 时用 <see cref="AppLog.DefaultDirectory"/>。</param>
    /// <param name="fallbackDirectory">降级日志目录；<see langword="null"/> 时用 <see cref="AppLog.FallbackDirectory"/>。</param>
    public CrashReporter(string? logDirectory = null, string? fallbackDirectory = null)
    {
        _logDirectory = logDirectory ?? AppLog.DefaultDirectory;
        _fallbackDirectory = fallbackDirectory ?? AppLog.FallbackDirectory;
    }

    /// <summary>最近一次成功写出的崩溃报告路径；未写过时为 <see langword="null"/>（诊断/测试断言用）。</summary>
    public string? LastWrittenPath { get; private set; }

    /// <summary>最近一次写失败的原因；成功后清空。</summary>
    public Exception? LastError { get; private set; }

    /// <summary>首选日志目录（仅测试使用）。</summary>
    internal string LogDirectory => _logDirectory;

    /// <summary>降级日志目录（仅测试使用）。</summary>
    internal string FallbackDirectory => _fallbackDirectory;

    /// <summary>写一份崩溃报告。</summary>
    /// <param name="source">来源（如 <c>XamlUnhandled</c> / <c>AppDomain</c> / <c>UnobservedTask</c> / <c>Startup</c>）。</param>
    /// <param name="exception">异常；AppDomain 事件可能给不出托管异常，此时允许为 <see langword="null"/>。</param>
    /// <param name="context">上下文信息。</param>
    /// <returns>写出的报告绝对路径；首选与降级目录都不可写时返回 <see langword="null"/>（不抛异常）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> 为 <see langword="null"/>。</exception>
    public string? WriteReport(string source, Exception? exception, CrashContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string text = BuildReport(source, exception, context);

        lock (_gate)
        {
            foreach (string directory in new[] { _logDirectory, _fallbackDirectory })
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    System.IO.Directory.CreateDirectory(directory);
                    string path = UniquePath(directory);
                    File.WriteAllText(path, text, Utf8NoBom);
                    LastWrittenPath = path;
                    LastError = null;
                    return path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LastError = ex;
                }
            }

            return null;
        }
    }

    private static string BuildReport(string source, Exception? exception, CrashContext context)
    {
        StringBuilder builder = new();
        builder.AppendLine("==== HunterBox 崩溃报告 ====");
        AppendField(builder, "时间", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        AppendField(builder, "来源", source);
        AppendField(builder, "版本", context.AppVersion);
        AppendField(builder, "游戏", context.Game ?? "未知");
        AppendField(builder, "进程", $"{Environment.ProcessId}（{(Environment.Is64BitProcess ? "64" : "32")} 位）");
        AppendField(builder, "操作系统", RuntimeInformation.OSDescription);
        AppendField(builder, "运行时", RuntimeInformation.FrameworkDescription);
        AppendField(builder, "程序目录", AppContext.BaseDirectory);
        AppendField(builder, "当前目录", Environment.CurrentDirectory);
        AppendField(builder, "日志文件", AppLog.CurrentFile);
        AppendField(
            builder,
            "启动参数",
            string.Join(' ', context.Arguments ?? Environment.GetCommandLineArgs()));
        if (!string.IsNullOrWhiteSpace(context.Note))
        {
            AppendField(builder, "说明", context.Note);
        }

        builder.AppendLine();
        if (exception is null)
        {
            builder.AppendLine("---- 异常 ----");
            builder.AppendLine("（事件没有携带托管异常对象）");
        }
        else
        {
            builder.AppendLine("---- 异常 ----");
            builder.AppendLine(exception.ToString());

            List<Exception> inners = [];
            for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            {
                inners.Add(inner);
            }

            if (inners.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("---- 内部异常链 ----");
                for (int i = 0; i < inners.Count; i++)
                {
                    builder.AppendLine($"[{i + 1}] {inners[i].GetType().FullName}: {inners[i].Message}");
                    builder.AppendLine(inners[i].StackTrace ?? "（无堆栈）");
                    builder.AppendLine();
                }
            }
        }

        builder.AppendLine("==== 报告结束 ====");
        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string name, string value) =>
        builder.Append(name).Append(": ").AppendLine(value);

    private static string UniquePath(string directory)
    {
        string baseName = $"crash-{DateTime.Now.ToString(Formats.CompactTimestamp, CultureInfo.InvariantCulture)}";
        string path = Path.Combine(directory, baseName + ".txt");
        int suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{baseName}-{suffix.ToString(CultureInfo.InvariantCulture)}.txt");
            suffix++;
        }

        return path;
    }
}
