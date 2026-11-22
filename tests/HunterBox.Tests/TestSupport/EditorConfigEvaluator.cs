using System.Text.RegularExpressions;

namespace HunterBox.Tests;

/// <summary>
/// EditorConfig 段匹配与最终值求值：
/// 分层守卫与行尾门禁共用同一实现，避免测试类之间互相引用。
/// </summary>
internal static class EditorConfigEvaluator
{
    /// <summary>
    /// 按 EditorConfig 规范求某相对路径的最终生效键值：按文件顺序应用所有匹配段，后匹配的段覆盖前值。
    /// </summary>
    /// <param name="text">配置文件内容。</param>
    /// <param name="relativePath">相对 .editorconfig 的文件路径（正斜杠）。</param>
    /// <returns>键 → 最终值。</returns>
    public static Dictionary<string, string> Evaluate(string text, string relativePath)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        string normalizedPath = relativePath.Replace('\\', '/');
        foreach ((string header, Dictionary<string, string> sectionValues) in
            ParseSections(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)))
        {
            if (!SectionMatches(header, normalizedPath))
            {
                continue;
            }

            foreach ((string key, string value) in sectionValues)
            {
                values[key] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// 读取 EditorConfig 指定段的键值对（忽略注释与空行；空值不写入；同名段按顺序合并）。
    /// 与 <see cref="Evaluate"/> 共用同一份段解析。
    /// </summary>
    /// <param name="lines">文件全部行。</param>
    /// <param name="header">段头，例如 `[*]`。</param>
    /// <returns>段内键值对；键不区分大小写。</returns>
    public static Dictionary<string, string> ReadSection(string[] lines, string header)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        // 兼容调用方传入 "[*]" 或 "*"（ParseSections 返回的段头不含方括号）。
        string normalizedHeader = header.Length >= 2 && header.StartsWith('[') && header.EndsWith(']')
            ? header[1..^1]
            : header;
        foreach ((string sectionHeader, Dictionary<string, string> sectionValues) in ParseSections(lines))
        {
            if (!string.Equals(sectionHeader, normalizedHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach ((string key, string value) in sectionValues)
            {
                values[key] = value;
            }
        }

        return values;
    }

    /// <summary>唯一的段解析器：忽略注释/空行与无值键，按出现顺序返回段与键值。</summary>
    /// <param name="lines">文件全部行。</param>
    /// <returns>段头 → 段内键值（保留重复段）。</returns>
    private static List<(string Header, Dictionary<string, string> Values)> ParseSections(
        IEnumerable<string> lines)
    {
        List<(string Header, Dictionary<string, string> Values)> sections = [];
        Dictionary<string, string>? current = null;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections.Add((line[1..^1], current));
                continue;
            }

            if (current is null)
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (value.Length > 0)
            {
                current[key] = value;
            }
        }

        return sections;
    }

    /// <summary>EditorConfig 段是否匹配指定相对路径（支持 <c>*</c>、<c>**</c>、<c>?</c>、<c>{a,b}</c> 与 <c>[...]</c>/<c>[!...]</c>）。</summary>
    /// <param name="section">段名（不含方括号）。</param>
    /// <param name="relativePath">相对路径（正斜杠）。</param>
    /// <returns>匹配时返回 <see langword="true"/>。</returns>
    public static bool SectionMatches(string section, string relativePath)
    {
        string name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        bool pathScoped = section.Contains('/');
        string candidate = pathScoped ? relativePath : name;
        string pattern = GlobToRegex(section);
        return Regex.IsMatch(candidate, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>把 EditorConfig 的 glob 段转成正则。</summary>
    /// <param name="glob">段名。</param>
    /// <returns>完整匹配的正则表达式。</returns>
    private static string GlobToRegex(string glob)
    {
        System.Text.StringBuilder builder = new("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            i++;
                            builder.Append("(?:.*/)?");
                        }
                        else
                        {
                            builder.Append(".*");
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                case '[':
                    int classEnd = glob.IndexOf(']', i + 1);
                    if (classEnd > i + 1)
                    {
                        string content = glob[(i + 1)..classEnd];
                        builder.Append('[');
                        if (content.StartsWith('!'))
                        {
                            builder.Append('^').Append(content[1..].Replace("\\", "\\\\", StringComparison.Ordinal));
                        }
                        else
                        {
                            builder.Append(content.Replace("\\", "\\\\", StringComparison.Ordinal));
                        }

                        builder.Append(']');
                        i = classEnd;
                    }
                    else
                    {
                        builder.Append("\\[");
                    }

                    break;
                case '{':
                    int close = glob.IndexOf('}', i + 1);
                    if (close > i)
                    {
                        string[] alternatives = glob[(i + 1)..close].Split(',');
                        builder.Append("(?:")
                            .Append(string.Join("|", alternatives.Select(Regex.Escape)))
                            .Append(')');
                        i = close;
                    }
                    else
                    {
                        builder.Append(Regex.Escape(c.ToString()));
                    }

                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return builder.Append('$').ToString();
    }
}
