using System.Security.Cryptography;
using System.Text;
using HunterBox.Application.Common;

namespace HunterBox.Application.Saves;

/// <summary>
/// 备份哈希清单的读写与路径校验（A-11）：备份时一次读盘写清单，还原时只读一次并复用。
/// </summary>
/// <remarks>清单的文本编解码经 <see cref="IBackupManifestStore"/> 端口交给 Infrastructure。</remarks>
internal static class SaveBackupManifest
{
    /// <summary>备份哈希清单文件名（存放在每个备份目录根下，不参与还原到游戏目录）。</summary>
    internal const string FileName = "hunterbox-backup-hashes.json";

    /// <summary>哈希清单的读写字节上限（16MB）：正常 5000 条约 0.7MB，读侧按不可读处理，写侧拒绝落盘。</summary>
    internal const long MaxManifestBytes = 16 * 1024 * 1024;

    /// <summary>把哈希清单原子写入备份目录。</summary>
    /// <param name="backupDirectory">备份目录。</param>
    /// <param name="entries">已计算的清单项；调用方保证路径已校验。</param>
    /// <param name="clock">写入清单用的时钟。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="manifestStore">清单文本编解码端口。</param>
    /// <exception cref="InvalidDataException">序列化后的清单超过 <see cref="MaxManifestBytes"/>。</exception>
    /// <remarks>直接序列化条目，不再先转换成 <see cref="HashManifestFile"/> 再转回。</remarks>
    internal static void WriteManifest(
        string backupDirectory,
        IReadOnlyList<BackupManifestEntry> entries,
        IClock clock,
        IFileSystem fileSystem,
        IBackupManifestStore manifestStore)
    {
        ArgumentNullException.ThrowIfNull(manifestStore);
        string text = manifestStore.Serialize(clock.UtcNow, entries);

        // 读侧上限必须在写侧同样生效，避免生成一个之后必然被判「不可读」的备份。
        int textBytes = Encoding.UTF8.GetByteCount(text);
        if (textBytes > MaxManifestBytes)
        {
            throw new InvalidDataException(
                $"备份哈希清单超过上限（{textBytes} > {MaxManifestBytes} 字节），"
                + $"请减少备份文件数或拆分备份：{Path.Combine(backupDirectory, FileName)}");
        }

        string manifestPath = Path.Combine(backupDirectory, FileName);
        string temp = manifestPath + ".tmp";
        try
        {
            fileSystem.WriteAllText(temp, text);
            fileSystem.MoveFile(temp, manifestPath, overwrite: true);
        }
        finally
        {
            // 写入/改名失败时清理临时清单，避免 .tmp 残留被复制进备份或计入体积。
            if (fileSystem.FileExists(temp))
            {
                _ = fileSystem.TryDeleteFile(temp);
            }
        }
    }

