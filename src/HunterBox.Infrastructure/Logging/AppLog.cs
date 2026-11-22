using System.Globalization;
using System.Text;

namespace HunterBox.Infrastructure.Logging;

/// <summary>文件日志：每天一个文件，写在 <c>&lt;程序目录&gt;/logs</c> 下；程序目录不可写时降级 <c>%LOCALAPPDATA%/HunterBox/logs</c>。</summary>
/// <remarks>
/// <para>
/// 只在 App 层与 Infrastructure（存储/解压等边界）调用；不参与业务逻辑，不抛异常（写日志失败时静默忽略，
/// 失败原因可通过 <see cref="LastError"/> 查看）。每行格式：<c>2026-11-15 01:50:00.123 [INF] 内容</c>。
/// </para>
/// <para>
/// 实现上常驻一个 <see cref="StreamWriter"/>（自动 flush，行写入后立即可读），不再每行
/// <c>CreateDirectory + AppendAllText</c> 开关文件；写入与轮转都在同一把锁内串行，线程安全（P-06）。
/// 进程退出时通过 <see cref="AppDomain.ProcessExit"/> 兜底 flush/释放句柄；
/// 单文件超过 <see cref="MaxLogFileBytes"/> 时滚动为 <c>hunterbox-yyyyMMdd.N.log</c>。
/// </para>
/// </remarks>
public static class AppLog
{
    /// <summary>单个日志文件的大小上限（字节）：超过后滚动为带序号的历史文件，写入不中断。</summary>
    public const long MaxLogFileBytes = 8L * 1024 * 1024;

    private static readonly object Gate = new();
    private static string _directory = DefaultDirectory;
    private static bool _explicitDirectory;
    private static bool _fellBack;
    private static string _lastError = string.Empty;
    private static StreamWriter? _writer;
    private static string? _writerPath;
    private static DateTime _writerDate;
    private static long _writerBytes;

    /// <summary>写入器当前所在目录（规范化）；避免每行都 Path.GetDirectoryName。</summary>
    private static string? _writerDirectory;
    private static string? _rotationBlockedPath;

    /// <summary>滚动被阻断后的重试窗口：到期后再次尝试，避免当天永久放弃滚动（C-24）。</summary>
    private static DateTime _rotationRetryAfterUtc;

    /// <summary>滚动失败后的重试间隔。</summary>
    private static readonly TimeSpan RotationRetryInterval = TimeSpan.FromMinutes(1);

    /// <summary><see cref="StreamWriter.WriteLine(string?)"/> 追加的换行符字节数（与平台换行一致）。</summary>
    private static readonly int NewLineByteCount = Encoding.UTF8.GetByteCount(Environment.NewLine);

    static AppLog()
    {
        // 进程退出时刷盘并释放常驻写入器；AutoFlush 已把每行交给 OS，这里兜底最后几行与文件句柄。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    /// <summary>默认日志目录：<c>&lt;程序目录&gt;/logs</c>。</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>程序目录不可写时的降级日志目录：<c>%LOCALAPPDATA%/HunterBox/logs</c>。</summary>
    public static string FallbackDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HunterBox",
        "logs");

    /// <summary>当前日志目录。</summary>
    public static string Directory => _directory;

    /// <summary>当前日志文件路径（按日期）。</summary>
    public static string CurrentFile => CurrentFilePath(_directory, DateTime.Now);

    /// <summary>最近一次写日志/建目录失败的原因；正常时为空串。</summary>
    public static string LastError => _lastError;

