using System.Text.RegularExpressions;

namespace HunterBox.Domain.Models;

/// <summary>崛起（MHR）的模型路径规则。</summary>
public static partial class ModelPathRules
{
    /// <summary>武器类型 → 崛起模型目录名（<c>natives/stm/weapon/{目录}</c>），下标即类型编号。</summary>
    private static readonly string[] RiseWeaponFolders =
    [
        "GreatSword", "ShortSword", "DualBlades", "LongSword", "Hammer", "Horn", "Lance", "GunLance",
        "SlashAxe", "ChargeAxe", "InsectGlaive", "Bow", "HeavyBowgun", "LightBowgun", "IG_Insect",
    ];

    /// <summary>武器类型 → 崛起模型代码前缀（模型目录名与文件名用），下标即类型编号。</summary>
    private static readonly string[] RiseWeaponCodes =
    [
        "G_Swd", "S_Swd", "D_Bld", "L_Swd", "Ham", "Hrn", "Lan", "G_Lan",
        "S_Axe", "C_Axe", "I_Gla", "BOW", "H_Bg", "L_Bg", "IG_Ins",
    ];

    /// <summary>崛起武器各类型的附加部件文件名前缀（其余类型只有主代码文件）。</summary>
    private static readonly string[][] RiseWeaponPartPrefixes =
    [
        [], ["SS_Sld", "SS_Swd"], ["DB_L", "DB_R"], ["LS_Saya", "LS_Swd"], [], [],
        ["L_Lan", "L_Sld"], ["GL_Lan", "GL_Sld"], [], ["CA_Sld", "CA_Swd"], [], ["B_Bow", "B_Ydt"], [], [], [],
    ];

    private static readonly string[] RiseArmorParts = ["arm", "body", "helm", "leg", "wst"];

    /// <summary>崛起路径 → 身份（<c>player/mod</c>、<c>weapon</c>、<c>player/prefab</c>、骨骼）。</summary>
    private static bool TryParseRisePath(string normalized, out ModelIdentity identity)
    {
        identity = EmptyIdentity;

        Match armor = RiseArmorPath().Match(normalized);
        if (armor.Success)
        {
            bool female = armor.Groups["sex"].Value.Equals("f", StringComparison.OrdinalIgnoreCase);
            identity = ModelIdentity.RiseArmor(int.Parse(armor.Groups["id"].Value), female);
            return true;
        }

        Match armorPrefab = RiseArmorPrefabPath().Match(normalized);
        if (armorPrefab.Success)
        {
            bool female = armorPrefab.Groups["sex"].Value.Equals("f", StringComparison.OrdinalIgnoreCase);
            identity = ModelIdentity.RiseArmor(int.Parse(armorPrefab.Groups["id"].Value), female);
            return true;
        }

        Match weapon = RiseWeaponPath().Match(normalized);
        if (weapon.Success
            && TryRiseWeaponType(weapon.Groups["folder"].Value, out int weaponType))
        {
            identity = ModelIdentity.RiseWeapon(weaponType, int.Parse(weapon.Groups["id"].Value));
            return true;
        }

        Match weaponPrefab = RiseWeaponPrefabPath().Match(normalized);
        if (weaponPrefab.Success
            && TryRiseWeaponType(weaponPrefab.Groups["folder"].Value, out int prefabType))
        {
            string token = weaponPrefab.Groups["file"].Value;
            int pfb = token.IndexOf(".pfb", StringComparison.OrdinalIgnoreCase);
            if (pfb >= 3 && int.TryParse(token.AsSpan(pfb - 3, 3), out int id))
            {
                identity = ModelIdentity.RiseWeapon(prefabType, id);
                return true;
            }
        }

        Match bone = RiseBoneTokenPattern().Match(normalized);
        if (bone.Success)
        {
            bool female = bone.Groups["sex"].Value.Equals("f", StringComparison.OrdinalIgnoreCase);
            identity = ModelIdentity.RiseArmor(int.Parse(bone.Groups["id"].Value), female);
            return true;
        }

        return false;
    }

    /// <summary>崛起完整身份路径形式 → 身份（<c>rise:pl/f/060</c>、<c>rise:wp/GreatSword/001</c>）。</summary>
    private static bool TryParseRiseForm(string text, out ModelIdentity identity)
    {
        identity = EmptyIdentity;

        Match armor = RiseArmorForm().Match(text);
        if (armor.Success)
        {
            bool female = armor.Groups["sex"].Value.Equals("f", StringComparison.OrdinalIgnoreCase);
            identity = ModelIdentity.RiseArmor(int.Parse(armor.Groups["id"].Value), female);
            return true;
        }

        Match weapon = RiseWeaponForm().Match(text);
        if (weapon.Success
            && TryRiseWeaponType(weapon.Groups["folder"].Value, out int weaponType))
        {
            identity = ModelIdentity.RiseWeapon(weaponType, int.Parse(weapon.Groups["id"].Value));
            return true;
        }

        return false;
    }

