using System.Text;
using System.Text.RegularExpressions;

namespace HunterBox.Domain.Models;

/// <summary>世界（MHW）的模型路径规则。</summary>
public static partial class ModelPathRules
{
    /// <summary>武器类型 → 世界模型目录/代码前缀（<c>nativepc/wp/{lite}</c>），下标即类型编号。</summary>
    private static readonly string[] WorldWeaponLiteNames =
    [
        "two", "one", "sou", "swo", "ham", "hue", "lan", "gun", "caxe", "saxe", "rod", "bow", "hbg", "lbg", "mus",
    ];

    /// <summary>世界路径 → 身份（<c>nativepc/pl/*_equip</c>、<c>nativepc/wp</c>）。</summary>
    private static bool TryParseWorldPath(string normalized, out ModelIdentity identity)
    {
        identity = EmptyIdentity;

        Match armor = WorldArmorPath().Match(normalized);
        if (armor.Success)
        {
            bool female = armor.Groups["sex"].Value.Equals("f", StringComparison.OrdinalIgnoreCase);
            identity = ModelIdentity.WorldArmor(
                female ? 3 : 2,
                int.Parse(armor.Groups["fid"].Value),
                int.Parse(armor.Groups["sid"].Value));
            return true;
        }

        Match weapon = WorldWeaponPath().Match(normalized);
        if (weapon.Success
            && TryWorldWeaponType(weapon.Groups["lite"].Value, out int weaponType))
        {
            string code = weapon.Groups["code"].Value;
            bool withBs = code.StartsWith("bs_", StringComparison.OrdinalIgnoreCase);
            int fid = int.Parse(code[^3..]);
            identity = ModelIdentity.WorldWeapon(weaponType, fid, withBs, 0);
            return true;
        }

        return false;
    }

    /// <summary>世界完整身份路径形式 → 身份（<c>world:pl/m/001_0000</c>、<c>world:wp/bs_bow001</c>）。</summary>
    private static bool TryParseWorldForm(string text, out ModelIdentity identity)
    {
        identity = EmptyIdentity;

        Match armor = WorldArmorForm().Match(text);
        if (armor.Success)
        {
            bool female = armor.Groups["sex"].Value.Equals("f", StringComparison.OrdinalIgnoreCase);
            identity = ModelIdentity.WorldArmor(
                female ? 3 : 2,
                int.Parse(armor.Groups["fid"].Value),
                int.Parse(armor.Groups["sid"].Value));
            return true;
        }

        Match weapon = WorldWeaponForm().Match(text);
        if (weapon.Success
            && TryWorldWeaponCode(weapon.Groups["code"].Value, out int weaponType, out bool withBs, out int fid))
        {
            int op = weapon.Groups["op"].Success ? int.Parse(weapon.Groups["op"].Value) : 0;
            identity = ModelIdentity.WorldWeapon(weaponType, fid, withBs, op);
            return true;
        }

        return false;
    }

    /// <summary>世界身份的 Keys：护甲 ID 对；武器的代码与部件 token（含 op 部件）。</summary>
    private static List<ModelPathKey> BuildWorldPathKeys(ModelIdentity identity)
    {
        if (identity.Game != ModelGame.World || identity.IsCatEquip)
        {
            return [];
        }

        if (!identity.IsWeapon)
        {
            return [new ModelPathKey("id", $"{identity.FirstId:000}_{identity.SecondId:0000}")];
        }

        string lite = WorldWeaponLite(identity.Type);
        if (lite.Length == 0)
        {
            return [];
        }

        bool withBs = identity.SecondId % 10 > 0;
        int opId = (identity.SecondId % 10000) / 10;
        string prefix = withBs ? "bs_" : string.Empty;
        string id = $"{identity.FirstId:000}";

        List<ModelPathKey> keys = [new ModelPathKey("code", prefix + lite + id)];
        string? part = identity.Type switch
        {
            ModelTypes.ShortSword or ModelTypes.Lance or ModelTypes.GunLance or ModelTypes.ChargeAxe =>
                prefix + "sld" + id,
            ModelTypes.Tachi => prefix + "saya" + id,
            ModelTypes.Bow => prefix + "ya" + id,
            ModelTypes.TwinSword => prefix + "sou_r" + id,
            _ => null,
        };
        if (part is not null)
        {
            keys.Add(new ModelPathKey("part", part));
        }

        if (opId > 0)
        {
            keys.Add(new ModelPathKey("op", $"op_{lite}{opId:000}"));
            if (identity.Type == ModelTypes.TwinSword)
            {
                keys.Add(new ModelPathKey("op.r", $"op_sou_r{opId:000}"));
            }
        }

        return keys;
    }