    /// <summary>初始化日志目录。</summary>
    /// <param name="directory">
    /// 显式日志目录（测试/高级用法，只建目录、不做降级）；为 <see langword="null"/> 或空白时按
    /// 「程序目录 → %LOCALAPPDATA%」顺序选第一个可写目录（启动时应使用这种形式）。
    /// </param>
    public static void Initialize(string? directory = null)
    {
        lock (Gate)
        {
            // 切换目录前先释放旧写入器，避免旧文件句柄长期占用（Windows 下会阻碍删除/移动）。
            CloseWriter();
            _rotationBlockedPath = null;

            if (!string.IsNullOrWhiteSpace(directory))
            {
                _directory = Path.GetFullPath(directory);
                _explicitDirectory = true;
                _fellBack = false;
                TryCreateDirectory(_directory);
                return;
            }

            _explicitDirectory = false;
            _directory = ResolveWritableDirectory(DefaultDirectory, FallbackDirectory);
            _fellBack = !string.Equals(_directory, DefaultDirectory, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>把常驻写入器缓冲的日志刷到磁盘（internal：仅测试使用，生产写入已自动 flush）。</summary>
    internal static void Flush()
    {
        lock (Gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _lastError = ex.Message;
            }
        }
    }

    /// <summary>按顺序挑第一个可写目录（会实际建目录 + 写探测文件验证）。</summary>
    /// <param name="preferred">首选目录。</param>
    /// <param name="fallback">降级目录。</param>
    /// <returns>首选可写时返回首选，否则返回降级目录；都不可写时返回首选（后续写入静默失败）。</returns>
    /// <exception cref="ArgumentException">任一参数为空或空白。</exception>
    public static string ResolveWritableDirectory(string preferred, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferred);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);

        if (IsWritableDirectory(preferred))
        {
            return preferred;
        }

        return IsWritableDirectory(fallback) ? fallback : preferred;
    }

    /// <summary>记录普通信息。</summary>
    /// <param name="message">内容。</param>
    public static void Info(string message) => Write("INF", message);

    /// <summary>记录警告。</summary>
    /// <param name="message">内容。</param>
    public static void Warn(string message) => Write("WRN", message);

    /// <summary>批量记录警告：一次加锁、一次刷盘，避免 UI 线程数百次同步 flush。</summary>
    /// <param name="messages">警告内容列表。</param>
    public static void WarnBatch(IReadOnlyList<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return;
        }

        DateTime now = DateTime.Now;
        StringBuilder block = new();
        foreach (string message in messages)
        {
            block.Append(FormatLine(now, "WRN", message)).Append(Environment.NewLine);
        }

