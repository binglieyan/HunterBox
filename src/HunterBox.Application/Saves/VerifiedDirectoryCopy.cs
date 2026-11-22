using System.Buffers;
using System.Security.Cryptography;
using HunterBox.Application.Common;
using HunterBox.Domain.Layout;

namespace HunterBox.Application.Saves;

/// <summary>带 SHA-256 的目录复制结果。</summary>
/// <param name="RelativePath">相对路径（正斜杠）。</param>
/// <param name="Size">字节数。</param>
/// <param name="Sha256">大写十六进制 SHA-256。</param>
internal readonly record struct CopiedFile(string RelativePath, long Size, string Sha256);

/// <summary>
/// 复制目录并在复制过程中计算 SHA-256：备份、还原共用一份实现，避免逐行重复。
/// </summary>
internal static class VerifiedDirectoryCopy
{
    /// <summary>把源目录复制到目标目录，返回每个文件的相对路径、大小与 SHA-256。</summary>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="source">源目录。</param>
    /// <param name="destination">目标目录（不存在会被创建）。</param>
    /// <param name="exclude">按相对路径（正斜杠）排除条目；必填，返回 <see langword="true"/> 表示跳过。</param>
    /// <returns>已复制的文件清单（顺序不保证）。</returns>
    /// <exception cref="ArgumentException">目标目录位于源目录内（会边枚举边复制）。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="exclude"/> 为 <see langword="null"/>。</exception>
    internal static List<CopiedFile> Copy(
        IFileSystem fileSystem,
        string source,
        string destination,
        Func<string, bool> exclude)
    {
        List<CopiedFile> copied = [];
        CopyCore(
            fileSystem,
            source,
            destination,
            exclude,
            (file, relative, target, buffer) =>
            {
                long size = CopyFileAndHash(fileSystem, file, target, buffer, out string sha256);
                copied.Add(new CopiedFile(relative, size, sha256));
            });
        return copied;
    }

    /// <summary>把源目录原样复制到目标目录（不计算哈希），与 <see cref="Copy"/> 共用同一套枚举/写入循环。</summary>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="source">源目录。</param>
    /// <param name="destination">目标目录（不存在会被创建）。</param>
    /// <param name="exclude">按相对路径（正斜杠）排除条目；必填。</param>
    /// <exception cref="ArgumentException">目标目录位于源目录内（会边枚举边复制）。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="exclude"/> 为 <see langword="null"/>。</exception>
    internal static void CopyPlain(
        IFileSystem fileSystem,
        string source,
        string destination,
        Func<string, bool> exclude) =>
        CopyCore(
            fileSystem,
            source,
            destination,
            exclude,
            (file, _, target, _) => fileSystem.CopyFile(file, target, overwrite: true));

    private static void CopyCore(
        IFileSystem fileSystem,
        string source,
        string destination,
        Func<string, bool> exclude,
        Action<string, string, string, byte[]> copyFile)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(exclude);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        string sourceFull = Path.GetFullPath(source);
        string destinationFull = Path.GetFullPath(destination);
        EnsureTargetOutsideSource(
            destinationFull,
            sourceFull,
            $"复制目标目录不能位于源目录内（会边枚举边复制，导致无限增长）: {destination}",
            nameof(destination));

        // 先物化文件清单再创建/写入目标目录：枚举期间目标目录尚不存在，复制过程不会反过来影响枚举结果。
        // 安全枚举：跳过符号链接/junction，绝不把链接目标的内容复制进目标目录。
        string[] files = SafeFileTree.EnumerateFiles(fileSystem, sourceFull);
        fileSystem.CreateDirectory(destinationFull);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(FileCopyBuffer.Size);
        try
        {
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(sourceFull, file).Replace('\\', '/');
                if (exclude(relative))
                {
                    continue;
                }

                string target = Path.Combine(destinationFull, relative.Replace('/', Path.DirectorySeparatorChar));
                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    fileSystem.CreateDirectory(parent);
                }

                copyFile(file, relative, target, buffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>目标路径不能等于或位于源路径内（备份/还原/复制共用）。</summary>
    /// <param name="targetFull">目标绝对路径。</param>
    /// <param name="sourceFull">源绝对路径。</param>
    /// <param name="message">异常消息。</param>
    /// <param name="parameterName">异常参数名。</param>
    /// <exception cref="ArgumentException">目标等于或位于源内。</exception>
    internal static void EnsureTargetOutsideSource(
        string targetFull,
        string sourceFull,
        string message,
        string parameterName)
    {
        if (BoxPaths.IsSameOrInside(targetFull, sourceFull))
        {
            throw new ArgumentException(message, parameterName);
        }
    }

    private static long CopyFileAndHash(
        IFileSystem fileSystem,
        string source,
        string destination,
        byte[] buffer,
        out string sha256)
    {
        using Stream input = fileSystem.OpenRead(source);
        using Stream output = fileSystem.OpenWrite(destination);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
            total += read;
        }

        sha256 = Convert.ToHexString(hash.GetHashAndReset());
        return total;
    }
}
