using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HunterBox.Tests;

/// <summary>给目录临时加一条「拒绝创建子目录/写文件」的 ACL，Dispose 时移除（仅 Windows）。</summary>
/// <remarks>
/// 供需要模拟「父目录只读」的测试使用；<see cref="IsSupported"/> 在发现阶段探测当前环境能否设置该 ACL，
/// 测试通过 <see cref="AclFactAttribute"/> 在无权限环境下显示 Skipped 而不是静默通过。
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class DirectoryWriteDeny : IDisposable
{
    private static readonly Lazy<bool> Support = new(Probe);

    private readonly DirectoryInfo _directory;
    private readonly FileSystemAccessRule _rule;

    private DirectoryWriteDeny(DirectoryInfo directory, FileSystemAccessRule rule)
    {
        _directory = directory;
        _rule = rule;
    }

    /// <summary>当前环境是否允许对目录设置「拒绝写入」ACL。</summary>
    public static bool IsSupported => Support.Value;

    /// <summary>对指定目录应用拒绝写入规则。</summary>
    /// <param name="directory">目标目录。</param>
    /// <returns>Dispose 时移除规则的句柄。</returns>
    public static DirectoryWriteDeny Apply(string directory)
    {
        DirectoryInfo info = new(directory);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        FileSystemAccessRule rule = new(
            identity.User!,
            FileSystemRights.WriteData | FileSystemRights.CreateDirectories,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);

        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new DirectoryWriteDeny(info, rule);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DirectorySecurity security = _directory.GetAccessControl();
        security.RemoveAccessRule(_rule);
        _directory.SetAccessControl(security);
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string directory = Path.Combine(Path.GetTempPath(), "hunterbox-acl-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            using DirectoryWriteDeny deny = Apply(directory);
            return true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException
            or IOException
            or UnauthorizedAccessException
            or SecurityException)
        {
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 探测残留由系统 Temp 清理。
            }
        }
    }
}
