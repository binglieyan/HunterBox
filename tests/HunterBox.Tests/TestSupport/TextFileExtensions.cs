using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HunterBox.Tests;

/// <summary>
/// 声明范围内文本文件的扩展名清单：从仓库根 <c>tools/text-file-extensions.txt</c> 读取，
/// 编码门禁、行尾门禁与 <c>.gitattributes</c> 一致性用例共用同一份定义。
/// </summary>
internal static class TextFileExtensions
{
    /// <summary>清单文件相对仓库根的路径。</summary>
    internal const string RelativePath = "tools/text-file-extensions.txt";

    /// <summary>按仓库根缓存已读取的扩展名，避免每个门禁用例重复读盘。</summary>
    private static readonly Dictionary<string, string[]> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>读取声明扩展名（含点）。</summary>
    /// <param name="root">仓库根目录。</param>
    /// <returns>扩展名数组；顺序与文件一致。</returns>
    /// <exception cref="InvalidOperationException">清单缺失、为空或含重复项。</exception>
    /// <remarks>清单缺失或为空时直接失败，避免门禁因配置丢失而静默通过。</remarks>
    public static string[] Load(string root)
    {
        string path = Path.GetFullPath(Path.Combine(root, RelativePath));
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out string[]? cached))
            {
                return cached;
            }
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"找不到行尾/编码门禁的扩展名清单：{path}");
        }

        string[] extensions =
        [
            .. File.ReadAllLines(path)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                .Select(static line => line.StartsWith('.') ? line : "." + line),
        ];
        if (extensions.Length == 0)
        {
            throw new InvalidOperationException($"扩展名清单为空，门禁不能静默通过：{path}");
        }

        if (extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != extensions.Length)
        {
            throw new InvalidOperationException($"扩展名清单存在重复项：{path}");
        }

        lock (Cache)
        {
            Cache[path] = extensions;
        }

        return extensions;
    }
}