        block.Length -= Environment.NewLine.Length;
        WriteBlock(now, block.ToString());
    }

    /// <summary>记录错误。</summary>
    /// <param name="message">内容。</param>
    /// <param name="exception">异常（可选，会附完整堆栈）。</param>
    public static void Error(string message, Exception? exception = null) =>
        Write("ERR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        DateTime now = DateTime.Now;
        WriteBlock(now, FormatLine(now, level, message));
    }

    /// <summary>按日志行格式拼接时间戳与级别（批量写入复用）。</summary>
    /// <param name="now">写入时刻。</param>
    /// <param name="level">级别（INF/WRN/ERR）。</param>
    /// <param name="message">内容（可含换行）。</param>
    /// <returns>完整日志行。</returns>
    private static string FormatLine(DateTime now, string level, string message) =>
        string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");

    /// <summary>一次加锁写入一段（可含多行）文本；自动 flush 只触发一次。</summary>
    /// <param name="now">写入时刻。</param>
    /// <param name="block">已格式化的日志文本。</param>
    private static void WriteBlock(DateTime now, string block)
    {
        lock (Gate)
        {
            if (TryAppendLine(_directory, block, now))
            {
                return;
            }

            // 自动选择目录（启动 Initialize）失败时降级到 %LOCALAPPDATA% 并固定下来；显式目录不降级，避免测试/调用方意外写用户目录。
            if (_explicitDirectory || _fellBack)
            {
                return;
            }

            string fallback = ResolveWritableDirectory(FallbackDirectory, FallbackDirectory);
            if (string.Equals(fallback, _directory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (TryAppendLine(fallback, block, now))
            {
                _directory = fallback;
                _fellBack = true;
            }
        }
    }

    private static bool TryAppendLine(string directory, string line, DateTime now)
    {
        try
        {
            EnsureWriter(directory, now);
            _writer!.WriteLine(line);
            // 按本行 UTF-8 字节数累加，避免每写一行都查询一次文件长度。
            _writerBytes += Encoding.UTF8.GetByteCount(line) + NewLineByteCount;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _lastError = ex.Message;
            CloseWriter();
            return false;
        }
    }

    /// <summary>确保 <paramref name="directory"/> 当天的写入器已打开；需要时切换日期或滚动超限文件。</summary>
    private static void EnsureWriter(string directory, DateTime now)
    {
        string path = _writer is not null
            && _writerPath is not null
            && _writerDate == now.Date
            && string.Equals(
                _writerDirectory,
                Path.TrimEndingDirectorySeparator(directory),
                StringComparison.OrdinalIgnoreCase)
                ? _writerPath
                : CurrentFilePath(directory, now);
        if (_writer is not null && string.Equals(_writerPath, path, StringComparison.OrdinalIgnoreCase))
        {
            if (_writerBytes < MaxLogFileBytes
                || RotationBlocked(path))
            {
                return;
            }

            CloseWriter();
            // 滚动失败（文件被占用/无权限等）时继续写当前文件，避免每行开关文件或直接丢日志。
            _rotationBlockedPath = TryRotate(path) ? null : path;
            _rotationRetryAfterUtc = _rotationBlockedPath is null
                ? default
                : DateTime.UtcNow + RotationRetryInterval;
        }
        else
        {
            CloseWriter();
            _rotationBlockedPath = null;
        }

        OpenWriter(directory, path);
        _writerDate = now.Date;

        // 首次打开时文件就已超限（上次运行残留的超大日志/长时间运行后重启）：先滚动再继续写。
        if (_writerBytes >= MaxLogFileBytes
            && !RotationBlocked(path))
        {
            CloseWriter();
            _rotationBlockedPath = TryRotate(path) ? null : path;
            _rotationRetryAfterUtc = _rotationBlockedPath is null
                ? default
                : DateTime.UtcNow + RotationRetryInterval;
            OpenWriter(directory, path);
            _writerDate = now.Date;
        }
    }

    /// <summary>滚动是否仍处于阻断窗口；窗口到期后允许重试（C-24）。</summary>
    private static bool RotationBlocked(string path) =>
        string.Equals(_rotationBlockedPath, path, StringComparison.OrdinalIgnoreCase)
        && DateTime.UtcNow < _rotationRetryAfterUtc;

    private static void OpenWriter(string directory, string path)
    {
        System.IO.Directory.CreateDirectory(directory);
        // FileShare.Delete：日志文件允许被删除/轮转（测试临时目录、外部清理工具），不影响本进程继续写。
        FileStream stream = new(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        _writerPath = path;
        _writerDirectory = Path.TrimEndingDirectorySeparator(directory);
        _writerBytes = stream.Length;
    }

    /// <summary>把超过大小上限的当前日志改名为 <c>hunterbox-yyyyMMdd.N.log</c>（序号不覆盖历史）。</summary>
    /// <param name="path">当前日志文件路径。</param>
    /// <returns>确实完成滚动时返回 <see langword="true"/>；文件被占用等失败时返回 <see langword="false"/>。</returns>
    private static bool TryRotate(string path)
    {
        try
        {
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            for (int index = 1; index < 1000; index++)
            {
                string archived = Path.Combine(
                    directory,
                    $"{name}.{index.ToString(CultureInfo.InvariantCulture)}{extension}");
                if (File.Exists(archived))
                {
                    continue;
                }

                File.Move(path, archived);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lastError = ex.Message;
            return false;
        }
    }

    private static string CurrentFilePath(string directory, DateTime timestamp) =>
        Path.Combine(directory, $"hunterbox-{timestamp:yyyyMMdd}.log");

    private static void CloseWriter()
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            _writer.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _lastError = ex.Message;
        }
        finally
        {
            _writer = null;
            _writerPath = null;
            _writerBytes = 0;
        }
    }

    /// <summary>进程退出时刷盘并关闭常驻写入器；最后一步不允许抛异常。</summary>
    private static void Shutdown()
    {
        try
        {
            lock (Gate)
            {
                CloseWriter();
            }
        }
        catch (Exception)
        {
            // 进程退出路径：任何异常都不能影响退出码，日志丢失也只能接受。
        }
    }

    private static bool TryCreateDirectory(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lastError = ex.Message;
            return false;
        }
    }

    private static bool IsWritableDirectory(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".hunterbox-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lastError = ex.Message;
            return false;
        }
    }
}
