namespace HunterBox.Domain.Models;

/// <summary>模型的身份类别。</summary>
public enum ModelKind
{
    /// <summary>玩家护甲（荒野 <c>art/model/character/ch02|ch03</c>、世界 <c>pl/*_equip</c>、崛起 <c>player/mod</c>）。</summary>
    Armor,

    /// <summary>随从猫装备（仅荒野 <c>art/model/character/ch05</c>）。</summary>
    CatEquip,

    /// <summary>武器。</summary>
    Weapon,
}

/// <summary>模型类型编号（与游戏内类型顺序一致）。</summary>
/// <remarks>
/// 0 大剑 … 13 轻弩、14 猎虫（RodInsect）；15 男角色、16 女角色、17 随从猫仅作名称表类型用。
/// 荒野路径里猎虫复用操虫棍的 <c>it10</c>/<c>wp10</c>（游戏内类型编号），崛起/世界则是独立目录。
/// </remarks>
public static class ModelTypes
{
    /// <summary>大剑。</summary>
    public const int LongSword = 0;

    /// <summary>单手剑。</summary>
    public const int ShortSword = 1;

    /// <summary>双剑。</summary>
    public const int TwinSword = 2;

    /// <summary>太刀。</summary>
    public const int Tachi = 3;

    /// <summary>大锤。</summary>
    public const int Hammer = 4;

    /// <summary>长枪。</summary>
    public const int Lance = 6;

    /// <summary>铳枪。</summary>
    public const int GunLance = 7;

    /// <summary>斩击斧。</summary>
    public const int SlashAxe = 8;

    /// <summary>盾斧。</summary>
    public const int ChargeAxe = 9;

    /// <summary>操虫棍。</summary>
    public const int Rod = 10;

    /// <summary>弓。</summary>
    public const int Bow = 11;

    /// <summary>重弩。</summary>
    public const int HeavyBowgun = 12;

    /// <summary>轻弩。</summary>
    public const int LightBowgun = 13;

    /// <summary>猎虫（RodInsect）。</summary>
    public const int RodInsect = 14;

    /// <summary>男角色护甲类型。</summary>
    public const int ManEquip = 15;

    /// <summary>女角色护甲类型。</summary>
    public const int FemanEquip = 16;

    /// <summary>随从猫装备类型。</summary>
    public const int CatEquip = 17;

    /// <summary>名称表类型列（与 CSV 里的写法一致）。</summary>
    private static readonly string[] TableNames =
    [
        "LONG_SWORD", "SHORT_SWORD", "TWIN_SWORD", "TACHI", "HAMMER", "WHISTLE",
        "LANCE", "GUN_LANCE", "SLASH_AXE", "CHARGE_AXE", "ROD", "BOW",
        "HEAVY_BOWGUN", "LIGHT_BOWGUN", "RodInsect", "ManEquip", "FemanEquip", "CatEquip",
    ];

    /// <summary>界面显示用的类型名（游戏内类型名顺序）。</summary>
    private static readonly string[] DisplayNames =
    [
        "大剑", "单手剑", "双剑", "太刀", "大锤", "狩猎笛", "长枪", "铳枪", "斩击斧", "盾斧",
        "操虫棍", "弓", "重弩", "轻弩", "猎虫", "男角色", "女角色", "随从猫",
    ];

    /// <summary>取名称表类型列（如 <c>BOW</c>、<c>RodInsect</c>）。</summary>
    /// <param name="type">类型编号。</param>
    /// <returns>类型列文本；越界时返回空串。</returns>
    public static string GetTableName(int type) =>
        type >= 0 && type < TableNames.Length ? TableNames[type] : string.Empty;

    /// <summary>取界面显示类型名（如「弓」「护甲」）。</summary>
    /// <param name="type">类型编号。</param>
    /// <returns>中文类型名；越界时返回 <c>未知</c>。</returns>
    public static string GetDisplayName(int type) =>
        type >= 0 && type < DisplayNames.Length ? DisplayNames[type] : "未知";

    /// <summary>是否武器类型（0–14）。</summary>
    /// <param name="type">类型编号。</param>
    /// <returns>是武器类型时返回 <see langword="true"/>。</returns>
    public static bool IsWeapon(int type) => type is >= 0 and <= RodInsect;

