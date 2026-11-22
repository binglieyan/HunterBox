using System.Collections.ObjectModel;
using HunterBox.Domain.Models;
using HunterBox.Infrastructure.Paks;
using DomainModelIdentity = HunterBox.Domain.Models.ModelIdentity;

namespace HunterBox.Infrastructure.Mods;

/// <summary>用内置名称表构建各作的模型目标候选。</summary>
/// <remarks>
/// 候选语义按作区分：护甲每个 CSV 行一个完整模型（荒野/世界为 <c>fid_sid</c>，崛起为 3 位序号）；
/// 荒野行名带「男款/女款」标注时按标注生成对应性别身份（未标注的行按男款），
/// 世界/崛起表不分性别、身份按男款生成。实际改写时目标性别由当前模型行决定，
/// 因此同一候选对男款（<c>ch02</c>）与女款（<c>ch03</c>）路径都能命中。
/// 武器以完整模型 ID 为候选（荒野 <c>AA_BBBB</c>、崛起 3 位序号、世界含 <c>bs_</c>/<c>op_</c> 变体的完整 ID）。
/// 候选的 (chain) 标记只来自荒野内置游戏清单（其他作没有该标记），依据该模型是否有 <c>.chain</c> 文件。
/// 无参构造函数默认荒野，保持旧调用不变。
/// </remarks>
public sealed class ModelCatalog : IModelCatalog
{
    private static readonly Lazy<IReadOnlySet<string>> WildsChainKeys = new(BuildWildsChainKeys);

    private static readonly Lazy<IReadOnlyList<ModelChoice>>[] ArmorChoices = CreateCaches(BuildArmorTargets);

    private static readonly Lazy<IReadOnlyList<ModelChoice>>[] CatChoices = CreateCaches(BuildCatTargets);

    /// <summary>武器候选缓存：游戏 → 武器类型 → 候选；避免编辑器每行都重建整张表（P-17）。</summary>
    private static readonly Lazy<IReadOnlyList<ModelChoice>>[][] WeaponChoices = CreateWeaponCaches();

    /// <summary>用荒野名称表构建目录（internal：仅兼容测试/旧调用）。</summary>
    internal ModelCatalog()
        : this(ModelGame.Wilds)
    {
    }

    /// <summary>用指定游戏的名称表构建目录。</summary>
    /// <param name="game">游戏；<see cref="ModelGame.Unknown"/> 退化为荒野。</param>
    public ModelCatalog(ModelGame game)
    {
        Game = game is ModelGame.Wilds or ModelGame.Rise or ModelGame.World ? game : ModelGame.Wilds;
    }

    /// <summary>本目录对应的游戏。</summary>
    public ModelGame Game { get; }

    /// <inheritdoc />
    public IReadOnlyList<ModelChoice> ArmorTargets => ArmorChoices[(int)Game].Value;

