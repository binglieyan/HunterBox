using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace HunterBox.Infrastructure.Startup;

/// <summary>
/// 单实例守卫：用命名 <see cref="Mutex"/> 判断是否已有同一用户/会话的实例在运行。
/// </summary>
/// <remarks>
/// <para>
/// HunterBox的 MOD 记录集中存放在 <c>Mods/index.json</c>，两个实例并发保存会整表覆盖、互相吞掉新增/改动，
/// 备份目录也会被共享。因此第二个实例只提示并退出，不创建窗口。
/// </para>
/// <para>
/// 名字用 <c>Local\</c> 前缀（按 Windows 会话隔离）+ 当前用户 SID：同一会话切换用户、
/// 或多用户同时登录时互不误判。
/// </para>
/// <para>
/// 判定方式：<c>WaitOne(0)</c> 取得成功 = 第一个实例，并在进程存活期间<b>持有</b>互斥体所有权，
/// <see cref="Dispose"/> 时 <see cref="Mutex.ReleaseMutex"/>。只依赖创建结果（<c>createdNew</c>）不可靠：
/// A 运行 → B 提示中 → A 退出 → C 启动时，B 仍打开着互斥体句柄，<c>createdNew</c> 会误判 C 为「已有实例」。
/// </para>
/// <para>
/// 上一个实例异常退出（未释放所有权）时，<c>WaitOne(0)</c> 抛 <see cref="AbandonedMutexException"/>，
/// 但所有权已归本线程，同样视为第一个实例，因此不会出现「崩溃一次后再也打不开」。
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>默认产品标识（用于 <see cref="CurrentName"/> 拼名字）。</summary>
    public const string DefaultAppId = "HunterBox";

    private readonly Mutex _mutex;

    /// <summary>Win32 互斥体所有权是线程级的：<see cref="Mutex.ReleaseMutex"/> 必须由取得它的线程调用。</summary>
    private readonly int _ownerThreadId;

    private bool _ownsMutex;
    private bool _disposed;

    /// <summary>创建（或打开）指定名字的单实例互斥体，并尝试立即取得所有权。</summary>
    /// <param name="name">完整的互斥体名字（建议用 <see cref="BuildName"/> 生成，含 <c>Local\</c> 前缀）。</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空或空白。</exception>
    public SingleInstanceGuard(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        _mutex = new Mutex(initiallyOwned: false, name);
        _ownerThreadId = Environment.CurrentManagedThreadId;
        IsFirstInstance = TryAcquireOwnership();
    }

    /// <summary>互斥体名字。</summary>
    public string Name { get; }

    /// <summary>是否为本用户/本会话的第一个实例（且已取得互斥体所有权）；<see langword="false"/> 表示已有实例在运行。</summary>
    public bool IsFirstInstance { get; }

    /// <summary>拼当前进程的单实例名字（会话 ID + 用户 SID 隔离）。</summary>
    /// <returns>形如 <c>Local\HunterBox.mhws.1.S-1-5-21-…</c>的名字。</returns>
    public static string CurrentName() =>
        BuildName(DefaultAppId, Process.GetCurrentProcess().SessionId, CurrentUserSid());

    /// <summary>拼单实例名字（可单测的纯逻辑）。</summary>
    /// <param name="appId">产品标识（如 <c>HunterBox</c>）。</param>
    /// <param name="sessionId">Windows 会话 ID（不同登录会话互不影响）。</param>
    /// <param name="userSid">当前用户 SID；为空时用 <c>default</c> 占位。</param>
    /// <returns>带 <c>Local\</c> 前缀的互斥体名字。</returns>
    /// <exception cref="ArgumentException"><paramref name="appId"/> 为空或空白。</exception>
    public static string BuildName(string appId, int sessionId, string? userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        string user = string.IsNullOrWhiteSpace(userSid) ? "default" : Sanitize(userSid);
        return $@"Local\{Sanitize(appId)}.{sessionId}.{user}";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_ownsMutex)
            {
                _ownsMutex = false;
                if (Environment.CurrentManagedThreadId == _ownerThreadId)
                {
                    _mutex.ReleaseMutex();
                }

                // 跨线程 Dispose：Mutex 只能由取得所有权的线程释放，这里放弃显式释放，
                // 由本进程退出时的 abandoned 语义兜底（下一个实例会按「第一个实例」接管）。
            }
        }
        catch (Exception ex) when (ex is ApplicationException or ObjectDisposedException)
        {
            // Dispose 不能抛：释放失败最坏只是让下一个实例走 abandoned 接管路径。
        }
        finally
        {
            _mutex.Dispose();
        }
    }

    /// <summary>尝试非阻塞取得互斥体所有权；无法取得（已有实例持有）时返回 <see langword="false"/>。</summary>
    /// <returns>取得所有权时为 <see langword="true"/>。</returns>
    private bool TryAcquireOwnership()
    {
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例未释放所有权就结束：所有权已归本线程，继续当第一个实例。
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    /// <summary>取当前用户 SID；非 Windows/受限环境取不到时退化为用户名（仍可区分用户）。</summary>
    private static string? CurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Environment.UserName;
        }

        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return Environment.UserName;
        }
    }

    /// <summary>互斥体名字只保留安全字符（用户名可能含 <c>\</c> 等）。</summary>
    private static string Sanitize(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        }

        return builder.Length == 0 ? "default" : builder.ToString();
    }
}
