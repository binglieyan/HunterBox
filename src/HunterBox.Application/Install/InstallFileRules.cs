using HunterBox.Domain.Mods;

namespace HunterBox.Application.Install;

/// <summary>
/// 安装文件树的默认勾选规则：说明/预览图、<c>modinfo.ini</c>（任意层级）与 FOMOD 配置默认不勾选。
/// </summary>
/// <remarks>
/// UI（安装树）与 <see cref="PackageFilters"/> 使用同一判定：凡是提交阶段会被排除的元数据
/// （<c>modinfo.ini</c>、FOMOD 配置与 <c>fomod/</c> 目录）都默认不勾选，避免「树里勾选但提交静默排除」；
/// 预览图仍允许用户显式勾选作为资源安装。
/// </remarks>
public static class InstallFileRules
{
    private static readonly string[] DocumentationExtensions = [".txt", ".md"];

    private static readonly string[] PreviewExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>判断安装树里的文件是否默认不勾选（用户仍可在树里手动勾选）。</summary>
    /// <param name="relativePath">包内相对路径（允许反斜杠）。</param>
    /// <returns>默认不勾选时返回 <see langword="true"/>。</returns>
    public static bool ShouldDefaultUncheck(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string path = relativePath.Replace('\\', '/');

        // modinfo.ini 在任意层级都只作为元数据来源，不进 MOD 文件。
        if (path.Equals(PackageInspector.ModInfoFileName, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/" + PackageInspector.ModInfoFileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // FOMOD 配置与 fomod/ 目录在提交阶段会被排除，树里必须一致地默认不勾选。
        if (IsFomodMetadata(path))
        {
            return true;
        }

        string extension = Path.GetExtension(path);
        return DocumentationExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || PreviewExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>判断包内相对路径是否属于 FOMOD 元数据（配置或 <c>fomod/</c> 目录）。</summary>
    /// <param name="relativePath">包内相对路径（允许反斜杠）。</param>
    /// <returns>属于 FOMOD 元数据时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> 为空或空白。</exception>
    public static bool IsFomodMetadata(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return IsFomodMetadataCore(relativePath.Replace('\\', '/'), normalized: false);
    }

    /// <summary>判断已规范化的包内路径是否属于 FOMOD 元数据；调用方已规范化时避免重复规范化。</summary>
    /// <param name="normalizedPath">已规范化的相对路径（小写、正斜杠）。</param>
    /// <returns>属于 FOMOD 元数据时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="normalizedPath"/> 为空或空白。</exception>
    public static bool IsFomodMetadataNormalized(string normalizedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPath);
        return IsFomodMetadataCore(normalizedPath, normalized: true);
    }

    /// <summary>FOMOD 元数据判定的唯一实现：<c>fomod/</c> 前缀 + 配置文件名。</summary>
    /// <param name="path">正斜杠路径。</param>
    /// <param name="normalized">路径是否已规范化（避免重复 Normalize）。</param>
    /// <returns>属于 FOMOD 元数据时返回 <see langword="true"/>。</returns>
    private static bool IsFomodMetadataCore(string path, bool normalized)
    {
        bool isConfig = normalized
            ? PackageInspector.IsFomodConfigNormalized(path)
            : PackageInspector.IsFomodConfig(path);
        return isConfig || path.StartsWith("fomod/", StringComparison.OrdinalIgnoreCase);
    }
}
