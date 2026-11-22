using HunterBox.Domain.Layout;

namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// pak 内条目路径的安全校验：复用 <see cref="BoxPaths.NormalizeRelative"/> 的权威规则，
/// 并把 <see cref="ArgumentException"/> 包装成 pak 解析语义的 <see cref="InvalidDataException"/>。
/// </summary>
/// <remarks>
/// RE Engine 的 pak 只存哈希不存路径，路径来自外部清单（<c>file.list</c> / 内置清单），不可信。
/// 绝对路径、<c>..</c>、Windows 保留设备名（<c>CON</c>/<c>NUL</c>…）、尾随点/空格与非法字符
/// 统一由 <see cref="BoxPaths.NormalizeRelative"/> 拒绝，避免两套规则漂移（A-09）。
/// </remarks>
internal static class PakPathSafety
{
    /// <summary>校验包内相对路径并拆成安全的路径段。</summary>
    /// <param name="relativePath">包内相对路径（<c>/</c> 或 <c>\</c> 分隔）。</param>
    /// <returns>逐段路径（不含空段、<c>.</c>、<c>..</c>）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="InvalidDataException">
    /// 路径为绝对路径、含非法字符/保留设备名、尾随点空格、<c>..</c>、空段（<c>a//b</c>）或前导 <c>./</c>。
    /// </exception>
    public static string[] SplitSafeSegments(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        // pak 条目是同一位置的唯一写法，空段与前导 "./" 直接拒绝而不是静默折叠，
        // 避免 "a//b"、"./a" 与 "a/b" 掩盖重复条目或大小写冲突。
        string slashNormalized = relativePath.Replace('\\', '/');
        if (slashNormalized.Contains("//", StringComparison.Ordinal)
            || slashNormalized.StartsWith("./", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"条目路径不安全（包含空段或 . 前缀）: {relativePath}");
        }

        string[] segments;
        try
        {
            // 直接拿到规范化后的分段，避免「先 Join 再 Split」的二次分配。
            segments = BoxPaths.NormalizeRelativeSegments(relativePath);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"条目路径不安全: {relativePath}（{ex.Message}）", ex);
        }

        // 包内条目不允许 "." 段：它会让同一目录出现两种写法，掩盖大小写/重复冲突。
        if (Array.Exists(segments, static segment => segment == "."))
        {
            throw new InvalidDataException($"条目路径不安全（包含 . 段）: {relativePath}");
        }

        return segments;
    }
}
