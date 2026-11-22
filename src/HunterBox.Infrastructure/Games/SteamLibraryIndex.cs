using System.Globalization;

namespace HunterBox.Infrastructure.Games;

/// <summary>Steam 库目录及其已知的 AppId 列表。</summary>
/// <param name="Path">库根目录（含 <c>steamapps</c>的那一级）。</param>
/// <param name="AppIds">该库里已安装的 AppId； <c>libraryfolders.vdf</c> 无此信息时为空集合。</param>
public sealed record SteamLibraryEntry(string Path, IReadOnlySet<int> AppIds);

/// <summary>解析 Steam的 <c>libraryfolders.vdf</c>，取出所有库目录。</summary>
/// <remarks>
/// 同时支持新版（每个库是一个带 <c>path</c> 与 <c>apps</c> 子键的段）与
/// （数值键直接对应库路径）两种格式。VDF的其它结构一律忽略。
/// </remarks>
public static class SteamLibraryIndex
{
    /// <summary>解析 VDF 文本里的库目录及其 Apps 映射。</summary>
    /// <param name="vdf">libraryfolders.vdf的内容。</param>
    /// <returns>库目录列表（按路径去重、保持出现顺序；同一路径的 AppId 合并）。</returns>
    public static IReadOnlyList<SteamLibraryEntry> ParseEntries(string? vdf)
    {
        SteamVdfNode? root = SteamVdfParser.Parse(vdf);
        if (root is null)
        {
            return [];
        }

        SteamVdfNode container = root.Child("libraryfolders") ?? root.Child("LibraryFolders") ?? root;
        List<SteamLibraryEntry> entries = [];
        Dictionary<string, HashSet<int>> indexByPath = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, SteamVdfNode node) in container.Children)
        {
            string? path = node.Value;
            HashSet<int> appIds = [];
            if (node.Child("apps") is { } apps)
            {
                foreach (string appIdText in apps.Children.Keys)
                {
                    if (int.TryParse(appIdText, NumberStyles.None, CultureInfo.InvariantCulture, out int appId))
                    {
                        appIds.Add(appId);
                    }
                }
            }

            path ??= node.Child("path")?.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            // libraryfolders.vdf 里除数值键外还有 TimeNextStatsReport 等元数据键，一律跳过。
            bool numericKey = key.Length > 0 && key.All(char.IsAsciiDigit);
            if (!numericKey && node.Child("path") is null)
            {
                continue;
            }

            path = path.Trim();
            if (indexByPath.TryGetValue(path, out HashSet<int>? existing))
            {
                existing.UnionWith(appIds);
                continue;
            }

            indexByPath[path] = appIds;
            entries.Add(new SteamLibraryEntry(path, appIds));
        }

        return entries;
    }
}
