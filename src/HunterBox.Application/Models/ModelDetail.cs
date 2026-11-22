using System.Text;
using HunterBox.Domain.Models;

namespace HunterBox.Application.Models;

/// <summary>检测到的一个模型行（供模型编辑器使用，模型显示名）。</summary>
/// <param name="RewriteId">完整模型 ID（护甲/猫 <c>fid_sid</c>、武器 <c>AA_BBBB</c>）。</param>
/// <param name="DisplayName">显示名（名称表查不到时退化为「类型 + 完整 ID」）。</param>
/// <param name="IsWeapon">是否武器模型。</param>
/// <param name="Type">护甲性别（2/3）、猫装备 5、武器类型编号。</param>
/// <param name="HasChain">该模型是否带 <c>.chain</c> 文件（当前模型被强制加回候选时显示 (chain)）。</param>
/// <param name="Kind">模型类别。</param>
/// <param name="FirstId">护甲/猫 fid、武器 AA。</param>
/// <param name="SecondId">护甲/猫 sid、武器 BBBB。</param>
/// <param name="FileCount">属于该行的文件/条目数。</param>
/// <param name="PakFile">pak 型行的 pak 文件名；散装为 <see langword="null"/>。</param>
/// <param name="HasPrefab">该行是否含 <c>_prefab</c> 文件（显示 <c>[pfb]</c>）。</param>
/// <param name="Game">该模型所属游戏（候选表/身份路径形式按作区分）。</param>
public sealed record ModelDetail(
    string RewriteId,
    string DisplayName,
    bool IsWeapon,
    int Type,
    bool HasChain = false,
    ModelKind Kind = ModelKind.Armor,
    int FirstId = 0,
    int SecondId = 0,
    int FileCount = 0,
    string? PakFile = null,
    bool HasPrefab = false,
    ModelGame Game = ModelGame.Wilds)
{
    /// <summary>完整模型身份（含所属游戏、武器类型/护甲性别）。</summary>
    public ModelIdentity Identity => new(Kind, Type, FirstId, SecondId) { Game = Game };

    /// <summary>改写服务使用的完整身份路径形式（如 <c>ch03/012/003</c>、<c>rise:pl/f/060</c>）。</summary>
    public string IdentityForm => Identity.PathForm;

    /// <summary>模型类型显示名（护甲(男款)/护甲(女款)/猫装备/大剑…）。</summary>
    public string TypeDisplayName => Identity.TypeDisplayName;

    /// <summary>行显示文本：<c>(文件数)模型类型|模型名[pfb][pak]</c>。</summary>
    public string Display
    {
        get
        {
            StringBuilder text = new();
            text.Append('(').Append(FileCount).Append(')')
                .Append(TypeDisplayName).Append('|')
                .Append(DisplayName);
            if (HasPrefab)
            {
                text.Append("[pfb]");
            }

            if (PakFile is not null)
            {
                text.Append("[pak]");
            }

            return text.ToString();
        }
    }

    /// <summary>从检测结果构建行（名称由名称表解析结果传入）。</summary>
    /// <param name="detected">检测到的模型。</param>
    /// <param name="name">名称表里的名字；查不到时退化为「类型 + 完整 ID」。</param>
    /// <returns>编辑器行。</returns>
    public static ModelDetail FromDetected(DetectedModel detected, string? name)
    {
        ArgumentNullException.ThrowIfNull(detected);
        ModelIdentity identity = detected.Identity;
        return new ModelDetail(
            identity.Code,
            name ?? $"{identity.TypeDisplayName} {identity.Code}",
            identity.IsWeapon,
            identity.Type,
            detected.HasChain,
            identity.Kind,
            identity.FirstId,
            identity.SecondId,
            detected.FileCount,
            detected.PakFile,
            detected.HasPrefab,
            identity.Game);
    }
}