    /// <inheritdoc />
    public IReadOnlyList<ModelChoice> WeaponTargets(int weaponType)
    {
        if (!ModelTypes.IsWeapon(weaponType))
        {
            return [];
        }

        return WeaponChoices[(int)Game][weaponType].Value;
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelChoice> CatTargets(int? secondId = null)
    {
        if (Game != ModelGame.Wilds)
        {
            return [];
        }

        List<ModelChoice> targets = [];
        foreach (ModelChoice choice in CatChoices[(int)Game].Value)
        {
            if (secondId is null || choice.Identity?.SecondId == secondId)
            {
                targets.Add(choice);
            }
        }

        return targets;
    }

    private static Lazy<IReadOnlyList<ModelChoice>>[][] CreateWeaponCaches()
    {
        Lazy<IReadOnlyList<ModelChoice>>[][] caches = new Lazy<IReadOnlyList<ModelChoice>>[4][];
        foreach (ModelGame game in new[] { ModelGame.Wilds, ModelGame.Rise, ModelGame.World })
        {
            ModelGame capturedGame = game;
            Lazy<IReadOnlyList<ModelChoice>>[] types =
                new Lazy<IReadOnlyList<ModelChoice>>[ModelTypes.RodInsect + 1];
            for (int type = 0; type < types.Length; type++)
            {
                int capturedType = type;
                types[type] = new Lazy<IReadOnlyList<ModelChoice>>(() => capturedGame switch
                {
                    ModelGame.Rise => BuildRiseWeaponTargets(capturedType),
                    ModelGame.World => BuildWorldWeaponTargets(capturedType),
                    _ => BuildWildsWeaponTargets(capturedType),
                });
            }

            caches[(int)game] = types;
        }

        for (int i = 0; i < caches.Length; i++)
        {
            caches[i] ??= [];
        }

        return caches;
    }

    private static Lazy<IReadOnlyList<ModelChoice>>[] CreateCaches(
        Func<ModelGame, IReadOnlyList<ModelChoice>> factory)
    {
        Lazy<IReadOnlyList<ModelChoice>>[] caches = new Lazy<IReadOnlyList<ModelChoice>>[4];
        foreach (ModelGame game in new[] { ModelGame.Wilds, ModelGame.Rise, ModelGame.World })
        {
            ModelGame captured = game;
            caches[(int)game] = new Lazy<IReadOnlyList<ModelChoice>>(() => factory(captured));
        }

        for (int i = 0; i < caches.Length; i++)
        {
            caches[i] ??= new Lazy<IReadOnlyList<ModelChoice>>(() => []);
        }

        return caches;
    }

    private static ReadOnlyCollection<ModelChoice> BuildWildsWeaponTargets(int weaponType)
    {
        string typeName = ModelTypes.GetTableName(weaponType);
        List<ModelChoice> targets = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (NameTableRow row in EmbeddedNameTables.LoadWeapons(ModelGame.Wilds))
        {
            if (!row.Type.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                || !TryParseCode(row.Id, out int first, out int second)
                || !seen.Add(row.Id))
            {
                continue;
            }

            DomainModelIdentity identity = new(ModelKind.Weapon, weaponType, first, second);
            targets.Add(new ModelChoice(row.Id, row.Name, IsWildsChain(identity), identity));
        }

        // 静态缓存会被多个实例/多次调用共享：只读包装防止调用方强转回 List 后污染缓存。
        return targets.AsReadOnly();
    }

    private static ReadOnlyCollection<ModelChoice> BuildRiseWeaponTargets(int weaponType)
    {
        string typeName = ModelTypes.GetTableName(weaponType);
        List<ModelChoice> targets = [];
        HashSet<int> seen = [];
        foreach (NameTableRow row in EmbeddedNameTables.LoadWeapons(ModelGame.Rise))
        {
            if (!row.Type.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(row.Id, out int id)
                || !seen.Add(id))
            {
                continue;
            }

            DomainModelIdentity identity = DomainModelIdentity.RiseWeapon(weaponType, id);
            targets.Add(new ModelChoice(identity.Code, row.Name, false, identity));
        }

        return targets.AsReadOnly();
    }

    private static ReadOnlyCollection<ModelChoice> BuildWorldWeaponTargets(int weaponType)
    {
        string typeName = ModelTypes.GetTableName(weaponType);
        List<ModelChoice> targets = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (NameTableRow row in EmbeddedNameTables.LoadWeapons(ModelGame.World))
        {
            if (!row.Type.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                || !TryParseWorldWeapon(row, weaponType, out DomainModelIdentity identity))
            {
                continue;
            }

            string code = identity.Code;
            if (!seen.Add(code))
            {
                continue;
            }

            targets.Add(new ModelChoice(code, row.Name, false, identity));
        }

        return targets.AsReadOnly();
    }

    private static IReadOnlyList<ModelChoice> BuildArmorTargets(ModelGame game)
    {
        List<ModelChoice> targets = [];
        foreach (NameTableRow row in EmbeddedNameTables.LoadArmors(game))
        {
            if (!TryParseArmor(game, row, out DomainModelIdentity identity))
            {
                continue;
            }

            targets.Add(new ModelChoice(identity.Code, row.Name, IsWildsChain(identity), identity));
        }

        // 静态缓存会被多个实例共享：以只读包装暴露，防止调用方强转回 List 后污染缓存。
        return targets.AsReadOnly();
    }

    private static IReadOnlyList<ModelChoice> BuildCatTargets(ModelGame game)
    {
        List<ModelChoice> targets = [];
        if (game != ModelGame.Wilds)
        {
            return targets.AsReadOnly();
        }

        foreach (NameTableRow row in EmbeddedNameTables.LoadCats(game))
        {
            if (!TryParseCode(row.Id, out int fid, out int sid))
            {
                continue;
            }

            DomainModelIdentity identity = DomainModelIdentity.Cat(fid, sid);
            targets.Add(new ModelChoice(row.Id, row.Name, IsWildsChain(identity), identity));
        }

        return targets.AsReadOnly();
    }

    /// <summary>按游戏解析护甲表行 → 身份（荒野/世界按 fid_sid，崛起按序号；荒野按行名区分男/女款）。</summary>
    private static bool TryParseArmor(ModelGame game, NameTableRow row, out DomainModelIdentity identity)
    {
        identity = DomainModelIdentity.Armor(2, 0, 0);
        string id = row.Id;
        switch (game)
        {
            case ModelGame.Rise:
                if (!id.StartsWith("pl", StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(id.AsSpan(2), out int riseId))
                {
                    return false;
                }

                identity = DomainModelIdentity.RiseArmor(riseId, female: false);
                return true;

            case ModelGame.World:
                if (!id.StartsWith("pl", StringComparison.OrdinalIgnoreCase)
                    || !TryParseCode(id[2..], out int worldFid, out int worldSid))
                {
                    return false;
                }

                identity = DomainModelIdentity.WorldArmor(2, worldFid, worldSid);
                return true;

            default:
                if (!TryParseCode(id, out int fid, out int sid))
                {
                    return false;
                }

                // 荒野名称表的行名带「男款/女款」标注：按标注生成对应性别身份；
                // 未标注性别的行（骨骼、饰品等）按男款生成。改写时最终性别仍由当前模型行决定。
                identity = DomainModelIdentity.Armor(InferArmorSex(row.Name), fid, sid);
                return true;
        }
    }

    /// <summary>从荒野名称表行名推断护甲性别：含「女款」按女款（3），其余按男款（2）。</summary>
    private static int InferArmorSex(string? name) =>
        name?.Contains("女款", StringComparison.Ordinal) == true ? 3 : 2;

    /// <summary>解析世界武器表行（代码 + op 列）→ 完整世界武器身份。</summary>
    private static bool TryParseWorldWeapon(
        NameTableRow row,
        int weaponType,
        out DomainModelIdentity identity)
    {
        identity = DomainModelIdentity.WorldWeapon(weaponType, 0);
        string code = row.Id.Trim();
        bool withBs = code.StartsWith("bs_", StringComparison.OrdinalIgnoreCase);
        string baseCode = withBs ? code[3..] : code;
        if (baseCode.Length < 4 || !int.TryParse(baseCode[^3..], out int fid))
        {
            return false;
        }

        int op = 0;
        if (row.OpCode is { Length: >= 3 } opCode
            && int.TryParse(opCode[^3..], out int parsedOp))
        {
            op = parsedOp;
        }

        identity = DomainModelIdentity.WorldWeapon(weaponType, fid, withBs, op);
        return true;
    }

    private static bool TryParseCode(string code, out int first, out int second)
    {
        first = 0;
        second = 0;
        int separator = code.IndexOf('_');
        return separator > 0
            && int.TryParse(code[..separator], out first)
            && int.TryParse(code[(separator + 1)..], out second);
    }

    private static bool IsWildsChain(DomainModelIdentity identity)
    {
        if (identity.Game != ModelGame.Wilds)
        {
            return false;
        }

        string key = identity.Kind switch
        {
            ModelKind.Armor => "A:" + identity.Code,
            ModelKind.CatEquip => "C:" + identity.Code,
            _ => "W:" + identity.Type + ":" + identity.Code,
        };
        return WildsChainKeys.Value.Contains(key);
    }

    /// <summary>扫描荒野内置清单，收集「哪些完整模型 ID 带 .chain 文件」。</summary>
    private static HashSet<string> BuildWildsChainKeys()
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in EmbeddedPakNameIndex.LoadWildsPaths())
        {
            if (!path.Contains(".chain", StringComparison.OrdinalIgnoreCase)
                || !DomainModelIdentity.TryParsePath(path, out DomainModelIdentity identity))
            {
                continue;
            }

            keys.Add(identity.Kind switch
            {
                ModelKind.Armor => "A:" + identity.Code,
                ModelKind.CatEquip => "C:" + identity.Code,
                _ => "W:" + identity.Type + ":" + identity.Code,
            });
        }

        return keys;
    }
}
