using HunterBox.Application.Common;
using HunterBox.Application.Settings;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.Application.Install;

/// <summary>安装源归档方式（对应 INI <c>[settings] install_option</c>，取值 0/1/2）。</summary>
public enum InstallSourceMode
{
    /// <summary>无操作（默认）：不复制/移动原始压缩包，仅记录来源路径。</summary>
    None = 0,

    /// <summary>把安装包复制到HunterBox的 <c>Download</c> 目录。</summary>
    Copy = 1,

    /// <summary>把安装包移动到HunterBox的 <c>Download</c> 目录。</summary>
    Move = 2,
}

/// <summary>读取「安装选项」设置。</summary>
public static class InstallSourceSettings
{
    /// <summary>INI 段名。</summary>
    public const string Section = SettingsKeys.Section;

    /// <summary>INI 键名。</summary>
    public const string Key = "install_option";

    /// <summary>解析设置值：0=无操作 1=复制 2=移动；非法值回退 0。</summary>
    /// <param name="raw">INI 原始值。</param>
    /// <returns>安装源归档方式。</returns>
    public static InstallSourceMode Parse(string? raw)
    {
        if (int.TryParse(raw?.Trim(), out int value) && value is >= 0 and <= 2)
        {
            return (InstallSourceMode)value;
        }

        return InstallSourceMode.None;
    }
}

/// <summary>安装包归档（缓存到 Download）的结果。</summary>
/// <param name="InstallSource">归档后的安装源路径（归档失败/无操作时为原路径）。</param>
/// <param name="Warnings">归档过程中的提示（失败不抛出，仅提示）。</param>
public sealed record InstallSourceArchiveResult(string? InstallSource, IReadOnlyList<string> Warnings);

/// <summary>
/// 把原始安装包归档到 <c>&lt;Root&gt;/Download/&lt;显示名&gt;.&lt;扩展名&gt;</c>。
/// </summary>
/// <remarks>
/// <para>复制/移动原始压缩包并按 MOD 显示名重命名，重名时追加
/// <c>(2)</c>/<c>(3)</c> 后缀，不覆盖已有归档。</para>
/// <para>失败不留半归档：先写 <c>.tmp</c> 再改名；任何一步失败都会删除临时文件并返回 warning，
/// 不会让归档失败中断已成功的安装。</para>
/// </remarks>
public static class InstallSourceArchiver
{
    /// <summary>归档安装源。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="fileSystem">文件系统端口（失败清理用）。</param>
    /// <param name="sourcePath">原始安装源（压缩包或目录）。</param>
    /// <param name="displayName">MOD 显示名（决定归档文件名）。</param>
    /// <param name="mode">归档方式。</param>
    /// <returns>归档结果（含新的安装源路径与提示）。</returns>
    public static InstallSourceArchiveResult Archive(
        BoxPaths paths,
        IFileSystem fileSystem,
        string? sourcePath,
        string? displayName,
        InstallSourceMode mode)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new InstallSourceArchiveResult(null, ["安装源路径为空，未记录安装来源"]);
        }

        if (fileSystem.DirectoryExists(sourcePath))
        {
            // 文件夹安装本身就在磁盘上，不做归档，只记录来源。
            return new InstallSourceArchiveResult(sourcePath, []);
        }

        if (!fileSystem.FileExists(sourcePath))
        {
            return new InstallSourceArchiveResult(null, [$"安装源不存在，未记录安装来源：{sourcePath}"]);
        }

        if (IsInside(paths.DownloadDirectory, sourcePath))
        {
            return new InstallSourceArchiveResult(sourcePath, []);
        }

        if (mode == InstallSourceMode.None)
        {
            return new InstallSourceArchiveResult(sourcePath, []);
        }

        List<string> warnings = [];
        string downloadDirectory = paths.DownloadDirectory;
        string extension = Path.GetExtension(sourcePath);
        string baseName = SanitizeFileName(displayName) + extension;
        string target = BuildUniquePath(downloadDirectory, baseName, fileSystem);
        string temp = Path.Combine(downloadDirectory, ".install-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            fileSystem.CreateDirectory(downloadDirectory);
            fileSystem.CopyFile(sourcePath, temp, overwrite: true);
            fileSystem.MoveFile(temp, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            fileSystem.TryDeleteFile(temp);
            return new InstallSourceArchiveResult(
                sourcePath,
                [$"安装源归档失败（已继续使用原安装源）：{ex.Message}"]);
        }

        if (mode == InstallSourceMode.Move)
        {
            try
            {
                fileSystem.DeleteFile(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"安装包已复制到 Download，但原文件删除失败（可手动删除）：{sourcePath}（{ex.Message}）");
            }
        }

        return new InstallSourceArchiveResult(target, warnings);
    }

    /// <summary>在 <paramref name="directory"/> 下生成不冲突的文件全路径（重名追加 (2)(3)…）。</summary>
    /// <param name="directory">目标目录。</param>
    /// <param name="fileName">期望文件名。</param>
    /// <param name="fileSystem">文件系统端口（存在性判断）。</param>
    /// <returns>不冲突的全路径。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    public static string BuildUniquePath(string directory, string fileName, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(fileSystem);

        string candidate = Path.Combine(directory, fileName);
        if (!fileSystem.FileExists(candidate) && !fileSystem.DirectoryExists(candidate))
        {
            return candidate;
        }

        string name = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 2; index < 1000; index++)
        {
            candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!fileSystem.FileExists(candidate) && !fileSystem.DirectoryExists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>把显示名清洗成安全的文件名（去掉路径字符与控制字符）。</summary>
    /// <param name="displayName">MOD 显示名。</param>
    /// <returns>安全文件名（不含扩展名）。</returns>
    public static string SanitizeFileName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "MOD";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = [.. displayName.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)];
        string sanitized = new string(chars).Trim().TrimEnd('.').Trim();
        if (sanitized.Length == 0)
        {
            return "MOD";
        }

        return sanitized.Length > 120 ? sanitized[..120] : sanitized;
    }

    private static bool IsInside(string directory, string filePath)
    {
        try
        {
            string fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullFile = Path.GetFullPath(filePath);
            string? parent = Path.GetDirectoryName(fullFile);
            return parent is not null
                && string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

}

/// <summary>重装/更新的安装源定位（对应：优先用记录的安装源，缺失再让用户选包）。</summary>
public static class InstallSourceLocator
{
    /// <summary>找到记录里仍然存在的安装源路径。</summary>
    /// <param name="record">MOD 记录。</param>
    /// <param name="fileSystem">文件系统端口（存在性判断）。</param>
    /// <returns>存在的安装源路径；未记录或已缺失时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    public static string? FindExisting(ModRecord? record, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (record is null || string.IsNullOrWhiteSpace(record.InstallSource))
        {
            return null;
        }

        string source = record.InstallSource;
        return fileSystem.FileExists(source) || fileSystem.DirectoryExists(source) ? source : null;
    }

    /// <summary>取记录里记录的安装源（即使已缺失），用于界面提示。</summary>
    /// <param name="record">MOD 记录。</param>
    /// <returns>记录的安装源路径；没有时为 <see langword="null"/>。</returns>
    public static string? GetRecorded(ModRecord? record) =>
        record is null || string.IsNullOrWhiteSpace(record.InstallSource) ? null : record.InstallSource;
}
