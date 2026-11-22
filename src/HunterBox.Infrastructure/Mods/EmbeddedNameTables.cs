using System.IO.Compression;
using System.Reflection;
using HunterBox.Domain.Models;

namespace HunterBox.Infrastructure.Mods;

/// <summary>一行名称表记录（类型、ID、显示名）。</summary>
/// <param name="Type">类型列（武器为 BOW/LONG_SWORD 等，护甲/猫装备为空）。</param>
/// <param name="Id">
/// 表内 ID：荒野武器 <c>00_0000</c>、荒野护甲 <c>003_000</c>、荒野猫装备 <c>003_0000</c>；
/// 崛起护甲 <c>pl001</c>、崛起武器 <c>001</c>；世界护甲 <c>pl001_0000</c>、世界武器 <c>bow001</c> / <c>bs_bow001</c>。
/// </param>
/// <param name="Name">显示名。</param>
public sealed record NameTableRow(string Type, string Id, string Name)
{
    /// <summary>世界武器的 op 部件代码（如 <c>op_bow005</c>）；其他行或非 op 行为 <see langword="null"/>。</summary>
    public string? OpCode { get; init; }
}

/// <summary>名称表种类。</summary>
public enum NameTableKind
{
    /// <summary>武器表。</summary>
    Weapon,

    /// <summary>护甲表。</summary>
    Armor,

    /// <summary>随从猫装备表（仅荒野）。</summary>
    Cat,
}

/// <summary>
/// 内置的怪物猎人名称表（武器 / 护甲 / 猫装备），用于把模型 ID 显示成可读名字。
/// </summary>
/// <remarks>
/// <para>
/// 数据来自内置资源（<c>mhws_*</c> / <c>mhr_*</c> / <c>mhw_*</c>），已 gzip 压缩后随程序集分发。
/// 无参重载默认荒野，保持旧调用不变；按 <see cref="ModelGame"/> 的重载返回对应作的表。
/// </para>
/// <para>
/// ID 与模型路径的对应关系见 <see cref="ModelNameResolver"/>：荒野护甲 <c>ch03/012/003</c> → <c>012_003</c>、
/// 武器 <c>it10/03/0000</c> → <c>03_0000</c>（类型 RodInsect）、猫装备 <c>ch051</c> → <c>003_0001</c>；
/// 崛起护甲 <c>pl060</c>、武器 <c>G_Swd001</c>；世界护甲 <c>pl060_0000</c>、武器 <c>bs_bow001</c>。
/// 表只读一次并缓存（编辑器逐路径查名时避免反复解压）。
/// </para>
/// </remarks>
public static class EmbeddedNameTables
{
    private const string Prefix = "HunterBox.Infrastructure.Mods.NameTables.";

    private static readonly Lazy<IReadOnlyList<NameTableRow>>[] WeaponTables =
        CreateTables(NameTableKind.Weapon);

    private static readonly Lazy<IReadOnlyList<NameTableRow>>[] ArmorTables =
        CreateTables(NameTableKind.Armor);

    private static readonly Lazy<IReadOnlyList<NameTableRow>>[] CatTables =
        CreateTables(NameTableKind.Cat);

    /// <summary>加载荒野武器名称表（兼容旧接口）。</summary>
    /// <returns>行集合；资源缺失时为空。</returns>
    internal static IReadOnlyList<NameTableRow> LoadWeapons() => LoadWeapons(ModelGame.Wilds);

    /// <summary>加载指定游戏的武器名称表。</summary>
    /// <param name="game">游戏。</param>
    /// <returns>行集合；资源缺失或该作没有武器表时为空。</returns>
    public static IReadOnlyList<NameTableRow> LoadWeapons(ModelGame game) => Load(WeaponTables, game);

    /// <summary>加载荒野护甲名称表（兼容旧接口）。</summary>
    /// <returns>行集合；资源缺失时为空。</returns>
    internal static IReadOnlyList<NameTableRow> LoadArmors() => LoadArmors(ModelGame.Wilds);

    /// <summary>加载指定游戏的护甲名称表。</summary>
    /// <param name="game">游戏。</param>
    /// <returns>行集合；资源缺失时为空。</returns>
    public static IReadOnlyList<NameTableRow> LoadArmors(ModelGame game) => Load(ArmorTables, game);

    /// <summary>加载荒野随从猫装备名称表（兼容旧接口）。</summary>
    /// <returns>行集合；资源缺失时为空。</returns>
    internal static IReadOnlyList<NameTableRow> LoadCats() => LoadCats(ModelGame.Wilds);

    /// <summary>加载指定游戏的随从猫装备名称表（只有荒野有猫装备表）。</summary>
    /// <param name="game">游戏。</param>
    /// <returns>行集合；其他游戏或资源缺失时为空。</returns>
    public static IReadOnlyList<NameTableRow> LoadCats(ModelGame game) => Load(CatTables, game);

    /// <summary>按表 ID 精确查名字。</summary>
    /// <param name="rows">名称表。</param>
    /// <param name="id">表 ID（大小写不敏感）。</param>
    /// <returns>找到的名字；没有则返回 <see langword="null"/>。</returns>
    public static string? FindName(IReadOnlyList<NameTableRow> rows, string id)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    /// <summary>解析名称表文本（<c>#</c> 开头为注释；荒野列格式）。</summary>
    /// <param name="content">CSV 文本。</param>
    /// <param name="withType">第一列是否为武器类型。</param>
    /// <returns>解析出的行。</returns>
    public static IReadOnlyList<NameTableRow> Parse(string? content, bool withType) =>
        ParseTable(content, ModelGame.Wilds, withType ? NameTableKind.Weapon : NameTableKind.Armor);