    /// <summary>是否护甲性别类型（2 = 男款、3 = 女款）。</summary>
    /// <param name="type">类型编号。</param>
    /// <returns>是护甲性别类型时返回 <see langword="true"/>。</returns>
    public static bool IsArmorSex(int type) => type is 2 or 3;

    /// <summary>路径里使用的 <c>itTT</c>/<c>wpTT</c> 类型编号（荒野猎虫复用操虫棍 10）。</summary>
    /// <param name="type">类型编号。</param>
    /// <returns>TT。</returns>
    public static int PathType(int type) => type == RodInsect ? Rod : type;
}

/// <summary>
/// 一个完整的模型身份：类别 + 类型 + 两段 ID，并带上所属游戏。
/// </summary>
/// <remarks>
/// <para>
/// 这是「MOD文件修改」的输入/输出单位：改写按完整模型身份整体替换路径里的身份片段，
/// 而不是只换一个数字段。
/// </para>
/// <para>
/// 两段 ID 的含义按游戏区分：荒野护甲/猫 <c>fid/sid</c>、荒野武器 <c>AA/BBBB</c>；
/// 崛起护甲 <c>id/0</c>、崛起武器 <c>id/0</c>；世界护甲 <c>fid/sid</c>、
/// 世界武器 <c>fid/fullId</c>（<c>fullId = fid*10000 + op*10 + bs</c>）。
/// </para>
/// <para>
/// 旧的 4 参数构造函数默认是荒野身份，保持既有调用/测试不变；其他游戏用
/// <see cref="RiseArmor"/> / <see cref="RiseWeapon"/> / <see cref="WorldArmor"/> /
/// <see cref="WorldWeapon(int, int)"/> 等工厂创建。
/// </para>
/// </remarks>
/// <param name="Kind">身份类别。</param>
/// <param name="Type">荒野护甲为 <c>ch02</c>=2 / <c>ch03</c>=3，荒野猫装备为 5，武器为类型编号（14 = 猎虫）。</param>
/// <param name="FirstId">护甲/猫的 fid、武器的 AA（荒野）或 f_id（崛起/世界）。</param>
/// <param name="SecondId">护甲/猫的 sid、武器的 BBBB（荒野）；崛起武器为 0；世界武器为完整 fullId。</param>
public sealed partial record ModelIdentity(ModelKind Kind, int Type, int FirstId, int SecondId)
{
    /// <summary>该身份所属的游戏；旧调用不指定时为荒野。</summary>
    public ModelGame Game { get; init; } = ModelGame.Wilds;

    /// <summary>是否武器身份。</summary>
    public bool IsWeapon => Kind == ModelKind.Weapon;

    /// <summary>是否随从猫装备身份。</summary>
    public bool IsCatEquip => Kind == ModelKind.CatEquip;

    /// <summary>路径里使用的类型编号（荒野武器把猎虫归一到操虫棍；其余游戏即 <see cref="Type"/>）。</summary>
    public int PathType =>
        Game == ModelGame.Wilds && IsWeapon ? ModelTypes.PathType(Type) : Type;

    /// <summary>完整模型 ID（按游戏给出可显示的稳定 ID）。</summary>
    public string Code => ModelPathRules.FormatCode(this);

    /// <summary>完整身份路径形式（改写服务/对话框提交用）。</summary>
    public string PathForm => ModelPathRules.FormatPathForm(this);

    /// <summary>界面显示用的类别名（护甲会带性别）。</summary>
    public string TypeDisplayName => Kind switch
    {
        ModelKind.Armor => Type == 3 ? "护甲(女款)" : "护甲(男款)",
        ModelKind.CatEquip => "猫装备",
        _ => ModelTypes.GetDisplayName(Type),
    };

    /// <summary>创建荒野护甲身份。</summary>
    /// <param name="sex">2 = <c>ch02</c>（男）、3 = <c>ch03</c>（女）。</param>
    /// <param name="fid">护甲系列 ID（3 位）。</param>
    /// <param name="sid">护甲子 ID（3 位）。</param>
    /// <returns>护甲身份。</returns>
    /// <exception cref="ArgumentOutOfRangeException">性别不是 2/3。</exception>
    public static ModelIdentity Armor(int sex, int fid, int sid) =>
        Armor(ModelGame.Wilds, sex, fid, sid);

