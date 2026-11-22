using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HunterBox.Domain.Models;

/// <summary>荒野（MHWS）的模型路径规则。</summary>
public static partial class ModelPathRules
{
    /// <summary>荒野路径 → 身份（<c>art/model</c> 与 <c>_prefab</c> 与骨骼）。</summary>
    private static bool TryParseWildsPath(string normalized, out ModelIdentity identity)
    {
        identity = EmptyIdentity;

        Match character = WildsCharacterPath().Match(normalized);
        if (character.Success)
        {
            int fid = int.Parse(character.Groups["fid"].Value, CultureInfo.InvariantCulture);
            string sidText = character.Groups["sid"].Value;
            int sid = int.Parse(sidText, CultureInfo.InvariantCulture);

            // 护甲 sid 固定 3 位、猫装备 4 位：位数不符时拒绝识别，避免把 4 位目录截断成另一个合法身份。
            switch (character.Groups["group"].Value)
            {
                case "02" when sidText.Length == 3:
                    identity = ModelIdentity.Armor(2, fid, sid);
                    return true;
                case "03" when sidText.Length == 3:
                    identity = ModelIdentity.Armor(3, fid, sid);
                    return true;
                case "05" when sidText.Length == 4:
                    identity = ModelIdentity.Cat(fid, sid);
                    return true;
            }
        }

        Match item = WildsItemPath().Match(normalized);
        if (item.Success
            && int.TryParse(
                item.Groups["type"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int pathType)
            && pathType <= ModelTypes.LightBowgun)
        {
            // it14+ 不是任何合法武器路径类型：先判范围再构造，TryParse 不抛异常。
            identity = ModelIdentity.Weapon(
                pathType,
                int.Parse(item.Groups["aa"].Value, CultureInfo.InvariantCulture),
                int.Parse(item.Groups["bbbb"].Value, CultureInfo.InvariantCulture));
            return true;
        }

        Match armorPrefab = WildsArmorPrefabPath().Match(normalized);
        if (armorPrefab.Success)
        {
            int sex = armorPrefab.Groups["sex"].Value.Equals("female", StringComparison.OrdinalIgnoreCase)
                ? 3
                : 2;
            identity = ModelIdentity.Armor(
                sex,
                int.Parse(armorPrefab.Groups["fid"].Value),
                int.Parse(armorPrefab.Groups["sid"].Value));
            return true;
        }

        Match weaponPrefab = WildsWeaponPrefabPath().Match(normalized);
        if (weaponPrefab.Success
            && int.TryParse(
                weaponPrefab.Groups["type"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int prefabType)
            && prefabType <= ModelTypes.LightBowgun)
        {
            // wp14+ 同样不是合法武器路径类型：先判范围再构造，避免 TryParse 抛异常。
            identity = ModelIdentity.Weapon(
                prefabType,
                int.Parse(weaponPrefab.Groups["aa"].Value, CultureInfo.InvariantCulture),
                int.Parse(weaponPrefab.Groups["bbbb"].Value, CultureInfo.InvariantCulture));
            return true;
        }

        Match bone = WildsBonePathPattern().Match(normalized);
        if (bone.Success && int.TryParse(bone.Groups["group"].Value, out int boneSex))
        {
            identity = ModelIdentity.Armor(
                boneSex,
                int.Parse(bone.Groups["fid"].Value),
                int.Parse(bone.Groups["sid"].Value));
            return true;
        }

        return false;
    }

    /// <summary>荒野完整身份路径形式 → 身份（<c>ch03/012/003</c>、<c>ch05/003/0001</c>、<c>it10/03/0000</c>）。</summary>
    private static bool TryParseWildsForm(string text, out ModelIdentity identity)
    {
        identity = EmptyIdentity;

        Match armor = WildsArmorIdentityForm().Match(text);
        if (armor.Success && int.TryParse(armor.Groups["group"].Value, out int sex))
        {
            identity = ModelIdentity.Armor(
                sex,
                int.Parse(armor.Groups["fid"].Value),
                int.Parse(armor.Groups["sid"].Value));
            return true;
        }

        Match cat = WildsCatIdentityForm().Match(text);
        if (cat.Success)
        {
            identity = ModelIdentity.Cat(
                int.Parse(cat.Groups["fid"].Value),
                int.Parse(cat.Groups["sid"].Value));
            return true;
        }

        Match weapon = WildsWeaponIdentityForm().Match(text);
        if (weapon.Success
            && int.TryParse(
                weapon.Groups["type"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int pathType)
            && pathType <= ModelTypes.LightBowgun)
        {
            // it14/99、wp14/99 不是合法武器路径类型：返回 false 而不是抛异常。
            identity = ModelIdentity.Weapon(
                pathType,
                int.Parse(weapon.Groups["aa"].Value, CultureInfo.InvariantCulture),
                int.Parse(weapon.Groups["bbbb"].Value, CultureInfo.InvariantCulture));
            return true;
        }

        return false;
    }

    /// <summary>荒野身份的 Keys：目录段与文件名 token。</summary>
    private static IReadOnlyList<ModelPathKey> BuildWildsPathKeys(ModelIdentity identity)
    {
        if (identity.IsCatEquip)
        {
            string catId = $"{identity.FirstId:000}/{identity.SecondId:0000}";
            return
            [
                new ModelPathKey("dir", $"ch05/{catId}"),
                new ModelPathKey("file", $"ch05_{identity.FirstId:000}_{identity.SecondId:0000}"),
                new ModelPathKey("id", $"{identity.FirstId:000}_{identity.SecondId:0000}"),
            ];
        }

        if (identity.IsWeapon)
        {
            string pathType = $"{identity.PathType:00}";
            string numbers = $"{identity.FirstId:00}/{identity.SecondId:0000}";
            return
            [
                new ModelPathKey("dir", $"it{pathType}/{numbers}"),
                new ModelPathKey("prefabDir", $"wp{pathType}/{numbers}"),
                new ModelPathKey("file", $"it{pathType}{identity.FirstId:00}_{identity.SecondId:0000}"),
            ];
        }

        string sex = identity.Type == 3 ? "female" : "male";
        string ids = $"{identity.FirstId:000}/{identity.SecondId:000}";
        return
        [
            new ModelPathKey("dir", $"ch{identity.Type:00}/{ids}"),
            new ModelPathKey("prefabDir", $"armor/{sex}/{ids}"),
            new ModelPathKey("file", $"ch{identity.Type:00}_{identity.FirstId:000}_{identity.SecondId:000}"),
            new ModelPathKey("avp", $"{identity.FirstId:000}_{identity.SecondId:000}_avp"),
        ];
    }

    /// <summary>荒野骨骼路径（<c>bonesystem/ch0X_FID_SIDN</c> 等）。</summary>
    private static bool IsWildsBonePath(string normalized) =>
        normalized.Contains("bonesystem", StringComparison.OrdinalIgnoreCase)
        && WildsBoneToken().IsMatch(normalized);

    /// <summary>荒野行触发：只由 <c>art/model</c> 与 <c>equip/_prefab</c> 路径触发行（骨骼不单独出行）。</summary>
    private static bool IsWildsRowTriggerPath(string normalized) =>
        normalized.Contains("art/model/", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("gamedesign/equip/_prefab/", StringComparison.OrdinalIgnoreCase);

    /// <summary>荒野按完整身份改写（保持现有大小写/数字段语义）。</summary>
    private static bool TryRewriteWildsPath(
        string path,
        ModelIdentity from,
        ModelIdentity to,
        out string rewritten)
    {
        rewritten = path;
        if (from == to || IsTexturePath(path) || !BelongsToWildsIdentity(NormalizePath(path), from))
        {
            return false;
        }

        string normalized = NormalizePath(path);
        StringBuilder? builder = null;
        switch (from.Kind)
        {
            case ModelKind.Armor:
                string sex = from.Type == 3 ? "female" : "male";
                string armorPrefix = $"ch{from.Type:00}/";
                string armorNumbers = $"{from.FirstId:000}/{from.SecondId:000}";
                string newArmorNumbers = $"{to.FirstId:000}/{to.SecondId:000}";
                builder = ReplaceIdentityToken(
                    builder,
                    normalized,
                    $"{armorPrefix}{armorNumbers}/",
                    newArmorNumbers,
                    armorPrefix.Length,
                    armorNumbers.Length);

                string prefabPrefix = $"armor/{sex}/";
                builder = ReplaceIdentityToken(
                    builder,
                    normalized,
                    $"{prefabPrefix}{armorNumbers}/",
                    newArmorNumbers,
                    prefabPrefix.Length,
                    armorNumbers.Length);

                string armorFilePrefix = $"ch{from.Type:00}_";
                string armorFileNumbers = $"{from.FirstId:000}_{from.SecondId:000}";
                builder = ReplaceIdentityToken(
                    builder,
                    normalized,
                    $"{armorFilePrefix}{armorFileNumbers}",
                    $"{to.FirstId:000}_{to.SecondId:000}",
                    armorFilePrefix.Length,
                    armorFileNumbers.Length);

                if (normalized.Contains(prefabPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    builder = ReplaceIdentityToken(
                        builder,
                        normalized,
                        $"{armorFileNumbers}_avp",
                        $"{to.FirstId:000}_{to.SecondId:000}",
                        0,
                        armorFileNumbers.Length);
                }

                break;

            case ModelKind.CatEquip:
                string catNumbers = $"{from.FirstId:000}/{from.SecondId:0000}";
                string newCatNumbers = $"{to.FirstId:000}/{to.SecondId:0000}";
                builder = ReplaceIdentityToken(
                    builder,
                    normalized,
                    $"ch05/{catNumbers}/",
                    newCatNumbers,
                    5,
                    catNumbers.Length);
                builder = ReplaceIdentityToken(
                    builder,
                    normalized,
                    $"ch05_{from.FirstId:000}_{from.SecondId:0000}",
                    $"{to.FirstId:000}_{to.SecondId:0000}",
                    5,
                    catNumbers.Length);
                break;

            default:
                string pathType = $"{from.PathType:00}";
                string weaponNumbers = $"{from.FirstId:00}/{from.SecondId:0000}";
                string newWeaponNumbers = $"{to.FirstId:00}/{to.SecondId:0000}";
                builder = ReplaceIdentityToken(
                    builder,
                    normalized,
                    $"it{pathType}/{weaponNumbers}/",
                    newWeaponNumbers,
                    5,
                    weaponNumbers.Length);
                builder = ReplaceIdentityToken(
                    builder,
                    normalized,
                    $"wp{pathType}/{weaponNumbers}/",
                    newWeaponNumbers,
                    5,
                    weaponNumbers.Length);
                builder = ReplaceIdentityToken(
                    builder,
                    normalized,
                    $"it{pathType}{from.FirstId:00}_{from.SecondId:0000}",
                    $"{to.FirstId:00}_{to.SecondId:0000}",
                    4,
                    weaponNumbers.Length);
                break;
        }

        if (builder is null)
        {
            return false;
        }

        rewritten = builder.ToString();
        return !string.Equals(rewritten, path, StringComparison.Ordinal);
    }

    /// <summary>判断路径是否属于该荒野身份（决定是否允许改写，避免误改无关文件里的数字）。</summary>
    private static bool BelongsToWildsIdentity(string normalized, ModelIdentity identity)
    {
        if (identity.IsWeapon)
        {
            string pathType = $"{identity.PathType:00}";
            return normalized.Contains($"art/model/item/it{pathType}/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(
                    $"gamedesign/equip/_prefab/weapon/wp{pathType}/",
                    StringComparison.OrdinalIgnoreCase);
        }

        if (identity.IsCatEquip)
        {
            return normalized.Contains("art/model/character/ch05/", StringComparison.OrdinalIgnoreCase);
        }

        string sex = identity.Type == 3 ? "female" : "male";
        if (normalized.Contains($"art/model/character/ch{identity.Type:00}/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains($"gamedesign/equip/_prefab/armor/{sex}/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 骨骼/lua 骨骼：reframework/data/bonesystem/ch0X_FID_SIDN
        bool isBone = normalized.Contains("bonesystem", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("bone system", StringComparison.OrdinalIgnoreCase);
        return isBone
            && normalized.Contains($"ch{identity.Type:00}_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把路径里所有 <paramref name="token"/> 的数字段换成 <paramref name="numbers"/>（保留其余字符的大小写）。</summary>
    /// <remarks>只替换数字段，且替换前后等长，因此下标稳定；未命中时返回 <see langword="null"/>。</remarks>
    private static StringBuilder? ReplaceIdentityToken(
        StringBuilder? builder,
        string normalized,
        string token,
        string numbers,
        int numbersOffset,
        int numbersLength)
    {
        if (numbers.Length != numbersLength)
        {
            throw new ArgumentException($"身份替换段长度不一致: {token} -> {numbers}");
        }

        if (numbersOffset + numbersLength > token.Length)
        {
            throw new ArgumentException($"身份替换段越界: {token} -> {numbers}");
        }

        int searchFrom = 0;
        while (searchFrom <= normalized.Length - token.Length)
        {
            int found = normalized.IndexOf(token, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            builder ??= new StringBuilder(normalized);
            builder.Remove(found + numbersOffset, numbersLength).Insert(found + numbersOffset, numbers);
            searchFrom = found + token.Length;
        }

        return builder;
    }

    [GeneratedRegex(@"^ch0(?<group>[23])/(?<fid>\d{3})/(?<sid>\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex WildsArmorIdentityForm();

    [GeneratedRegex(@"^ch05/(?<fid>\d{3})/(?<sid>\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex WildsCatIdentityForm();

    [GeneratedRegex(@"^(?:it|wp)(?<type>\d{2})/(?<aa>\d{2})/(?<bbbb>\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex WildsWeaponIdentityForm();

    [GeneratedRegex(@"art[/\\]model[/\\]character[/\\]ch(?<group>0[235])[/\\](?<fid>\d{3})[/\\](?<sid>\d{3,4})[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex WildsCharacterPath();

    [GeneratedRegex(@"art[/\\]model[/\\]item[/\\]it(?<type>\d{2})[/\\](?<aa>\d{2})[/\\](?<bbbb>\d{4})[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex WildsItemPath();

    [GeneratedRegex(@"gamedesign[/\\]equip[/\\]_prefab[/\\]armor[/\\](?<sex>male|female)[/\\](?<fid>\d{3})[/\\](?<sid>\d{3})[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex WildsArmorPrefabPath();

    [GeneratedRegex(@"gamedesign[/\\]equip[/\\]_prefab[/\\]weapon[/\\]wp(?<type>\d{2})[/\\](?<aa>\d{2})[/\\](?<bbbb>\d{4})[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex WildsWeaponPrefabPath();

    [GeneratedRegex(@"bonesystem[/\\].*?ch(?<group>0[23])_(?<fid>\d{3})_(?<sid>\d{3})\d", RegexOptions.IgnoreCase)]
    private static partial Regex WildsBonePathPattern();

    [GeneratedRegex(@"ch0[23]_\d{3}_\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex WildsBoneToken();
}
