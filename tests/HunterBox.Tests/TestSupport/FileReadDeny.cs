using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HunterBox.Tests;

/// <summary>给文件临时加一条「拒绝读取数据」的 ACL，Dispose 时移除（仅 Windows）。</summary>
/// <remarks>
/// 用于构造「文件属性可读、内容不可读」的场景，验证旧数据检测会把该文件计入
/// <c>UnreadablePaths</c>。权限不足或非 Windows 环境由 <see cref="FileAclFactAttribute"/> 在发现阶段跳过。
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class FileReadDeny : IDisposable
{
    private static readonly Lazy<bool> Support = new(Probe);

    private readonly FileInfo _file;
    private readonly FileSystemAccessRule _rule;

    private FileReadDeny(FileInfo file, FileSystemAccessRule rule)
    {
        _file = file;
        _rule = rule;
    }

    /// <summary>当前环境是否允许对文件设置「拒绝读取」ACL。</summary>
    public static bool IsSupported => Support.Value;

    /// <summary>给文件加一条拒绝读取数据的 ACL；Dispose 时移除。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>可撤销的 ACL 作用域。</returns>
    public static FileReadDeny Apply(string path)
    {
        FileInfo info = new(path);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        FileSystemAccessRule rule = new(
            identity.User!,
            FileSystemRights.ReadData,
            AccessControlType.Deny);

        FileSecurity security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new FileReadDeny(info, rule);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            FileSecurity security = _file.GetAccessControl();
            security.RemoveAccessRule(_rule);
            _file.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 清理失败时临时文件会随 TempDir 一起被清理；不掩盖测试断言。
        }
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string path = Path.Combine(Path.GetTempPath(), "hb-file-probe-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "x");
            using FileReadDeny deny = Apply(path);
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 探测残留由系统 Temp 清理。
            }
        }
    }
}

/// <summary>当前环境不能设置文件读取拒绝 ACL 时，在发现阶段标记为跳过。</summary>
internal sealed class FileAclFactAttribute : FactAttribute
{
    public FileAclFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "非 Windows 环境无法设置文件 ACL，跳过文件不可读用例。";
        }
        else if (!FileReadDeny.IsSupported)
        {
            Skip = "当前环境无法设置文件「拒绝读取」ACL，跳过文件不可读用例。";
        }
    }
}
