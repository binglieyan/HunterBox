using System.Text.RegularExpressions;

namespace HunterBox.Tests;

/// <summary>
/// 构建/提交路径的行尾门禁命令守卫：
/// 提取 MSBuild targets 与 hook 的实际调用，并按 PowerShell 参数前缀判定禁用开关。
/// </summary>
internal static class CommandLineGuard
{
    /// <summary>从 targets 文本提取所有 Exec 命令：先剔除 XML 注释，再匹配单/双引号属性与子元素。</summary>
    /// <param name="targetsText">`.targets` 文件内容。</param>
    /// <returns>命令文本列表（保持出现顺序）。</returns>
    public static IReadOnlyList<string> ExtractTargetCommands(string targetsText)
    {
        string text = Regex.Replace(targetsText, "<!--.*?-->", " ", RegexOptions.Singleline);
        List<string> commands =
        [
            .. Regex.Matches(text, @"(?i)\bCommand\s*=\s*(['""])(?<command>.*?)\1", RegexOptions.Singleline)
                .Select(match => match.Groups["command"].Value),
            .. Regex.Matches(
                text,
                "<Command>(?<command>.*?)</Command>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase)
                .Select(match => match.Groups["command"].Value),
        ];
        return commands;
    }

    /// <summary>从 hook 文本提取 powershell 命令：先拼接反斜杠续行，再跳过注释行。</summary>
    /// <param name="hookText">hook 文件内容。</param>
    /// <returns>命令文本列表（保持出现顺序）。</returns>
    public static IReadOnlyList<string> ExtractHookCommands(string hookText)
    {
        string text = hookText.Replace("\\\r\n", " ", StringComparison.Ordinal)
            .Replace("\\\n", " ", StringComparison.Ordinal);
        return
        [
            .. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.TrimStart().StartsWith('#')
                    && line.Contains("powershell", StringComparison.OrdinalIgnoreCase)),
        ];
    }

    /// <summary>命令是否携带 `-Fix`/`-Extensions` 或其 PowerShell 唯一前缀缩写（如 `-Fi`、`-Ex`）。</summary>
    /// <param name="command">命令文本。</param>
    /// <returns>命中禁用开关时返回 <see langword="true"/>。</returns>
    public static bool HasForbiddenSwitch(string command)
    {
        foreach (Match match in Regex.Matches(command, @"(?:^|\s)-([A-Za-z][A-Za-z0-9]*)"))
        {
            string token = match.Groups[1].Value;
            if ("Fix".StartsWith(token, StringComparison.OrdinalIgnoreCase)
                || "Extensions".StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
