using HunterBox.Application.Common;
using HunterBox.Domain.Models;
using HunterBox.Domain.Paks;
using ModelIdentity = HunterBox.Domain.Models.ModelIdentity;

namespace HunterBox.Application.Models;

/// <summary>「把MOD文件夹拖到列表」的扫描结果。</summary>
/// <param name="Models">识别到的模型行（含 pak 条目）。</param>
/// <param name="UnsupportedPaks">无法扫描的根层 pak 及原因（没有模型时用于提示）。</param>
public sealed record FolderModelScan(
    IReadOnlyList<ModelDetail> Models,
    IReadOnlyList<string> UnsupportedPaks);

/// <summary>
/// 对未安装的 MOD 文件夹直接改模型 ID（拖文件夹入口）：
/// 扫描文件夹里的散装文件与根层 pak 条目识别模型，应用后只改路径（散装用 <c>IFileSystem.MoveFile</c>，
/// 根层 pak 用 pak 改写服务改 TOC 路径哈希），不修改文件内容。
/// </summary>
/// <remarks>
/// 从 App 层下沉：pak 条目读取通过 <see cref="IPakEntryPathReader"/> 注入，显示名通过
/// <see cref="IModelNameResolver"/> 注入，pak 改写仍由调用方传入 <see cref="PakModelRewriteService"/>。
/// </remarks>
public static class ModelFolderEditor
{
    /// <summary>扫描文件夹里的模型行（散装文件 + 根层 pak 条目）。</summary>
    /// <param name="folder">文件夹绝对路径。</param>
    /// <param name="game">文件夹所属游戏（决定 pak 路径清单；世界 pak 不展开）。</param>
    /// <param name="reader">pak 条目路径读取端口。</param>
    /// <param name="names">模型显示名解析端口。</param>
    /// <param name="fileSystem">文件系统端口（存在性与文件枚举）。</param>
    /// <param name="warn">warning 回调（无法扫描的 pak）；不传则不记录。</param>
    /// <returns>模型行与无法扫描的 pak。</returns>
    /// <exception cref="ArgumentException">文件夹路径为空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/>、<paramref name="names"/> 或 <paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="DirectoryNotFoundException">文件夹不存在。</exception>
    /// <exception cref="IOException">读取文件夹失败。</exception>
    /// <exception cref="UnauthorizedAccessException">没有读取权限。</exception>
    public static FolderModelScan Scan(
        string folder,
        ModelGame game,
        IPakEntryPathReader reader,
        IModelNameResolver names,
        IFileSystem fileSystem,
        Action<string>? warn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(fileSystem);

        string full = Path.GetFullPath(folder);
        if (!fileSystem.DirectoryExists(full))
        {
            throw new DirectoryNotFoundException("文件夹不存在：" + full);
        }

        List<(string Path, string? PakFile)> entries = [];
        List<string> rootPaks = [];
        // 单次遍历同时得到文件清单与链接标记，不再扫描两次全树。
        (string[] files, bool containsLink, _) = SafeFileTree.EnumerateFilesAndCheckLinks(fileSystem, full);
        foreach (string file in files)
        {
            string relative = ToRelative(full, file);
            entries.Add((relative, null));
            if (ModelPathRules.IsRootPak(relative))
            {
                rootPaks.Add(relative);
            }
        }

        List<string> unsupported = [];
        if (containsLink)
        {
            unsupported.Add("文件夹包含符号链接/junction，已跳过链接及其目标。");
        }

        if (ModelPathRules.SupportsPakRewrite(game))
        {
            PakNameIndex index = reader.LoadNameIndex(game);
            foreach (string pak in rootPaks)
            {
                string pakPath = Path.Combine(full, pak.Replace('/', Path.DirectorySeparatorChar));
                IReadOnlyList<string>? paths = reader.TryReadEntryPaths(pakPath, pak, index, out string? reason);
                if (paths is null)
                {
                    warn?.Invoke(ModelAutoClassifier.BuildPakReadWarning(pak, reason));
                    unsupported.Add($"{pak}（{reason ?? "无法读取"}）");
                    continue;
                }

                entries.AddRange(paths.Select(path => (path, (string?)pak)));
            }
        }
        else
        {
            unsupported.AddRange(rootPaks.Select(pak =>
                $"{pak}（{ModelGameInfo.GetDisplayName(game)}的 pak 不支持改模型ID）"));
        }

        IReadOnlyList<DetectedModel> detected = game == ModelGame.Unknown
            ? ModelDetector.Detect(entries)
            : ModelDetector.Detect(entries, game);
        List<ModelDetail> models = new(detected.Count);
        foreach (DetectedModel model in detected)
        {
            models.Add(ModelDetail.FromDetected(model, names.Resolve(model.SamplePath)));
        }

        return new FolderModelScan(models, unsupported);
    }

