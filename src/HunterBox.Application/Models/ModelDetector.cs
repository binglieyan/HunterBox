using System.Runtime.InteropServices;
using HunterBox.Domain.Models;

namespace HunterBox.Application.Models;

/// <summary>检测到的一个模型行（兼容模型行的粒度，不含名称表名字）。</summary>
/// <param name="Identity">完整模型身份（含游戏、护甲性别、猫装备、武器类型）。</param>
/// <param name="FileCount">属于该模型行的文件/条目数。</param>
/// <param name="PakFile">pak 型行对应的 pak 文件名；散装文件为 <see langword="null"/>。</param>
/// <param name="HasPrefab">是否包含 prefab 文件（行显示 <c>[pfb]</c>）。</param>
/// <param name="HasChain">是否包含 <c>.chain</c> 文件（行显示 <c>(chain)</c>）。</param>
/// <param name="SamplePath">第一条命中路径（用于查名字）。</param>
public sealed record DetectedModel(
    ModelIdentity Identity,
    int FileCount,
    string? PakFile,
    bool HasPrefab,
    bool HasChain,
    string SamplePath)
{
    /// <summary>该模型行所属的游戏。</summary>
    public ModelGame Game => Identity.Game;

    /// <summary>完整模型 ID（护甲/猫 <c>fid_sid</c>、武器 <c>AA_BBBB</c>；崛起/世界为各自代码）。</summary>
    public string RewriteId => Identity.Code;

    /// <summary>完整身份路径形式（改写服务输入）。</summary>
    public string IdentityForm => Identity.PathForm;

    /// <summary>界面显示的类型名（护甲带性别、武器带具体类型、猫装备）。</summary>
    public string TypeDisplayName => Identity.TypeDisplayName;
}

/// <summary>
/// 从「散装文件 + pak 条目路径」里检测模型行（去重语义）。
/// </summary>
/// <remarks>
/// 去重键 = 完整模型身份（含游戏）+ pak 文件名：
/// 护甲同 fid 不同 sid/性别、不同武器类型同 BBBB、多个 pak 里的同名模型都会各成一行。
/// 行触发按游戏规则：荒野由 <c>art/model</c> 与 <c>_prefab</c> 路径触发行；
/// 崛起由 <c>player/mod</c>、<c>weapon</c>、<c>player/prefab</c> 触发；
/// 世界只由 <c>nativepc/pl|wp</c> 且含 <c>/mod/</c> 的路径触发行。骨骼/贴图不单独出行。
/// </remarks>
public static class ModelDetector
{
    /// <summary>检测模型行（每条路径按前缀自动判断游戏）。</summary>
    /// <param name="entries">路径集合：散装文件 pak 名为 <see langword="null"/>，pak 条目带 pak 文件名。</param>
    /// <returns>模型行（按游戏/类别/类型/ID/pak 排序，结果稳定）。</returns>
    public static IReadOnlyList<DetectedModel> Detect(IEnumerable<(string Path, string? PakFile)> entries) =>
        DetectCore(entries, ModelGame.Unknown);