    /// <summary>按游戏创建护甲身份。</summary>
    /// <param name="game">所属游戏。</param>
    /// <param name="sex">荒野为 2/3（男/女），世界与崛起同样用 2/3 表示男女款。</param>
    /// <param name="fid">护甲系列 ID（荒野/世界为 3 位 fid，崛起为 3 位序号）。</param>
    /// <param name="sid">护甲子 ID（荒野为 3 位；世界为 4 位；崛起为 0）。</param>
    /// <returns>护甲身份。</returns>
    /// <exception cref="ArgumentOutOfRangeException">性别不是 2/3。</exception>
    public static ModelIdentity Armor(ModelGame game, int sex, int fid, int sid) =>
        sex is 2 or 3
            ? new ModelIdentity(ModelKind.Armor, sex, fid, sid) { Game = game }
            : throw new ArgumentOutOfRangeException(nameof(sex), "护甲性别必须是 2(男款) 或 3(女款)");

    /// <summary>创建荒野随从猫装备身份。</summary>
    /// <param name="fid">猫装备系列 ID（3 位）。</param>
    /// <param name="sid">猫装备子 ID（4 位）。</param>
    /// <returns>猫装备身份。</returns>
    public static ModelIdentity Cat(int fid, int sid) =>
        new(ModelKind.CatEquip, 5, fid, sid) { Game = ModelGame.Wilds };

    /// <summary>创建荒野武器身份（自动把 <c>it10/03</c> 归一为猎虫类型 14）。</summary>
    /// <param name="pathType"><c>itTT</c> 的类型编号（0–13）。</param>
    /// <param name="first">AA（2 位）。</param>
    /// <param name="second">BBBB（4 位）。</param>
    /// <returns>武器身份。</returns>
    /// <exception cref="ArgumentOutOfRangeException">类型编号越界。</exception>
    public static ModelIdentity Weapon(int pathType, int first, int second) =>
        pathType is >= 0 and <= ModelTypes.LightBowgun
            ? new ModelIdentity(
                ModelKind.Weapon,
                pathType == ModelTypes.Rod && first == 3 ? ModelTypes.RodInsect : pathType,
                first,
                second)
            {
                Game = ModelGame.Wilds,
            }
            : throw new ArgumentOutOfRangeException(nameof(pathType), "武器类型编号必须在 0–13 之间");

    /// <summary>按游戏创建武器身份（崛起 <paramref name="second"/> 传 0；世界传完整 fullId）。</summary>
    /// <param name="game">所属游戏。</param>
    /// <param name="type">武器类型编号（0 大剑 … 14 猎虫）。</param>
    /// <param name="first">崛起为 3 位序号；世界为 3 位 f_id；荒野为 AA。</param>
    /// <param name="second">荒野为 BBBB；崛起为 0；世界为 <c>fid*10000 + op*10 + bs</c>。</param>
    /// <returns>武器身份。</returns>
    /// <exception cref="ArgumentOutOfRangeException">类型编号越界。</exception>
    public static ModelIdentity Weapon(ModelGame game, int type, int first, int second) =>
        ModelTypes.IsWeapon(type)
            ? new ModelIdentity(ModelKind.Weapon, type, first, second) { Game = game }
            : throw new ArgumentOutOfRangeException(nameof(type), "武器类型编号必须在 0–14 之间");

    /// <summary>创建崛起护甲身份。</summary>
    /// <param name="id">护甲序号（<c>pl001</c> 里的 1）。</param>
    /// <param name="female">是否为女款（<c>natives/stm/player/mod/f</c>）。</param>
    /// <returns>护甲身份。</returns>
    public static ModelIdentity RiseArmor(int id, bool female) =>
        Armor(ModelGame.Rise, female ? 3 : 2, id, 0);

    /// <summary>创建崛起武器身份。</summary>
    /// <param name="weaponType">武器类型编号（0 大剑 … 14 猎虫）。</param>
    /// <param name="id">武器序号（模型目录名末尾 3 位）。</param>
    /// <returns>武器身份。</returns>
    /// <exception cref="ArgumentOutOfRangeException">类型编号越界。</exception>
    public static ModelIdentity RiseWeapon(int weaponType, int id) =>
        Weapon(ModelGame.Rise, weaponType, id, 0);