    /// <summary>判断相对路径是否为备份根目录下的清单文件或其临时文件（复制/统计时统一排除）。</summary>
    /// <remarks>只匹配根目录的精确文件名；子目录里的同名用户文件仍应计入扫描体积。</remarks>
    /// <param name="relativePath">相对备份目录的路径（正斜杠或反斜杠均可，来自 <c>Path.GetRelativePath</c>）。</param>
    /// <returns>是根目录清单文件或临时文件时返回 <see langword="true"/>。</returns>
    internal static bool IsManifestFile(string relativePath) =>
        string.Equals(relativePath, FileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(relativePath, FileName + ".tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读取哈希清单并报告是否真正读取了内容；读取前的长度已超限时 <paramref name="contentRead"/> 为
    /// <see langword="false"/>，读入后发现超限时为 <see langword="true"/>（内容已被读取）。
    /// </summary>
    /// <remarks>
    /// 调用方必须确认路径存在，或把
    /// <see cref="FileNotFoundException"/> 当作「没有清单」处理；<paramref name="knownLength"/>
    /// 是调用方已取得的长度，避免重复元数据探测。读入后仍按 UTF-8 字节数复核上限，
    /// 以发现“取得长度后文件被并发改写变大”的窗口。
    /// </remarks>
    /// <param name="manifestPath">清单文件路径；调用方必须先确认文件存在。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="manifestStore">清单文本编解码端口。</param>
    /// <param name="contentRead">是否真正读取了清单内容；读取前长度超限时为 <see langword="false"/>（诊断）。</param>
    /// <param name="knownLength">调用方已取得的清单字节数。</param>
    /// <returns>清单；不可读或超限时为 <see langword="null"/>。</returns>
    /// <exception cref="FileNotFoundException">清单文件不存在（由文件元数据/读取端口抛出）。</exception>
    /// <exception cref="IOException">读取失败（占用/权限/IO 错误）。</exception>
    internal static HashManifest? Read(
        string manifestPath,
        IFileSystem fileSystem,
        IBackupManifestStore manifestStore,
        out bool contentRead,
        long knownLength)
    {
        ArgumentNullException.ThrowIfNull(manifestStore);
        contentRead = false;

        // 复用调用方已取得的长度，避免同一路径两次元数据调用。
        if (knownLength > MaxManifestBytes)
        {
            return null;
        }

        contentRead = true;
        string json = fileSystem.ReadAllText(manifestPath);

        // 元数据与读取之间文件可能被改写；按实际读到的内容复核，避免把超大清单载入内存后放行。
        if (Encoding.UTF8.GetByteCount(json) > MaxManifestBytes)
        {
            return null;
        }

        // 编解码端口负责格式、版本与「没有任何有效条目」的判定。
        IReadOnlyList<BackupManifestEntry>? entries = manifestStore.TryDeserialize(json);
        if (entries is null)
        {
            return null;
        }

        return new HashManifest
        {
            Files = [.. entries.Select(static entry => new HashManifestFile
            {
                Path = entry.Path,
                Size = entry.Size,
                Sha256 = entry.Sha256,
            })],
        };
    }

    /// <summary>
    /// 读取并预校验备份的哈希清单（还原时只读一次，P-12）：没有清单的旧备份返回 <see langword="null"/>；
    /// 清单损坏、版本不支持或含非法路径时抛 <see cref="SaveBackupVerificationException"/>（在改动当前存档之前）。
    /// </summary>
    /// <param name="backup">要还原的备份。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="manifestStore">清单文本编解码端口。</param>
    /// <returns>清单；旧备份无清单时为 <see langword="null"/>。</returns>
    internal static HashManifest? LoadForRestore(
        SaveBackup backup,
        IFileSystem fileSystem,
        IBackupManifestStore manifestStore)
    {
        string manifestPath = Path.Combine(backup.Directory, FileName);
        if (!fileSystem.FileExists(manifestPath))
        {
            return null;
        }

        // 长度只取一次，同时用于超限判断与 Read，避免重复探测。
        long length = fileSystem.GetFileLength(manifestPath);
        if (length > MaxManifestBytes)
        {
            throw new SaveBackupVerificationException(
                new SaveBackupVerification(
                    true,
                    0,
                    [new SaveBackupIssue(SaveBackupIssueKind.ManifestTooLarge, string.Empty)]));
        }

        HashManifest? manifest = Read(manifestPath, fileSystem, manifestStore, out _, length);
        List<SaveBackupIssue> issues = manifest is null
            ? [new SaveBackupIssue(SaveBackupIssueKind.ManifestUnreadable, string.Empty)]
            :
            [
                .. manifest.Files
                    .Where(entry => string.IsNullOrWhiteSpace(entry.Path)
                        || Path.IsPathRooted(entry.Path)
                        || ContainsParentSegment(entry.Path))
                    .Select(static entry => new SaveBackupIssue(SaveBackupIssueKind.InvalidPath, entry.Path)),
            ];
        if (issues.Count > 0)
        {
            throw new SaveBackupVerificationException(
                new SaveBackupVerification(true, manifest?.Files.Count ?? 0, issues));
        }

        return manifest;
    }

    /// <summary>计算文件的 SHA-256（大写十六进制）。</summary>
    /// <param name="file">文件路径。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <returns>哈希文本。</returns>
    internal static string ComputeSha256(string file, IFileSystem fileSystem)
    {
        using Stream stream = fileSystem.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>判断路径里是否有 <c>..</c> 段（不分配数组，逐段扫描，P-23）。</summary>
    /// <param name="path">清单里的相对路径。</param>
    /// <returns>含 <c>..</c> 段时返回 <see langword="true"/>。</returns>
    internal static bool ContainsParentSegment(string path)
    {
        ReadOnlySpan<char> remaining = path;
        while (true)
        {
            int separator = remaining.IndexOfAny('/', '\\');
            ReadOnlySpan<char> segment = separator < 0 ? remaining : remaining[..separator];
            if (segment.SequenceEqual(".."))
            {
                return true;
            }

            if (separator < 0)
            {
                return false;
            }

            remaining = remaining[(separator + 1)..];
        }
    }

    /// <summary>清单文件结构（已解码的条目集合；格式与版本由编解码端口处理）。</summary>
    internal sealed class HashManifest
    {
        public List<HashManifestFile> Files { get; set; } = [];
    }

    /// <summary>清单里的一条文件记录（JSON 反序列化的具体类型；空子类用于与条目基类区分）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S2094:Classes should not be empty",
        Justification = "空子类用于 JSON 多态反序列化，成员继承自 HashManifestEntry。")]
    internal sealed class HashManifestFile : HashManifestEntry
    {
    }

    /// <summary>清单条目的公共字段（校验时按接口读取，不依赖具体子类）。</summary>
    internal class HashManifestEntry
    {
        public string Path { get; set; } = string.Empty;

        public long Size { get; set; }

        public string Sha256 { get; set; } = string.Empty;
    }
}
