using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HunterBox.Application.Common;
using Microsoft.Win32.SafeHandles;

namespace HunterBox.Tests;

public class TempHousekeepingTests
{
    [Fact]
    public void PurgeStale_RemovesOldEntriesAndKeepsRecent()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("Temp");
        string oldDirectory = Path.Combine(root, "old-work");
        Directory.CreateDirectory(oldDirectory);
        File.WriteAllText(Path.Combine(oldDirectory, "a.txt"), "x");
        string readOnly = Path.Combine(oldDirectory, "readonly.txt");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        string newDirectory = Path.Combine(root, "new-work");
        Directory.CreateDirectory(newDirectory);
        File.WriteAllText(Path.Combine(newDirectory, "a.txt"), "x");
        string oldFile = Path.Combine(root, "old.tmp");
        File.WriteAllText(oldFile, "x");
        File.SetAttributes(oldFile, FileAttributes.ReadOnly);
        string newFile = Path.Combine(root, "new.tmp");
        File.WriteAllText(newFile, "x");

        DateTimeOffset now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        Directory.SetLastWriteTimeUtc(oldDirectory, now.UtcDateTime.AddDays(-30));
        Directory.SetLastWriteTimeUtc(newDirectory, now.UtcDateTime.AddHours(-1));
        File.SetLastWriteTimeUtc(oldFile, now.UtcDateTime.AddDays(-30));
        File.SetLastWriteTimeUtc(newFile, now.UtcDateTime.AddHours(-1));

