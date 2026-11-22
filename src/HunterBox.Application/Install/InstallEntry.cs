using HunterBox.Application.Common;
using HunterBox.Domain.Archives;

namespace HunterBox.Application.Install;

/// <summary>拖拽/文件选择器里的一个条目的处理方式。</summary>
public enum InstallEntryKind
{
    /// <summary>目录：语义是「模型编辑」（拖入文件夹走模型文件夹流程；与压缩包/单文件混合拖入时整体拒绝，不按安装源处理）。</summary>
    Directory,

    /// <summary>压缩包（按内容魔数识别，不依赖扩展名）。</summary>
    Archive,

    /// <summary>可单独安装的裸文件（.pak/.lua/.dll 等，「全部件」路径）。</summary>
    SingleFile,

    /// <summary>文件存在但读不出来（被占用/权限不足）；与「类型不支持」区分，提示用户重试。</summary>
    Unreadable,

    /// <summary>不支持的文件类型。</summary>
    Unsupported,
}

/// <summary>安装入口的条目分类结果。</summary>
/// <param name="Path">条目绝对路径。</param>
/// <param name="Kind">处理方式。</param>
public sealed record InstallEntryDecision(string Path, InstallEntryKind Kind);

/// <summary>
/// 安装入口的条目分类（纯逻辑，便于单测）：目录 → 目录流程；压缩包 → 安装；
/// 裸 .pak/.lua/.dll → 单文件安装；其它 → 提示不支持。
/// </summary>
public static class InstallEntryClassifier
{
    /// <summary>可以直接作为裸文件安装的扩展名。</summary>
    public static IReadOnlyList<string> InstallableSingleFileExtensions { get; } =
        [".pak", ".lua", ".dll"];

    /// <summary>分类单个条目。</summary>
    /// <param name="path">条目路径。</param>
    /// <param name="fileSystem">文件系统端口（存在性与压缩包魔数探测）。</param>
    /// <returns>处理方式。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    public static InstallEntryKind Classify(string? path, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (string.IsNullOrWhiteSpace(path))
        {
            return InstallEntryKind.Unsupported;
        }

        if (fileSystem.DirectoryExists(path))
        {
            return InstallEntryKind.Directory;
        }

        if (!fileSystem.FileExists(path))
        {
            // ACL 拒绝时 FileExists 同样返回 false：再用属性读取区分「不存在」与「存在但不可读」。
            try
            {
                fileSystem.GetAttributes(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return InstallEntryKind.Unsupported;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return InstallEntryKind.Unreadable;
            }

            return InstallEntryKind.Unsupported;
        }

        // 先探测内容：读不出来（占用/权限）要单独提示，不能误报成「不是可安装的 MOD 文件」。
        ArchiveProbeResult probe = ArchiveFileProbe.Probe(path, fileSystem);
        if (probe.Format != ArchiveFormat.Unknown)
        {
            return InstallEntryKind.Archive;
        }

        if (!probe.Readable)
        {
            return InstallEntryKind.Unreadable;
        }

        string extension = Path.GetExtension(path);
        return InstallableSingleFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? InstallEntryKind.SingleFile
            : InstallEntryKind.Unsupported;
    }

    /// <summary>分类一组拖入条目（保持顺序、忽略空路径；空集合返回空列表）。</summary>
    /// <param name="paths">条目路径集合。</param>
    /// <param name="fileSystem">文件系统端口（存在性与压缩包魔数探测）。</param>
    /// <returns>逐条决策。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    public static IReadOnlyList<InstallEntryDecision> ClassifyAll(
        IEnumerable<string>? paths,
        IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (paths is null)
        {
            return [];
        }

        return [.. paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new InstallEntryDecision(p, Classify(p, fileSystem)))];
    }
}
