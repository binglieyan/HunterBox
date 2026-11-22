using HunterBox.Application.Models;
using HunterBox.Domain.Games;
using HunterBox.Domain.Mods;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HunterBox.App;

/// <summary>MainWindow 的刷新/重建/筛选/选中与操作按钮联动。</summary>
public sealed partial class MainWindow
{
    /// <summary>缓存未命中时列表先显示的占位文本；后台预热完成后原地回填。</summary>
    private const string PendingModelText = "正在识别模型…";

    /// <summary>搜索状态提示的统一后缀：关键词与刷新后的比例文案共用一处来源。</summary>
    private const string SearchExitHint = "（清空关键词后再点「搜索」退出）";

    /// <summary>已确认「没有可识别模型」（或扫描失败）的 MOD（游戏 + ID + 当前文件签名）；避免每次 Reload 重扫 pak。</summary>
    /// <remarks>键含游戏稳定 ID：跨游戏同 ID 不会互相影响；游戏 ID 统一走 <see cref="GameProfile.NormalizeId"/>。</remarks>
    private readonly Dictionary<(string GameId, int ModId), string> _classificationNoModel = [];

    /// <summary>分类负缓存键：游戏 ID 统一规范化，大小写/空白不会产生重复条目。</summary>
    /// <param name="gameId">MOD 或当前游戏的稳定 ID；为空时使用空串（旧数据没有游戏标记）。</param>
    /// <param name="modId">MOD ID。</param>
    /// <returns>规范化后的字典键。</returns>
    private static (string GameId, int ModId) ClassificationKey(string? gameId, int modId) =>
        (string.IsNullOrWhiteSpace(gameId) ? string.Empty : GameProfile.NormalizeId(gameId), modId);

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReloadMods();
            UpdateActionButtons();
        }
        catch (Exception ex)
        {
            // 刷新是尽力而为：写盘/扫描失败只提示，不让异常冒到 UI 线程。
            _services.Logger.Error("刷新列表失败", ex);
            ViewModel.Status = "刷新失败：" + ex.Message;
        }
    }

    /// <summary>把当前游戏的序号归一为 1..N（序号整理）；已连续时不写盘。</summary>
    /// <param name="mods">本次刷新已取好的记录快照，避免重复读取仓储。</param>
    private void NormalizeIndexes(IReadOnlyList<ModRecord> mods)
    {
        // 顺序判断与重排由 Application 协调器负责（A-06），窗口只在确有改动时触发后台落盘。
        if (_services.ModList.NormalizeIndexes(_services.GameSession.Game.Id, mods))
        {
            // 只在序号不连续时触发；写盘放后台，避免大索引/慢盘在 UI 线程等 fsync。
            _ = TrySaveStoreAsync("序号整理");
        }
    }

    /// <summary>异步版本的尽力保存：后台串行落盘，避免 UI 线程等待 fsync（A-07）。</summary>
    /// <param name="context">保存原因（日志用）。</param>
    /// <returns>本次保存完成的任务（包含等待前序保存）。</returns>
    private Task TrySaveStoreAsync(string context)
    {
        // 宿主已在线程池串行执行，工作委托里直接同步保存，不再多一次 Task.Run 调度。
        return _services.BackgroundTasks.EnqueueAsync(() =>
        {
            try
            {
                _services.Store.Save();
            }
            catch (Exception ex)
            {
                // 非 IO 异常（如序列化错误）也必须在这里观察并记录。
                _services.Logger.Error("保存 MOD 索引失败（" + context + "）", ex);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 把一次索引保存登记到后台宿主并等待完成；异常原样抛给调用方用于回滚。
    /// </summary>
    /// <returns>本次保存完成的任务。</returns>
    private Task SaveStoreAsync() =>
        _services.BackgroundTasks.EnqueueAsync(() =>
        {
            _services.Store.Save();
            return Task.CompletedTask;
        });

    /// <summary>等待已排队的索引保存完成；操作入口在修改记录前调用，避免与后台序列化并发（C-07）。</summary>
    /// <remarks>
    /// 等待有上限：慢盘/占用时不无限期占住 UI 互斥门，超时后继续本次操作（仓储自身的保存锁仍保证串行化）。
    /// 明确接受的取舍：超时窗口内可能落下一份「旧快照 + 部分新修改」的索引，但存储保存前会在锁内复制完整列表，
    /// 不会写出半个文件；本次操作随后的保存会写入最终状态，最坏情况是期间崩溃导致索引回退到混合快照，可由重扫恢复。
    /// </remarks>
    /// <returns>后台任务全部完成的任务。</returns>
    private async Task WaitForPendingIndexSavesAsync()
    {
        if (await _services.BackgroundTasks.WhenIdleAsync(PendingSaveWaitTimeout))
        {
            return;
        }

        _services.Logger.Warn($"等待后台索引保存超时（{PendingSaveWaitTimeout.TotalSeconds:0} 秒），继续执行本次操作");
        ViewModel.Status = "后台保存较慢，已继续本次操作…";
    }

    /// <summary>操作入口等待后台保存完成的上限。</summary>
    private static readonly TimeSpan PendingSaveWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 操作完成后的轻量刷新：文件/元数据/排序都没变时只通知勾选状态，
    /// 不重建列表（保留滚动位置与选中的行对象）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ReloadMods"/> 一样只读模型文本缓存：缺缓存的行显示占位并调度后台预热，
    /// 不在 UI 线程同步扫描 pak（否则安装/启用后刷新会假死）。
    /// </remarks>
    private void RefreshAfterOperation()
    {
        IReadOnlyList<ModRecord> mods = _services.Store.ModsFor(_services.GameSession.Game.Id);
        bool sameRows = mods.Count == _list.AllRows.Count;
        bool modelCacheIncomplete = false;
        if (sameRows)
        {
            for (int index = 0; index < mods.Count; index++)
            {
                ModRow row = _list.AllRows[index];
                if (row.Mod.Id != mods[index].Id)
                {
                    sameRows = false;
                    break;
                }

                // P-14 / 文本签名按「当前 RelativeFiles 实例 + 描述」缓存，刷新不再重拼整份文件清单；
                // 记录被替换时直接按新记录做一次增量哈希。
                string textSignature = ReferenceEquals(row.Mod, mods[index])
                    ? row.TextSignature
                    : ModelTextPrefetchService.TextSignature(mods[index]);
                bool cached = _modelPanel.TryGetCachedText(
                    mods[index].Id, textSignature, mods[index].GameId, out string text);
                if (!cached)
                {
                    modelCacheIncomplete = true;
                    text = PendingModelText;
                }

                if (string.IsNullOrWhiteSpace(mods[index].Metadata.Description)
                    && !(_classificationNoModel.TryGetValue(ClassificationKey(mods[index].GameId, mods[index].Id), out string? noModelSignature)
                        && string.Equals(noModelSignature, textSignature, StringComparison.Ordinal)))
                {
                    // 有 MOD 还等着补分类：等后台预热处理，行内容随后由重建刷新。
                    modelCacheIncomplete = true;
                }

                // P-18：同一记录实例且备注文本未变时，显示字段与构建行时一致，跳过整行签名拼接；
                // 只有记录被替换/文本变化时才需要精确比较签名。
                if (ReferenceEquals(row.Mod, mods[index])
                    && string.Equals(row.ModelText, text, StringComparison.Ordinal))
                {
                    continue;
                }

                string currentSignature = row.CurrentRowSignature(text);
                if (!string.Equals(row.Signature, currentSignature, StringComparison.Ordinal))
                {
                    sameRows = false;
                    break;
                }
            }
        }

        if (!sameRows)
        {
            ReloadMods(updateStatus: false);
            return;
        }

        foreach (ModRow row in _list.AllRows)
        {
            row.NotifyEnabledChanged();
        }

        UpdateActionButtons();

        // 轻量刷新不重建列表，但缺失的模型信息仍要补：后台预热后原地回填。
        if (modelCacheIncomplete)
        {
            ScheduleModelWarm();
        }
    }

    /// <summary>重建列表（按当前游戏），并尽量保持状态栏里的操作结果不被「已加载 N 个」覆盖。</summary>
    /// <param name="updateStatus">是否用列表状态覆盖状态栏。</param>
    /// <param name="scheduleWarm">
    /// 是否调度缺失模型信息的后台预热。用户主动取消预热后传 <see langword="false"/>，
    /// 避免刚取消就立刻重新起一轮（已完成项此前已落盘）。
    /// </param>
    /// <remarks>
    /// UI 线程只读模型文本缓存：缓存未命中的行先显示 <see cref="PendingModelText"/>，
    /// 真正的 pak 扫描交给 <see cref="ScheduleModelWarm"/> 在后台只补缺失项，完成后回填。
    /// </remarks>
    private void ReloadMods(bool updateStatus = true, bool scheduleWarm = true)
    {
        // P-05：一次刷新只读取一次仓储快照，序号整理/存活裁剪/重建列表共用。
        IReadOnlyList<ModRecord> mods = _services.Store.ModsFor(_services.GameSession.Game.Id);
        NormalizeIndexes(mods);
        // P-05：可复用的旧行直接使用其缓存的文本签名，未变化的行不再产生清单级字符串拷贝。
        // 新行只做一次增量哈希，并把签名随结果写回新行。
        // 存活 ID 由 Rebuild 在同一次遍历里收集，不再单独对 mods 做一趟 Select。
        _list.Rebuild(
            mods,
            (mod, reusable) =>
            {
                if (reusable is not null
                    && _modelPanel.TryGetCachedText(
                        mod.Id, reusable.TextSignature, mod.GameId ?? string.Empty, out string cached))
                {
                    return (cached, reusable.TextSignature);
                }

                string textSignature = ModelTextPrefetchService.TextSignature(mod);
                return (_modelPanel.TryGetCachedText(mod.Id, textSignature, mod.GameId ?? string.Empty, out string text)
                    ? text
                    : PendingModelText, textSignature);
            },
            out HashSet<int> aliveIds);
        _modelPanel.EnsureTextCacheCapacity(aliveIds.Count);
        _modelPanel.Prune(_services.GameSession.Game.Id, aliveIds);
        // 分类负缓存按游戏裁剪，存活集合复用同一份 HashSet。
        // 键统一规范化，比较时不再需要忽略大小写。
        string currentGameId = _services.GameSession.Game.Id;
        string currentGame = string.IsNullOrWhiteSpace(currentGameId) ? string.Empty : GameProfile.NormalizeId(currentGameId);
        foreach ((string gameId, int modId) in _classificationNoModel.Keys
            .Where(key => string.Equals(key.GameId, currentGame, StringComparison.Ordinal)
                && !aliveIds.Contains(key.ModId))
            .ToList())
        {
            _classificationNoModel.Remove((gameId, modId));
        }

        ApplyFilter();

        if (updateStatus)
        {
            ViewModel.Status = RestoreLoadedStatus();
        }

        // 只补缺失项的后台预热：缓存命中的行不会重扫，重复刷新不会反复触发全量扫描。
        if (scheduleWarm)
        {
            ScheduleModelWarm();
        }
    }

    /// <summary>
    /// 恢复列表状态文案：搜索模式显示关键词与筛选计数，普通模式显示已加载数量。
    /// 刷新、退出搜索、预热清理与安装后的零安装分支共用。
    /// </summary>
    private string RestoreLoadedStatus() =>
        IsFilterMode ? BuildSearchStatus() : BuildLoadedStatus();

    /// <summary>构造搜索模式状态文案：搜索按钮与刷新后的状态必须使用同一模板。</summary>
    private string BuildSearchStatus() =>
        $"搜索模式：关键词「{ViewModel.FilterKeyword.Trim()}」，显示 {_list.Rows.Count} / {_list.AllRows.Count} 个 MOD{SearchExitHint}";

    private string BuildLoadedStatus()
    {
        string status = $"已加载 {_list.AllRows.Count} 个 MOD";
        if (string.IsNullOrWhiteSpace(_services.GameSession.GameRoot))
        {
            status += "（未设置游戏目录，请在「游戏设置」里设置游戏路径）";
        }
        return status;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 输入框与 ViewModel 双向绑定；这里只补非绑定路径（键盘输入已由 TwoWay 回写），
        // 值相同不重复写回，避免每次键入形成通知回环。
        if (!string.Equals(ViewModel.FilterKeyword, FilterBox.Text, StringComparison.Ordinal))
        {
            ViewModel.FilterKeyword = FilterBox.Text;
        }

        EnsureFilterDebounce();
        _filterDebounce!.Stop();
        _filterDebounce.Start();
    }

    private void EnsureFilterDebounce()
    {
        if (_filterDebounce is not null)
        {
            return;
        }

        Microsoft.UI.Dispatching.DispatcherQueue queue =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("当前线程没有 UI DispatcherQueue");
        _filterDebounce = queue.CreateTimer();
        _filterDebounce.Interval = TimeSpan.FromMilliseconds(200);
        _filterDebounce.IsRepeating = false;
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            ApplyFilterAndStatus();
        };
    }

    /// <summary>
    /// 搜索去抖只负责过滤与计数（<see cref="ApplyFilter"/> 会更新 <c>CountText</c>），
    /// 状态栏文案由「搜索」按钮、<see cref="ReloadMods"/> 与操作结果负责：
    /// 晚到的去抖回调不得覆盖「已启用/操作失败/操作已取消」等结论。
    /// </summary>
    private void ApplyFilterAndStatus()
    {
        _filterDebounce?.Stop();
        ApplyFilter();
    }

    private void FilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        _filterDebounce?.Stop();
        ApplyFilter();
        e.Handled = true;
    }

    /// <summary>「搜索」：有关键词时应用过滤；无关键词时清空并退出搜索模式。</summary>
    private void Search_Click(object sender, RoutedEventArgs e)
    {
        _filterDebounce?.Stop();
        string keyword = ViewModel.FilterKeyword.Trim();
        if (keyword.Length == 0)
        {
            ViewModel.FilterKeyword = string.Empty;
            ApplyFilter();
            ViewModel.Status = RestoreLoadedStatus();
            return;
        }

        ApplyFilter();
        ViewModel.Status = BuildSearchStatus();
    }

    private void ApplyFilter()
    {
        string keyword = ViewModel.FilterKeyword.Trim();
        List<ModRow> matched = string.IsNullOrEmpty(keyword)
            ? [.. _list.AllRows]
            : [.. _list.AllRows.Where(row =>
                row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || row.Category.Contains(keyword, StringComparison.OrdinalIgnoreCase))];

        _list.Rebuilding = true;
        List<int> visibleSelectedIds = [];
        try
        {
            ModsList.SelectedItems.Clear();
            // 整体替换只触发一次 Reset，避免数百个 MOD 时逐项 Add 引发 N 次布局。
            _list.Rows.ReplaceAll(matched);

            // 恢复选中（做法）：按 MOD ID 匹配，支持多选。
            foreach (ModRow row in matched)
            {
                if (ViewModel.IsSelected(row.Mod.Id))
                {
                    ModsList.SelectedItems.Add(row);
                    visibleSelectedIds.Add(row.Mod.Id);
                }
            }
        }
        finally
        {
            _list.Rebuilding = false;
        }

        // 被过滤/卸载掉的行不能继续留在选中集合：否则依赖选中的按钮仍可点，
        // 但命令按可见行找不到目标而静默返回。
        ViewModel.SetSelectedIds(visibleSelectedIds);

        ViewModel.CountText = $"{_list.Rows.Count} 个";
        UpdateActionButtons();
    }

    /// <summary>选中的行，按列表顺序（序号）升序。</summary>
    private List<ModRow> SelectedRows()
    {
        // 只取当前可见行：被过滤掉但 VM 仍记住的选中项不应参与命令。
        List<ModRow> rows = [.. _list.Rows.Where(row => ViewModel.IsSelected(row.Mod.Id))];
        rows.Sort((left, right) => left.Mod.Index.CompareTo(right.Mod.Index));
        return rows;
    }

    /// <summary>操作目标：第一个选中的 MOD（上移/下移/快速移动只作用于它）。</summary>
    private ModRow? SelectedRow
    {
        get
        {
            // 直接取 Index 最小项，避免每次访问都分配 List 并排序（P-27）。
            ModRow? best = null;
            foreach (ModRow row in _list.Rows)
            {
                if (ViewModel.IsSelected(row.Mod.Id) && (best is null || row.Mod.Index < best.Mod.Index))
                {
                    best = row;
                }
            }

            return best;
        }
    }

    private void ModsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_list.Rebuilding)
        {
            return;
        }

        List<int> ids = [];
        foreach (object item in ModsList.SelectedItems)
        {
            if (item is ModRow row)
            {
                ids.Add(row.Mod.Id);
            }
        }

        ViewModel.SetSelectedIds(ids);
        UpdateActionButtons();
    }

    /// <summary>把「忙碌 / 有 MOD」同步给 ViewModel，选中状态由选择事件单独维护。</summary>
    private void UpdateActionButtons() =>
        ViewModel.SetState(
            _operations.IsBusy,
            _list.AllRows.Count > 0);

}