    /// <summary>判断路径是否属于该世界身份（决定是否允许改写）。</summary>
    private static bool BelongsToWorldIdentity(string normalized, ModelIdentity identity)
    {
        if (identity.IsCatEquip)
        {
            return false;
        }

        if (identity.IsWeapon)
        {
            string lite = WorldWeaponLite(identity.Type);
            if (lite.Length == 0
                || !normalized.Contains($"wp/{lite}/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // /parts/ 下的文件是 op 部件，只有带 op 的身份才参与（基础武器不能改部件文件名）
            return !normalized.Contains("/parts/", StringComparison.OrdinalIgnoreCase)
                || (identity.SecondId % 10000) / 10 > 0;
        }

        if (!ModelTypes.IsArmorSex(identity.Type))
        {
            return false;
        }

        string sex = identity.Type == 3 ? "f_equip" : "m_equip";
        return normalized.Contains($"pl/{sex}/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>世界改写：<c>/parts/</c> 下只改 op 部件文件名，普通武器/护甲按 Keys 片段替换。</summary>
    private static bool TryRewriteWorldPath(
        string normalized,
        string original,
        ModelIdentity from,
        ModelIdentity to,
        out string rewritten)
    {
        rewritten = original;

        if (!normalized.Contains("/parts/", StringComparison.OrdinalIgnoreCase))
        {
            return TryRewriteByKeys(normalized, original, from, to, out rewritten);
        }

        Dictionary<string, string> targetKeys = [];
        foreach (ModelPathKey key in BuildWorldPathKeys(to))
        {
            targetKeys[key.Role] = key.Key;
        }

        StringBuilder path = new(normalized);
        bool changed = false;
        foreach (ModelPathKey key in SortForMatching(BuildWorldPathKeys(from)))
        {
            if (key.Role is not ("op" or "op.r")
                || !targetKeys.TryGetValue(key.Role, out string? target)
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

        rewritten = path.ToString();
        return true;
    }

    /// <summary>世界武器类型 → 模型目录/代码前缀；越界或不支持返回空串。</summary>
    private static string WorldWeaponLite(int type) =>
        type >= 0 && type < WorldWeaponLiteNames.Length ? WorldWeaponLiteNames[type] : string.Empty;

    /// <summary>世界武器完整 ID → 模型代码（<c>bs_bow001</c>，含 op 时追加 <c>:op_bow005</c> 以便区分候选）。</summary>
    private static string WorldWeaponCode(int type, int fid, int fullId)
    {
        string lite = WorldWeaponLite(type);
        if (lite.Length == 0)
        {
            return $"{fid:000}_{fullId:0000}";
        }

        bool withBs = fullId % 10 > 0;
        int opId = (fullId % 10000) / 10;
        string code = (withBs ? "bs_" : string.Empty) + lite + $"{fid:000}";
        return opId > 0 ? code + ":op_" + lite + $"{opId:000}" : code;
    }

    /// <summary>世界武器身份的路径形式（<c>world:wp/bs_bow001</c>，op 部件为 <c>world:wp/bs_bow001/op005</c>）。</summary>
    private static string WorldWeaponForm(ModelIdentity identity)
    {
        string lite = WorldWeaponLite(identity.Type);
        bool withBs = identity.SecondId % 10 > 0;
        int opId = (identity.SecondId % 10000) / 10;
        string code = (withBs ? "bs_" : string.Empty) + lite + $"{identity.FirstId:000}";
        return opId > 0
            ? $"world:wp/{code}/op{opId:000}"
            : $"world:wp/{code}";
    }

    /// <summary>世界护甲性别目录名：3 → <c>f</c>、2 → <c>m</c>；其它类型返回值无意义（调用方须先确认是护甲性别）。</summary>
    private static string WorldSexTag(int type) => type == 3 ? "f" : "m";

    /// <summary>按世界模型代码（<c>bow001</c> / <c>bs_bow001</c>）解析类型、bs 与 f_id。</summary>
    private static bool TryWorldWeaponCode(
        string code,
        out int type,
        out bool withBs,
        out int fid)
    {
        type = -1;
        withBs = false;
        fid = 0;

        string text = code.Trim();
        if (text.StartsWith("bs_", StringComparison.OrdinalIgnoreCase))
        {
            withBs = true;
            text = text[3..];
        }

        if (text.Length < 4 || !int.TryParse(text[^3..], out fid))
        {
            return false;
        }

        return TryWorldWeaponType(text[..^3], out type);
    }

    /// <summary>按世界模型目录/代码前缀解析武器类型。</summary>
    private static bool TryWorldWeaponType(string lite, out int type)
    {
        for (int i = 0; i < WorldWeaponLiteNames.Length; i++)
        {
            if (WorldWeaponLiteNames[i].Equals(lite, StringComparison.OrdinalIgnoreCase))
            {
                type = i;
                return true;
            }
        }

        string tableName = lite.ToUpperInvariant();
        for (int i = 0; i <= ModelTypes.RodInsect; i++)
        {
            if (ModelTypes.GetTableName(i).Equals(tableName, StringComparison.OrdinalIgnoreCase))
            {
                type = i;
                return true;
            }
        }

        type = -1;
        return false;
    }

    [GeneratedRegex(@"^nativepc[/\\]pl[/\\](?<sex>[fm])_equip[/\\]pl(?<fid>\d{3})_(?<sid>\d{4})[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex WorldArmorPath();

    [GeneratedRegex(@"^nativepc[/\\]wp[/\\](?<lite>[A-Za-z]+)[/\\](?<code>[A-Za-z_]*\d{3})[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex WorldWeaponPath();

    [GeneratedRegex(@"^nativepc[/\\]wp[/\\](?<lite>[A-Za-z]+)[/\\]parts[/\\](?<file>op_[^/\\]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WorldWeaponOpPartPath();

    [GeneratedRegex(@"^world:pl/(?<sex>[mf])/(?<fid>\d{3})_(?<sid>\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex WorldArmorForm();

    [GeneratedRegex(@"^world:wp/(?<code>[A-Za-z_]+?\d{3})(?:/op(?<op>\d{3}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex WorldWeaponForm();
}