        int removed = TempHousekeeping.PurgeStale(root, TimeSpan.FromDays(7), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, now);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(oldDirectory));
        Assert.False(File.Exists(oldFile));
        Assert.True(Directory.Exists(newDirectory));
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public void PurgeStale_PreCanceled_ThrowsAndKeepsEntries()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("Temp");
        string oldDirectory = Path.Combine(root, "old-work");
        Directory.CreateDirectory(oldDirectory);
        DateTimeOffset now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        Directory.SetLastWriteTimeUtc(oldDirectory, now.UtcDateTime.AddDays(-30));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            TempHousekeeping.PurgeStale(root, TimeSpan.FromDays(7), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, now, cancellation.Token));
        Assert.True(Directory.Exists(oldDirectory));
    }

    [Fact]
    public void PurgeStale_MissingRoot_ReturnsZero()
    {
        using TempDir temp = TempDir.Create();
        string missing = Path.Combine(temp.Path, "missing");

        int removed = TempHousekeeping.PurgeStale(missing, TimeSpan.FromDays(7), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, DateTimeOffset.UtcNow);

        Assert.Equal(0, removed);
    }

    [Fact]
    public void PurgeStale_NonPositiveRetention_Throws()
    {
        using TempDir temp = TempDir.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TempHousekeeping.PurgeStale(temp.Path, TimeSpan.Zero, HunterBox.Infrastructure.IO.SystemFileSystem.Instance, DateTimeOffset.UtcNow));
    }

    /// <summary>根目录本身是目录链接时，只删链接，不进入目标。</summary>
    [DirectoryLinkFact]
    public void TryDeleteDirectoryForce_RootIsDirectoryLink_DeletesLinkOnly()
    {
        using TempDir temp = TempDir.Create();
        string target = temp.Dir("external");
        string keep = Path.Combine(target, "keep.txt");
        File.WriteAllText(keep, "keep");
        string link = Path.Combine(temp.Path, "old-link");
        Assert.True(
            LinkTestSupport.TryCreateDirectoryLink(link, target),
            "无法创建目录链接（符号链接与 junction 均失败），链接防护用例无法执行");

        Assert.True(HunterBox.Infrastructure.IO.SystemFileSystem.Instance.TryDeleteDirectoryForce(link));

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(keep));
    }

    /// <summary>子树含目录链接时 force 删除整体跳过，链接与目标都保留。</summary>
    [DirectoryLinkFact]
    public void TryDeleteDirectoryForce_SubtreeContainingLink_ReturnsFalseAndKeepsTarget()
    {
        using TempDir temp = TempDir.Create();
        string target = temp.Dir("external");
        string keep = Path.Combine(target, "keep.txt");
        File.WriteAllText(keep, "keep");
        string root = temp.Dir("old-work");
        string link = Path.Combine(root, "link");
        Assert.True(
            LinkTestSupport.TryCreateDirectoryLink(link, target),
            "无法创建目录链接（符号链接与 junction 均失败），链接防护用例无法执行");

        try
        {
            Assert.False(HunterBox.Infrastructure.IO.SystemFileSystem.Instance.TryDeleteDirectoryForce(root));
            Assert.True(Directory.Exists(link));
            Assert.True(File.Exists(keep));
        }
        finally
        {
            // 用例故意保留链接；递归删除无法处理链接，先单独移除链接本身。
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }
        }
    }

    /// <summary>子树无法枚举时必须 fail-closed，不进入递归删除。</summary>
    [AclFact]
    [SupportedOSPlatform("windows")]
    public void TryDeleteDirectoryForce_UnreadableSubtree_ReturnsFalseAndKeepsTree()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("old-work");
        string keep = Path.Combine(root, "keep.txt");
        File.WriteAllText(keep, "keep");
        string blocked = Path.Combine(root, "blocked");
        Directory.CreateDirectory(blocked);
        using DirectoryListDeny deny = DirectoryListDeny.Apply(blocked);

        Assert.False(HunterBox.Infrastructure.IO.SystemFileSystem.Instance.TryDeleteDirectoryForce(root));
        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(blocked));
        Assert.True(File.Exists(keep));
    }

    /// <summary>断链（目标已删除的目录链接）也能被清理。</summary>
    [DirectoryLinkFact]
    public void TryDeleteDirectoryForce_BrokenDirectoryLink_DeletesLink()
    {
        using TempDir temp = TempDir.Create();
        string target = temp.Dir("external");
        string link = Path.Combine(temp.Path, "broken-link");
        Assert.True(LinkTestSupport.TryCreateDirectoryLink(link, target), "无法创建目录链接（符号链接与 junction 均失败）");
        Directory.Delete(target, recursive: true);

        Assert.True(HunterBox.Infrastructure.IO.SystemFileSystem.Instance.TryDeleteDirectoryForce(link));
        Assert.False(Directory.Exists(link));
    }

    /// <summary>断链文件链接只删链接本身。</summary>
    [FileSymlinkFact]
    public void TryDeleteFileForce_BrokenFileLink_DeletesLinkOnly()
    {
        using TempDir temp = TempDir.Create();
        string target = Path.Combine(temp.Path, "external.txt");
        File.WriteAllText(target, "keep");
        string link = Path.Combine(temp.Path, "broken-link.txt");
        Assert.True(LinkTestSupport.TryCreateFileLink(link, target), "文件符号链接创建失败");
        File.Delete(target);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);

        Assert.True(HunterBox.Infrastructure.IO.SystemFileSystem.Instance.TryDeleteFileForce(link));
        Assert.False(File.Exists(link));
    }

    /// <summary>文件链接只删链接本身，目标内容与属性不变。</summary>
    [FileSymlinkFact]
    public void TryDeleteFileForce_LinkToExternalFile_DeletesLinkOnly()
    {
        using TempDir temp = TempDir.Create();
        string target = Path.Combine(temp.Path, "external.txt");
        File.WriteAllText(target, "keep");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        string link = Path.Combine(temp.Path, "old-link.txt");
        Assert.True(LinkTestSupport.TryCreateFileLink(link, target), "文件符号链接创建失败");
        try
        {
            Assert.True(HunterBox.Infrastructure.IO.SystemFileSystem.Instance.TryDeleteFileForce(link));

            Assert.False(File.Exists(link));
            Assert.True(File.Exists(target));
            Assert.Equal("keep", File.ReadAllText(target));
            Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }
}

