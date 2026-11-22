using System.Diagnostics;
using System.Text;
using HunterBox.Application.Common;
using HunterBox.Domain.Archives;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

using HunterBox.Infrastructure.IO;
namespace HunterBox.Infrastructure.Archives;

/// <summary>
/// 基于 SharpCompress的解压实现：按魔数探测 zip / 7z / rar / tar / gz 等格式。
/// </summary>
/// <remarks>
/// <para>支持：按魔数探测格式（不依赖扩展名）、GBK(936) 文件名编码、
/// 按文件名「解压码/密码」猜密码、递归解压嵌套压缩包、进度回调与取消。</para>
/// <para>每个条目都会先经过 <see cref="BoxPaths.NormalizeRelative"/> 与
/// <see cref="BoxPaths.ResolveInside"/>：越界/非法条目转成 <see cref="InvalidDataException"/>，不做静默跳过；
/// 底层读写失败保持原异常类型（<see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>）。</para>
/// </remarks>
public sealed class ArchiveExtractor : IArchiveExtractor
{
    private const int BufferSize = FileCopyBuffer.Size;

    private static readonly Lazy<Encoding> ArchiveDefaultEncoding = new(CreateArchiveEncoding);

    private readonly IAppLogger _logger;

    /// <summary>装配解压器。</summary>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    public ArchiveExtractor(IAppLogger? logger = null)
    {
        _logger = logger ?? AppLogAdapter.Instance;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">路径为空或空白。</exception>
    /// <exception cref="FileNotFoundException">压缩包不存在。</exception>
    /// <exception cref="InvalidDataException">压缩包损坏/加密密码错误/条目路径非法或越界/条目大小写冲突。</exception>
    /// <exception cref="NotSupportedException">压缩格式不受支持。</exception>
    /// <exception cref="OperationCanceledException">取消令牌被触发。</exception>
    /// <exception cref="IOException">目标目录或文件无法创建/写入。</exception>
    /// <exception cref="UnauthorizedAccessException">没有读取压缩包或写入目标目录的权限。</exception>
    public IReadOnlyList<string> Extract(
        string archivePath,
        string destinationDirectory,
        ArchiveExtractionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("压缩包不存在", archivePath);
        }

        options ??= new ArchiveExtractionOptions();
        Directory.CreateDirectory(destinationDirectory);

        string? password = options.Password
            ?? ArchiveFormatDetector.GuessPasswordFromFileName(Path.GetFileName(archivePath));

        // 每次解压使用独立的隔离子目录：深度 ≥ 2 的嵌套包也必须移出产物树，
        // 且结束清理时只删本次子目录，绝不碰预先存在的隔离内容。
        string quarantineBase =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory)) + ".nested-kept";
        string quarantineRoot = Path.Combine(quarantineBase, Guid.NewGuid().ToString("N"));
        bool createdQuarantineBase = !Directory.Exists(quarantineBase);

        List<string> extracted;
        ExtractionBudget budget = new(options.MaxTotalBytes);
        try
        {
            extracted = ExtractArchive(
                archivePath, destinationDirectory, password, options, depth: 0, quarantineRoot, budget, _logger);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"解压已取消：{archivePath} → {destinationDirectory}");
            throw;
        }
        catch (Exception ex) when (IsPasswordRelated(ex))
        {
            string message = BuildPasswordErrorMessage(archivePath, password, ex);
            _logger.Error(message, ex);
            throw new InvalidDataException(message, ex);
        }
        catch (InvalidFormatException ex)
        {
            string message = $"压缩包已损坏或格式不支持：{Path.GetFileName(archivePath)}（{ex.Message}）";
            _logger.Error($"解压失败：{archivePath}（{message}）", ex);
            throw new InvalidDataException(message, ex);
        }
        catch (Exception ex)
        {
            // 失败路径留下可诊断记录（含压缩包、目标目录与完整异常）。
            _logger.Error($"解压失败：{archivePath} → {destinationDirectory}", ex);
            throw;
        }
        finally
        {
            // 只清理本次运行的隔离子目录；父目录仅在「本次创建且为空」时删除，
            // 预存在的空目录/其它来源文件一律保留。
            TryDeleteQuarantine(quarantineRoot, _logger);
            if (createdQuarantineBase)
            {
                TryDeleteEmptyDirectory(quarantineBase);
            }
        }

        // 直接返回解压过程中收集的清单，不再整目录枚举一遍（P-21）；
        // 排序与去重保持旧输出稳定（嵌套包合并后可能与已有条目同路径）。
        extracted.Sort(StringComparer.Ordinal);
        int unique = 0;
        for (int index = 0; index < extracted.Count; index++)
        {
            if (index == 0 || !string.Equals(extracted[index], extracted[index - 1], StringComparison.Ordinal))
            {
                extracted[unique++] = extracted[index];
            }
        }

        extracted.RemoveRange(unique, extracted.Count - unique);
        _logger.Info($"解压完成：{archivePath} → {destinationDirectory}（{extracted.Count} 个文件）");
        return extracted;
    }

    private static string BuildPasswordErrorMessage(string archivePath, string? password, Exception ex)
    {
        string hint = password is null
            ? "压缩包已加密，但无法从文件名推断密码（文件名含「解压码/密码」时可自动尝试）。"
            : $"压缩包已加密，自动尝试的密码「{password}」不正确。";
        return $"解压失败：{hint}请手动解压后把文件夹拖入HunterBox，或把文件名改成「xxx密码<密码>.zip」重试。"
            + $"（{Path.GetFileName(archivePath)}：{ex.Message}）";
    }

    private static bool IsPasswordRelated(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            string typeName = current.GetType().Name;
            if (typeName.Contains("Cryptographic", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string message = current.Message;
            if (message.Contains("password", StringComparison.OrdinalIgnoreCase)
                || message.Contains("密码", StringComparison.Ordinal)
                || message.Contains("encrypt", StringComparison.OrdinalIgnoreCase)
                || message.Contains("cryptographic", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current is InvalidFormatException && message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>解压一个归档到目标目录，返回本次解出的文件相对路径（小写、正斜杠）。</summary>
    /// <param name="archivePath">压缩包路径。</param>
    /// <param name="destinationDirectory">目标目录。</param>
    /// <param name="password">解压密码。</param>
    /// <param name="options">解压选项。</param>
    /// <param name="depth">当前嵌套深度。</param>
    /// <param name="quarantineRoot">本次运行的隔离目录。</param>
    /// <param name="budget">整次解压共享的字节预算（含嵌套包）。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志。</param>
    /// <returns>解出的文件相对路径。</returns>
    private static List<string> ExtractArchive(
        string archivePath,
        string destinationDirectory,
        string? password,
        ArchiveExtractionOptions options,
        int depth,
        string quarantineRoot,
        ExtractionBudget budget,
        IAppLogger? logger = null)
    {
        List<string> extracted = [];

        // 根目录只规范化一次：条目循环里复用，避免每条目两次 GetFullPath（P-07/P-08）。
        string rootFull = Path.GetFullPath(destinationDirectory);
        using (IArchive archive = Open(archivePath, password))
        {
            // SharpCompress 的 Entries 对 7z/rar 可能触发中央目录/头部解析；
            // 物化一次供「总量统计 + 实际解压」复用，避免同一归档解析两遍。
            List<IArchiveEntry> entries = [.. archive.Entries];

            long total = 0;
            foreach (IArchiveEntry entry in entries)
            {
                if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                long size = Math.Max(entry.Size, 0);
                if (size > options.MaxEntryBytes)
                {
                    throw new InvalidDataException(
                        $"压缩包条目超过单文件上限（{options.MaxEntryBytes} 字节）：{entry.Key}");
                }

                total += size;
                if (budget.Used + total > options.MaxTotalBytes)
                {
                    // 压缩炸弹/损坏包防御：解压前按声明总量拒绝，嵌套包共享同一预算（C-13/）。
                    budget.MarkExceeded();
                    throw new InvalidDataException(
                        $"压缩包解压总量超过上限（{options.MaxTotalBytes} 字节）：{Path.GetFileName(archivePath)}");
                }
            }

            long done = 0;
            HashSet<string> extractedKeys = new(StringComparer.OrdinalIgnoreCase);

            // 父目录只创建一次：几万个小文件同目录时，避免每个文件都调一次 CreateDirectory（P-07）。
            HashSet<string> createdDirectories = new(StringComparer.OrdinalIgnoreCase);
            ThrottledProgress progress = new(options.Progress);

            // 整个压缩包共用一个分块缓冲，而不是每个条目都 new 一次（P-05）。
            byte[] buffer = new byte[BufferSize];
            foreach (IArchiveEntry entry in entries)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                string relative = SafeKey(entry.Key, rootFull);
                if (!extractedKeys.Add(relative))
                {
                    // 大小写不同/重复的条目会映射到同一个落盘路径；静默覆盖等于丢文件。
                    throw new InvalidDataException($"压缩包内存在大小写冲突或重复条目，无法安全解压：{entry.Key}");
                }

                // relative 已规范化且不越界，这里只需拼出绝对路径，不再重复规范化（P-07）。
                string target = BoxPaths.ResolveInsideNormalized(rootFull, relative);
                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent) && createdDirectories.Add(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                using (Stream stream = entry.OpenEntryStream())
                using (FileStream file = File.Create(target))
                {
                    done = CopyWithProgress(
                        stream,
                        file,
                        entry.Key,
                        total,
                        done,
                        buffer,
                        progress,
                        budget,
                        options.CancellationToken);
                }

                extracted.Add(relative);
            }

            // total=0（条目大小未知/全为空文件）时不会自然到达 100%，补一次完成事件。
            progress.Complete();
        }

        if (options.ExtractNestedArchives && depth < options.MaxNestedDepth)
        {
            extracted = ExtractNestedArchives(
                destinationDirectory, extracted, options, depth, quarantineRoot, budget, logger);
        }

        return extracted;
    }

    private static long CopyWithProgress(
        Stream source,
        Stream destination,
        string entryKey,
        long total,
        long done,
        byte[] buffer,
        ThrottledProgress progress,
        ExtractionBudget budget,
        CancellationToken cancellationToken)
    {
        // 先登记条目（即使 0 字节也要让完成事件知道最后一个条目），再由节流器决定是否派发。
        progress.Report(entryKey, done, total);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            done += read;
            // 声明大小可能与实际不符：按实际写入量扣减共享预算，嵌套包合计超限即中止（C-13/）。
            budget.Consume(read);
            progress.Report(entryKey, done, total);
        }

        return done;
    }

    /// <summary>整次解压（含嵌套包）共享的写入字节预算。</summary>
    internal sealed class ExtractionBudget
    {
        private readonly long _maxTotalBytes;
        private long _used;

        public ExtractionBudget(long maxTotalBytes) => _maxTotalBytes = maxTotalBytes;

        /// <summary>已写入的字节数（含嵌套包）。</summary>
        public long Used => _used;

        /// <summary>
        /// 是否已触发总量上限。<see cref="InvalidDataException"/> 是密封类型无法细分异常，
        /// 用该标志让嵌套解压把「预算耗尽」与「嵌套包损坏」区分开。
        /// </summary>
        public bool Exceeded { get; private set; }

        /// <summary>标记预算已耗尽（声明总量预检超限时使用）。</summary>
        public void MarkExceeded() => Exceeded = true;

        /// <summary>扣减实际写入字节；累计超过上限时标记并抛 <see cref="InvalidDataException"/>。</summary>
        /// <param name="bytes">本次写入的字节数。</param>
        public void Consume(long bytes)
        {
            _used += bytes;
            if (_used > _maxTotalBytes)
            {
                Exceeded = true;
                throw new InvalidDataException($"压缩包解压总量超过上限（{_maxTotalBytes} 字节）");
            }
        }
    }

    /// <summary>
    /// 解压进度节流器：按「百分比变化」派发，避免 1GB 包按 80KB 分块产生上万次 UI 派发（P-05）。
    /// 同一条目百分比不变时不派发（结束时的完成事件单独补发）。
    /// </summary>
    private sealed class ThrottledProgress
    {
        /// <summary>两次上报的最小间隔（毫秒）。</summary>
        private const double MinIntervalMilliseconds = 100;

        private readonly IProgress<ArchiveProgress>? _progress;
        private long _lastReportTimestamp;
        private int _lastPercent = -1;
        private string? _lastEntryKey;
        private long _lastDone;
        private long _lastTotal;

        public ThrottledProgress(IProgress<ArchiveProgress>? progress) => _progress = progress;

        /// <summary>解压结束时补一次完成事件（仅未知总量/尚未上报 100% 时）。</summary>
        public void Complete()
        {
            if (_progress is null || _lastEntryKey is null || _lastPercent >= 100)
            {
                return;
            }

            _lastPercent = 100;
            _progress.Report(new ArchiveProgress(
                _lastEntryKey,
                _lastDone,
                _lastTotal > 0 ? _lastTotal : null,
                100));
        }

        /// <summary>按节流规则上报一次进度。</summary>
        /// <param name="entryKey">当前条目。</param>
        /// <param name="done">累计已传输字节。</param>
        /// <param name="total">总字节（未知为 0）。</param>
        public void Report(string entryKey, long done, long total)
        {
            if (_progress is null)
            {
                return;
            }

            int percent = total > 0
                ? (int)Math.Clamp(done * 100 / total, 0, 100)
                : 0;
            _lastDone = done;
            _lastTotal = total;
            long now = Stopwatch.GetTimestamp();
            double elapsedMilliseconds = (now - _lastReportTimestamp) * 1000.0 / Stopwatch.Frequency;
            bool entryChanged = !string.Equals(entryKey, _lastEntryKey, StringComparison.Ordinal);
            bool percentChanged = percent != _lastPercent;
            // 只在第一次到达 100% 时免闸门；total 低估导致后续仍为 100% 时不能绕过节流。
            bool completionDue = percent >= 100 && _lastPercent < 100;

            // 切换条目也必须走时间闸门：否则数万小文件会派发数万次 UI 回调（N-12）。
            // 同一文件内的百分比进度仍实时派发；完成事件例外，保证小包也能收到最终进度。
            if (entryChanged && !completionDue && elapsedMilliseconds < MinIntervalMilliseconds)
            {
                return;
            }

            if (!entryChanged && !percentChanged && !completionDue)
            {
                return;
            }

            _lastEntryKey = entryKey;
            _lastPercent = percent;
            _lastReportTimestamp = now;
            _progress.Report(new ArchiveProgress(entryKey, done, total > 0 ? total : null, percent));
        }
    }

    /// <summary>递归解压包内压缩包到「&lt;文件名&gt;files」目录并把原包移出产物目录（安装包解析流程）。</summary>
    /// <param name="root">产物根目录。</param>
    /// <param name="extracted">当前已解出的相对路径清单。</param>
    /// <param name="options">解压选项。</param>
    /// <param name="depth">当前嵌套深度。</param>
    /// <param name="quarantineRoot">本次运行的隔离目录。</param>
    /// <param name="budget">共享字节预算；为 <see langword="null"/> 时按 <paramref name="options"/> 新建（独立调用）。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志。</param>
    /// <returns>移出嵌套包后的产物清单。</returns>
    internal static List<string> ExtractNestedArchives(
        string root,
        List<string> extracted,
        ArchiveExtractionOptions options,
        int depth,
        string quarantineRoot,
        ExtractionBudget? budget = null,
        IAppLogger? logger = null)
    {
        logger ??= AppLogAdapter.Instance;
        budget ??= new ExtractionBudget(options.MaxTotalBytes);

        // 产物清单由「普通条目 + 各嵌套包解出的文件」重建：原包已移出产物目录，
        // 不能再把它的路径返回给调用方（P-21）。
        List<string> result = new(extracted.Count);
        byte[] probeBuffer = new byte[512];
        for (int index = 0; index < extracted.Count; index++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            string relative = extracted[index];
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            // P-12b：复用探测缓冲区，且不再先 File.Exists 再打开（缺失文件按「非压缩包」处理，与原语义一致）。
            if (!LooksLikeArchive(full, probeBuffer))
            {
                result.Add(relative);
                continue;
            }

            string target = full + "files";
            // 始终先解到同级暂存目录：解压失败/取消只清理暂存，目标目录（即使预先存在）不会被混入半成品；
            // 合并阶段若遇到 IO 失败，目标可能部分更新，下方会显式告警。
            string staging = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(staging);
                string? password = options.Password
                    ?? ArchiveFormatDetector.GuessPasswordFromFileName(Path.GetFileName(full));
                List<string> nested;
                try
                {
                    nested = ExtractArchive(
                        full, staging, password, options, depth + 1, quarantineRoot, budget, logger);
                }
                catch (InvalidDataException) when (budget.Exceeded)
                {
                    // 共享预算耗尽必须中止整次解压，不能当成嵌套包损坏跳过。
                    throw;
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArchiveException or ArchiveOperationException)
                {
                    // 内容损坏/加密：不因一个嵌套包让整个安装失败，但原包必须移出产物目录，
                    // 否则会被当成普通 MOD 文件安装。删除或隔离由 RemoveNestedArchive 决定。
                    logger.Warn($"嵌套压缩包解压失败，已移出产物目录（删除或隔离）：{full}（{ex.Message}）");
                    RemoveNestedArchive(full, quarantineRoot);
                    continue;
                }

                // 解压成功后的 IO 错误（磁盘满/权限/占用）不能被当成内容损坏吞掉。
                try
                {
                    MergeOrMoveDirectory(staging, target, options.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    string message = $"嵌套压缩包已解出，但合并到目标失败：{target}（{ex.Message}）";
                    logger.Error(message, ex);
                    RemoveNestedArchive(full, quarantineRoot);
                    // 合并失败目标可能已部分更新，必须让调用方失败，不能静默继续。
                    throw new IOException(
                        message + "；原包已移出产物目录，本次解压中止。请关闭占用程序后重试。",
                        ex);
                }

                // 原包必须从产物目录移除；删除失败时移动到隔离目录。
                RemoveNestedArchive(full, quarantineRoot);

                // 合并后嵌套文件位于「<包名>files/」下；原包路径不再出现在产物清单里。
                string prefix = relative + "files/";
                foreach (string nestedRelative in nested)
                {
                    result.Add(prefix + nestedRelative);
                }
            }
            finally
            {
                SystemFileSystem.Instance.TryDeleteDirectory(staging);
            }
        }

        return result;
    }

    /// <summary>
    /// 常见非压缩产物扩展名：只用于跳过魔数探测，避免每个纹理/模型文件都打开一次（P-09）；
    /// 不在此列（含无扩展名）的文件仍按内容识别。
    /// </summary>
    private static readonly HashSet<string> NonArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tex", ".dds", ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif",
        ".mesh", ".mod3", ".mdf2", ".mot", ".motlist", ".user",
        ".ini", ".json", ".xml", ".txt", ".md", ".lua", ".dll", ".pak",
        ".cfg", ".log", ".csv", ".yaml", ".yml",
    };

    /// <summary>判断产物是否为嵌套压缩包：白名单/未知扩展名按内容识别，已知非压缩扩展名直接跳过。</summary>
    /// <param name="path">产物文件路径。</param>
    /// <param name="probeBuffer">复用的文件头缓冲区。</param>
    /// <returns>内容可识别为受支持压缩包时返回 <see langword="true"/>。</returns>
    private static bool LooksLikeArchive(string path, byte[] probeBuffer)
    {
        string extension = Path.GetExtension(path);
        if (extension.Length > 0 && NonArchiveExtensions.Contains(extension))
        {
            return false;
        }

        return ArchiveFileProbe.Probe(path, SystemFileSystem.Instance, probeBuffer).Format
            != ArchiveFormat.Unknown;
    }

    /// <summary>把暂存目录整体换入目标；目标已存在时按文件合并（同名覆盖）。</summary>
    /// <param name="source">已完整解出的暂存目录。</param>
    /// <param name="destination">最终目标目录。</param>
    /// <param name="cancellationToken">取消令牌；每 64 个文件检查一次。</param>
    internal static void MergeOrMoveDirectory(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(destination))
        {
            Directory.Move(source, destination);
            return;
        }

        // 先物化清单再移动，避免边枚举边修改源目录导致条目被跳过。
        string[] files = [.. Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)];
        for (int index = 0; index < files.Length; index++)
        {
            if ((index & 0x3F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string file = files[index];
            string relative = Path.GetRelativePath(source, file);
            string targetFile = Path.Combine(destination, relative);
            string? parent = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Move(file, targetFile, overwrite: true);
        }
    }

    /// <summary>把已解压的嵌套包移入本次运行的隔离目录；移动失败时回退删除，仍失败则中止安装。</summary>
    /// <param name="archivePath">已解压完成的嵌套压缩包路径。</param>
    /// <param name="quarantineRoot">本次运行专属的隔离子目录。</param>
    /// <exception cref="IOException">原包既不能移出产物目录也不能删除。</exception>
    internal static void RemoveNestedArchive(string archivePath, string quarantineRoot)
    {
        Exception? moveError;
        try
        {
            Directory.CreateDirectory(quarantineRoot);
            string target = Path.Combine(
                quarantineRoot,
                Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(archivePath));
            File.Move(archivePath, target);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            moveError = ex;
        }

        try
        {
            File.Delete(archivePath);
        }
        catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"嵌套压缩包已解出，但原文件无法移出产物目录或删除：{archivePath}。"
                + "请关闭占用该文件的程序后重试。",
                new AggregateException(moveError, deleteError));
        }
    }

    /// <summary>清理本次解压的隔离子目录（先去掉文件/目录只读属性）；失败只告警，不影响解压结果。</summary>
    /// <param name="quarantineRoot">本次运行专属的隔离子目录。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志。</param>
    internal static void TryDeleteQuarantine(string quarantineRoot, IAppLogger? logger = null)
    {
        if (!Directory.Exists(quarantineRoot))
        {
            return;
        }

        if (!SystemFileSystem.Instance.TryDeleteDirectoryForce(quarantineRoot))
        {
            (logger ?? AppLogAdapter.Instance).Warn($"隔离目录清理失败，请手工检查：{quarantineRoot}");
        }
    }

    /// <summary>只在目录为空时删除它；非空/不存在/无权时安静返回。</summary>
    /// <param name="directory">待删除的空目录。</param>
    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 非空（其它来源的隔离内容）或无权删除时保留，符合 M-01 的「不误删」语义。
        }
    }

    private static IArchive Open(string archivePath, string? password)
    {
        if (!ArchiveFileProbe.IsSupportedArchive(archivePath, SystemFileSystem.Instance))
        {
            throw new NotSupportedException(
                $"不支持的压缩格式（按内容识别，扩展名 {Path.GetExtension(archivePath)}）：请使用 zip / 7z / rar。");
        }

        ReaderOptions readerOptions = new()
        {
            ArchiveEncoding = new ArchiveEncoding
            {
                Default = ArchiveDefaultEncoding.Value,
                Password = Encoding.UTF8,
            },
        };
        if (!string.IsNullOrEmpty(password))
        {
            readerOptions.Password = password;
        }

        return ArchiveFactory.OpenArchive(archivePath, readerOptions);
    }

    private static Encoding CreateArchiveEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(936);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
        catch (NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private static string SafeKey(string key, string destinationRootFull)
    {
        string normalized;
        try
        {
            normalized = BoxPaths.NormalizeRelative(key);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"压缩包条目路径非法: {key}", ex);
        }

        _ = BoxPaths.ResolveInsideNormalized(destinationRootFull, normalized);
        return normalized.ToLowerInvariant();
    }
}
