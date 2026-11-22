using System.Security;

namespace HunterBox.Application.Common;

/// <summary>
/// 清理应用 Temp 根目录下的超期顶层条目，处理崩溃/强杀留下的安装工作目录与隔离目录。
/// </summary>
/// <remarks>
/// 只按最后写入时间清理顶层条目，不递归判断内部文件；保留期默认 7 天，
/// 足以避开仍在使用的安装会话，同时避免 Temp 无限增长。
/// </remarks>
public static class TempHousekeeping
{
    /// <summary>默认保留时长：7 天。</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    /// <summary>删除 <paramref name="tempDirectory"/> 下超过保留期的顶层文件/目录。</summary>
    /// <param name="tempDirectory">应用 Temp 根目录。</param>
    /// <param name="retention">保留时长，必须为正。</param>
    /// <param name="fileSystem">文件系统端口（强制删除用）。</param>
    /// <param name="now">当前时间（必填，调用方统一从 IClock 取得）。</param>
    /// <param name="cancellationToken">启动取消令牌；枚举后与每个条目循环中检查。</param>
    /// <returns>成功删除的顶层条目数；无法枚举时返回 0。</returns>
    /// <exception cref="OperationCanceledException">清理开始前或循环中发现取消。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retention"/> 不是正值。</exception>
    public static int PurgeStale(
        string tempDirectory,
        TimeSpan retention,
        IFileSystem fileSystem,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "保留时长必须为正。");
        }

        if (!fileSystem.DirectoryExists(tempDirectory))
        {
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        DateTime cutoff = now.UtcDateTime - retention;
        string[] entries;
        try
        {
            entries = fileSystem.EnumerateFileSystemEntries(tempDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        int removed = 0;
        foreach (string entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // 用属性判断类型：断链（目标已不存在）的 Exists 可能为 false，会被误当成文件；
                // 链接自身属性始终可读，按 Directory 位分类后再交给对应删除方法。
                FileAttributes attributes = fileSystem.GetAttributes(entry);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                DateTime lastWrite = isDirectory
                    ? fileSystem.GetDirectoryLastWriteTimeUtc(entry)
                    : fileSystem.GetFileLastWriteTimeUtc(entry);
                if (lastWrite >= cutoff)
                {
                    continue;
                }

                bool deleted = isDirectory
                    ? fileSystem.TryDeleteDirectoryForce(entry, cancellationToken)
                    : fileSystem.TryDeleteFileForce(entry, cancellationToken);
                if (deleted)
                {
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // 单个条目被占用/无权时不阻塞其它清理。
            }
        }

        return removed;
    }
}
