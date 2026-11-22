using System.Runtime.InteropServices;
using System.Security;
using HunterBox.Application.Common;

namespace HunterBox.Infrastructure.IO;

/// <summary>
/// <see cref="IFileSystem"/> 的系统实现：尽力删除、强制删除与链接安全规则（A-29）。
/// </summary>
/// <remarks>
/// <para>
/// 旧实现位于 Application/Common 的 <c>FileSystemUtil</c>，被 Infrastructure 适配器反向依赖；
/// 现统一由本类实现 Application 端口，适配器与用例都只通过 <see cref="IFileSystem"/> 调用。
/// </para>
/// <para>
/// 无状态，可安全共享；<see cref="Instance"/> 供 Infrastructure 内部与组合根复用。
/// <c>Try*</c> 删除失败（文件占用、权限不足、安全策略拒绝）返回 <see langword="false"/>，不抛异常；
/// 严格删除（<see cref="DeleteFile"/> 等）保持 BCL 语义，失败抛异常。
/// </para>
/// </remarks>
public sealed class SystemFileSystem : IFileSystem
{
    /// <summary>无状态共享实例。</summary>
    public static SystemFileSystem Instance { get; } = new();

    /// <summary>文件是否存在；断链链接返回 <see langword="false"/>（只看链接目标）。</summary>
    /// <param name="path">文件路径，可为 <see langword="null"/>。</param>
    /// <returns>存在时返回 <see langword="true"/>。</returns>
    public bool FileExists(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>目录是否存在；断链链接返回 <see langword="false"/>（只看链接目标）。</summary>
    /// <param name="path">目录路径，可为 <see langword="null"/>。</param>
    /// <returns>存在时返回 <see langword="true"/>。</returns>
    public bool DirectoryExists(string? path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    /// <summary>创建目录（含父目录）；已存在时为空操作。</summary>
    /// <param name="path">目录路径。</param>
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <summary>复制文件。</summary>
    /// <param name="source">源文件。</param>
    /// <param name="destination">目标文件。</param>
    /// <param name="overwrite">目标已存在时是否覆盖。</param>
    public void CopyFile(string source, string destination, bool overwrite = false) =>
        File.Copy(source, destination, overwrite);

    /// <summary>移动文件（可跨卷复制后删除）。</summary>
    /// <param name="source">源文件。</param>
    /// <param name="destination">目标文件。</param>
    /// <param name="overwrite">目标已存在时是否覆盖。</param>
    public void MoveFile(string source, string destination, bool overwrite = false) =>
        File.Move(source, destination, overwrite);

    /// <summary>移动目录（同卷改名；目标已存在时抛出）。</summary>
    /// <param name="source">源目录。</param>
    /// <param name="destination">目标目录。</param>
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    /// <summary>严格删除文件（失败抛异常）。</summary>
    /// <param name="path">文件路径。</param>
    public void DeleteFile(string path) => File.Delete(path);

    /// <summary>递归删除目录（失败抛异常）。</summary>
    /// <param name="path">目录路径。</param>
    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    /// <summary>严格删除空目录（非递归；目录非空时抛 <see cref="IOException"/>）。</summary>
    /// <param name="path">目录路径。</param>
    public void DeleteEmptyDirectory(string path) => Directory.Delete(path, recursive: false);

    /// <summary>打开文件读取（共享读）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>只读流。</returns>
    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>打开文件写入（创建或截断，独占写）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>可写流。</returns>
    public Stream OpenWrite(string path) =>
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

    /// <summary>读取文本（UTF-8）。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>文件内容。</returns>
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <summary>写入文本（UTF-8，无 BOM；覆盖写入）。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="contents">内容。</param>
    public void WriteAllText(string path, string contents) =>
        File.WriteAllText(path, contents, new System.Text.UTF8Encoding(false));

    /// <summary>枚举文件（不保证顺序）。</summary>
    /// <param name="directory">目录。</param>
    /// <param name="searchPattern">匹配模式。</param>
    /// <param name="searchOption">是否递归。</param>
    /// <returns>文件全路径数组。</returns>
    public string[] EnumerateFiles(
        string directory,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly) =>
        [.. Directory.EnumerateFiles(directory, searchPattern, searchOption)];

    /// <summary>惰性枚举文件（不保证顺序），调用方可逐项检查取消。</summary>
    /// <param name="directory">目录。</param>
    /// <param name="searchPattern">匹配模式。</param>
    /// <param name="searchOption">是否递归。</param>
    /// <returns>文件全路径序列。</returns>
    public IEnumerable<string> EnumerateFilesLazy(
        string directory,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly) =>
        Directory.EnumerateFiles(directory, searchPattern, searchOption);

    /// <summary>枚举目录（不保证顺序）。</summary>
    /// <param name="directory">目录。</param>
    /// <param name="searchPattern">匹配模式。</param>
    /// <param name="searchOption">是否递归。</param>
    /// <returns>目录全路径数组。</returns>
    public string[] EnumerateDirectories(
        string directory,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly) =>
        [.. Directory.EnumerateDirectories(directory, searchPattern, searchOption)];

    /// <summary>取文件长度。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>字节数。</returns>
    public long GetFileLength(string path) => new FileInfo(path).Length;

    /// <summary>一次读取文件长度与最后写入时间（UTC），只构造一次 <see cref="FileInfo"/>。</summary>
    /// <param name="path">文件路径。</param>
    /// <returns>字节数与 UTC 最后写入时间。</returns>
    public (long Length, DateTime LastWriteTimeUtc) GetFileLengthAndLastWriteTimeUtc(string path)
    {
        FileInfo info = new(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>枚举文件与目录（不保证顺序）。</summary>
    /// <param name="directory">目录。</param>
    /// <returns>条目全路径数组。</returns>
    public string[] EnumerateFileSystemEntries(string directory) =>
        [.. Directory.EnumerateFileSystemEntries(directory)];

    /// <summary>判断目录是否为空（短路枚举，不物化全部条目）。</summary>
    /// <param name="directory">目录路径。</param>
    /// <returns>没有任何条目时返回 <see langword="true"/>。</returns>
    public bool IsDirectoryEmpty(string directory) =>
        !Directory.EnumerateFileSystemEntries(directory).Any();

    /// <summary>取目录最后写入时间（UTC）。</summary>
    /// <param name="path">目录路径。</param>
    /// <returns>UTC 时间。</returns>
    public DateTime GetDirectoryLastWriteTimeUtc(string path) => Directory.GetLastWriteTimeUtc(path);

    /// <summary>一次读取目录存在性与最后写入时间；缺失与无权限由同一次 Win32 元数据读取区分。</summary>
    /// <param name="path">目录路径。</param>
    /// <returns>存在标志与 UTC 最后写入时间。</returns>
    /// <exception cref="UnauthorizedAccessException">没有权限。</exception>
    /// <exception cref="IOException">其它读取失败。</exception>
    public (bool Exists, DateTime LastWriteTimeUtc) GetDirectoryExistenceAndLastWriteTimeUtc(string path)
    {
        // GetFileAttributesEx 一次调用同时返回属性与时间；
        // 缺失通过 Win32 错误码返回，不再依赖异常控制流，也不为首个目录额外读一次。
        if (!NativeMethods.TryGetFileAttributeData(path, out Win32FileAttributeData data, out int error))
        {
            if (error is NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound)
            {
                return (false, default);
            }

            if (error == NativeMethods.ErrorAccessDenied)
            {
                throw new UnauthorizedAccessException($"没有权限读取目录属性：{path}");
            }

            throw new IOException($"读取目录属性失败（Win32 错误 {error}）：{path}");
        }

        if (((FileAttributes)data.FileAttributes & FileAttributes.Directory) == 0)
        {
            return (false, default);
        }

        long fileTime = ((long)data.LastWriteTimeHigh << 32) | data.LastWriteTimeLow;
        return (true, DateTime.FromFileTimeUtc(fileTime));
    }

    /// <summary>取目录创建时间（UTC）。</summary>
    /// <param name="path">目录路径。</param>
    /// <returns>UTC 时间。</returns>
    public DateTime GetDirectoryCreationTimeUtc(string path) => Directory.GetCreationTimeUtc(path);

    /// <inheritdoc />
    public bool IsReparseLink(string path, FileAttributes attributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.LinkTarget is not null;
    }

    /// <summary>取文件最后写入时间（UTC）；文件不存在时返回 1601-01-01T00:00:00Z，不抛异常。</summary>
    /// <param name="path">文件或目录路径。</param>
    /// <returns>UTC 时间；路径不存在时为 1601-01-01T00:00:00Z。</returns>
    public DateTime GetFileLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    /// <summary>取文件/目录属性。</summary>
    /// <param name="path">路径。</param>
    /// <returns>属性位。</returns>
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    /// <summary>尽力删除文件；路径为空、文件不存在或删除失败时返回 <see langword="false"/>。</summary>
    /// <param name="path">要删除的文件路径，可为 <see langword="null"/>。</param>
    /// <returns>实际删除了文件时返回 <see langword="true"/>。</returns>
    public bool TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>尽力递归删除目录；路径为空、目录不存在或删除失败时返回 <see langword="false"/>。</summary>
    /// <param name="path">要删除的目录路径，可为 <see langword="null"/>。</param>
    /// <returns>实际删除了目录时返回 <see langword="true"/>。</returns>
    public bool TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// 尽力删除文件：普通文件先清只读属性；文件链接只删链接本身（不清属性，避免作用到链接目标）；
    /// 目录与目录链接返回 <see langword="false"/>。
    /// </summary>
    /// <param name="path">要删除的文件路径，可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌；删除前检查。</param>
    /// <returns>实际删除了文件时返回 <see langword="true"/>。</returns>
    public bool TryDeleteFileForce(string? path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        FileAttributes attributes;
        try
        {
            // 先用属性判断存在性：断链（目标已不存在）的 File.Exists 为 false，
            // 但链接自身的属性仍可读取，否则断链永远无法清理。
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // 不存在或无法读取时保留条目。
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            bool isLink;
            try
            {
                isLink = IsReparseLink(path, attributes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Try*Force 契约是不抛异常：无法确认链接时保留条目（fail-closed）。
                return false;
            }

            if (isLink)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // 目录链接不按文件删除，避免误删目录结构。
                    return false;
                }

                // 文件链接：只删链接本身，绝不清除属性，避免作用到链接目标。
                return TryDeleteFileLink(path);
            }
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return false;
        }

        TryClearReadOnly(path);
        if (TryDeleteFile(path))
        {
            return true;
        }

        // 某些文件系统在属性写入后仍需短暂重试；失败则返回 false，由调用方决定是否保留。
        cancellationToken.ThrowIfCancellationRequested();
        TryClearReadOnly(path);
        return TryDeleteFile(path);
    }

    /// <summary>递归删除目录，先清除目录与文件的只读属性；不存在或仍失败时返回 <see langword="false"/>。</summary>
    /// <param name="path">要删除的目录路径，可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌；扫描与清理属性期间检查。最后的删除是单次系统调用，不可中断。</param>
    /// <returns>实际删除了目录时返回 <see langword="true"/>。</returns>
    public bool TryDeleteDirectoryForce(string? path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        FileAttributes attributes;
        try
        {
            // 断链目录的 Directory.Exists 可能为 false，改用链接自身属性判断。
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            bool isLink;
            try
            {
                isLink = IsReparseLink(path, attributes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Try*Force 契约是不抛异常：无法确认链接时保留条目（fail-closed）。
                return false;
            }

            if (isLink)
            {
                // 只删除链接本身，绝不递归进入目标目录。
                return TryDeleteLink(path);
            }
        }

        if ((attributes & FileAttributes.Directory) == 0 || !Directory.Exists(path))
        {
            return false;
        }

        try
        {
            // 一次遍历同时完成「查链接」与「收集需清只读的条目」；
            // 发现链接立即放弃（此时还没有改任何属性），保持 fail-closed。
            if (!TryCollectForForceDelete(path, cancellationToken, out List<string> directories, out List<string> files))
            {
                return false;
            }

            TryClearReadOnly(path);
            foreach (string directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryClearReadOnly(directory);
            }

            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryClearReadOnly(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // 无法确认「子树不含链接」时 fail-closed：宁可保留条目，也不递归删除。
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // 扫描与最终删除之间存在 TOCTOU 窗口：本地他人在扫描后新建链接时仍可能被递归删除。
        // Temp 为应用自有目录、创建链接需要本地写权限，接受该极端窗口。
        return TryDeleteDirectory(path);
    }

    /// <summary>
    /// 单次遍历收集目录与文件，同时检查子树是否含重解析点；不跟随链接。
    /// </summary>
    /// <param name="root">待删除目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="directories">收集到的子目录（不含根）。</param>
    /// <param name="files">收集到的文件。</param>
    /// <returns>子树不含链接时返回 <see langword="true"/>；发现链接返回 <see langword="false"/>。</returns>
    private bool TryCollectForForceDelete(
        string root,
        CancellationToken cancellationToken,
        out List<string> directories,
        out List<string> files)
    {
        directories = [];
        files = [];
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    try
                    {
                        if (IsReparseLink(entry, attributes))
                        {
                            return false;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                    {
                        // 无法确认链接时整体放弃删除，保持 fail-closed。
                        return false;
                    }
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(entry);
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        return true;
    }

    /// <summary>只删除目录链接本身，不进入目标。</summary>
    private static bool TryDeleteLink(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>只删除文件链接本身，不清除属性，避免作用到链接目标。</summary>
    private static bool TryDeleteFileLink(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>尽力清除路径的只读属性；失败不抛异常。</summary>
    private static void TryClearReadOnly(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // 属性清除失败由后续删除结果体现。
        }
    }

    /// <summary>一次读取文件属性与时间的 Win32 结构（布局与 <c>WIN32_FILE_ATTRIBUTE_DATA</c> 一致）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Win32FileAttributeData
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint FileSizeHigh;
        public uint FileSizeLow;
    }

    /// <summary>目录元数据读取所需的 Win32 入口。</summary>
    private static class NativeMethods
    {
        /// <summary>Win32 错误：文件不存在。</summary>
        public const int ErrorFileNotFound = 2;

        /// <summary>Win32 错误：路径不存在。</summary>
        public const int ErrorPathNotFound = 3;

        /// <summary>Win32 错误：拒绝访问。</summary>
        public const int ErrorAccessDenied = 5;

        /// <summary>读取文件/目录属性与时间；失败返回 <see langword="false"/>。</summary>
        /// <param name="fileName">路径。</param>
        /// <param name="infoLevel">信息级别（0 = <c>GetFileExInfoStandard</c>）。</param>
        /// <param name="fileInformation">输出的属性与时间。</param>
        /// <returns>成功时返回 <see langword="true"/>。</returns>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileAttributesExW(
            string fileName,
            int infoLevel,
            out Win32FileAttributeData fileInformation);

        /// <summary>调用 <see cref="GetFileAttributesExW"/> 并把失败转换为 Win32 错误码。</summary>
        /// <param name="path">路径。</param>
        /// <param name="data">读取结果。</param>
        /// <param name="error">失败时的 Win32 错误码；成功时为 0。</param>
        /// <returns>成功时返回 <see langword="true"/>。</returns>
        public static bool TryGetFileAttributeData(string path, out Win32FileAttributeData data, out int error)
        {
            bool ok = GetFileAttributesExW(ToExtendedPath(path), 0, out data);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }

        /// <summary>Win32 路径长度上限（含结尾 NUL 前的 259 个字符）。</summary>
        private const int MaxPath = 260;

        /// <summary>超过传统长度上限时补 <c>\\?\</c> 前缀，避免长路径被误判为缺失。</summary>
        /// <remarks>
        /// 短路径保持原样，避免 <c>\\?\</c> 关闭 <c>.</c>/<c>..</c> 规范化改变调用方语义；
        /// 已带扩展前缀的路径原样返回，UNC 路径统一分隔符后转换为 <c>\\?\UNC\</c> 形式。
        /// 超长相对路径无法解析时抛出带上下文的 <see cref="IOException"/>。
        /// </remarks>
        /// <param name="path">路径。</param>
        /// <returns>可直接传给 Win32 API 的路径。</returns>
        /// <exception cref="IOException">相对路径无法解析或输入为设备路径。</exception>
        private static string ToExtendedPath(string path)
        {
            if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                // 设备路径不能拼 \\?\ 前缀，明确拒绝而不是生成非法路径。
                throw new IOException($"不支持设备路径：{path}");
            }

            if (path.Length < MaxPath || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path;
            }

            if (!Path.IsPathFullyQualified(path))
            {
                try
                {
                    path = Path.GetFullPath(path);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    throw new IOException($"无法解析过长或不支持的路径：{ex.Message}", ex);
                }
            }

            path = path.Replace('/', '\\');
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return @"\\?\UNC\" + path[2..];
            }

            return @"\\?\" + path;
        }
    }
}
