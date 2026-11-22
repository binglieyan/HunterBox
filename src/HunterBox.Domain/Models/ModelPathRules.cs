using System.Text;
using System.Text.RegularExpressions;

namespace HunterBox.Domain.Models;

/// <summary>
/// 各游戏的模型路径规则：路径 → 身份、身份 → 路径形式/ID、Keys 模板与按身份改写路径。
/// </summary>
/// <remarks>
/// 所有方法都是纯字符串处理，不做文件 IO。改写只改路径里的模型身份片段（护甲 <c>fid/sid</c>、
/// 武器 <c>AA/BBBB</c> 或各作的对应目录/文件名 token），不读取也不修改文件内容；
/// 贴图路径（<c>.tex.</c>）一律跳过，避免模型引用对不上。
/// </remarks>
public static partial class ModelPathRules
{
    /// <summary>判断 MOD 相对路径是否为游戏根层的 pak（不区分大小写，必须不含子目录）。</summary>
    /// <param name="relativePath">MOD 内相对路径。</param>
    /// <returns>根层 pak 时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> 为 <see langword="null"/>。</exception>
    public static bool IsRootPak(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return relativePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
            && !relativePath.Contains('/');
    }

    /// <summary>默认身份（解析失败时的 out 值）。</summary>
    private static ModelIdentity EmptyIdentity => new(ModelKind.Armor, 2, 0, 0) { Game = ModelGame.Unknown };

    // ------------------------------------------------------------------ 解析

    /// <summary>解析真实游戏路径里的模型身份（按路径前缀自动判断游戏）。</summary>
    /// <param name="path">相对路径（<c>/</c> 或 <c>\</c> 分隔）。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParsePath(string? path, out ModelIdentity identity)
    {
        identity = EmptyIdentity;
        return !string.IsNullOrWhiteSpace(path) && TryParsePathNormalized(NormalizePath(path), out identity);
    }

    /// <summary>解析已规范化的路径（正斜杠）里的模型身份，按路径前缀自动判断游戏（P-26）。</summary>
    /// <param name="normalizedPath">已用 <see cref="NormalizePath"/> 规范化的相对路径。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParsePathNormalized(string? normalizedPath, out ModelIdentity identity)
    {
        identity = EmptyIdentity;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        ModelGame game = DetectPathGame(normalizedPath);
        return game switch
        {
            ModelGame.Wilds => TryParseWildsPath(normalizedPath, out identity),
            ModelGame.Rise => TryParseRisePath(normalizedPath, out identity),
            ModelGame.World => TryParseWorldPath(normalizedPath, out identity),
            _ => false,
        };
    }

    /// <summary>按指定游戏解析真实游戏路径里的模型身份。</summary>
    /// <param name="path">相对路径（<c>/</c> 或 <c>\</c> 分隔）。</param>
    /// <param name="game">所属游戏。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParsePath(string? path, ModelGame game, out ModelIdentity identity)
    {
        identity = EmptyIdentity;
        return !string.IsNullOrWhiteSpace(path)
            && TryParsePathNormalized(NormalizePath(path), game, out identity);
    }

    /// <summary>按指定游戏解析已规范化路径里的模型身份（P-26）。</summary>
    /// <param name="normalizedPath">已用 <see cref="NormalizePath"/> 规范化的相对路径。</param>
    /// <param name="game">所属游戏；<see cref="ModelGame.Unknown"/> 时按路径前缀判断。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParsePathNormalized(string? normalizedPath, ModelGame game, out ModelIdentity identity)
    {
        identity = EmptyIdentity;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        return game switch
        {
            ModelGame.Wilds => TryParseWildsPath(normalizedPath, out identity),
            ModelGame.Rise => TryParseRisePath(normalizedPath, out identity),
            ModelGame.World => TryParseWorldPath(normalizedPath, out identity),
            _ => TryParsePathNormalized(normalizedPath, out identity),
        };
    }

