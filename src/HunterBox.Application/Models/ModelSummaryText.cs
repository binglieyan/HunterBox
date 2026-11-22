using System.Text;

namespace HunterBox.Application.Models;

/// <summary>
/// 「备注信息」列里的单行模型摘要。
/// </summary>
/// <param name="Name">名称表里的可读名字；未知模型为 <see langword="null"/>。</param>
/// <param name="Code">完整模型 ID（护甲/猫 <c>fid_sid</c>、武器 <c>AA_BBBB</c>）。</param>
/// <param name="HasPrefab">该行是否包含 prefab 文件（显示 <c>[pfb]</c>）。</param>
/// <param name="FromPak">该行是否来自 pak 条目（显示 <c>[pak]</c>）。</param>
public sealed record ModelSummaryRow(
    string? Name,
    string Code,
    bool HasPrefab = false,
    bool FromPak = false);

/// <summary>
/// 把模型检测结果拼成「备注信息」列文本。
/// </summary>
/// <remarks>
///的「备注列 = 被替换模型清单」语义：每行显示 <c>名字 (完整ID)</c>，
/// 含 prefab 时追加 <c>[pfb]</c>、来自 pak 时追加 <c>[pak]</c>，多行用「、」连接；
/// 同一个完整 ID 出现在多个 pak/散装路径时合并为一行（标记取并集）。
/// 没有识别到模型时回退到调用方提供的用户描述（<c>modinfo.ini</c>的 description / MOD信息对话框备注）。
/// </remarks>
public static class ModelSummaryText
{
    /// <summary>拼接模型摘要文本。</summary>
    /// <param name="rows">模型行（顺序即展示顺序；同一完整 ID的多行会合并到第一次出现的位置）。</param>
    /// <param name="fallback">没有模型行时返回的回退文本（通常是用户备注）；为空时返回空串。</param>
    /// <returns>形如 <c>公会骑士 (046_000) [pak]、机械军刀 (03_0017) [pfb]</c>的文本。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> 为 <see langword="null"/>。</exception>
    public static string Build(IEnumerable<ModelSummaryRow> rows, string? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        List<ModelSummaryRow> merged = [];
        Dictionary<string, int> positions = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModelSummaryRow row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Code))
            {
                continue;
            }

            if (positions.TryGetValue(row.Code, out int position))
            {
                ModelSummaryRow existing = merged[position];
                merged[position] = existing with
                {
                    Name = existing.Name ?? row.Name,
                    HasPrefab = existing.HasPrefab || row.HasPrefab,
                    FromPak = existing.FromPak || row.FromPak,
                };
                continue;
            }

            positions[row.Code] = merged.Count;
            merged.Add(row);
        }

        if (merged.Count == 0)
        {
            return fallback ?? string.Empty;
        }

        return string.Join("、", merged.Select(Describe));
    }

    /// <summary>单行文本：<c>名字 (完整ID) [pfb] [pak]</c>；名字未知时只显示完整 ID。</summary>
    private static string Describe(ModelSummaryRow row)
    {
        StringBuilder text = new();
        bool hasName = !string.IsNullOrWhiteSpace(row.Name)
            && !string.Equals(row.Name, row.Code, StringComparison.OrdinalIgnoreCase);
        if (hasName)
        {
            text.Append(row.Name).Append(" (").Append(row.Code).Append(')');
        }
        else
        {
            text.Append(row.Code);
        }

        if (row.HasPrefab)
        {
            text.Append(" [pfb]");
        }

        if (row.FromPak)
        {
            text.Append(" [pak]");
        }

        return text.ToString();
    }
}