/// <summary>当前环境无法创建文件符号链接时，在发现阶段标记为跳过（xUnit v2 没有运行时跳过 API）。</summary>
internal sealed class FileSymlinkFactAttribute : FactAttribute
{
    public FileSymlinkFactAttribute()
    {
        if (!LinkTestSupport.CanCreateFileSymlink)
        {
            Skip = "当前环境无法创建文件符号链接（未启用开发者模式且非管理员），跳过文件链接防护用例。";
        }
    }
}

/// <summary>当前环境既不能创建符号链接、也不能创建 junction 时，在发现阶段标记为跳过。</summary>
internal sealed class DirectoryLinkFactAttribute : FactAttribute
{
    public DirectoryLinkFactAttribute()
    {
        if (!LinkTestSupport.CanCreateDirectoryLink)
        {
            Skip = "当前环境无法创建目录链接（符号链接与 junction 均不可用），跳过目录链接防护用例。";
        }
    }
}

/// <summary>链接测试支撑：目录链接优先符号链接，失败退化为 junction（免管理员权限）。</summary>
internal static class LinkTestSupport
{
    private static readonly Lazy<bool> FileSymlinkSupport = new(ProbeFileSymlink);
    private static readonly Lazy<bool> DirectoryLinkSupport = new(ProbeDirectoryLink);

    public static bool CanCreateFileSymlink => FileSymlinkSupport.Value;

    public static bool CanCreateDirectoryLink => DirectoryLinkSupport.Value;

