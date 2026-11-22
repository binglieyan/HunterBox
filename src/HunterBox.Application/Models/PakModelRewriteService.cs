using HunterBox.Application.Paks;
using HunterBox.Domain.Models;
using HunterBox.Domain.Paks;

namespace HunterBox.Application.Models;

/// <summary>pak 内模型改写结果（只描述路径改写，内容等长替换链路已删除）。</summary>
/// <param name="EntriesScanned">扫描的条目数。</param>
/// <param name="EntriesChanged">被改写的条目数（路径哈希改名条目数）。</param>
/// <param name="Replacements">模型身份替换次数；与 <paramref name="EntriesChanged"/> 相同。</param>
/// <param name="Warnings">未能处理的条目说明（不在路径清单中、TOC 加密等），调用方必须展示/记录，不得静默丢弃。</param>
public sealed record ModelRewriteReport(
    int EntriesScanned,
    int EntriesChanged,
    int Replacements,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 在 MOD的 pak 内改写模型身份。
/// </summary>
/// <remarks>
/// <para>
/// 正确做法（与 pak 条目表重写等价）：<c>RewritePaths</c> 把条目 TOC 里的
/// 8 字节路径哈希改成目标完整模型身份对应的路径哈希，数据区不动。
/// 压缩/加密「条目」也能改名（只动 TOC）；整张 TOC 加密（feature 8/24）时用注入的
/// 重打包实现解密条目表，改哈希后按包内同一密钥块重新加密写回，仍是 O(TOC) 成本。
/// 没有重打包实现时抛 <see cref="NotSupportedException"/>。
/// </para>
/// <para>
/// 输入可以是完整模型身份路径形式（如 <c>ch03/012/003</c>、<c>it10/03/0000</c>，推荐），
/// 或旧的单个 3/4 位 ID（仅兼容旧调用，不会同步 sid/AA/文件名，已不满足完整身份语义）。
/// </para>
/// <para>
/// 旧的内容等长替换链路已删除：它不改变条目路径、对 pak 型 MOD 不生效，且可能破坏资源。
/// </para>
/// </remarks>
public sealed class PakModelRewriteService
{
    /// <summary>单次改写最多收集多少条逐条警告，避免损坏的 pak 撑爆界面。</summary>
    private const int MaxWarnings = 100;

    private readonly IPakArchiveReader _archiveReader;
    private readonly IPakEntryRenamer? _renamer;
    private readonly IPakPathHasher? _pathHasher;
    private readonly IPakRepacker? _repacker;

    /// <summary>装配服务。</summary>
    /// <param name="archiveReader">pak 解析器。</param>
    /// <param name="renamer">条目改名器；不传则无法按路径改名。</param>
    /// <param name="pathHasher">路径哈希实现；不传则无法按路径改名。</param>
    /// <param name="repacker">整包解包/重打包实现；传入后才能解密 TOC 加密的 pak。</param>
    /// <exception cref="ArgumentNullException"><paramref name="archiveReader"/> 为 <see langword="null"/>。</exception>
    public PakModelRewriteService(
        IPakArchiveReader archiveReader,
        IPakEntryRenamer? renamer = null,
        IPakPathHasher? pathHasher = null,
        IPakRepacker? repacker = null)
    {
        ArgumentNullException.ThrowIfNull(archiveReader);

        _archiveReader = archiveReader;
        _renamer = renamer;
        _pathHasher = pathHasher;
        _repacker = repacker;
    }

    /// <summary>读取 pak 条目表；被中断的原地写先用前像恢复，必要时退到整包解析器解密。</summary>
    /// <param name="pakPath">pak 路径。</param>
    /// <param name="warnings">恢复/降级说明的收集目标；可为 <see langword="null"/>。</param>
    /// <returns>解析后的包头与条目。</returns>
    /// <exception cref="InvalidDataException">pak 结构不合法且无法恢复。</exception>
    /// <exception cref="NotSupportedException">pak 不受支持且没有整包解析实现。</exception>
    private PakArchiveInfo ReadArchive(string pakPath, List<string>? warnings)
    {
        try
        {
            // 上次原地写 TOC 被中断时，必须在解析/规划 rename 之前恢复，避免用半写（哈希混合）的 TOC
            // 计算目标条目。
            if (_renamer is IPakTocRecovery pendingRecovery
                && pendingRecovery.TryRecoverPending(pakPath, out string pendingMessage))
            {
                warnings?.Add(pendingMessage);
            }

            // 读取期间持有只读锁，避免与并发改名（原地写 TOC）交错。
            using IDisposable? readLock = (_renamer as IPakTocRecovery)?.AcquireReadLock(pakPath);
            return _archiveReader.Read(pakPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            // 读取失败（结构损坏）时再尝试完整恢复：未提交日志优先，其次用已提交前像。
            if (_renamer is IPakTocRecovery recovery && recovery.TryRecover(pakPath, out string message))
            {
                warnings?.Add(message);
                using IDisposable? retryLock = recovery.AcquireReadLock(pakPath);
                return _archiveReader.Read(pakPath);
            }

            if (_repacker is not null)
            {
                return _repacker.Inspect(pakPath);
            }

            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            // 无权限（目录被同名文件占用、ACL 拒绝等）也给出可操作提示。
            throw new IOException(
                $"无法访问 pak 或它的恢复日志（权限不足）：{pakPath}。"
                + "请检查文件权限，或关闭占用该文件的程序后重试。",
                ex);
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            // 恢复日志读取/写入被占用：把它转成用户能照做的提示；文件不存在保持原异常。
            throw new IOException(
                $"无法读取或恢复 pak 的条目表（可能被游戏/杀毒软件占用）：{pakPath}。"
                + "请关闭占用该文件的程序后重试。",
                ex);
        }
    }

    /// <summary>判断 pak的条目表是否加密（feature 8/24）。</summary>
    /// <param name="archive">解析后的 pak 头。</param>
    /// <returns>TOC 加密时返回 <see langword="true"/>。</returns>
    private static bool IsTocEncrypted(PakArchiveInfo archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return PakFeature.IsTocEncrypted(archive.Feature);
    }

    /// <summary>按完整模型身份改写 pak 内条目路径哈希（明文 TOC 只动 8 字节哈希）。</summary>
    /// <remarks>
    /// 对每个条目用 <paramref name="index"/> 还原路径，再按完整模型身份把 <c>ch0X/fid/sid</c>
    /// 或 <c>itTT/AA/BBBB</c>（含 _prefab 与文件名 token）换成目标身份；变化时只重写 TOC 的前
    /// 8 字节哈希（等价的 pak 条目表重写）。压缩/加密「条目」不影响本操作。
    /// 整张 TOC 加密的 pak（feature 8/24）：有 <c>IPakRepacker</c> 时先用 <c>Inspect</c> 解密条目表，
    /// 再由改名器按包内同一密钥块加密写回；没有重打包实现时抛 <see cref="NotSupportedException"/>。
    /// 改名由改名器原地写条目表，只生成 <c>&lt;pak&gt;.toc.bak</c> 前像（O(TOC)）。
    /// </remarks>
    /// <param name="pakPath">pak 文件绝对路径。</param>
    /// <param name="from">源模型身份路径形式（如 <c>ch03</c>）或旧的三/四位 ID（如 <c>012</c>）。</param>
    /// <param name="to">目标模型身份路径形式或旧 ID（位数与源相同）。</param>
    /// <param name="index">路径索引（hash → 路径），可用内置游戏清单构建。</param>
    /// <param name="backup">是否保留 TOC 前像 <c>&lt;pak&gt;.toc.bak</c>（未提交日志始终生成，用于崩溃恢复）。</param>
    /// <returns>改名统计；<see cref="ModelRewriteReport.EntriesChanged"/> 为改名条目数。</returns>
    /// <exception cref="ArgumentException">身份/ID 非法、解析失败或源/目标类别类型不一致。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="FileNotFoundException">pak 不存在。</exception>
    /// <exception cref="InvalidDataException">pak 结构不合法。</exception>
    /// <exception cref="InvalidOperationException">未注入改名器或路径哈希实现。</exception>
    /// <exception cref="NotSupportedException">TOC 加密的 pak 未配置整包解析实现（无法解密条目表）。</exception>
    public ModelRewriteReport RewritePaths(
        string pakPath,
        string from,
        string to,
        PakNameIndex index,
        bool backup = true)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (_renamer is null || _pathHasher is null)
        {
            throw new InvalidOperationException("未注入条目改名器或路径哈希实现");
        }

        // 单条只是批量的单元素特例：复用同一套目标哈希占用/同批重复检测，
        // 避免两条重载的安全级别漂移。
        return RewritePaths(pakPath, [(from, to)], index, backup);
    }

    /// <summary>按 pak 聚合一批模型身份改写，只读/写一次 TOC。</summary>
    /// <remarks>
    /// 同一 pak 的多个 change 按顺序叠加到条目路径上，最后只调用一次批量改名器：明文 TOC 原地写
    /// 条目表（成本 O(TOC)，前像 <c>.toc.bak</c>），不再有「change 数 × pak 大小」次整包复制。
    /// 整张 TOC 加密（feature 8/24）先解密条目表，多个 change 同样一次写回。
    /// </remarks>
    /// <param name="pakPath">pak 文件绝对路径。</param>
    /// <param name="changes">源/目标身份（或旧 ID）对。</param>
    /// <param name="index">路径索引（hash → 路径）。</param>
    /// <param name="backup">是否保留 TOC 前像 <c>&lt;pak&gt;.toc.bak</c>（未提交日志始终生成，用于崩溃恢复）。</param>
    /// <returns>改名统计与警告。</returns>
    /// <exception cref="ArgumentException">任一身份/ID 非法或类别不一致。</exception>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    /// <exception cref="InvalidOperationException">未注入改名器或路径哈希实现。</exception>
    /// <exception cref="FileNotFoundException">pak 不存在。</exception>
    public ModelRewriteReport RewritePaths(
        string pakPath,
        IReadOnlyList<(string From, string To)> changes,
        PakNameIndex index,
        bool backup = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(index);
        if (_renamer is null || _pathHasher is null)
        {
            throw new InvalidOperationException("未注入条目改名器或路径哈希实现");
        }

        if (changes.Count == 0)
        {
            return new ModelRewriteReport(0, 0, 0, []);
        }

        // 同一源身份只能有一个目标：对话框按「身份 + pak」出行，同身份跨 pak 选不同目标时
        // 每个 pak 都会收到全部 change，最终结果与用户逐 pak 的选择不一致。
        Dictionary<string, string> targetBySource = new(StringComparer.OrdinalIgnoreCase);
        List<(string From, string To)> distinct = new(changes.Count);
        HashSet<string> seenPairs = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string from, string to) in changes)
        {
            if (targetBySource.TryGetValue(from, out string? existing)
                && !string.Equals(existing, to, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"同一模型身份不能在一次改写中指定多个目标：{from} → {existing} / {to}。"
                    + "同一模型出现在多个 pak 时，请为它们选择相同的目标后重试。",
                    nameof(changes));
            }

            targetBySource[from] = to;
            if (seenPairs.Add(from + "\u0000" + to))
            {
                distinct.Add((from, to));
            }
        }

        List<ChangeSpec> specs = new(distinct.Count);
        foreach ((string from, string to) in distinct)
        {
            specs.Add(ParseChange(from, to));
        }

        List<string> warnings = [];
        PakArchiveInfo archive = ReadArchive(pakPath, warnings);

        if (IsTocEncrypted(archive))
        {
            if (_repacker is null)
            {
                throw new NotSupportedException(
                    $"pak 的条目表已加密（feature={archive.Feature}），当前未配置整包解析能力；"
                    + "请改用未加密的 MOD pak。");
            }

            // 加密 TOC 的条目字段是密文，先解密再聚合所有 change；改名器用同一密钥块加密写回。
            archive = _repacker.Inspect(pakPath);
        }

        int suppressedWarnings = 0;
        int renamed = 0;
        List<PakEntryRename> pendingRenames = [];
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            PakEntryInfo entry = archive.Entries[i];
            string? path = index.TryResolve(entry.Hash);
            if (path is null)
            {
                if (warnings.Count < MaxWarnings)
                {
                    warnings.Add($"条目 hash={entry.Hash:X16} 不在路径清单中，已跳过");
                }
                else
                {
                    suppressedWarnings++;
                }

                continue;
            }

            string current = path;
            bool changed = false;
            foreach (ChangeSpec spec in specs)
            {
                string rewritten;
                bool stepChanged = spec.IdentityMode
                    ? ModelIdRewriter.TryRewriteIdentityPath(
                        current, spec.FromIdentity!, spec.ToIdentity!, out rewritten)
                    : ModelIdRewriter.TryRewritePath(current, spec.From, spec.To, out rewritten);
                if (stepChanged)
                {
                    current = rewritten;
                    changed = true;
                }
            }

            if (!changed)
            {
                continue;
            }

            ulong newHash = _pathHasher.HashPath(current.ToLowerInvariant());
            if (warnings.Count < MaxWarnings && index.TryResolve(newHash) is null)
            {
                warnings.Add($"改写后的路径不在游戏路径清单中：{current}（目标模型可能不存在）");
            }

            pendingRenames.Add(new PakEntryRename(i, newHash));
            renamed++;
        }

        // 目标哈希已被 pak 内未参与改写的条目占用：写入会产生重复条目，跳过并告警。
        // 被跳过的条目会保留自己的旧哈希，可能继续挡住别的条目的目标（A→B 的旧路径、B→C 且 C 已存在），
        // 所以占用集合与同批去重都要反复重算到没有新冲突为止。
        if (pendingRenames.Count > 0)
        {
            int conflicts = 0;
            int duplicates = 0;

            // 一次建全表哈希计数；每轮只处理剩余条目，避免反复扫描整个条目表（P-05）。
            Dictionary<ulong, int> hashCounts = [];
            foreach (PakEntryInfo entry in archive.Entries)
            {
                hashCounts[entry.Hash] = hashCounts.GetValueOrDefault(entry.Hash) + 1;
            }

            List<PakEntryRename> remaining = pendingRenames;
            while (remaining.Count > 0)
            {
                // 被移走条目自己的旧哈希不再占用目标位置：统计各旧哈希的移动数量。
                Dictionary<ulong, int> movingOldHashes = [];
                foreach (PakEntryRename rename in remaining)
                {
                    ulong oldHash = archive.Entries[rename.EntryIndex].Hash;
                    movingOldHashes[oldHash] = movingOldHashes.GetValueOrDefault(oldHash) + 1;
                }

                List<PakEntryRename> next = new(remaining.Count);
                int rejected = 0;
                foreach (PakEntryRename rename in remaining)
                {
                    int occupied = hashCounts.GetValueOrDefault(rename.NewHash)
                        - movingOldHashes.GetValueOrDefault(rename.NewHash);
                    if (occupied > 0)
                    {
                        rejected++;
                        continue;
                    }

                    next.Add(rename);
                }

                if (rejected > 0)
                {
                    conflicts += rejected;
                    remaining = next;
                    continue;
                }

                // 多个条目改写后落到同一路径哈希时，TOC 会出现重复条目；保留第一个。
                List<PakEntryRename> unique = new(next.Count);
                HashSet<ulong> targetHashes = [];
                int duplicateCount = 0;
                foreach (PakEntryRename rename in next)
                {
                    if (!targetHashes.Add(rename.NewHash))
                    {
                        duplicateCount++;
                        continue;
                    }

                    unique.Add(rename);
                }

                if (duplicateCount == 0)
                {
                    break;
                }

                duplicates += duplicateCount;
                remaining = unique;
            }

            if (conflicts > 0)
            {
                warnings.Add(
                    $"有 {conflicts} 个条目改写后的路径已被 pak 内既有条目占用，已跳过；"
                    + "请确认是否把两个模型改成了同一身份");
            }

            if (duplicates > 0)
            {
                warnings.Add(
                    $"有 {duplicates} 个条目改写后与其它条目落到同一路径哈希，已跳过重复项；"
                    + "请确认是否把多个模型改成了同一身份");
            }

            if (conflicts > 0 || duplicates > 0)
            {
                pendingRenames = remaining;
                renamed = remaining.Count;
            }
        }

        if (pendingRenames.Count > 0)
        {
            if (_renamer is IBatchPakEntryRenamer batchRenamer)
            {
                batchRenamer.RenameEntries(pakPath, pendingRenames, backup);
            }
            else
            {
                foreach (PakEntryRename rename in pendingRenames)
                {
                    _renamer.RenameEntry(pakPath, rename.EntryIndex, rename.NewHash, backup);
                }
            }
        }

        if (suppressedWarnings > 0)
        {
            warnings.Add($"另有 {suppressedWarnings} 个条目不在路径清单中，已跳过");
        }

        return new ModelRewriteReport(archive.Entries.Count, renamed, renamed, warnings);
    }

    /// <summary>解析并校验一条 change（完整身份或旧 ID）。</summary>
    private static ChangeSpec ParseChange(string from, string to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (ModelIdentity.TryParse(from, out ModelIdentity fromIdentity))
        {
            if (!ModelIdentity.TryParse(to, out ModelIdentity toIdentity))
            {
                throw new ArgumentException($"目标模型身份不合法: {to}（应为 ch03/012/003、ch05/003/0001 或 it10/03/0000）");
            }

            ModelIdRewriter.ValidateIdentityPair(fromIdentity, toIdentity);
            return new ChangeSpec(true, from, to, fromIdentity, toIdentity);
        }

        if (!ModelIdRewriter.IsValidId(from) || !ModelIdRewriter.IsValidId(to))
        {
            throw new ArgumentException(
                $"模型身份不合法: {from} -> {to}（应为完整身份 ch03/012/003，或旧的三/四位 ID）");
        }

        if (from.Length != to.Length)
        {
            throw new ArgumentException($"源/目标模型 ID 位数必须一致: {from} -> {to}");
        }

        return new ChangeSpec(false, from, to, null, null);
    }

    /// <summary>一条已解析的模型改写。</summary>
    private readonly record struct ChangeSpec(
        bool IdentityMode,
        string From,
        string To,
        ModelIdentity? FromIdentity,
        ModelIdentity? ToIdentity);

}