    /// <summary>创建世界护甲身份。</summary>
    /// <param name="sex">2 = 男款（<c>m_equip</c>）、3 = 女款（<c>f_equip</c>）。</param>
    /// <param name="fid">护甲系列 ID（<c>pl{fid:000}_{sid:0000}</c> 的 fid）。</param>
    /// <param name="sid">护甲子 ID（4 位）。</param>
    /// <returns>护甲身份。</returns>
    /// <exception cref="ArgumentOutOfRangeException">性别不是 2/3。</exception>
    public static ModelIdentity WorldArmor(int sex, int fid, int sid) =>
        Armor(ModelGame.World, sex, fid, sid);

    /// <summary>创建世界武器身份（完整 fullId 形式）。</summary>
    /// <param name="weaponType">武器类型编号（0 大剑 … 14 猎虫）。</param>
    /// <param name="fullId">游戏内完整 ID：<c>fid*10000 + op*10 + bs</c>。</param>
    /// <returns>武器身份。</returns>
    /// <exception cref="ArgumentOutOfRangeException">类型编号越界。</exception>
    public static ModelIdentity WorldWeapon(int weaponType, int fullId) =>
        Weapon(ModelGame.World, weaponType, fullId / 10000, fullId);

    /// <summary>创建世界武器身份（拆分形式）。</summary>
    /// <param name="weaponType">武器类型编号。</param>
    /// <param name="fid">武器系列 ID（3 位，完整 ID 的高位）。</param>
    /// <param name="withBs">是否带 <c>bs_</c> 叠层（完整 ID 个位为 1）。</param>
    /// <param name="opId">部件/叠层 ID（0–999）；0 表示基础武器。</param>
    /// <returns>武器身份。</returns>
    /// <exception cref="ArgumentOutOfRangeException">类型编号越界。</exception>
    public static ModelIdentity WorldWeapon(int weaponType, int fid, bool withBs, int opId) =>
        WorldWeapon(weaponType, (fid * 10000) + (opId * 10) + (withBs ? 1 : 0));

    /// <summary>解析「完整身份路径形式」（荒野 <c>ch03/012/003</c>、崛起 <c>rise:pl/f/060</c>、世界 <c>world:wp/bow001</c> 等）。</summary>
    /// <param name="text">身份文本。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>解析成功返回 <see langword="true"/>。</returns>
    public static bool TryParse(string? text, out ModelIdentity identity) =>
        ModelPathRules.TryParseForm(text, out identity);

    /// <summary>解析真实游戏路径里的模型身份（按路径前缀自动判断所属游戏）。</summary>
    /// <param name="path">相对路径（<c>/</c> 或 <c>\</c> 分隔）。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParsePath(string? path, out ModelIdentity identity) =>
        ModelPathRules.TryParsePath(path, out identity);

    /// <summary>按指定游戏解析真实游戏路径里的模型身份。</summary>
    /// <param name="path">相对路径（<c>/</c> 或 <c>\</c> 分隔）。</param>
    /// <param name="game">所属游戏。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParsePath(string? path, ModelGame game, out ModelIdentity identity) =>
        ModelPathRules.TryParsePath(path, game, out identity);

    /// <summary>解析已规范化路径（正斜杠）里的模型身份，按路径前缀自动判断游戏（P-26）。</summary>
    /// <param name="normalizedPath">已用 <see cref="ModelPathRules.NormalizePath"/> 规范化的相对路径。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParsePathNormalized(string? normalizedPath, out ModelIdentity identity) =>
        ModelPathRules.TryParsePathNormalized(normalizedPath, out identity);

    /// <summary>按指定游戏解析已规范化路径里的模型身份（P-26）。</summary>
    /// <param name="normalizedPath">已用 <see cref="ModelPathRules.NormalizePath"/> 规范化的相对路径。</param>
    /// <param name="game">所属游戏。</param>
    /// <param name="identity">解析结果。</param>
    /// <returns>识别成功返回 <see langword="true"/>。</returns>
    public static bool TryParsePathNormalized(
        string? normalizedPath,
        ModelGame game,
        out ModelIdentity identity) =>
        ModelPathRules.TryParsePathNormalized(normalizedPath, game, out identity);
}