    /// <summary>解析完整身份路径形式（荒野 <c>ch03/012/003</c>、崛起 <c>rise:pl/f/060</c>、世界 <c>world:wp/bow001</c> 等）。</summary>
    /// <param name="text">身份文本。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>解析成功返回 <see langword="true"/>。</returns>
    public static bool TryParseForm(string? text, out ModelIdentity identity)
    {
        identity = EmptyIdentity;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("rise:", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRiseForm(trimmed, out identity);
        }

        if (trimmed.StartsWith("world:", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseWorldForm(trimmed, out identity);
        }

        return TryParseWildsForm(trimmed, out identity);
    }

    // ------------------------------------------------------------------ 显示

    /// <summary>生成身份的完整模型 ID（用于候选面板/备注显示，按游戏给出稳定写法）。</summary>
    /// <param name="identity">模型身份。</param>
    /// <returns>如 <c>012_003</c>（荒野护甲）、<c>pl060</c>（崛起护甲）、<c>bs_bow001</c>（世界武器）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> 为 <see langword="null"/>。</exception>
    public static string FormatCode(ModelIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return identity.Game switch
        {
            ModelGame.Rise => identity.Kind switch
            {
                ModelKind.Armor => $"pl{identity.FirstId:000}",
                ModelKind.Weapon => RiseWeaponCode(identity.Type, identity.FirstId),
                _ => $"{identity.FirstId:000}_{identity.SecondId:0000}",
            },
            ModelGame.World => identity.Kind switch
            {
                ModelKind.Armor => $"pl{identity.FirstId:000}_{identity.SecondId:0000}",
                ModelKind.Weapon => WorldWeaponCode(identity.Type, identity.FirstId, identity.SecondId),
                _ => $"{identity.FirstId:000}_{identity.SecondId:0000}",
            },
            _ => identity.Kind switch
            {
                ModelKind.Armor => $"{identity.FirstId:000}_{identity.SecondId:000}",
                ModelKind.CatEquip => $"{identity.FirstId:000}_{identity.SecondId:0000}",
                _ => $"{identity.FirstId:00}_{identity.SecondId:0000}",
            },
        };
    }

    /// <summary>生成身份的完整路径形式（改写服务/对话框提交用；荒野保持旧格式）。</summary>
    /// <param name="identity">模型身份。</param>
    /// <returns>如 <c>ch03/012/003</c>、<c>rise:wp/GreatSword/001</c>、<c>world:pl/m/001_0000</c>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> 为 <see langword="null"/>。</exception>
    public static string FormatPathForm(ModelIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return identity.Game switch
        {
            ModelGame.Rise => identity.Kind switch
            {
                ModelKind.Armor => $"rise:pl/{RiseSexTag(identity.Type)}/{identity.FirstId:000}",
                ModelKind.Weapon => $"rise:wp/{RiseWeaponFolder(identity.Type)}/{identity.FirstId:000}",
                _ => $"rise:cat/{identity.FirstId:000}_{identity.SecondId:0000}",
            },
            ModelGame.World => identity.Kind switch
            {
                ModelKind.Armor =>
                    $"world:pl/{WorldSexTag(identity.Type)}/{identity.FirstId:000}_{identity.SecondId:0000}",
                ModelKind.Weapon => WorldWeaponForm(identity),
                _ => $"world:cat/{identity.FirstId:000}_{identity.SecondId:0000}",
            },
            _ => identity.Kind switch
            {
                ModelKind.Armor => $"ch{identity.Type:00}/{identity.FirstId:000}/{identity.SecondId:000}",
                ModelKind.CatEquip => $"ch05/{identity.FirstId:000}/{identity.SecondId:0000}",
                _ => $"it{identity.PathType:00}/{identity.FirstId:00}/{identity.SecondId:0000}",
            },
        };
    }

    // ------------------------------------------------------------------ 改写

    /// <summary>按完整模型身份改写一条路径（只改路径里的身份片段，不改文件内容；<c>.tex.</c> 跳过）。</summary>
    /// <param name="path">原始相对路径（<c>/</c> 或 <c>\</c> 分隔均可）。</param>
    /// <param name="from">源模型身份。</param>
    /// <param name="to">目标模型身份（须与 <paramref name="from"/> 同游戏、同类别、同类型）。</param>
    /// <param name="rewritten">改写后的路径；未命中时等于 <paramref name="path"/>。</param>
    /// <returns>路径确实被改写时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空，或源/目标身份不兼容。</exception>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static bool TryRewritePath(
        string path,
        ModelIdentity from,
        ModelIdentity to,
        out string rewritten)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureCompatible(from, to);

