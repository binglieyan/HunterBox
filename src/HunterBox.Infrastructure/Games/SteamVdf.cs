namespace HunterBox.Infrastructure.Games;

/// <summary>Steam VDF（KeyValues 文本）解析后的一个节点。</summary>
/// <remarks>
/// VDF 没有数组：一个键要么是字符串值（<see cref="Value"/>），要么带一组子键（<see cref="Children"/>）。
/// 键名大小写不敏感；同一个键出现多次时后者覆盖前者。
/// </remarks>
public sealed class SteamVdfNode
{
    private readonly Dictionary<string, SteamVdfNode> _children = new(StringComparer.OrdinalIgnoreCase);

    internal SteamVdfNode(string? value) => Value = value;

    /// <summary>字符串值；这是一个「段」节点（带子键）时为 <see langword="null"/>。</summary>
    public string? Value { get; }

    /// <summary>子节点（保持文件出现顺序）。</summary>
    public IReadOnlyDictionary<string, SteamVdfNode> Children => _children;

    /// <summary>按名字取子节点（大小写不敏感）。</summary>
    /// <param name="name">子键名。</param>
    /// <returns>子节点；不存在时为 <see langword="null"/>。</returns>
    public SteamVdfNode? Child(string name) =>
        _children.TryGetValue(name, out SteamVdfNode? node) ? node : null;

    internal void Add(string key, SteamVdfNode node) => _children[key] = node;
}

/// <summary>
/// Steam的 KeyValues 文本格式（<c>libraryfolders.vdf</c> / <c>config.vdf</c>）解析器。
/// </summary>
/// <remarks>
/// 只实现本功能需要的部分：带引号/不带引号的键与值、<c>{}</c> 嵌套、<c>//</c> 行注释、
/// 引号字符串里的 <c>\\</c> / <c>\"</c> 转义。解析失败返回 <see langword="null"/>，不抛异常。
/// </remarks>
public static class SteamVdfParser
{
    /// <summary>解析 VDF 文本。</summary>
    /// <param name="text">VDF 文本（文件内容）。</param>
    /// <returns>根节点（其子节点为顶层键）；文本为空或格式损坏时为 <see langword="null"/>。</returns>
    public static SteamVdfNode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return new Reader(text).ParseDocument();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class Reader
    {
        private readonly string _text;
        private int _index;

        public Reader(string text) => _text = text;

        public SteamVdfNode ParseDocument()
        {
            SteamVdfNode root = new(null);
            ParseEntries(root, topLevel: true);
            return root;
        }

        private void ParseEntries(SteamVdfNode node, bool topLevel)
        {
            while (true)
            {
                string? key = NextToken();
                if (key is null)
                {
                    if (topLevel)
                    {
                        return;
                    }

                    throw new FormatException("Unexpected end of VDF");
                }

                if (key == "}")
                {
                    if (topLevel)
                    {
                        throw new FormatException("Unexpected '}'");
                    }

                    return;
                }

                if (key == "{")
                {
                    throw new FormatException("Unexpected '{'");
                }

                string? next = NextToken();
                if (next is null)
                {
                    if (topLevel)
                    {
                        // 容忍文件末尾多出来的裸键，不当作解析失败。
                        return;
                    }

                    throw new FormatException("Missing value");
                }

                if (next == "{")
                {
                    SteamVdfNode child = new(null);
                    ParseEntries(child, topLevel: false);
                    node.Add(key, child);
                    continue;
                }

                if (next == "}")
                {
                    throw new FormatException("Missing value");
                }

                node.Add(key, new SteamVdfNode(next));
            }
        }

        private string? NextToken()
        {
            SkipTrivia();
            if (_index >= _text.Length)
            {
                return null;
            }

            char current = _text[_index];
            switch (current)
            {
                case '{':
                case '}':
                    _index++;
                    return current.ToString();
                case '"':
                    return ReadQuoted();
                default:
                    return ReadBare();
            }
        }

        private void SkipTrivia()
        {
            while (_index < _text.Length)
            {
                char current = _text[_index];
                if (char.IsWhiteSpace(current) || current == '\uFEFF')
                {
                    _index++;
                    continue;
                }

                if (current == '/' && _index + 1 < _text.Length && _text[_index + 1] == '/')
                {
                    _index += 2;
                    while (_index < _text.Length && _text[_index] is not '\n' and not '\r')
                    {
                        _index++;
                    }

                    continue;
                }

                break;
            }
        }

        private string ReadQuoted()
        {
            _index++; // 跳过开头的引号
            System.Text.StringBuilder builder = new();
            while (_index < _text.Length)
            {
                char current = _text[_index++];
                if (current == '"')
                {
                    return builder.ToString();
                }

                if (current == '\\' && _index < _text.Length)
                {
                    // Steam 对路径里的反斜杠写作 \\；其它转义按「反斜杠+字符」原样还原。
                    char escaped = _text[_index++];
                    builder.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        _ => escaped,
                    });
                    continue;
                }

                builder.Append(current);
            }

            throw new FormatException("Unterminated quoted string");
        }

        private string ReadBare()
        {
            int start = _index;
            while (_index < _text.Length)
            {
                char current = _text[_index];
                if (char.IsWhiteSpace(current) || current is '{' or '}' or '"')
                {
                    break;
                }

                _index++;
            }

            if (_index == start)
            {
                // 无法识别的字符（例如单引号）：跳过一个，避免死循环。
                _index++;
                throw new FormatException("Unexpected character");
            }

            return _text[start.._index];
        }
    }
}
