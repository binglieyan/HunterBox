using HunterBox.Application.Models;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Mods;
using HunterBox.Infrastructure.Paks;
using Microsoft.UI.Xaml.Controls;

namespace HunterBox.App;

/// <summary>MainWindow 的模型编辑入口与模型信息编辑。</summary>
public sealed partial class MainWindow
{

    /// <summary>
    /// 右键「MOD文件修改」：打开模型编辑器，按完整模型身份改写模型路径。
    /// 已启用 MOD 允许打开查看（行只读），点确定后提示「请先禁用MOD」；有改动才执行改写。
    /// </summary>
    /// <remarks>
    /// 打开编辑器前的模型识别（自身 + 当前游戏全部 MOD 的 pak 扫描）是重 IO：
    /// 统一放后台线程做（UI 只亮 BusyRing/可取消），结果回 UI 线程写缓存，避免「必然假死数秒到数十秒」。
    /// </remarks>
    private async Task RewriteModelsAsync(ModRow row)
    {
        (ModelEditorPreparation? preparation, string? preparationError) = await PrepareModelEditorAsync(row.Mod);
        if (preparation is null)
        {
            if (IsCanceled(preparationError))
            {
                ReportCanceled("已取消模型识别");
                return;
            }

            if (string.Equals(preparationError, BusyReentryMessage, StringComparison.Ordinal))
            {
                ViewModel.Status = BusyReentryMessage;
                return;
            }

            await ShowMessageAsync("MOD文件修改", preparationError ?? "识别模型失败");
            return;
        }

        // 模型编辑器使用「完整模型身份」路径形式（护甲 ch03/012/003、武器 it10/03/0000、猫 ch05/003/0001）。
        IReadOnlyList<ModelDetail> models = preparation.Models;
        if (models.Count == 0)
        {
            await ShowMessageAsync("MOD文件修改", preparation.NoModelsMessage ?? "这个MOD没什么模型可以修改");
            return;
        }

        ModelGame game = models[0].Game;

        // 「仅显示未使用的ID」= 当前游戏全部 MOD（含禁用、含自身其它模型）已占用的模型身份。
        ModelEditorDialog dialog = new(
            models,
            new ModelCatalog(game),
            preparation.UsedIds,
            row.Mod.DisplayName,
            row.Mod.Enabled)
        {
            XamlRoot = Root.XamlRoot,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        if (row.Mod.Enabled)
        {
            await ShowMessageAsync("MOD文件修改", "请先禁用MOD");
            return;
        }

        IReadOnlyList<(string From, string To)> changes = dialog.Changes;
        if (changes.Count == 0)
        {
            await ShowMessageAsync("MOD文件修改", "没有任何改动");
            return;
        }

        PakModelRewriteService pakService = _services.CreatePakRewrite();
        ModelRewriteCoordinator coordinator = new(_services.Paths, pakService, _services.FileSystem);
        ModelRewriteSummary? summary = null;
        int total = changes.Count;
        int done = 0;
        int paksScanned = 0;
        int entriesChanged = 0;
        int filesMoved = 0;
        List<string> warnings = [];
        List<string> unsupported = [];
        _modelPanel.Invalidate(row.Mod.Id);

        // 后台只改这份副本；回到 UI 线程后再合并回真实记录，避免后台线程直接改 store 里的对象。
        ModRecord working = row.Mod.WithGame(row.Mod.GameId ?? _services.GameSession.Game.Id, row.Mod.Index);

        int epoch = _operations.Epoch;
        IProgress<(int Done, int Total)> progress = CreateOperationProgress(
            epoch,
            p => $"正在改写模型... ({p.Done}/{p.Total})");

        (bool ok, string? error) = await RunOperationAsync("正在改写模型...", token =>
        {
            PakNameIndex pakIndex = EmbeddedPakNameIndex.Load(game);
            try
            {
                // P-02：按 pak 聚合全部 change 一次提交，同一 TOC 只读/写一次；
                // 进度在完成后一次性推进到总数。
                token.ThrowIfCancellationRequested();
                ModelRewriteSummary step = coordinator.Rewrite(working, changes, pakIndex, game);
                paksScanned = step.PaksScanned;
                entriesChanged += step.EntriesChanged;
                filesMoved += step.FilesMoved;
                warnings.AddRange(step.Warnings);
                unsupported.AddRange(step.UnsupportedPaks);
                done = total;
                progress.Report((done, total));
            }
            finally
            {
                summary = new ModelRewriteSummary(
                    paksScanned,
                    entriesChanged,
                    filesMoved,
                    [.. warnings.Distinct(StringComparer.Ordinal)],
                    [.. unsupported.Distinct(StringComparer.Ordinal)]);

                // 后台只重算副本的模型信息；真实记录与 store 留到 UI 线程合并/落盘。
                try
                {
                    ModelAutoClassifier.Apply(
                        _services.Paths,
                        working,
                        game,
                        _services.PakEntryPathReader,
                        _services.ModelNames,
                        _services.Logger.Warn,
                        recompute: true);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    _services.Logger.Warn($"改写后重算模型信息失败（{row.Mod.DisplayName}）：{ex.Message}");
                }
            }

            return Task.CompletedTask;
        });

        // 回 UI 线程后再合并副本结果并保存一次，保证 store 只在 UI 线程被修改。
        bool changed = !row.Mod.RelativeFiles.SequenceEqual(working.RelativeFiles, StringComparer.OrdinalIgnoreCase)
            || !Equals(row.Mod.Metadata, working.Metadata);
        string? persistenceWarning = null;
        if (changed)
        {
            row.Mod.RelativeFiles = [.. working.RelativeFiles];
            row.Mod.Metadata = working.Metadata;
            try
            {
                await SaveStoreAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 文件已经改名成功，但记录没落盘：必须让用户知道，否则重启后路径与磁盘不一致。
                persistenceWarning = "模型记录保存失败（重启后可能仍显示旧路径），请关闭占用程序后重试或重启修复。";
                _services.Logger.Warn($"改写后保存模型信息失败（{row.Mod.DisplayName}）：{ex.Message}");
            }
        }

        _modelPanel.Invalidate(row.Mod.Id);

        // 先批量写全提示与跳过的 pak，弹窗截断后的「其余见日志」才成立。
        if (summary is not null)
        {
            _services.Logger.WarnBatch(
            [
                .. summary.Warnings.Select(warning => $"模型改写提示（{row.Mod.DisplayName}）：{warning}"),
                .. summary.UnsupportedPaks.Select(pak => $"模型改写跳过 pak（{row.Mod.DisplayName}）：{pak}"),
            ]);
        }

        if (IsCanceled(error))
        {
            string canceledText = summary is { HasChanges: true }
                ? $"已改写 {summary.EntriesChanged} 个 pak 条目、{summary.FilesMoved} 个散装文件，其余未处理"
                : "模型文件未改写";
            ReportCanceled(ModelRewriteText.AppendPersistenceWarning(canceledText, persistenceWarning));
            return;
        }

        if (!ok)
        {
            await ShowMessageAsync(
                "改写模型失败",
                ModelRewriteText.AppendPersistenceWarning(error ?? "未知错误", persistenceWarning));
            return;
        }

        if (summary is null || !summary.HasChanges)
        {
            string text = "没有任何改动";
            if (summary is not null && (summary.UnsupportedPaks.Count > 0 || summary.Warnings.Count > 0))
            {
                text += Environment.NewLine + Environment.NewLine + ModelRewriteText.Build(summary);
            }

            await ShowMessageAsync(
                "模型改写结果",
                ModelRewriteText.AppendPersistenceWarning(text, persistenceWarning));
            return;
        }

        // 汇总弹窗必须带上不支持/跳过的原因与 warnings（不得静默丢弃）。
        await ShowMessageAsync(
            "模型改写结果",
            ModelRewriteText.AppendPersistenceWarning(ModelRewriteText.Build(summary), persistenceWarning));
        ViewModel.Status = $"模型改写完成：{row.Mod.DisplayName}"
            + $"（已改写 {summary.EntriesChanged} 个 pak 条目、{summary.FilesMoved} 个散装文件）"
            + (persistenceWarning is null ? string.Empty : "（模型记录保存失败）");
    }

    /// <summary>
    /// 后台准备模型编辑器数据：目标 MOD 的模型行 + 当前游戏全部 MOD 的已用模型身份。
    /// </summary>
    /// <remarks>
    /// pak 扫描（单包上限 100MB、TOC 加密还要整包解密）绝不能留在 UI 线程（右键「MOD文件修改」曾因此假死）。
    /// 缓存按内容戳校验（不再只看文件清单签名）；身份快照一次物化，缓存内部自带锁，起始快照传给后台。
    /// </remarks>
    /// <param name="target">要编辑的 MOD。</param>
    /// <returns>准备结果；忙碌/取消/失败时返回 <see langword="null"/> 并带回原因。</returns>
    private async Task<(ModelEditorPreparation? Preparation, string? Error)> PrepareModelEditorAsync(ModRecord target)
    {
        ModelGame game = CurrentModelGame;
        string gameId = _services.GameSession.Game.Id;
        List<ModRecord> mods = [.. _services.Store.ModsFor(gameId)];

        // 把开始时的游戏 ID 一起传入，回写缓存时使用同一命名空间，切换游戏不会串数据。
        // 身份快照在后台任务内物化，UI 线程不再复制明细/已用身份两份字典。
        // showStatus: false：与启动预热一致，ExitBusy 不恢复状态栏；成功路径由调用方打开对话框，
        // 若这里写「正在识别模型...」，关闭对话框后状态栏会残留该文案。
        (ModelEditorPreparation? preparation, string? error) = await RunBackgroundAsync(
            "正在识别模型...",
            token => _modelPanel.BuildPreparation(
                gameId,
                target,
                mods,
                _modelPanel.IdentitySnapshot,
                game,
                token),
            showStatus: false);

        if (preparation is null)
        {
            return (null, error);
        }

        // 回到 UI 线程后统一写缓存（明细行 + 已用身份增量缓存），避免后台线程修改字典。
        _modelPanel.StorePreparation(gameId, preparation);
        return (preparation, null);
    }
    private async Task EditInfoAsync(ModRow row)
    {
        ModInfoDialog dialog = new(row.Mod.Metadata) { XamlRoot = Root.XamlRoot };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        // 写盘失败时回滚内存元数据，避免「界面显示已改、下次任意 Save 才落盘」的隐性分叉。
        ModMetadata previous = row.Mod.Metadata;
        row.Mod.Metadata = dialog.BuildResult();
        try
        {
            await SaveStoreAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            row.Mod.Metadata = previous;
            _services.Logger.Error("保存 MOD 信息失败", ex);
            ViewModel.Status = "保存 MOD 信息失败：" + ex.Message;
            return;
        }

        ReloadMods();
        ViewModel.Status = "MOD信息已保存";
    }

    /// <summary>一轮后台预热最多重试的次数（缓存持续失效时防止重试风暴）。</summary>
    private const int MaxModelWarmRounds = 3;

    /// <summary>合并连续刷新的等待时间（毫秒）：期间新的刷新只更新缺失集合，不叠加后台任务。</summary>
    private const int ModelWarmDebounceMilliseconds = 150;

    /// <summary>等待进行中操作结束的轮询间隔（毫秒）。</summary>
    private const int ModelWarmBusyPollMilliseconds = 200;

    /// <summary>最多等待进行中操作结束的时间（毫秒）；超时放弃本轮，等下次刷新再补。</summary>
    private const int ModelWarmMaxBusyWaitMilliseconds = 60_000;

    /// <summary>
    /// 启动预热失败时写入状态栏的一次性提示；
    /// 后续预热成功时由 <see cref="ClearModelWarmFailureStatus"/> 清除，避免状态与事实不一致。
    /// </summary>
    private const string ModelWarmFailureStatus = "模型识别失败，列表暂显示占位（详见日志）";

    /// <summary>是否已有一轮模型信息后台预热在跑（UI 线程读写）。</summary>
    private bool _modelWarmRunning;

    /// <summary>
    /// 调度一次「缺失模型信息」的后台预热：缓存命中的行不重扫，只补缺失项，完成后回填列表。
    /// </summary>
    /// <remarks>
    /// 已有预热在跑时直接复用（不再叠加后台任务），因此连续刷新不会引发「每次刷新全量扫 pak」的抖动。
    /// 预热本身用 <see cref="RunBackgroundAsync{T}"/> 执行：忙碌环/取消按钮可见，用户可协作取消。
    /// </remarks>
    private void ScheduleModelWarm()
    {
        if (_modelWarmRunning || _windowClosing)
        {
            return;
        }

        _modelWarmRunning = true;
        _ = WarmMissingModelTextsAsync();
    }

    /// <summary>后台补齐缺失的模型分类/备注文本，并在回 UI 线程后回填缓存与已显示的行。</summary>
    private async Task WarmMissingModelTextsAsync()
    {
        try
        {
            for (int round = 0; round < MaxModelWarmRounds; round++)
            {
                // 合并连续刷新：稍等一拍再开始，避免每次 ReloadMods 都立刻起一个后台任务。
                await Task.Delay(ModelWarmDebounceMilliseconds);
                if (_windowClosing)
                {
                    return;
                }

                // 有操作进行中时 RunBackgroundAsync 会直接返回忙碌重入：等它结束再补，
                // 期间重新取快照即可拿到操作后的最新文件清单。
                int waited = 0;
                while (_operations.IsBusy && waited < ModelWarmMaxBusyWaitMilliseconds)
                {
                    await Task.Delay(ModelWarmBusyPollMilliseconds);
                    waited += ModelWarmBusyPollMilliseconds;
                }

                if (_operations.IsBusy || _windowClosing)
                {
                    return;
                }

                if (!TryCreateModelWarmRequest(
                    out List<ModRecord> mods,
                    out HashSet<int> needText,
                    out HashSet<int> needClassification,
                    out string warmGameId,
                    out ModelGame game))
                {
                    return;
                }

                (ModelPrewarmResult? warm, string? error) = await RunBackgroundAsync(
                    "正在识别模型...",
                    token => _modelPanel.PrewarmMissing(mods, needText, needClassification, game, token),
                    showStatus: false);
                if (warm is null)
                {
                    if (IsCanceled(error))
                    {
                        // 静默模式不改状态栏：只有用户主动取消时给一次明确反馈。
                        ViewModel.Status = "已取消模型识别";
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        _services.Logger.Warn("后台预热模型信息失败，列表暂保留占位：" + error);
                        // 整体失败时无法区分哪些 MOD 已成功扫描，不能写「无模型」负缓存；
                        // 保留占位并等下一次刷新重试，避免把未扫描的 MOD 永久当成无模型。
                    }

                    return;
                }

                // 回到 UI 线程后统一写缓存，避免后台线程修改字典；取消时这里只含「已完成项」。
                // 游戏 ID 用预热开始时的快照，切换游戏后旧结果不会写到新游戏。
                _modelPanel.DropInvalidated(warm.InvalidatedIds, warmGameId);
                _modelPanel.StoreText(warmGameId, warm.TextEntries);
                foreach ((int id, string signature) in warm.NoModelEntries)
                {
                    _classificationNoModel[ClassificationKey(warmGameId, id)] = signature;
                }

                if (warm.ClassificationChanged)
                {
                    // 分类会写描述：行的 ToolTip/分类列与缓存签名都要更新，保存后重建一次
                    //（此时文本已全部缓存，不会再次触发扫描）。取消时也必须保存：后台已改内存记录，
                    // 不落盘会出现「界面有分类、磁盘没有记录」（C-23）。
                    await TrySaveStoreAsync(warm.Canceled ? "自动分类（取消前已完成）" : "自动分类");
                    ReloadMods(updateStatus: false, scheduleWarm: !warm.Canceled);
                    if (warm.Canceled)
                    {
                        ViewModel.Status = "已取消模型识别（已保存取消前完成的部分）";
                    }
                    else
                    {
                        ClearModelWarmFailureStatus();
                    }

                    return;
                }

                if (warm.Canceled)
                {
                    // 取消前算好的文本已写入缓存；不再自动排下一轮，避免「取消后又自己跑起来」。
                    BackfillModelTexts();
                    ViewModel.Status = "已取消模型识别";
                    return;
                }

                // 回填与「是否还有缺失」合并为一次遍历，全部命中时不再空跑下一轮。
                if (!BackfillModelTexts())
                {
                    ClearModelWarmFailureStatus();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // 模型信息只是显示增强：任何失败都保留占位，等下一次刷新再补，绝不能影响主流程。
            _services.Logger.Error("后台预热模型信息失败", ex);
        }
        finally
        {
            _modelWarmRunning = false;
        }
    }

    /// <summary>
    /// 预热成功后清除启动期的一次性失败提示；按前缀替换并保留后续追加的内容
    /// （例如「索引保存失败」），不覆盖用户操作产生的其它状态文本。
    /// </summary>
    private void ClearModelWarmFailureStatus()
    {
        ViewModel.Status = ViewModel.ReplaceStatusPrefix(
            ModelWarmFailureStatus,
            RestoreLoadedStatus());
    }

    /// <summary>UI 线程快照：找出模型信息缓存缺失、需要后台预热的 MOD。</summary>
    /// <param name="mods">需要预热的缺失 MOD 子集（不是当前游戏全量）。</param>
    /// <param name="needText">需要计算备注文本的 MOD ID。</param>
    /// <param name="needClassification">需要补自动分类的 MOD ID（同时必然需要重算文本）。</param>
    /// <param name="gameId">预热所属游戏快照。</param>
    /// <param name="game">当前模型改写游戏。</param>
    /// <returns>存在缺失项时为 <see langword="true"/>。</returns>
    private bool TryCreateModelWarmRequest(
        out List<ModRecord> mods,
        out HashSet<int> needText,
        out HashSet<int> needClassification,
        out string gameId,
        out ModelGame game)
    {
        // P-11：直接复用已构建的行签名缓存，避免在 UI 线程为每个 MOD 重新拼整份文件清单。
        // 全部命中时不物化 O(N) 的 MOD 列表；只有确有缺失项才收集对应记录。
        // 整表一次批量查询，命中判定不再逐行加锁。
        List<ModRow> rows = _list.AllRows;
        List<ModRecord>? missingMods = null;
        game = CurrentModelGame;
        gameId = _services.GameSession.Game.Id;
        needText = [];
        needClassification = [];

        string?[] cachedTexts = QueryTextCache(rows);
        for (int index = 0; index < rows.Count; index++)
        {
            ModRow row = rows[index];
            ModRecord mod = row.Mod;
            // 直接复用 ModRow 缓存的文本签名，正常预热路径不再逐行重拼「描述 + 整份文件清单」。
            string signature = _textCacheRequests[index].Signature;
            bool classify = string.IsNullOrWhiteSpace(mod.Metadata.Description)
                && !(_classificationNoModel.TryGetValue(ClassificationKey(mod.GameId, mod.Id), out string? cachedSignature)
                    && string.Equals(cachedSignature, signature, StringComparison.Ordinal));
            if (classify)
            {
                // 分类会写描述，文本缓存签名随之变化：必须一并重算文本。
                needClassification.Add(mod.Id);
                needText.Add(mod.Id);
                (missingMods ??= []).Add(mod);
                continue;
            }

            if (cachedTexts[index] is null)
            {
                needText.Add(mod.Id);
                (missingMods ??= []).Add(mod);
            }
        }

        mods = missingMods ?? [];
        return needText.Count > 0;
    }

    /// <summary>文本缓存批量查询的请求缓冲（UI 线程复用）。</summary>
    private readonly List<(int ModId, string Signature, string GameId)> _textCacheRequests = [];

    /// <summary>文本缓存批量查询的结果缓冲（按最大行数扩容后复用）。</summary>
    private string?[] _textCacheResults = [];

    /// <summary>批量查询列表行的模型文本；请求/结果缓冲在 UI 线程复用，避免每次打开分配 O(N) 数组。</summary>
    /// <param name="rows">当前列表行。</param>
    /// <returns>结果缓冲（前 <c>rows.Count</c> 项有效）。</returns>
    private string?[] QueryTextCache(List<ModRow> rows)
    {
        _textCacheRequests.Clear();
        foreach (ModRow row in rows)
        {
            _textCacheRequests.Add((row.Mod.Id, row.TextSignature, row.Mod.GameId ?? string.Empty));
        }

        if (_textCacheResults.Length < rows.Count)
        {
            _textCacheResults = new string?[rows.Count];
        }

        _modelPanel.TryGetCachedTexts(_textCacheRequests, _textCacheResults.AsSpan(0, rows.Count));
        return _textCacheResults;
    }

    /// <summary>把后台预热算好并写入缓存的文本回填到已显示的行（不重建整表，保留滚动/选中）。</summary>
    /// <returns>回填后是否仍有行缺少模型文本（一次遍历同时完成回填与缺失判定）。</returns>
    private bool BackfillModelTexts()
    {
        List<ModRow> rows = _list.AllRows;
        // 整表一次批量查询，避免逐行加锁：请求/结果缓冲复用。
        string?[] texts = QueryTextCache(rows);
        bool missing = false;
        for (int index = 0; index < rows.Count; index++)
        {
            string? text = texts[index];
            if (text is null)
            {
                missing = true;
                continue;
            }

            ModRow row = rows[index];
            if (!string.Equals(row.ModelText, text, StringComparison.Ordinal))
            {
                row.UpdateModelText(text);
            }
        }

        return missing;
    }

}