    /// <summary>按游戏的列格式解析名称表文本。</summary>
    /// <param name="content">CSV 文本。</param>
    /// <param name="game">游戏（列格式随作不同）。</param>
    /// <param name="kind">表种类。</param>
    /// <returns>解析出的行。</returns>
    public static IReadOnlyList<NameTableRow> ParseTable(
        string? content,
        ModelGame game,
        NameTableKind kind) => ParseCore(content, game, kind);

    private static Lazy<IReadOnlyList<NameTableRow>>[] CreateTables(NameTableKind kind)
    {
        Lazy<IReadOnlyList<NameTableRow>>[] tables = new Lazy<IReadOnlyList<NameTableRow>>[4];
        foreach (ModelGame game in new[] { ModelGame.Wilds, ModelGame.Rise, ModelGame.World })
        {
            ModelGame captured = game;
            tables[(int)game] = new Lazy<IReadOnlyList<NameTableRow>>(() => LoadCore(captured, kind));
        }

        for (int i = 0; i < tables.Length; i++)
        {
            tables[i] ??= new Lazy<IReadOnlyList<NameTableRow>>(() => []);
        }

        return tables;
    }

    private static IReadOnlyList<NameTableRow> Load(
        Lazy<IReadOnlyList<NameTableRow>>[] tables,
        ModelGame game)
    {
        int index = (int)game;
        return index >= 0 && index < tables.Length ? tables[index].Value : [];
    }

    private static List<NameTableRow> LoadCore(ModelGame game, NameTableKind kind)
    {
        string? fileName = FileName(game, kind);
        if (fileName is null)
        {
            return [];
        }

        using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + fileName);
        if (resource is null)
        {
            return [];
        }

        using GZipStream gzip = new(resource, CompressionMode.Decompress);
        using StreamReader reader = new(gzip);
        return ParseCore(reader.ReadToEnd(), game, kind);
    }

    private static string? FileName(ModelGame game, NameTableKind kind) => (game, kind) switch
    {
        (ModelGame.Wilds, NameTableKind.Weapon) => "mhws_weapon.csv.gz",
        (ModelGame.Wilds, NameTableKind.Armor) => "mhws_armor.csv.gz",
        (ModelGame.Wilds, NameTableKind.Cat) => "mhws_cat_armor.csv.gz",
        (ModelGame.Rise, NameTableKind.Weapon) => "mhr_weapon.csv.gz",
        (ModelGame.Rise, NameTableKind.Armor) => "mhr_armor.csv.gz",
        (ModelGame.World, NameTableKind.Weapon) => "mhw_weapon.csv.gz",
        (ModelGame.World, NameTableKind.Armor) => "mhw_armor.csv.gz",
        _ => null,
    };

    private static List<NameTableRow> ParseCore(
        string? content,
        ModelGame game,
        NameTableKind kind)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        List<NameTableRow> rows = [];
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            NameTableRow? row = game switch
            {
                ModelGame.Rise => kind == NameTableKind.Weapon ? ParseRiseWeapon(line) : ParseRiseArmor(line),
                ModelGame.World => kind == NameTableKind.Weapon ? ParseWorldWeapon(line) : ParseWorldArmor(line),
                _ => kind == NameTableKind.Weapon ? ParseWildsWeapon(line) : ParseNamedPair(line),
            };
            if (row is not null)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>荒野武器：<c>类型,ID,名字</c>（名字里可能还有逗号）。</summary>
    private static NameTableRow? ParseWildsWeapon(string line)
    {
        string[] parts = line.Split(',');
        return parts.Length >= 3
            ? new NameTableRow(parts[0].Trim(), parts[1].Trim(), string.Join(',', parts[2..]).Trim())
            : null;
    }

    /// <summary>荒野护甲/猫装备：<c>ID,名字</c>。</summary>
    private static NameTableRow? ParseNamedPair(string line)
    {
        string[] parts = line.Split(',');
        return parts.Length >= 2
            ? new NameTableRow(string.Empty, parts[0].Trim(), string.Join(',', parts[1..]).Trim())
            : null;
    }

    /// <summary>崛起武器：<c>类型,3 位 ID,中文名[,英文名…]</c>（只取中文名）。</summary>
    private static NameTableRow? ParseRiseWeapon(string line)
    {
        string[] parts = line.Split(',');
        return parts.Length >= 3
            ? new NameTableRow(parts[0].Trim(), parts[1].Trim(), parts[2].Trim())
            : null;
    }

    /// <summary>崛起护甲：<c>pl001,名字</c>。</summary>
    private static NameTableRow? ParseRiseArmor(string line)
    {
        string[] parts = line.Split(',');
        return parts.Length >= 2
            ? new NameTableRow(string.Empty, parts[0].Trim(), parts[1].Trim())
            : null;
    }

    /// <summary>世界武器：<c>类型,代码,名字[,op 代码]</c>。</summary>
    private static NameTableRow? ParseWorldWeapon(string line)
    {
        string[] parts = line.Split(',');
        if (parts.Length < 3)
        {
            return null;
        }

        string? op = parts.Length >= 4 && parts[3].Trim().Length > 0 ? parts[3].Trim() : null;
        return new NameTableRow(parts[0].Trim(), parts[1].Trim(), parts[2].Trim()) { OpCode = op };
    }

    /// <summary>世界护甲：<c>pl001_0000,名字</c>。</summary>
    private static NameTableRow? ParseWorldArmor(string line)
    {
        string[] parts = line.Split(',');
        return parts.Length >= 2
            ? new NameTableRow(string.Empty, parts[0].Trim(), parts[1].Trim())
            : null;
    }
}