    /// <summary>崛起身份的 Keys：护甲目录/文件名 token，武器代码与部件 token。</summary>
    private static List<ModelPathKey> BuildRisePathKeys(ModelIdentity identity)
    {
        if (identity.Game != ModelGame.Rise || identity.IsCatEquip)
        {
            return [];
        }

        if (identity.IsWeapon)
        {
            if (identity.Type is < 0 or > ModelTypes.RodInsect)
            {
                return [];
            }

            List<ModelPathKey> weaponKeys =
            [
                new ModelPathKey("code", RiseWeaponCode(identity.Type, identity.FirstId)),
            ];
            string id = $"{identity.FirstId:000}";
            string[] partPrefixes = RiseWeaponPartPrefixes[identity.Type];
            for (int i = 0; i < partPrefixes.Length; i++)
            {
                weaponKeys.Add(new ModelPathKey(
                    "part." + i.ToString("00"),
                    partPrefixes[i] + id));
            }

            return weaponKeys;
        }

        if (!ModelTypes.IsArmorSex(identity.Type))
        {
            return [];
        }

        string sex = RiseSexTag(identity.Type);
        List<ModelPathKey> armorKeys = [new ModelPathKey("dir", $"pl{identity.FirstId:000}")];
        foreach (string part in RiseArmorParts)
        {
            armorKeys.Add(new ModelPathKey(
                "file." + part,
                $"{sex}_{part}{identity.FirstId:000}"));
        }

        return armorKeys;
    }

    /// <summary>判断路径是否属于该崛起身份（决定是否允许改写）。</summary>
    private static bool BelongsToRiseIdentity(string normalized, ModelIdentity identity)
    {
        if (identity.IsCatEquip)
        {
            return false;
        }

        if (identity.IsWeapon)
        {
            string folder = RiseWeaponFolder(identity.Type);
            if (folder.Length == 0)
            {
                return false;
            }

            return normalized.Contains($"weapon/{folder}/", StringComparison.OrdinalIgnoreCase);
        }

        if (!ModelTypes.IsArmorSex(identity.Type))
        {
            return false;
        }

        string sex = RiseSexTag(identity.Type);
        return normalized.Contains($"player/mod/{sex}/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains($"player/prefab/mod/{sex}/", StringComparison.OrdinalIgnoreCase)
            || IsRiseBonePath(normalized);
    }

    /// <summary>崛起骨骼路径（<c>bonesystem/f_arm001</c> 等 lua 骨骼目录）。</summary>
    private static bool IsRiseBonePath(string normalized) =>
        (normalized.Contains("bonesystem", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("bone system", StringComparison.OrdinalIgnoreCase))
        && RiseBoneTokenPattern().IsMatch(normalized);

    /// <summary>崛起武器类型 → 模型目录名；越界返回空串。</summary>
    private static string RiseWeaponFolder(int type) =>
        type >= 0 && type < RiseWeaponFolders.Length ? RiseWeaponFolders[type] : string.Empty;

    /// <summary>崛起武器类型 + 序号 → 模型代码（如 <c>G_Swd001</c>）。</summary>
    private static string RiseWeaponCode(int type, int id) =>
        type >= 0 && type < RiseWeaponCodes.Length ? RiseWeaponCodes[type] + $"{id:000}" : string.Empty;

    /// <summary>护甲性别目录名：3 → <c>f</c>、2 → <c>m</c>；其它类型返回值无意义（调用方须先确认是护甲性别）。</summary>
    private static string RiseSexTag(int type) => type == 3 ? "f" : "m";

    /// <summary>按崛起模型目录名解析武器类型（大小写不敏感，也接受类型表名）。</summary>
    private static bool TryRiseWeaponType(string folder, out int type)
    {
        for (int i = 0; i < RiseWeaponFolders.Length; i++)
        {
            if (RiseWeaponFolders[i].Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                type = i;
                return true;
            }
        }

        string tableName = folder.ToUpperInvariant();
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

    [GeneratedRegex(@"natives[/\\]stm[/\\]player[/\\]mod[/\\](?<sex>[fm])[/\\]pl(?<id>\d{3})[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex RiseArmorPath();

    [GeneratedRegex(@"natives[/\\]stm[/\\]player[/\\]prefab[/\\]mod[/\\](?<sex>[fm])[/\\]pl(?<id>\d{3})[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex RiseArmorPrefabPath();

    [GeneratedRegex(@"natives[/\\]stm[/\\]weapon[/\\](?<folder>[A-Za-z_]+)[/\\](?<code>[A-Za-z_]+?)(?<id>\d{3})[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex RiseWeaponPath();

    [GeneratedRegex(@"natives[/\\]stm[/\\]player[/\\]prefab[/\\]weapon[/\\](?<folder>[A-Za-z_]+)[/\\](?<file>[^/\\]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RiseWeaponPrefabPath();

    [GeneratedRegex(@"(?<sex>[fm])_(?:arm|body|helm|leg|wst)(?<id>\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex RiseBoneTokenPattern();

    [GeneratedRegex(@"^rise:pl/(?<sex>[fm])/(?<id>\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex RiseArmorForm();

    [GeneratedRegex(@"^rise:wp/(?<folder>[A-Za-z_]+)/(?<id>\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex RiseWeaponForm();
}