        rewritten = path;
        if (from == to)
        {
            return false;
        }

        string normalized = NormalizePath(path);
        if (IsTexturePath(normalized) || !BelongsToIdentity(normalized, from))
        {
            return false;
        }

        return from.Game switch
        {
            ModelGame.Wilds => TryRewriteWildsPath(path, from, to, out rewritten),
            ModelGame.World => TryRewriteWorldPath(normalized, path, from, to, out rewritten),
            _ => TryRewriteByKeys(normalized, path, from, to, out rewritten),
        };
    }

    /// <summary>判断路径文件名是否贴图（<c>.tex.</c>）；贴图路径不参与改名。</summary>
    /// <param name="path">相对路径。</param>
    /// <returns>是贴图时返回 <see langword="true"/>。</returns>
    public static bool IsTexturePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized = NormalizePath(path);
        int lastSlash = normalized.LastIndexOf('/');
        string fileName = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
        return fileName.Contains(".tex.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>该游戏是否支持按路径哈希改写 pak 条目（世界 pak 不在支持范围）。</summary>
    /// <param name="game">游戏。</param>
    /// <returns>荒野/崛起返回 <see langword="true"/>。</returns>
    public static bool SupportsPakRewrite(ModelGame game) => game is ModelGame.Wilds or ModelGame.Rise;

    /// <summary>解析世界武器的 op 部件路径（<c>nativepc/wp/{类型}/parts/op_*</c>）。</summary>
    /// <param name="path">相对路径。</param>
    /// <param name="weaponType">解析出的武器类型编号。</param>
    /// <param name="opId">op 部件 ID（文件名末尾 3 位）。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParseWorldWeaponOpPart(string? path, out int weaponType, out int opId)
    {
        weaponType = -1;
        opId = 0;
        return !string.IsNullOrWhiteSpace(path)
            && TryParseWorldWeaponOpPartNormalized(NormalizePath(path), out weaponType, out opId);
    }

    /// <summary>解析已规范化路径里的世界武器 op 部件（P-26）。</summary>
    /// <param name="normalizedPath">已用 <see cref="NormalizePath"/> 规范化的相对路径。</param>
    /// <param name="weaponType">武器类型编号；不匹配时为 -1。</param>
    /// <param name="opId">op 部件 ID；不匹配时为 0。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParseWorldWeaponOpPartNormalized(
        string? normalizedPath,
        out int weaponType,
        out int opId)
    {
        weaponType = -1;
        opId = 0;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        Match match = WorldWeaponOpPartPath().Match(normalizedPath);
        if (!match.Success
            || !TryWorldWeaponType(match.Groups["lite"].Value, out weaponType))
        {
            weaponType = -1;
            return false;
        }

        string file = match.Groups["file"].Value;
        int dot = file.IndexOf('.', StringComparison.Ordinal);
        string stem = dot >= 0 ? file[..dot] : file;
        if (stem.Length < 3 || !int.TryParse(stem.AsSpan(stem.Length - 3), out opId))
        {
            // 所有失败分支都返回哨兵值，调用方可以安全忽略返回值读取 out 参数。
            weaponType = -1;
            opId = 0;
            return false;
        }

        return true;
    }

    /// <summary>生成世界武器的 op 部件代码（如 <c>op_bow005</c>）。</summary>
    /// <param name="weaponType">武器类型编号。</param>
    /// <param name="opId">op 部件 ID。</param>
    /// <returns>op 代码；类型不支持时返回空串。</returns>
    public static string FormatWorldWeaponOpCode(int weaponType, int opId)
    {
        string lite = WorldWeaponLite(weaponType);
        return lite.Length == 0 ? string.Empty : $"op_{lite}{opId:000}";
    }

    /// <summary>判断已规范化路径是否应触发模型行（P-26）。</summary>
    /// <param name="normalizedPath">已用 <see cref="NormalizePath"/> 规范化的相对路径。</param>
    /// <param name="game">所属游戏。</param>
    /// <returns>应成行时返回 <see langword="true"/>。</returns>
    public static bool IsRowTriggerPathNormalized(string? normalizedPath, ModelGame game)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        return game switch
        {
            ModelGame.Wilds => IsWildsRowTriggerPath(normalizedPath),
            ModelGame.Rise => !IsTexturePath(normalizedPath)
                && !IsRiseBonePath(normalizedPath)
                && TryParseRisePath(normalizedPath, out _),
            ModelGame.World => !IsTexturePath(normalizedPath)
                && normalizedPath.Contains("/mod/", StringComparison.OrdinalIgnoreCase)
                && TryParseWorldPath(normalizedPath, out _),
            _ => false,
        };
    }

    /// <summary>校验源/目标身份可以互相改写（同游戏、同类别、同类型；护甲不允许换性别）。</summary>
    /// <param name="from">源模型身份。</param>
    /// <param name="to">目标模型身份。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">游戏/类别/类型不一致。</exception>
    internal static void EnsureCompatible(ModelIdentity from, ModelIdentity to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from.Game != to.Game)
        {
            throw new ArgumentException(
                $"源/目标模型所属游戏不一致: {from.Game} -> {to.Game}（一次改写只能在同一游戏内进行）");
        }

        if (from.Kind != to.Kind)
        {
            throw new ArgumentException($"源/目标模型类别不一致: {from.Kind} -> {to.Kind}");
        }

        int fromType = EffectiveType(from);
        int toType = EffectiveType(to);
        if (fromType != toType)
        {
            throw new ArgumentException(
                $"源/目标模型类型不一致: {from.TypeDisplayName} -> {to.TypeDisplayName}（目标候选应只列同类型模型）");
        }
    }

    // ------------------------------------------------------------------ 内部：公共辅助

    /// <summary>按游戏生成身份的 Keys 片段。</summary>
    private static IReadOnlyList<ModelPathKey> BuildPathKeys(ModelIdentity identity) => identity.Game switch
    {
        ModelGame.Wilds => BuildWildsPathKeys(identity),
        ModelGame.Rise => BuildRisePathKeys(identity),
        ModelGame.World => BuildWorldPathKeys(identity),
        _ => [],
    };

    /// <summary>按游戏判断路径是否属于该身份。</summary>
    private static bool BelongsToIdentity(string normalized, ModelIdentity identity) => identity.Game switch
    {
        ModelGame.Wilds => BelongsToWildsIdentity(normalized, identity),
        ModelGame.Rise => BelongsToRiseIdentity(normalized, identity),
        ModelGame.World => BelongsToWorldIdentity(normalized, identity),
        _ => false,
    };

    /// <summary>把路径分隔符统一成正斜杠；批量调用方可规范化一次后复用（P-26）。</summary>
    /// <param name="path">非空路径。</param>
    /// <returns>正斜杠形式的路径。</returns>
    /// <exception cref="ArgumentException">路径为空或空白。</exception>
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.Replace('\\', '/');
    }

    /// <summary>改写比较用的类型编号：荒野武器按路径类型（猎虫复用操虫棍），其余游戏即类型本身。</summary>
    private static int EffectiveType(ModelIdentity identity) =>
        identity.IsWeapon && identity.Game == ModelGame.Wilds ? identity.PathType : identity.Type;

    /// <summary>按已知目录特征（路径前缀或关键片段）判断游戏；无法判断时返回 <see cref="ModelGame.Unknown"/>。</summary>
    private static ModelGame DetectPathGame(string normalized)
    {
        if (normalized.StartsWith("nativepc/", StringComparison.OrdinalIgnoreCase))
        {
            return ModelGame.World;
        }

        if (normalized.Contains("natives/stm/art/model/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("natives/stm/gamedesign/", StringComparison.OrdinalIgnoreCase))
        {
            return ModelGame.Wilds;
        }

        if (normalized.Contains("natives/stm/player/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("natives/stm/weapon/", StringComparison.OrdinalIgnoreCase))
        {
            return ModelGame.Rise;
        }

        if (IsWildsBonePath(normalized) || normalized.Contains("/ch0", StringComparison.OrdinalIgnoreCase))
        {
            return ModelGame.Wilds;
        }

        if (IsRiseBonePath(normalized))
        {
            return ModelGame.Rise;
        }

        return ModelGame.Unknown;
    }

    /// <summary>按 Keys 模板改写（崛起/世界）：把源身份片段换成目标身份片段。</summary>
    private static bool TryRewriteByKeys(
        string normalized,
        string original,
        ModelIdentity from,
        ModelIdentity to,
        out string rewritten)
    {
        rewritten = original;

        List<ModelPathKey> fromKeys = SortForMatching(BuildPathKeys(from));
        Dictionary<string, string> targetKeys = [];
        foreach (ModelPathKey key in BuildPathKeys(to))
        {
            targetKeys[key.Role] = key.Key;
        }

        if (fromKeys.Count == 0)
        {
            return false;
        }

        StringBuilder path = new(normalized);
        bool changed = false;
        foreach (ModelPathKey key in fromKeys)
        {
            if (!targetKeys.TryGetValue(key.Role, out string? target)
                || string.Equals(key.Key, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            changed |= ReplaceTokenPreservingCase(path, key.Key, target);
        }

        if (!changed)
        {
            return false;
        }

        string result = path.ToString();
        if (!BelongsToIdentity(result, to))
        {
            return false;
        }

        rewritten = result;
        return true;
    }

    /// <summary>替换身份片段；源/目标只差数字且等长时只改数字，保留路径原有大小写。</summary>
    private static bool ReplaceTokenPreservingCase(StringBuilder builder, string token, string target)
    {
        if (token.Length != target.Length || !OnlyDigitsDiffer(token, target))
        {
            return ReplaceAllInPlace(builder, token, target);
        }

        if (builder.Length < token.Length)
        {
            return false;
        }

        string text = builder.ToString();
        int found = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (found < 0)
        {
            return false;
        }

        builder.Clear();
        int position = 0;
        while (found >= 0)
        {
            builder.Append(text, position, found - position);
            for (int i = 0; i < token.Length; i++)
            {
                builder.Append(char.IsAsciiDigit(target[i]) ? target[i] : text[found + i]);
            }

            position = found + token.Length;
            found = position <= text.Length - token.Length
                ? text.IndexOf(token, position, StringComparison.OrdinalIgnoreCase)
                : -1;
        }

        builder.Append(text, position, text.Length - position);
        return true;
    }

    /// <summary>两个等长片段是否只在数字位上不同（其余字符忽略大小写一致）。</summary>
    private static bool OnlyDigitsDiffer(string left, string right)
    {
        int digitCount = 0;
        for (int i = 0; i < left.Length; i++)
        {
            bool leftDigit = char.IsAsciiDigit(left[i]);
            bool rightDigit = char.IsAsciiDigit(right[i]);
            if (leftDigit != rightDigit)
            {
                return false;
            }

            if (leftDigit)
            {
                digitCount++;
            }
            else if (char.ToUpperInvariant(left[i]) != char.ToUpperInvariant(right[i]))
            {
                return false;
            }
        }

        return digitCount > 0;
    }

    /// <summary>按匹配优先级排序：长片段先替换，避免短片段是长片段子串时破坏匹配。</summary>
    private static List<ModelPathKey> SortForMatching(IReadOnlyList<ModelPathKey> keys)
    {
        List<ModelPathKey> sorted = [.. keys.Where(key => key.Key.Length > 0)];
        sorted.Sort((left, right) => right.Key.Length.CompareTo(left.Key.Length));
        return sorted;
    }

    /// <summary>把 <paramref name="builder"/> 里所有 <paramref name="token"/>（大小写不敏感）替换成 <paramref name="replacement"/>。</summary>
    /// <returns>确实替换过时返回 <see langword="true"/>。</returns>
    private static bool ReplaceAllInPlace(StringBuilder builder, string token, string replacement)
    {
        if (token.Length == 0 || builder.Length < token.Length)
        {
            return false;
        }

        string text = builder.ToString();
        int found = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (found < 0)
        {
            return false;
        }

        builder.Clear();
        int position = 0;
        while (found >= 0)
        {
            builder.Append(text, position, found - position).Append(replacement);
            position = found + token.Length;
            found = position <= text.Length - token.Length
                ? text.IndexOf(token, position, StringComparison.OrdinalIgnoreCase)
                : -1;
        }

        builder.Append(text, position, text.Length - position);
        return true;
    }
}

/// <summary>一条按角色命名的模型身份路径片段（Keys）。</summary>
/// <param name="Role">角色（同一身份的源/目标按角色对应，如 <c>code</c>、<c>part.arm</c>）。</param>
/// <param name="Key">路径片段文本。</param>
internal sealed record ModelPathKey(string Role, string Key);
