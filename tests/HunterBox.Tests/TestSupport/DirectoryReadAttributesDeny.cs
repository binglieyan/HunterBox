using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HunterBox.Tests;

/// <summary>给目录临时加一条「拒绝读取属性」的 ACL，Dispose 时移除（仅 Windows）。</summary>
/// <remarks>
/// 用于验证 <c>GetDirectoryExistenceAndLastWriteTimeUtc</c> 能区分「缺失」与「无权限」：
/// 拒绝 <see cref="FileSystemRights.ReadAttributes"/> 后，读取元数据应抛 <see cref="UnauthorizedAccessException"/>。
/// 环境不支持（非 Windows/无法设置 ACL/ACL 未生效）时由 <see cref="DirectoryAttributeAclFactAttribute"/>
/// 在发现阶段跳过。
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class DirectoryReadAttributesDeny : IDisposable
{
    private static readonly Lazy<bool> Support = new(Probe);

    private readonly DirectoryInfo _directory;
    private readonly FileSystemAccessRule _rule;

    private DirectoryReadAttributesDeny(DirectoryInfo directory, FileSystemAccessRule rule)
    {
        _directory = directory;
        _rule = rule;
    }

    /// <summary>当前环境是否支持「拒绝读取属性」ACL 且确实阻止元数据读取。</summary>
    public static bool IsSupported => Support.Value;

    /// <summary>对指定目录应用拒绝读取属性的规则。</summary>
    /// <param name="directory">目标目录。</param>
    /// <returns>Dispose 时移除规则的句柄。</returns>
    public static DirectoryReadAttributesDeny Apply(string directory)
    {
        DirectoryInfo info = new(directory);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        FileSystemAccessRule rule = new(
            identity.User!,
            FileSystemRights.ReadAttributes,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);

        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new DirectoryReadAttributesDeny(info, rule);
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
        string root = Path.Combine(Path.GetTempPath(), "hunterbox-attr-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            using DirectoryReadAttributesDeny deny = Apply(root);
            try
            {
                _ = new DirectoryInfo(root).Attributes;
                // 当前环境（如具备备份权限）仍能读取属性：用例无法模拟无权限，跳过而不是静默通过。
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
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
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 探测残留由系统 Temp 清理。
            }
        }
    }
}

/// <summary>要求环境支持「拒绝读取目录属性」ACL 的用例特性。</summary>
internal sealed class DirectoryAttributeAclFactAttribute : FactAttribute
{
    public DirectoryAttributeAclFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "非 Windows 环境无法设置目录 ACL，跳过无权限用例。";
        }
        else if (!DirectoryReadAttributesDeny.IsSupported)
        {
            Skip = "当前环境无法用 ACL 阻止读取目录属性，跳过无权限用例。";
        }
    }
}