    /// <summary>按指定游戏检测模型行。</summary>
    /// <param name="entries">路径集合：散装文件 pak 名为 <see langword="null"/>，pak 条目带 pak 文件名。</param>
    /// <param name="game">所属游戏；<see cref="ModelGame.Unknown"/> 时逐条自动判断。</param>
    /// <returns>模型行（按游戏/类别/类型/ID/pak 排序，结果稳定）。</returns>
    public static IReadOnlyList<DetectedModel> Detect(
        IEnumerable<(string Path, string? PakFile)> entries,
        ModelGame game)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return DetectCore(entries, game);
    }

    /// <summary>按指定游戏检测模型行，并顺带收集全部可解析的模型身份（P-06：与检测共用一次遍历）。</summary>
    /// <param name="entries">路径集合：散装文件 pak 名为 <see langword="null"/>，pak 条目带 pak 文件名。</param>
    /// <param name="game">所属游戏；<see cref="ModelGame.Unknown"/> 时逐条自动判断。</param>
    /// <param name="identities">收集去重保序的完整模型身份（按首次出现顺序）。</param>
    /// <returns>模型行（按游戏/类别/类型/ID/pak 排序，结果稳定）。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<DetectedModel> Detect(
        IEnumerable<(string Path, string? PakFile)> entries,
        ModelGame game,
        List<ModelIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(identities);
        return DetectCore(entries, game, identities);
    }

    /// <summary>按游戏收集 MOD 的全部路径（世界 pak 不参与改写，不展开）。</summary>
    /// <param name="relativeFiles">MOD 的相对文件列表（正斜杠）。</param>
    /// <param name="readPakEntries">读取某个根层 pak 条目路径的回调（返回 null 表示读取失败）。</param>
    /// <param name="game">所属游戏；<see cref="ModelGame.Unknown"/> 时按旧行为展开 pak。</param>
    /// <returns>路径 + 来源 pak（散装为 null）。</returns>
    public static IReadOnlyList<(string Path, string? PakFile)> CollectEntries(
        IEnumerable<string> relativeFiles,
        Func<string, IEnumerable<string>?> readPakEntries,
        ModelGame game)
    {
        ArgumentNullException.ThrowIfNull(relativeFiles);
        ArgumentNullException.ThrowIfNull(readPakEntries);

        List<(string, string?)> entries = [];
        List<string> paks = [];
        foreach (string file in relativeFiles)
        {
            entries.Add((file, null));
            if (ModelPathRules.IsRootPak(file))
            {
                paks.Add(file);
            }
        }

        // 世界 pak 不在模型改写的支持范围，不读取条目
        if (game != ModelGame.Unknown && !ModelPathRules.SupportsPakRewrite(game))
        {
            return entries;
        }

        foreach (string pak in paks)
        {
            IEnumerable<string>? patched = readPakEntries(pak);
            if (patched is null)
            {
                continue;
            }

            entries.AddRange(patched.Select(path => (path, (string?)pak)));
        }

        return entries;
    }

    private static IReadOnlyList<DetectedModel> DetectCore(
        IEnumerable<(string Path, string? PakFile)> entries,
        ModelGame game,
        List<ModelIdentity>? identities = null)
    {
        Dictionary<(ModelIdentity Identity, string Pak), DetectedModel> rows = [];
        Dictionary<(int WeaponType, int OpId), List<string>> worldOpParts = [];
        HashSet<ModelIdentity>? identitySeen = identities is null ? null : [];
        foreach ((string path, string? pakFile) in entries)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            // 每条路径只规范化一次：op 部件、身份解析、行触发判断都复用（P-26）。
            string normalized = ModelPathRules.NormalizePath(path);

            // 世界武器的 op 部件没有独立身份：先收集，稍后按基础 bs 武器扩展成候选行
            if (ModelPathRules.TryParseWorldWeaponOpPartNormalized(normalized, out int opType, out int opId))
            {
                (int, int) opKey = (opType, opId);
                if (!worldOpParts.TryGetValue(opKey, out List<string>? opPaths))
                {
                    opPaths = [];
                    worldOpParts[opKey] = opPaths;
                }

                opPaths.Add(path);
            }

            bool parsed = game == ModelGame.Unknown
                ? ModelIdentity.TryParsePathNormalized(normalized, out ModelIdentity identity)
                : ModelIdentity.TryParsePathNormalized(normalized, game, out identity);
            if (!parsed)
            {
                continue;
            }

            if (identities is not null && identitySeen!.Add(identity))
            {
                identities.Add(identity);
            }

            ModelGame pathGame = identity.Game;
            if (!ModelPathRules.IsRowTriggerPathNormalized(normalized, pathGame))
            {
                continue;
            }

            string pakKey = (pakFile ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            bool hasPrefab = pathGame switch
            {
                ModelGame.Wilds => normalized.Contains("_prefab", StringComparison.OrdinalIgnoreCase),
                ModelGame.Rise => normalized.Contains("player/prefab/", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            bool hasChain = normalized.Contains(".chain", StringComparison.OrdinalIgnoreCase);
            // P-12a：用引用查找一次完成「命中则累加 / 未命中则新增」，不再 TryGetValue 后再索引赋值查两次。
            ref DetectedModel? row = ref CollectionsMarshal.GetValueRefOrAddDefault(
                rows,
                (identity, pakKey),
                out bool exists);
            if (exists)
            {
                row = row! with
                {
                    FileCount = row.FileCount + 1,
                    HasPrefab = row.HasPrefab || hasPrefab,
                    HasChain = row.HasChain || hasChain,
                };
            }
            else
            {
                row = new DetectedModel(
                    identity,
                    1,
                    pakKey.Length == 0 ? null : pakFile,
                    hasPrefab,
                    hasChain,
                    path);
            }
        }

        ExpandWorldOpRows(rows, worldOpParts);

        return
        [
            .. rows.Values
                .OrderBy(r => (int)r.Game)
                .ThenBy(r => r.Identity.Kind)
                .ThenBy(r => r.Identity.Type)
                .ThenBy(r => r.Identity.FirstId)
                .ThenBy(r => r.Identity.SecondId)
                .ThenBy(r => r.PakFile ?? string.Empty, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// 世界武器的 op 部件扩展：带 <c>bs_</c> 的基础武器 + MOD 内的 <c>parts/op_*</c> 文件，
    /// 生成对应叠层模型行（与游戏内 bs/op 组合后的完整 ID 一致），便于单独改写叠层。
    /// </summary>
    private static void ExpandWorldOpRows(
        Dictionary<(ModelIdentity Identity, string Pak), DetectedModel> rows,
        Dictionary<(int WeaponType, int OpId), List<string>> opParts)
    {
        if (opParts.Count == 0)
        {
            return;
        }

        List<DetectedModel> bases =
        [
            .. rows.Values.Where(row => row.Game == ModelGame.World
                && row.Identity.IsWeapon
                && row.Identity.SecondId % 10 > 0),
        ];

        // P-10：预建身份索引，避免在「基础行 × op 组」双重循环里对已有行做线性查找。
        HashSet<ModelIdentity> identities = [.. rows.Keys.Select(static key => key.Identity)];
        foreach (DetectedModel baseRow in bases)
        {
            foreach (((int weaponType, int opId), List<string> paths) in opParts)
            {
                if (weaponType != baseRow.Identity.Type)
                {
                    continue;
                }

                ModelIdentity identity = ModelIdentity.WorldWeapon(
                    weaponType,
                    baseRow.Identity.FirstId,
                    withBs: true,
                    opId);
                if (!identities.Add(identity))
                {
                    continue;
                }

                rows[(identity, string.Empty)] = new DetectedModel(
                    identity,
                    paths.Count,
                    null,
                    false,
                    false,
                    paths[0]);
            }
        }
    }
}