    public static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 无符号链接权限时继续尝试 junction。
        }

        return TryCreateJunction(link, target);
    }

    public static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>用 FSCTL_SET_REPARSE_POINT 创建 junction：不经过 cmd.exe，路径无需命令行转义。</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(link);
            using SafeFileHandle handle = CreateFileW(
                link,
                GenericWrite,
                FileShareAll,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                Directory.Delete(link, recursive: false);
                return TryCreateJunctionWithCommand(link, target);
            }

            byte[] buffer = BuildMountPointBuffer(Path.GetFullPath(target));
            if (DeviceIoControl(
                handle,
                FsctlSetReparsePoint,
                buffer,
                buffer.Length,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
            {
                return true;
            }

            Directory.Delete(link, recursive: false);
            return TryCreateJunctionWithCommand(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return TryCreateJunctionWithCommand(link, target);
        }
    }

    /// <summary>部分 Windows/沙箱环境下 DeviceIoControl 创建 junction 会被拒绝，退化为系统 mklink /J。</summary>
    /// <param name="link">链接路径。</param>
    /// <param name="target">目标目录。</param>
    /// <returns>创建成功时返回 <see langword="true"/>。</returns>
    private static bool TryCreateJunctionWithCommand(string link, string target)
    {
        try
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }

            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>构造 REPARSE_DATA_BUFFER（IO_REPARSE_TAG_MOUNT_POINT，无 Flags 字段）。</summary>
    private static byte[] BuildMountPointBuffer(string target)
    {
        string substitute = @"\??\" + target;
        int substituteBytes = (substitute.Length + 1) * 2;
        int printBytes = (target.Length + 1) * 2;
        int dataLength = 8 + substituteBytes + printBytes;
        byte[] buffer = new byte[8 + dataLength];

        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), IoReparseTagMountPoint);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), (ushort)dataLength);
        // 偏移 6 的 Reserved 保持 0。
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 2), (ushort)0);                // SubstituteNameOffset
        BitConverter.TryWriteBytes(buffer.AsSpan(10, 2), (ushort)substituteBytes); // SubstituteNameLength
        BitConverter.TryWriteBytes(buffer.AsSpan(12, 2), (ushort)substituteBytes); // PrintNameOffset
        BitConverter.TryWriteBytes(buffer.AsSpan(14, 2), (ushort)printBytes);      // PrintNameLength
        Encoding.Unicode.GetBytes(substitute + '\0', buffer.AsSpan(16, substituteBytes));
        Encoding.Unicode.GetBytes(target + '\0', buffer.AsSpan(16 + substituteBytes, printBytes));
        return buffer;
    }

    private const uint GenericWrite = 0x40000000;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[] inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    private static bool ProbeFileSymlink()
    {
        string root = Path.Combine(Path.GetTempPath(), "hb-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, "target.txt");
            File.WriteAllText(target, "x");
            return TryCreateFileLink(Path.Combine(root, "link.txt"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            TryDeleteProbe(root, link: null);
        }
    }

    private static bool ProbeDirectoryLink()
    {
        // 探测可能被瞬时占用/杀毒扫描打断：多试几次，避免整组链接用例被误跳过。
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string root = Path.Combine(Path.GetTempPath(), "hb-probe-" + Guid.NewGuid().ToString("N"));
            string link = Path.Combine(root, "link");
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "target"));
                if (TryCreateDirectoryLink(link, Path.Combine(root, "target")))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // 换一个临时目录重试。
            }
            finally
            {
                TryDeleteProbe(root, link);
            }

            if (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }

    private static void TryDeleteProbe(string root, string? link)
    {
        try
        {
            // 链接必须先单独删除：递归删除无法处理 junction。
            if (link is not null && Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }

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

/// <summary>当前环境无法设置目录 ACL 时，在发现阶段标记为跳过。</summary>
internal sealed class AclFactAttribute : FactAttribute
{
    public AclFactAttribute()
        : this(requireWriteDeny: false)
    {
    }

    /// <param name="requireWriteDeny">
    /// 为 <see langword="true"/> 时要求环境支持「拒绝写入」ACL（<see cref="DirectoryWriteDeny"/>）；
    /// 否则要求支持「拒绝列出内容」ACL（<see cref="DirectoryListDeny"/>）。
    /// </param>
    public AclFactAttribute(bool requireWriteDeny)
    {
        RequireWriteDeny = requireWriteDeny;
        if (!OperatingSystem.IsWindows())
        {
            Skip = "非 Windows 环境无法设置目录 ACL，跳过 ACL 用例。";
        }
        else if (requireWriteDeny && !DirectoryWriteDeny.IsSupported)
        {
            Skip = "当前环境无法设置目录「拒绝写入」ACL，跳过写拒绝用例。";
        }
        else if (!requireWriteDeny && !DirectoryListDeny.IsSupported)
        {
            Skip = "当前环境无法设置目录 ACL（非 Windows 或权限不足），跳过不可读子树用例。";
        }
    }

    /// <summary>是否要求环境支持「拒绝写入」ACL；对应的诊断属性只用于构造期判定。</summary>
    public bool RequireWriteDeny { get; }
}

/// <summary>给目录临时加一条「拒绝列出内容」的 ACL，Dispose 时移除（仅 Windows）。</summary>
[SupportedOSPlatform("windows")]
internal sealed class DirectoryListDeny : IDisposable
{
    private static readonly Lazy<bool> Support = new(Probe);

    private readonly DirectoryInfo _directory;
    private readonly FileSystemAccessRule _rule;

    private DirectoryListDeny(DirectoryInfo directory, FileSystemAccessRule rule)
    {
        _directory = directory;
        _rule = rule;
    }

    /// <summary>当前环境是否允许对目录设置 ACL。</summary>
    public static bool IsSupported => Support.Value;

    public static DirectoryListDeny Apply(string directory)
    {
        DirectoryInfo info = new(directory);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        FileSystemAccessRule rule = new(
            identity.User!,
            FileSystemRights.ListDirectory,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);

        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new DirectoryListDeny(info, rule);
    }

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

        string root = Path.Combine(Path.GetTempPath(), "hb-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            using DirectoryListDeny deny = Apply(root);
            return true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
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
