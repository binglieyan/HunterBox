using System.Text.RegularExpressions;

namespace HunterBox.Domain.Models;

/// <summary>从 MOD 的文件路径里识别「模型替换」类 MOD（三作按各自路径规则）。</summary>
/// <remarks>
/// 安装后模型识别流程里用游戏数据表把文件路径映射成「装备 / 怪物名字」，
/// 再把分类设为「模型替换」、备注写成模型列表。这里做路径层面的识别：
/// 护甲/猫/武器 + prefab 都能解析成完整 <see cref="ModelIdentity"/>，
/// 通过 <see cref="ModelGame"/> 区分荒野/崛起/世界。
/// </remarks>
public static partial class ModelPathRecognizer
{
    /// <summary>识别出的模型 ID（荒野形如 <c>ch03/012</c> 或 <c>it10/03/0000</c>）。</summary>
    /// <param name="resolvedPaths">已还原成完整路径的 pak 条目列表。</param>
    /// <returns>去重后的模型 ID，按首次出现顺序。</returns>
    /// <remarks>
    /// 兼容旧接口：只覆盖荒野 <c>art/model</c> 规则；三作请用带 <see cref="ModelGame"/> 的重载。
    /// 契约保留：生产识别走 <see cref="FindIdentities(IEnumerable{string})"/>，本组 <c>FindModelIds</c>
    /// 仅用于测试/诊断。
    /// </remarks>
    public static IReadOnlyList<string> FindModelIds(IEnumerable<string> resolvedPaths)
    {
        ArgumentNullException.ThrowIfNull(resolvedPaths);

        List<string> ids = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string path in resolvedPaths)
        {
            string? id = null;

            // 猫装备 ch05/{fid:3}/{sid:4}：sid 是 4 位，必须拼完整才区分得开
            Match cat = CatModel().Match(path);
            if (cat.Success)
            {
                id = "ch05/" + cat.Groups["id"].Value + "/" + cat.Groups["sid"].Value;
            }
            else
            {
                Match character = CharacterModel().Match(path);
                if (character.Success)
                {
                    // 旧格式保持 chXX/FFF（编辑器内部的完整身份由 ModelIdentity.TryParsePath 负责）
                    id = character.Groups["group"].Value + "/" + character.Groups["id"].Value;
                }
                else
                {
                    Match item = ItemModel().Match(path);
                    if (item.Success)
                    {
                        id = item.Groups["group"].Value + "/" + item.Groups["series"].Value + "/" + item.Groups["id"].Value;
                    }
                }
            }

            if (id is not null)
            {
                id = id.ToLowerInvariant();
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>按指定游戏识别出的模型 ID（护甲/武器完整 ID，去重保序）。</summary>
    /// <param name="resolvedPaths">已还原成完整路径的 pak 条目列表。</param>
    /// <param name="game">所属游戏。</param>
    /// <returns>去重后的模型 ID。</returns>
    /// <remarks>契约保留：生产识别走 <see cref="FindIdentities(IEnumerable{string})"/>，本重载仅用于测试/诊断。</remarks>
    public static IReadOnlyList<string> FindModelIds(IEnumerable<string> resolvedPaths, ModelGame game)
    {
        ArgumentNullException.ThrowIfNull(resolvedPaths);
        if (game == ModelGame.Wilds)
        {
            return FindModelIds(resolvedPaths);
        }

        List<string> ids = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in resolvedPaths)
        {
            if (ModelIdentity.TryParsePath(path, game, out ModelIdentity identity)
                && seen.Add(identity.Code))
            {
                ids.Add(identity.Code);
            }
        }

        return ids;
    }

    /// <summary>识别出的完整模型身份（含游戏/性别/武器类型，护甲与 prefab 会合并成同一身份）。</summary>
    /// <param name="resolvedPaths">已还原成完整路径的 pak 条目列表。</param>
    /// <returns>去重后的完整身份，按首次出现顺序。</returns>
    public static IReadOnlyList<ModelIdentity> FindIdentities(IEnumerable<string> resolvedPaths)
    {
        ArgumentNullException.ThrowIfNull(resolvedPaths);

        List<ModelIdentity> identities = [];
        HashSet<ModelIdentity> seen = [];
        foreach (string path in resolvedPaths)
        {
            if (ModelIdentity.TryParsePath(path, out ModelIdentity identity) && seen.Add(identity))
            {
                identities.Add(identity);
            }
        }

        return identities;
    }

    /// <summary>按指定游戏识别出的完整模型身份（去重保序）。</summary>
    /// <param name="resolvedPaths">已还原成完整路径的 pak 条目列表。</param>
    /// <param name="game">所属游戏。</param>
    /// <returns>去重后的完整身份。</returns>
    /// <remarks>契约保留：生产使用不带游戏的重载（按路径前缀自动判断游戏），本重载仅用于测试/诊断。</remarks>
    public static IReadOnlyList<ModelIdentity> FindIdentities(
        IEnumerable<string> resolvedPaths,
        ModelGame game)
    {
        ArgumentNullException.ThrowIfNull(resolvedPaths);

        List<ModelIdentity> identities = [];
        HashSet<ModelIdentity> seen = [];
        foreach (string path in resolvedPaths)
        {
            if (ModelIdentity.TryParsePath(path, game, out ModelIdentity identity) && seen.Add(identity))
            {
                identities.Add(identity);
            }
        }

        return identities;
    }

    [GeneratedRegex(
        @"art[/\\]model[/\\]character[/\\]ch05[/\\](?<id>\d{3})[/\\](?<sid>\d{4})[/\\]",
        RegexOptions.IgnoreCase)]
    private static partial Regex CatModel();

    [GeneratedRegex(@"art[/\\]model[/\\]character[/\\](?<group>ch\d{2})[/\\](?<id>\d{3})[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterModel();

    [GeneratedRegex(@"art[/\\]model[/\\]item[/\\](?<group>[a-z]{2}\d{2})[/\\](?<series>\d{2})[/\\](?<id>\d{4})[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex ItemModel();
}