    /// <summary>按完整模型身份改写文件夹里的模型路径（只改名，不改内容）。</summary>
    /// <param name="folder">文件夹绝对路径。</param>
    /// <param name="changes">源/目标完整身份路径形式对（来自模型编辑器）。</param>
    /// <param name="pakService">pak 改写服务（根层 pak 改 TOC 路径哈希）。</param>
    /// <param name="game">文件夹所属游戏（世界 pak 不支持改写，会跳过并提示）。</param>
    /// <param name="reader">pak 条目路径读取端口（提供当前游戏的内置路径清单）。</param>
    /// <param name="fileSystem">文件系统端口（存在性判断、改名与空目录清理）。</param>
    /// <param name="progress">进度回调（已完成/总数，按 change 计数）。</param>
    /// <param name="token">取消令牌（每个 change 与 pak 处理前检查）。</param>
    /// <returns>汇总（已改写条目/文件、跳过原因、warning）。</returns>
    /// <exception cref="ArgumentException">文件夹路径为空、身份不合法或源/目标游戏/类别/类型不一致。</exception>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    /// <exception cref="DirectoryNotFoundException">文件夹不存在。</exception>
    public static ModelRewriteSummary Rewrite(
        string folder,
        IReadOnlyList<(string From, string To)> changes,
        PakModelRewriteService pakService,
        ModelGame game,
        IPakEntryPathReader reader,
        IFileSystem fileSystem,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(pakService);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(fileSystem);

        string full = Path.GetFullPath(folder);
        List<string> warnings = [];
        List<string> unsupported = [];
        int entriesChanged = 0;
        int filesMoved = 0;
        int paksScanned = 0;
        int done = 0;

        // 单次遍历同时得到文件清单与链接标记；含链接直接拒绝改名。
        string[] looseFiles = [];
        if (fileSystem.DirectoryExists(full))
        {
            (looseFiles, bool containsLink, _) = SafeFileTree.EnumerateFilesAndCheckLinks(fileSystem, full);
            if (containsLink)
            {
                warnings.Add("文件夹包含符号链接/junction，已停止改名以避免写入链接目标；请先移除链接后重试。");
                return new ModelRewriteSummary(0, 0, 0, [.. warnings], []);
            }
        }

        bool rewritePaks = ModelPathRules.SupportsPakRewrite(game);
        PakNameIndex index = reader.LoadNameIndex(game);
        string[] topLevelPaks =
        [
            .. fileSystem.EnumerateFiles(full, "*.pak", SearchOption.TopDirectoryOnly)
                .Where(pak => !SafeFileTree.IsReparseLink(fileSystem, pak)),
        ];
        if (!rewritePaks && topLevelPaks.Length > 0)
        {
            warnings.Add($"{ModelGameInfo.GetDisplayName(game)}的 pak 不支持按路径改写，已跳过 "
                + topLevelPaks.Length + " 个 pak：" + string.Join("、", topLevelPaks.Select(Path.GetFileName)));
        }

        // 散装文件只遍历一次，先按解析出的模型身份分组；
        // change 循环直接取对应分组，无法解析身份的文件只用于下方一次性预分组。
        Dictionary<string, string> relativeByFull = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<ModelIdentity, List<string>> filesByIdentity = [];
        List<string> unparsedFiles = [];
        foreach (string file in looseFiles)
        {
            string relative = ToRelative(full, file);
            relativeByFull[file] = relative;
            if (ModelIdentity.TryParsePath(relative, game, out ModelIdentity identity))
            {
                if (!filesByIdentity.TryGetValue(identity, out List<string>? group))
                {
                    group = [];
                    filesByIdentity[identity] = group;
                }

                group.Add(file);
            }
            else
            {
                unparsedFiles.Add(file);
            }
        }

        // 先把可解析的 change 预解析成步骤表；未解析文件只在建表时完整扫描一次，
        // 按「第一个能改写它的 change」分组，后续 change 只处理自己的候选，不再每轮遍历全量未解析集合。
        // 兼容性校验仍留到主循环按 change 顺序执行，保持「先完成前序 change，再报告非法身份」的原语义。
        (ModelIdentity From, ModelIdentity To)?[] parsedChanges = new (ModelIdentity From, ModelIdentity To)?[changes.Count];
        bool[] compatibleChanges = new bool[changes.Count];
        for (int i = 0; i < changes.Count; i++)
        {
            if (!ModelIdentity.TryParse(changes[i].From, out ModelIdentity fromIdentity)
                || !ModelIdentity.TryParse(changes[i].To, out ModelIdentity toIdentity))
            {
                continue;
            }

            parsedChanges[i] = (fromIdentity, toIdentity);
            try
            {
                ModelIdRewriter.ValidateIdentityPair(fromIdentity, toIdentity);
                compatibleChanges[i] = true;
            }
            catch (ArgumentException)
            {
                // 预分组阶段先跳过；主循环走到该 change 时仍会按原行为抛出。
            }
        }

        List<string>?[] unparsedByChange = new List<string>?[changes.Count];
        foreach (string file in unparsedFiles)
        {
            string relative = relativeByFull[file];
            int step = FindFirstRewriteStep(parsedChanges, compatibleChanges, 0, relative);
            if (step >= 0)
            {
                (unparsedByChange[step] ??= []).Add(file);
            }
        }

        for (int i = 0; i < changes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            (string from, string to) = changes[i];
            if (parsedChanges[i] is not { } changeStep)
            {
                warnings.Add($"跳过无法识别的模型身份：{from} → {to}");
                continue;
            }

            ModelIdentity fromIdentity = changeStep.From;
            ModelIdentity toIdentity = changeStep.To;
            ModelIdRewriter.ValidateIdentityPair(fromIdentity, toIdentity);

            // 散装文件：先全量预检（目标已存在时绝不静默覆盖），再批量移动；失败逆序回滚本批。
            List<(string Source, string Target)> moves = [];
            if (filesByIdentity.TryGetValue(fromIdentity, out List<string>? identityFiles))
            {
                AddMoves(identityFiles);
            }

            if (unparsedByChange[i] is { } unparsedCandidates)
            {
                AddMoves(unparsedCandidates);
            }

            void AddMoves(IEnumerable<string> candidates)
            {
                foreach (string file in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    // 前一个 change 可能已经移动同一路径（链式改写）：查不到当前相对路径就跳过，
                    // 不能对已失效快照直接索引。
                    if (!relativeByFull.TryGetValue(file, out string? relative))
                    {
                        continue;
                    }

                    if (!ModelIdRewriter.TryRewriteIdentityPath(relative, fromIdentity, toIdentity, out string rewritten))
                    {
                        continue;
                    }

                    string target = Path.Combine(full, rewritten.Replace('/', Path.DirectorySeparatorChar));
                    if (string.Equals(file, target, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (fileSystem.FileExists(target))
                    {
                        warnings.Add($"目标路径已被占用，保留原路径不覆盖：{relative} → {rewritten}");
                        continue;
                    }

                    moves.Add((file, target));
                }
            }

            // 批内两条源路径可能改写后落到同一目标（大小写变体、重复记录、不同源目录折叠）。
            // 磁盘预检对「目标尚不存在」的批内冲突无效：按目标分组，整组保留原路径并提示，
            // 避免第二个 MoveFile 抛 IOException 后整批回滚。
            moves = ExcludeDuplicateTargets(moves, path => ToRelative(full, path), warnings);

            List<(string Source, string Target)> moved = [];
            try
            {
                foreach ((string source, string target) in moves)
                {
                    token.ThrowIfCancellationRequested();
                    fileSystem.CreateDirectory(Path.GetDirectoryName(target)!);
                    fileSystem.MoveFile(source, target, overwrite: false);
                    moved.Add((source, target));
                    filesMoved++;
                }
            }
            catch (OperationCanceledException)
            {
                RollbackMoves(moved, warnings, fileSystem);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                int completedFiles = filesMoved - moved.Count;
                RollbackMoves(moved, warnings, fileSystem);
                throw new IOException(
                    $"散装模型改名失败，已尝试回滚本批移动的文件；本批此前已完成 {done} 个 change、"
                    + $"{completedFiles} 个文件，其余未处理。" + Environment.NewLine + ex.Message,
                    ex);
            }

            done++;
            progress?.Report((done, changes.Count));

            // 后续 change 需要基于改名后的实际路径匹配：把本次移动的文件迁到目标身份分组。
            if (moved.Count > 0)
            {
                foreach ((string source, string target) in moved)
                {
                    if (filesByIdentity.TryGetValue(fromIdentity, out List<string>? sourceGroup))
                    {
                        sourceGroup.Remove(source);
                    }

                    relativeByFull.Remove(source);
                    string targetRelative = ToRelative(full, target);
                    relativeByFull[target] = targetRelative;
                    if (ModelIdentity.TryParsePath(targetRelative, game, out ModelIdentity targetIdentity))
                    {
                        if (!filesByIdentity.TryGetValue(targetIdentity, out List<string>? targetGroup))
                        {
                            targetGroup = [];
                            filesByIdentity[targetIdentity] = targetGroup;
                        }

                        targetGroup.Add(target);
                    }
                    else
                    {
                        int next = FindFirstRewriteStep(parsedChanges, compatibleChanges, i + 1, targetRelative);
                        if (next >= 0)
                        {
                            (unparsedByChange[next] ??= []).Add(target);
                        }
                    }
                }
            }
        }

        // 根层 pak：按 pak 聚合全部 change，只读/写一次 TOC。
        if (rewritePaks)
        {
            paksScanned = topLevelPaks.Length;
            foreach (string pakPath in topLevelPaks)
            {
                token.ThrowIfCancellationRequested();
                string pak = Path.GetFileName(pakPath);
                try
                {
                    ModelRewriteReport report = pakService.RewritePaths(pakPath, changes, index, backup: true);
                    entriesChanged += report.EntriesChanged;
                    warnings.AddRange(report.Warnings);
                }
                catch (NotSupportedException ex)
                {
                    // TOC 加密：记录原因并继续；不会写坏文件。
                    unsupported.Add($"{pak}：{ex.Message}");
                }
                catch (FileNotFoundException ex)
                {
                    warnings.Add($"{pak}：{ex.Message}");
                }
                catch (InvalidDataException ex)
                {
                    warnings.Add($"{pak} 不是有效的 pak：{ex.Message}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    warnings.Add($"{pak} 改写失败：{ex.Message}");
                }
            }
        }

        if (filesMoved > 0 || entriesChanged > 0)
        {
            DirectoryPruner.DeleteEmptyDirectories(full, fileSystem);
        }

        return new ModelRewriteSummary(
            paksScanned,
            entriesChanged,
            filesMoved,
            [.. warnings.Distinct(StringComparer.Ordinal)],
            [.. unsupported.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>剔除「批内多条源路径映射到同一目标」的移动项：整组保留原路径并记录 warning。</summary>
    /// <param name="moves">预检通过的移动项（源/目标绝对路径）。</param>
    /// <param name="describe">把绝对路径转成提示里的相对路径。</param>
    /// <param name="warnings">收集冲突提示。</param>
    /// <returns>可以安全执行的移动项。</returns>
    internal static List<(string Source, string Target)> ExcludeDuplicateTargets(
        IReadOnlyList<(string Source, string Target)> moves,
        Func<string, string> describe,
        ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(warnings);

        Dictionary<string, int> targetCounts = new(StringComparer.OrdinalIgnoreCase);
        foreach ((_, string target) in moves)
        {
            targetCounts[target] = targetCounts.GetValueOrDefault(target) + 1;
        }

        if (!targetCounts.Values.Any(count => count > 1))
        {
            return [.. moves];
        }

        List<(string Source, string Target)> accepted = new(moves.Count);
        foreach ((string source, string target) in moves)
        {
            if (targetCounts[target] > 1)
            {
                warnings.Add(
                    "目标路径与本次另一文件冲突，保留原路径："
                    + describe(source) + " → " + describe(target));
                continue;
            }

            accepted.Add((source, target));
        }

        return accepted;
    }

    /// <summary>从指定下标开始，找第一个能改写该相对路径的 change。</summary>
    /// <param name="steps">预解析的身份对（无法解析的 change 为 <see langword="null"/>）。</param>
    /// <param name="compatible">该 change 的身份对是否通过兼容性校验（不兼容留给主循环按顺序抛出）。</param>
    /// <param name="start">起始下标。</param>
    /// <param name="relative">待匹配的相对路径。</param>
    /// <returns>命中的 change 下标；没有命中时返回 -1。</returns>
    private static int FindFirstRewriteStep(
        (ModelIdentity From, ModelIdentity To)?[] steps,
        bool[] compatible,
        int start,
        string relative)
    {
        for (int i = start; i < steps.Length; i++)
        {
            if (!compatible[i] || steps[i] is not { } step)
            {
                continue;
            }

            if (ModelIdRewriter.TryRewriteIdentityPath(relative, step.From, step.To, out _))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>逆序回滚本批已移动的文件（取消与失败共用）；单项失败只告警。</summary>
    private static void RollbackMoves(
        List<(string Source, string Target)> moved,
        List<string> warnings,
        IFileSystem fileSystem)
    {
        for (int i = moved.Count - 1; i >= 0; i--)
        {
            try
            {
                if (fileSystem.FileExists(moved[i].Target) && !fileSystem.FileExists(moved[i].Source))
                {
                    fileSystem.MoveFile(moved[i].Target, moved[i].Source, overwrite: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"改名回滚失败：{moved[i].Target}（{ex.Message}）");
            }
        }
    }

    private static string ToRelative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');
}
