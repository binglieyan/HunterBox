using System.Collections.Concurrent;
using System.Globalization;
using HunterBox.Domain.Models;

namespace HunterBox.Infrastructure.Mods;

/// <summary>
/// 把模型路径映射成名称表里的可读名字（荒野 / 崛起 / 世界）。
/// </summary>
/// <remarks>
/// 编号规则：
/// 荒野护甲 <c>ch0X/FFF/SSS</c>（含 <c>_prefab/armor/{male,female}</c> 与骨骼）→ 名称表键 <c>FFF_SSS</c>；
/// 荒野猫装备 <c>ch05/FFF/SSSS</c> → 猫表键 <c>FFF_SSSS</c>；
/// 荒野武器 <c>item/itTT/AA/BBBB</c>（含 <c>_prefab/weapon/wpTT</c>）→ 名称表键 <c>AA_BBBB</c>，类型取 TT
/// （<c>it10/03</c> 按游戏规则特判为猎虫 RodInsect）。
/// 崛起护甲 <c>player/mod/{f,m}/pl{id}</c> → 护甲表 <c>pl{id:000}</c>；崛起武器 <c>weapon/{类型目录}/{代码}</c> → 武器表
/// 类型 + 3 位序号。世界护甲 <c>nativepc/pl/{f,m}_equip/pl{fid}_{sid}</c> → 护甲表 <c>pl{fid:000}_{sid:0000}</c>；
/// 世界武器 <c>nativepc/wp/{类型}/{代码}</c> → 武器表类型 + <c>bs_</c> 代码。
/// </remarks>
public static class ModelNameResolver
{
    /// <summary>武器名称缓存：类型 + 表 ID → 名字（避免每次刷新都线性扫表，P-25）。</summary>
    private static readonly ConcurrentDictionary<(ModelGame Game, string Type, string Id), string?> WeaponNames = new();

    /// <summary>护甲/猫装备名称缓存：表 ID → 名字。</summary>
    private static readonly ConcurrentDictionary<(ModelGame Game, string Id), string?> TableNames = new();

    /// <summary>世界武器 op 部件名称缓存。</summary>
    private static readonly ConcurrentDictionary<(string Type, string OpCode), string?> OpNames = new();
    /// <summary>从路径解析模型身份（按路径前缀自动判断游戏）。</summary>
    /// <param name="path">pak 内或散装文件的相对路径。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParse(string path, out ModelIdentity identity) =>
        ModelIdentity.TryParsePath(path, out identity);

    /// <summary>按指定游戏从路径解析模型身份。</summary>
    /// <param name="path">pak 内或散装文件的相对路径。</param>
    /// <param name="game">所属游戏。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParse(string path, ModelGame game, out ModelIdentity identity) =>
        ModelIdentity.TryParsePath(path, game, out identity);

    /// <summary>查名字（按路径自动判断游戏）。</summary>
    /// <param name="path">模型路径。</param>
    /// <returns>名字；识别不到或表里没有时返回 <see langword="null"/>。</returns>
    public static string? Resolve(string path)
    {
        if (TryParse(path, out ModelIdentity identity))
        {
            return Resolve(identity);
        }

        // 世界武器的 op 部件路径没有独立身份，按 CSV 的 op 列直接查叠层名字
        if (ModelPathRules.TryParseWorldWeaponOpPart(path, out int weaponType, out int opId))
        {
            string typeName = ModelTypes.GetTableName(weaponType);
            string opCode = ModelPathRules.FormatWorldWeaponOpCode(weaponType, opId);
            return CachedName(OpNames, (Type: typeName, OpCode: opCode), key =>
                EmbeddedNameTables.LoadWeapons(ModelGame.World)
                    .FirstOrDefault(row => row.Type.Equals(key.Type, StringComparison.OrdinalIgnoreCase)
                        && row.OpCode is not null
                        && row.OpCode.Equals(key.OpCode, StringComparison.OrdinalIgnoreCase))?.Name);
        }

        return null;
    }

    /// <summary>按解析出的身份查名字。</summary>
    /// <param name="identity">模型身份。</param>
    /// <returns>名字；表里没有时返回 <see langword="null"/>。</returns>
    public static string? Resolve(ModelIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.IsCatEquip)
        {
            return CachedName(TableNames, (Game: identity.Game, Id: identity.Code), key =>
                EmbeddedNameTables.FindName(EmbeddedNameTables.LoadCats(key.Game), key.Id));
        }

        if (identity.IsWeapon)
        {
            string typeName = ModelTypes.GetTableName(identity.Type);
            if (typeName.Length == 0)
            {
                return null;
            }

            string tableId = identity.Game == ModelGame.Rise
                ? identity.FirstId.ToString(CultureInfo.InvariantCulture)
                : identity.Code;
            return CachedName(WeaponNames, (Game: identity.Game, Type: typeName, Id: tableId), key =>
            {
                IReadOnlyList<NameTableRow> rows = EmbeddedNameTables.LoadWeapons(key.Game);
                if (key.Game == ModelGame.Rise)
                {
                    return int.TryParse(key.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowId)
                        ? rows.FirstOrDefault(row =>
                            row.Type.Equals(key.Type, StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(row.Id, out int id)
                            && id == rowId)?.Name
                        : null;
                }

                return rows.FirstOrDefault(row =>
                    row.Type.Equals(key.Type, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(row.Id, key.Id, StringComparison.OrdinalIgnoreCase))?.Name;
            });
        }

        return CachedName(TableNames, (Game: identity.Game, Id: identity.Code), key =>
            EmbeddedNameTables.FindName(EmbeddedNameTables.LoadArmors(key.Game), key.Id));
    }

    /// <summary>只缓存成功结果：未命中的随机/异常 code 不写入，避免缓存随调用次数无限增长。</summary>
    private static string? CachedName<TKey>(
        ConcurrentDictionary<TKey, string?> cache,
        TKey key,
        Func<TKey, string?> factory)
        where TKey : notnull
    {
        if (cache.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        string? name = factory(key);
        if (name is not null)
        {
            cache[key] = name;
        }

        return name;
    }

}
