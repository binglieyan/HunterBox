using System.Globalization;
using HunterBox.Application.Models;
using HunterBox.Domain.Mods;

namespace HunterBox.App;

/// <summary>
/// MOD 列表的纯状态/投影：行集合、模型文本与模型身份缓存、筛选和行签名（选中 ID 由 MainWindowViewModel 维护）。
/// </summary>
/// <remarks>
/// 从 MainWindow 抽出的第二块职责；不直接操作 ListView/TreeView 等控件，只维护数据和纯计算，
/// 由窗口负责把 <see cref="Rows"/> 绑到控件、恢复选中并触发按钮刷新。
/// </remarks>
internal sealed class ModListView
{
    /// <summary>当前游戏全部行（重新加载时整体替换）。</summary>
    public List<ModRow> AllRows { get; } = [];

    /// <summary>当前筛选结果，绑定到 ListView。</summary>
    public BulkObservableCollection<ModRow> Rows { get; } = [];

    /// <summary>列表重建期间抑制选中变化回调。</summary>
    public bool Rebuilding { get; set; }

    /// <summary>重建全部行；记录引用与显示内容都未变的行直接复用（P-05）。</summary>
    /// <param name="mods">当前游戏的 MOD 记录（按列表顺序）。</param>
    /// <param name="resolve">
    /// MOD + 可复用的旧行 →（备注文本, 文本签名）。旧行非空表示记录引用与显示字段都没变，
    /// 应优先复用其缓存签名；传入 <see langword="null"/> 表示没有可复用行，需按当前记录重新计算。
    /// 文本签名随结果一起返回并写回新行，构造后不再重复计算。
    /// </param>
    /// <param name="aliveIds">
    /// 输出本次重建覆盖的 MOD ID 集合：与行构建共用同一次遍历，调用方不再额外 LINQ 扫描。
    /// </param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public void Rebuild(
        IReadOnlyList<ModRecord> mods,
        Func<ModRecord, ModRow?, (string Text, string TextSignature)> resolve,
        out HashSet<int> aliveIds)
    {
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(resolve);

        Dictionary<int, ModRow> previous = new(AllRows.Count);
        foreach (ModRow row in AllRows)
        {
            previous[row.Mod.Id] = row;
        }

        aliveIds = new HashSet<int>(mods.Count);
        AllRows.Clear();
        foreach (ModRecord mod in mods)
        {
            aliveIds.Add(mod.Id);
            // P-05/记录引用与显示字段都没变时复用旧行；用引用快照比较，避免每行重拼字符串。
            ModRow? reusable = previous.GetValueOrDefault(mod.Id);
            if (reusable is not null && !reusable.HasSameDisplay(mod))
            {
                reusable = null;
            }

            if (reusable is not null)
            {
                string text = resolve(mod, reusable).Text;
                if (!string.Equals(reusable.ModelText, text, StringComparison.Ordinal))
                {
                    reusable.UpdateModelText(text);
                }

                AllRows.Add(reusable);
                continue;
            }

            (string newText, string textSignature) = resolve(mod, null);
            AllRows.Add(new ModRow(mod, newText, textSignature));
        }

        // Rows 由 MainWindow 的 ApplyFilter 统一填充：直接清空会触发 SelectionChanged，
        // 可能在恢复选择前把 ViewModel 的选中集合清掉。
    }

    /// <summary>一行在列表里显示的全部字段签名；只有真正影响显示的内容变化才重建整表。</summary>
    /// <param name="mod">目标记录。</param>
    /// <param name="contentSignature">固定长度的文本签名（描述 + 文件清单摘要）；不再内嵌整份清单。</param>
    /// <returns>稳定签名。</returns>
    internal static string RowSignature(ModRecord mod, string contentSignature) => string.Join(
        ModelTextPrefetchService.DescriptionSeparatorChar,
        mod.Index.ToString(CultureInfo.InvariantCulture),
        mod.Metadata.Name ?? string.Empty,
        mod.Metadata.Category ?? string.Empty,
        mod.Metadata.Description ?? string.Empty,
        mod.Metadata.Version ?? string.Empty,
        mod.Metadata.NexusId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        mod.TargetDirectory ?? string.Empty,
        mod.Kind.ToString(),
        contentSignature);
}
