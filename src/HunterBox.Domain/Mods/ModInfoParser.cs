using System.Globalization;

namespace HunterBox.Domain.Mods;

/// <summary>
/// 解析 MOD 包里常见的 <c>modinfo.ini</c>。
/// </summary>
/// <remarks>
/// 支持：<c>key=value</c>、<c>;</c> / <c>#</c> 注释、无意义的段落头、键名大小写不敏感、值两侧成对引号。
/// 同一键出现多次时以第一次为准。解析失败不抛异常，缺失字段一律为 <see langword="null"/>。
/// </remarks>
public static class ModInfoParser
{
    private static readonly char[] QuoteChars = ['"', '\''];

    /// <summary>解析 <c>modinfo.ini</c> 文本。</summary>
    /// <param name="content">文件内容；为 <see langword="null"/> 或空白时返回空元数据。</param>
    /// <returns>解析结果，未找到的字段为 <see langword="null"/>。</returns>
    public static ModMetadata Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new ModMetadata();
        }

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('['))
            {
                continue;
            }

            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = Unquote(line[(separator + 1)..].Trim());
            if (key.Length > 0)
            {
                values.TryAdd(key, value);
            }
        }

        return new ModMetadata
        {
            Name = Get(values, "name", "title"),
            Category = Get(values, "category", "categories", "type"),
            Version = Get(values, "version", "ver"),
            Author = Get(values, "author", "creator"),
            NexusId = ParseNexusId(Get(values, "nexusid", "nexus_id", "nexusmodsid")),
            HomeUrl = Get(values, "homepage", "homeurl", "home_url", "url", "website"),
            Description = Get(values, "description", "desc"),
        };
    }

    private static string? Get(Dictionary<string, string> values, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? ParseNexusId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // 常见写法：纯数字，或 https://www.nexusmods.com/xxx/mods45?tab=files
        string[] chunks = raw.Split(
            [.. raw.Where(c => !char.IsAsciiDigit(c)).Distinct()],
            StringSplitOptions.RemoveEmptyEntries);
        string? lastDigits = chunks.LastOrDefault(chunk => chunk.All(char.IsAsciiDigit));

        return int.TryParse(lastDigits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed > 0
            ? parsed
            : null;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && QuoteChars.Contains(value[0]) && value[^1] == value[0]
            ? value[1..^1].Trim()
            : value;
}
