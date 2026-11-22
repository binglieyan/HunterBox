using System.Security.Cryptography;
using System.Text;
using HunterBox.Application.Common;
using HunterBox.Domain.Mods;

namespace HunterBox.Application.Install;

/// <summary>
/// 安装/重装共用的包级规则：安装排除清单、包内容指纹、预览图识别。
/// </summary>
/// <remarks>
/// <c>modinfo.ini</c>（任意层级）、FOMOD 配置（根级与 <c>fomod/</c> 目录）属于元数据，硬排除；
/// 预览图/截图默认只用于识别与展示，用户显式勾选（<c>allowPreview</c>）时允许作为真实资源安装。
/// 安装与重装必须使用同一套规则。
/// </remarks>
internal static class PackageFilters
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    /// <summary>判断包内相对路径是否应从安装文件中排除。</summary>
    /// <param name="relativePath">包内相对路径（调用方通常已相对安装根/去掉前缀）。</param>
    /// <param name="previewRelativePath">包内预览图的相对路径（可空）。</param>
    /// <param name="allowPreview">用户显式勾选时允许图片/截图作为真实资源安装（元数据仍硬排除）。</param>
    /// <returns>应排除时返回 <see langword="true"/>。</returns>
    internal static bool ShouldExclude(
        string relativePath,
        string? previewRelativePath = null,
        bool allowPreview = false)
    {
        string path = PackageInspector.Normalize(relativePath);

        // modinfo.ini 只作为元数据来源：根级、子目录（多变体包）都不进 MOD 文件。
        if (path.Equals(PackageInspector.ModInfoFileName, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/" + PackageInspector.ModInfoFileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // FOMOD 配置与 fomod/ 目录（配置、分组图片）属于安装器元数据，不进 MOD 文件。
        // 与安装树默认勾选共用判定；path 已规范化，避免二次 Normalize。
        if (InstallFileRules.IsFomodMetadataNormalized(path))
        {
            return true;
        }

        if (!allowPreview
            && previewRelativePath is not null
            && string.Equals(path, PackageInspector.Normalize(previewRelativePath), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 根级截图/预览图（screenshot.png、preview.jpg 等）。
        if (!allowPreview && !path.Contains('/'))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            if ((name.Equals("screenshot", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("preview", StringComparison.OrdinalIgnoreCase))
                && ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>当前指纹算法版本前缀；旧版本记录没有前缀，按旧算法单独比较。</summary>
    private const string FingerprintVersionPrefix = "v2:";

    /// <summary>按「相对路径 + 文件大小」计算包指纹；关键文件额外纳入内容哈希，用于识别重复安装。</summary>
    /// <remarks>
    /// 只比路径/大小时，同尺寸但内容不同的 pak/dll/lua 会被误判为重复安装（C-12）；
    /// 这些关键文件数量少，追加 SHA256 的成本可接受。结果带 <c>v2:</c> 前缀，
    /// 升级前安装的记录用 <see cref="ComputeLegacyFingerprint"/> 比较。
    /// </remarks>
    /// <param name="contentRoot">包内容根目录。</param>
    /// <param name="files">包内全部文件（相对路径）。</param>
    /// <param name="fileSystem">文件系统端口（读取文件长度与内容哈希）。</param>
    /// <returns>稳定指纹（<c>v2:</c> + SHA256 十六进制）。</returns>
    internal static string ComputeFingerprint(
        string contentRoot,
        IReadOnlyList<string> files,
        IFileSystem fileSystem) =>
        FingerprintVersionPrefix + ComputeHash(contentRoot, files, fileSystem, includeKeyHashes: true);

    /// <summary>旧版指纹（只有路径 + 大小，无版本前缀）：仅用于匹配升级前的安装记录。</summary>
    /// <param name="contentRoot">包内容根目录。</param>
    /// <param name="files">包内全部文件（相对路径）。</param>
    /// <param name="fileSystem">文件系统端口（读取文件长度）。</param>
    /// <returns>旧算法指纹（SHA256 十六进制）。</returns>
    internal static string ComputeLegacyFingerprint(
        string contentRoot,
        IReadOnlyList<string> files,
        IFileSystem fileSystem) =>
        ComputeHash(contentRoot, files, fileSystem, includeKeyHashes: false);

    private static string ComputeHash(
        string contentRoot,
        IReadOnlyList<string> files,
        IFileSystem fileSystem,
        bool includeKeyHashes)
    {
        IEnumerable<string> lines = files
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                string full = Path.Combine(contentRoot, f.Replace('/', Path.DirectorySeparatorChar));
                // 直接取长度（缺失按 -1），不再先 FileExists 再取长度做两次系统调用。
                long size;
                try
                {
                    size = fileSystem.GetFileLength(full);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    size = -1;
                }

                string hash = includeKeyHashes && size >= 0 && IsKeyFile(f)
                    ? "|" + HashFile(full, fileSystem)
                    : string.Empty;
                return f + "|" + size + hash;
            });

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        return Convert.ToHexString(hash);
    }

    private static bool IsKeyFile(string relativePath) =>
        relativePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
        || relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || relativePath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);

    private static string HashFile(string fullPath, IFileSystem fileSystem)
    {
        using Stream stream = fileSystem.OpenRead(fullPath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>挑出包内第一张候选预览图（相对路径）；没有则为 <see langword="null"/>。</summary>
    /// <param name="files">包内全部文件（相对路径）。</param>
    /// <returns>预览图的包内相对路径。</returns>
    internal static string? FindPreviewRelativePath(IReadOnlyList<string> files) =>
        files
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Count(c => c == '/') <= 2)
            .Where(f => !InstallFileRules.IsFomodMetadata(f))
            .FirstOrDefault();
}
