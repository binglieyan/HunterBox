using HunterBox.Domain.Paks;

using HunterBox.Application.Paks;

namespace HunterBox.Infrastructure.Paks;

/// <summary>按 RE Engine 规则计算路径哈希，并构建 <see cref="PakNameIndex"/>。</summary>
/// <remarks>字符串哈希使用 UTF-16LE 字节 + Murmur3（seed 0xFFFFFFFF），与引擎一致。</remarks>
public sealed class PakPathHashProvider : IPakPathHasher
{
    /// <inheritdoc />
    public ulong HashPath(string path) => PakPathHasher.HashPath(path);
}

/// <summary>按 RE Engine 规则计算路径哈希（UTF-16LE + Murmur3，seed 0xFFFFFFFF）。</summary>
public static class PakPathHasher
{
    /// <summary>引擎使用的固定种子。</summary>
    public const uint Seed = 0xFFFFFFFFu;

    /// <summary>计算单个字符串的 32 位哈希。</summary>
    /// <param name="text">路径文本（区分大小写）。</param>
    /// <returns>32 位哈希。</returns>
    public static uint HashString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // 直接按 char 计算 UTF-16LE 字节的 Murmur3，避免每行分配临时 byte[]（见 P-13）。
        return Murmur3.Hash32(text.AsSpan(), Seed);
    }

    /// <summary>计算路径的组合哈希：<c>(hash(大写) &lt;&lt; 32) | hash(原样)</c>。</summary>
    /// <param name="path">相对路径（必须区分大小写）。</param>
    /// <returns>组合哈希。</returns>
    /// <remarks>
    /// 引擎约定低 32 位是 <c>hash(全小写路径)</c>：调用方必须先把路径归一为小写再调用
    /// （打包见 <c>PakPacker.ResolveEntryHash</c>，改写见 <c>PakModelRewriteService</c>），
    /// 否则大写文件名会算出游戏认不出的条目哈希。
    /// </remarks>
    public static ulong HashPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // 没有反斜杠时不额外分配副本：绝大多数调用方传进来的已是正斜杠路径。
        string normalized = path.Contains('\\') ? path.Replace('\\', '/') : path;
        string upper = normalized.ToUpperInvariant();
        return ((ulong)HashString(upper) << 32) | HashString(normalized);
    }

    /// <summary>从清单文本构建索引。</summary>
    /// <param name="lines">每行一个路径；空行与 <c>#</c>/<c>;</c> 注释会被跳过。</param>
    /// <returns>构建好的索引。</returns>
    public static PakNameIndex BuildIndex(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        PakNameIndex index = new();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            string normalized = line.Contains('\\') ? line.Replace('\\', '/') : line;
            index.Add(HashPath(normalized), normalized);

            // mod 打包时通常把路径全小写，而清单保留游戏原始大小写；
            // 两种写法都登记，避免大小写不同导致认不出条目。
            string lowered = normalized.ToLowerInvariant();
            if (!string.Equals(lowered, normalized, StringComparison.Ordinal))
            {
                index.Add(HashPath(lowered), normalized);
            }
        }

        return index;
    }
}
