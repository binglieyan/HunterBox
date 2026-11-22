namespace HunterBox.Domain.Models;

/// <summary>一个可选的模型目标。</summary>
/// <param name="Code">完整模型 ID：用作显示与去重键，格式随游戏不同（荒野护甲/猫 <c>fid_sid</c>、
/// 荒野武器 <c>AA_BBBB</c>、崛起护甲 <c>pl###</c>、世界武器 <c>bow001</c> 等），<b>不是路径片段，不要拼进路径</b>。</param>
/// <param name="Name">显示名（来自名称表）。</param>
/// <param name="HasChain">该目标模型在游戏清单里是否有 <c>.chain</c> 文件（候选的 (chain) 标记）。</param>
/// <param name="Identity">完整身份（便于对话框直接拼出目标路径形式；护甲的最终性别由当前模型行决定）。</param>
public sealed record ModelChoice(
    string Code,
    string Name,
    bool HasChain = false,
    ModelIdentity? Identity = null)
{
    /// <summary>候选面板显示文本：名字 + 完整 ID（<c>名字 (完整ID)</c> 形式）。</summary>
    public string Display => HasChain ? $"{Name} ({Code}) (chain)" : $"{Name} ({Code})";
}

/// <summary>模型目标目录：供「MOD文件修改」的候选面板使用。</summary>
public interface IModelCatalog
{
    /// <summary>本目录对应的游戏（候选表按作区分）。</summary>
    ModelGame Game { get; }

    /// <summary>护甲目标：名称表每行一个完整模型（荒野按行名的「男款/女款」标注生成对应性别身份）。</summary>
    IReadOnlyList<ModelChoice> ArmorTargets { get; }

    /// <summary>某类武器的目标（完整 <c>AA_BBBB</c>，按完整 ID 去重）。</summary>
    /// <param name="weaponType">武器类型序号（0 大剑 … 14 猎虫）。</param>
    /// <returns>该类武器的目标列表。</returns>
    IReadOnlyList<ModelChoice> WeaponTargets(int weaponType);

    /// <summary>随从猫装备目标（只列同 sid的候选；<c>sid</c> 为 4 位子 ID）。</summary>
    /// <param name="secondId">当前猫装备的 sid；为 <see langword="null"/> 时列全部。</param>
    /// <returns>猫装备目标列表。</returns>
    IReadOnlyList<ModelChoice> CatTargets(int? secondId = null);
}
