using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HunterBox.Application.Common;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;

namespace HunterBox.Application.Models;

/// <summary>
/// 模型文本预热规则（A-06）：分类/文本/负缓存判定与缓存签名留在 Application，
/// App 只保留 UI 线程的缓存快照与后台调用编排。
/// </summary>
public static class ModelTextPrefetchService
{
    /// <summary>是否需要为当前 MOD 做模型自动分类。</summary>
    /// <param name="mod">目标 MOD。</param>
    /// <param name="classificationIds">需要重算分类的 MOD 集合；为 <see langword="null"/> 表示按「描述为空」判断。</param>
    /// <returns>需要分类时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> 为 <see langword="null"/>。</exception>
    public static bool ShouldClassify(ModRecord mod, IReadOnlySet<int>? classificationIds)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return classificationIds is null
            ? string.IsNullOrWhiteSpace(mod.Metadata.Description)
            : classificationIds.Contains(mod.Id);
    }

    /// <summary>
    /// 只有「扫描成功且分类成功、但没有识别到模型」才允许写负缓存；
    /// 扫描/分类异常都必须允许下次重试。
    /// </summary>
    /// <param name="classify">本次是否执行了分类。</param>
    /// <param name="scanSucceeded">pak 条目扫描是否成功。</param>
    /// <param name="classifySucceeded">分类是否成功完成。</param>
    /// <param name="classified">是否识别到至少一个模型。</param>
    /// <returns>应写负缓存时返回 <see langword="true"/>。</returns>
    public static bool NeedsNegativeCache(bool classify, bool scanSucceeded, bool classifySucceeded, bool classified) =>
        classify && scanSucceeded && classifySucceeded && !classified;

    /// <summary>
    /// 是否应把「这个 MOD 没有可改模型」写入负缓存：
    /// 由 <see cref="NeedsNegativeCache"/> 与根层 pak 读取状态共同决定，App 只传状态、不做判断。
    /// </summary>
    /// <param name="classify">本次是否执行了分类。</param>
    /// <param name="scanSucceeded">条目扫描是否成功。</param>
    /// <param name="classifySucceeded">分类是否成功完成。</param>
    /// <param name="classified">是否识别到至少一个模型。</param>
    /// <param name="allPaksReadable">全部根层 pak 是否读取成功（空 pak 也算成功）。</param>
    /// <returns>应写负缓存时返回 <see langword="true"/>。</returns>
    public static bool ShouldRecordNoModel(
        bool classify,
        bool scanSucceeded,
        bool classifySucceeded,
        bool classified,
        bool allPaksReadable) =>
        allPaksReadable && NeedsNegativeCache(classify, scanSucceeded, classifySucceeded, classified);

    /// <summary>单条备注文本的字符上限：与字节上限共同约束持久缓存体积。</summary>
    public const int MaxTextLength = 2048;

    /// <summary>单条备注文本的 UTF-8 字节上限：中文等非 ASCII 文本按实际字节提前截断。</summary>
    public const int MaxTextUtf8Bytes = 4096;

    /// <summary>截断标记：超出上限时替换末尾，明确提示文本不完整。</summary>
    private const string TruncationMarker = "…（已截断）";

    /// <summary>截断标记的 UTF-8 字节数（只计算一次）。</summary>
    private static readonly int TruncationMarkerBytes = Encoding.UTF8.GetByteCount(TruncationMarker);

    /// <summary>
    /// 收紧单条备注文本长度：同时限制字符数与 UTF-8 字节数，
    /// 截断点避开代理对（emoji 等不会被截成孤立代理）。
    /// </summary>
    /// <remarks>
    /// 体积论证：单条最多 2048 字符、4096 UTF-8 字节
    /// （约 1365 个中文字符或 2042 个 ASCII 字符）；JSON 最坏转义为 6 字节/字符（控制字符），
    /// 即每条文本不超过约 12KB。内容戳上限 512 字符/1024 字节（最坏约 3KB），
    /// 连同字段开销 5000 条约 77MB，低于缓存文件 128MB 损坏判据。
    /// 孤立代理按替换字符的字节数计，不抛异常；短文本走常数时间上界检查，不扫描全文。
    /// </remarks>
    /// <param name="text">原始文本。</param>
    /// <returns>不超过上限的文本；未超限时原样返回。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 <see langword="null"/>。</exception>
    public static string ClampText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int charBudget = MaxTextLength - TruncationMarker.Length;
        int byteBudget = MaxTextUtf8Bytes - TruncationMarkerBytes;

        // UTF-16 每个 char 最多产生 3 个 UTF-8 字节，该上界满足时无需逐字符扫描。
        if (text.Length <= charBudget && text.Length * 3 <= byteBudget)
        {
            return text;
        }

        int bytes = 0;
        int cut = 0;
        while (cut < text.Length)
        {
            char current = text[cut];
            int charCount = 1;
            int byteCount;
            if (char.IsHighSurrogate(current)
                && cut + 1 < text.Length
                && char.IsLowSurrogate(text[cut + 1]))
            {
                charCount = 2;
                byteCount = 4;
            }
            else if (char.IsSurrogate(current))
            {
                // 孤立代理按替换字符的 3 字节计，绝不能调用会抛异常的 Rune.GetRuneAt。
                byteCount = 3;
            }
            else if (current < 0x80)
            {
                byteCount = 1;
            }
            else if (current < 0x800)
            {
                byteCount = 2;
            }
            else
            {
                byteCount = 3;
            }

            if (cut + charCount > charBudget || bytes + byteCount > byteBudget)
            {
                break;
            }

            bytes += byteCount;
            cut += charCount;
        }

        if (cut >= text.Length)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, cut), TruncationMarker);
    }

    /// <summary>扫描失败时与文本构建的兜底一致：退回用户描述。</summary>
    /// <param name="mod">目标 MOD。</param>
    /// <returns>可直接展示的文本。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> 为 <see langword="null"/>。</exception>
    public static string FallbackText(ModRecord mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return mod.Metadata.Description ?? string.Empty;
    }

    /// <summary>模型文本缓存签名：描述 + 文件清单的 SHA-256 摘要，文件/描述变化时重算。</summary>
    /// <remarks>
    /// 用固定长度摘要代替完整文件清单，避免持久缓存随清单长度线性膨胀；
    /// 读写两侧共用本方法，升级后旧格式签名自然不命中并重算一次。
    /// 描述与文件清单直接增量写入哈希，不再依次创建「清单连接串」与「描述 + 清单」两份副本。
    /// 逐段写入改用复用缓冲，不再为每个文件名分配一个 <see cref="byte"/> 数组。
    /// </remarks>
    /// <param name="mod">目标记录。</param>
    /// <returns>64 位十六进制摘要。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> 为 <see langword="null"/>。</exception>
    public static string TextSignature(ModRecord mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, mod.Metadata.Description ?? string.Empty);
        hash.AppendData(DescriptionSeparator);
        AppendFileList(hash, mod.RelativeFiles);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// 复用 UI 行构建阶段已算好的文本签名；记录实例、文件清单实例或描述变化时重新计算。
    /// </summary>
    /// <remarks>
    /// 启动时首帧为了查缓存必须算一次签名，后台预热若再算一遍会让大库哈希两遍。
    /// 同一记录实例也可能被原地替换 <see cref="ModRecord.RelativeFiles"/> 或元数据（安装提交/改写），
    /// 因此必须同时比较文件清单引用与描述内容，避免使用过期签名。
    /// </remarks>
    /// <param name="mod">目标记录。</param>
    /// <param name="known">已知签名表（MOD ID → 记录/清单/描述 + 签名）；为 <see langword="null"/> 时直接计算。</param>
    /// <returns>文本签名。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> 为 <see langword="null"/>。</exception>
    public static string ReuseOrComputeSignature(
        ModRecord mod,
        IReadOnlyDictionary<int, KnownTextSignature>? known)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (known is not null
            && known.TryGetValue(mod.Id, out KnownTextSignature entry)
            && ReferenceEquals(entry.Mod, mod)
            && ReferenceEquals(entry.Files, mod.RelativeFiles)
            && string.Equals(entry.Description, mod.Metadata.Description ?? string.Empty, StringComparison.Ordinal))
        {
            return entry.Signature;
        }

        return TextSignature(mod);
    }

    /// <summary>
    /// 模型身份的轻量内容戳：根层 pak 用「文件名 + 长度 + 最后写入时间」，
    /// 纯散装 MOD 用「文件清单规模 + 各文件所在目录的最大写入时间 + 缺失目录数」。
    /// </summary>
    /// <remarks>
    /// 外部替换 pak 会改变戳；根层 pak 与散装文件混合的 MOD 两部分都会计入。
    /// 散装 MOD 的增删/改名会改变文件所在目录（含深层目录）的写入时间，目录缺失/出现也会改变戳，
    /// 避免把已占用的模型 ID 误判为空闲。
    /// 目录集合每次重算时由文件清单派生，不再按文件清单实例做进程级缓存；
    /// 每个目录只做一次「存在 + 写入时间」查询，不枚举整个文件树。
    /// 根目录不存在时返回稳定哨兵 <c>dir:missing</c>，不会每次打开编辑器都重扫；
    /// 任一部分读取失败（含无权限）时返回统一的一次性 <c>unreadable:</c> 哨兵：
    /// 文本/明细缓存不接受这类戳，身份戳缓存只做 30 秒退避。
    /// </remarks>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="mod">目标 MOD。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="warn">读取失败的告警回调；为 <see langword="null"/> 时不记录。</param>
    /// <returns>稳定内容戳。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static string IdentityStamp(
        BoxPaths paths,
        ModRecord mod,
        IFileSystem fileSystem,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(fileSystem);

        bool hasRootPak = false;
        bool hasLooseFiles = false;
        foreach (string file in mod.RelativeFiles)
        {
            if (ModelPathRules.IsRootPak(file))
            {
                hasRootPak = true;
            }
            else
            {
                hasLooseFiles = true;
            }
        }

        if (!hasRootPak)
        {
            return LooseStamp(paths, mod, fileSystem, warn);
        }

        string pakStamp = PakStamp(paths, mod, fileSystem, warn);
        if (IsUnreadableStamp(pakStamp))
        {
            return pakStamp;
        }

        string pakResult = "pak:" + pakStamp;
        if (!hasLooseFiles)
        {
            return pakResult;
        }

        // 混合 MOD 的散装部分也要参与，否则散装文件增删/改名不会让身份缓存失效。
        // 任一部分不可读时返回统一的一次性哨兵，调用方据此禁止写缓存。
        string looseStamp = LooseStamp(paths, mod, fileSystem, warn);
        return IsUnreadableStamp(looseStamp) ? looseStamp : pakResult + "|" + looseStamp;
    }

    /// <summary>不可读内容戳的统一前缀：文本/明细缓存不接受；身份戳缓存仅做 30 秒退避。</summary>
    public const string UnreadableStampPrefix = "unreadable:";

    /// <summary>判断内容戳是否是一次性「不可读」哨兵。</summary>
    /// <param name="stamp">内容戳；可为 <see langword="null"/>。</param>
    /// <returns>不可读哨兵时返回 <see langword="true"/>。</returns>
    public static bool IsUnreadableStamp(string? stamp) =>
        stamp is not null && stamp.StartsWith(UnreadableStampPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 模型明细/已用身份能否写入缓存：戳可读且 pak 条目全部读取成功才允许；
    /// 条目级不可读与 <c>unreadable:</c> 同策略，避免把空明细当成「没有占用模型」缓存。
    /// </summary>
    /// <param name="stamp">本次计算的内容戳。</param>
    /// <param name="entriesReadable">根层 pak 条目是否全部读取成功。</param>
    /// <returns>可以缓存时返回 <see langword="true"/>。</returns>
    public static bool CanCacheModelDetails(string stamp, bool entriesReadable) =>
        entriesReadable && !IsUnreadableStamp(stamp);

    /// <summary>纯散装 MOD 的内容戳：只读取文件清单中每个文件所在目录的元数据。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="mod">目标 MOD。</param>
    /// <param name="fileSystem">文件系统端口。</param>
    /// <param name="warn">读取失败的告警回调。</param>
    /// <returns>稳定内容戳。</returns>
    private static string LooseStamp(
        BoxPaths paths,
        ModRecord mod,
        IFileSystem fileSystem,
        Action<string>? warn)
    {
        string root = paths.FilesDirectory(mod.Id);
        string[] directories = BuildAbsoluteDirectories(root, mod.RelativeFiles);
        try
        {
            (bool rootExists, DateTime rootWrite) = fileSystem.GetDirectoryExistenceAndLastWriteTimeUtc(root);
            if (!rootExists)
            {
                // 端口已区分「不存在」与「无权限」：无权限会抛异常走下面的 unreadable 分支。
                return "dir:missing";
            }

            long latestTicks = rootWrite.Ticks;
            int missingDirectories = 0;
            for (int index = 1; index < directories.Length; index++)
            {
                (bool exists, DateTime lastWrite) = fileSystem.GetDirectoryExistenceAndLastWriteTimeUtc(directories[index]);
                if (!exists)
                {
                    // 目录被外部改名/删除：计入缺失数，戳随之变化。
                    missingDirectories++;
                    continue;
                }

                if (lastWrite.Ticks > latestTicks)
                {
                    latestTicks = lastWrite.Ticks;
                }
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"dir:{mod.RelativeFiles.Count}:{latestTicks}:{missingDirectories}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"读取模型身份戳失败，本次将重扫该 MOD（{mod.DisplayName}）：{ex.Message}");
            return UnreadableStampPrefix + Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>由文件清单构建绝对目录路径（去重、忽略大小写，根目录固定在最前）。</summary>
    /// <remarks>
    /// 不再按可变 <see cref="List{T}"/> 实例做进程级缓存；只在真正重算内容戳时构建，
    /// 由上层戳缓存的 TTL/精确失效控制频率，避免原地修改清单后读到陈旧目录。
    /// </remarks>
    /// <param name="root">MOD 根目录。</param>
    /// <param name="files">相对文件列表。</param>
    /// <returns>绝对目录路径集合。</returns>
    private static string[] BuildAbsoluteDirectories(string root, List<string> files)
    {
        HashSet<string> relatives = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            string normalized = file.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            if (slash > 0)
            {
                relatives.Add(normalized[..slash]);
            }
        }

        string[] absolutes = new string[relatives.Count + 1];
        absolutes[0] = root;
        int index = 1;
        foreach (string relative in relatives)
        {
            absolutes[index++] = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        return absolutes;
    }

    /// <summary>增量写入文件清单：条目之间使用分隔字符，与原「先连接再哈希」的字节流完全一致。</summary>
    /// <param name="hash">目标哈希。</param>
    /// <param name="files">相对文件列表。</param>
    private static void AppendFileList(IncrementalHash hash, IEnumerable<string> files)
    {
        bool first = true;
        foreach (string file in files)
        {
            if (!first)
            {
                hash.AppendData(FileSeparator);
            }

            AppendUtf8(hash, file);
            first = false;
        }
    }

    /// <summary>把字符串以 UTF-8 增量写入哈希：小串用栈缓冲、大串租借数组，避免逐文件名分配。</summary>
    /// <param name="hash">目标哈希。</param>
    /// <param name="value">要写入的字符串。</param>
    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        int needed = Encoding.UTF8.GetByteCount(value);
        if (needed <= StackUtf8BufferSize)
        {
            Span<byte> buffer = stackalloc byte[StackUtf8BufferSize];
            int written = Encoding.UTF8.GetBytes(value, buffer);
            hash.AppendData(buffer[..written]);
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(needed);
        try
        {
            int written = Encoding.UTF8.GetBytes(value, rented);
            hash.AppendData(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>栈上 UTF-8 缓冲大小：覆盖常见路径长度，超长路径改用租借数组。</summary>
    private const int StackUtf8BufferSize = 256;

    /// <summary>描述与文件清单之间的分隔字节（<c>\u0001</c>）。</summary>
    private static readonly byte[] DescriptionSeparator = [(byte)DescriptionSeparatorChar];

    /// <summary>文件清单条目之间的分隔字节（<c>\u0002</c>）。</summary>
    private static readonly byte[] FileSeparator = [(byte)FileListSeparatorChar];

    /// <summary>描述与文件清单之间的分隔字符，供行签名等复用。</summary>
    public const char DescriptionSeparatorChar = '\u0001';

    /// <summary>文件清单条目之间的分隔字符。</summary>
    public const char FileListSeparatorChar = '\u0002';

    /// <summary>行签名内容与模型文本之间的分隔字符。</summary>
    public const char RowSignatureSeparatorChar = '\u0003';

    /// <summary>模型文本缓存容量上限：避免超大库让缓存与持久化文件无限增长。</summary>
    public const int MaxTextCacheCapacity = 5000;

    /// <summary>模型文本缓存容量：按条目数留 2 倍余量，至少 1024、至多 <see cref="MaxTextCacheCapacity"/>。</summary>
    /// <param name="entryCount">需要承载的条目数。</param>
    /// <returns>建议容量。</returns>
    public static int TextCacheCapacity(int entryCount) =>
        Math.Clamp(entryCount * 2, 1024, MaxTextCacheCapacity);

    /// <summary>根层 pak 的内容戳：文件名 + 字节数 + 最后写入时间；没有根层 pak 时为空串。</summary>
    /// <remarks>
    /// 同名 pak 被替换/重装后戳会变化，跨会话文本缓存据此失效，避免长期显示旧模型。
    /// 某个 pak 读取失败时记录一次告警并返回统一的一次性哨兵（<see cref="UnreadableStampPrefix"/>）：
    /// 文本/明细缓存不会接受它；身份戳缓存仅做 30 秒退避，到期后重扫。
    /// 戳长度超过 <see cref="MaxStampLength"/> 时改为内容哈希，体积论证覆盖全部字段。
    /// </remarks>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="mod">目标 MOD。</param>
    /// <param name="fileSystem">文件系统端口（读取长度与最后写入时间）。</param>
    /// <param name="warn">读取失败的告警回调；为 <see langword="null"/> 时不记录。</param>
    /// <returns>稳定内容戳；读取失败的文件以一次性标记代替。</returns>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public static string PakStamp(
        BoxPaths paths,
        ModRecord mod,
        IFileSystem fileSystem,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(fileSystem);

        List<string>? stamps = null;
        bool unreadable = false;
        string modFiles = paths.FilesDirectory(mod.Id);
        foreach (string file in mod.RelativeFiles)
        {
            if (!ModelPathRules.IsRootPak(file))
            {
                continue;
            }

            string full = Path.Combine(modFiles, file.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                stamps ??= [];
                // 长度与最后写入时间来自同一次文件元数据读取。
                (long length, DateTime lastWrite) = fileSystem.GetFileLengthAndLastWriteTimeUtc(full);
                stamps.Add(
                    file.ToLowerInvariant()
                    + "|" + length.ToString(CultureInfo.InvariantCulture)
                    + "|" + lastWrite.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到时告警并让戳失效；本次预热会重扫该 MOD 而不是命中旧文本。
                unreadable = true;
                warn?.Invoke($"读取 pak 内容戳失败，本次预热将重扫该 MOD（{file}）：{ex.Message}");
            }
        }

        if (unreadable)
        {
            // 读取失败返回统一的一次性哨兵，而不是把不可读标记拼进 pak 戳。
            return UnreadableStampPrefix + Guid.NewGuid().ToString("N");
        }

        return stamps is null ? string.Empty : BoundStamp(string.Join(FileListSeparatorChar, stamps));
    }

    /// <summary>
    /// 缓存条目的统一不变量：null 字符串字段归一为空串，文本截断，签名/内容戳/游戏 ID 收紧到有界长度
    /// 。Application 与 Infrastructure 的加载/写入路径共用这一处。
    /// </summary>
    /// <param name="entry">原始条目。</param>
    /// <returns>已归一化的条目；无需变化时返回原对象。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> 为 <see langword="null"/>。</exception>
    public static ModelTextCacheEntry NormalizeEntry(ModelTextCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string text = ClampText(entry.Text ?? string.Empty);
        // 签名/内容戳/游戏 ID 同样收紧；正常值远低于上限，损坏字段改为确定性哈希后仍可比较。
        string signature = BoundStamp(entry.Signature ?? string.Empty);
        string pakStamp = BoundStamp(entry.PakStamp ?? string.Empty);
        string gameId = BoundStamp(entry.GameId ?? string.Empty);
        return ReferenceEquals(text, entry.Text)
            && ReferenceEquals(signature, entry.Signature)
            && ReferenceEquals(pakStamp, entry.PakStamp)
            && ReferenceEquals(gameId, entry.GameId)
            ? entry
            : entry with { Text = text, Signature = signature, PakStamp = pakStamp, GameId = gameId };
    }

    /// <summary>内容戳的最大长度（字符）：超过后改为内容哈希，避免缓存条目被超长戳支配。</summary>
    public const int MaxStampLength = 512;

    /// <summary>内容戳的最大 UTF-8 字节数：非 ASCII 戳按实际字节提前哈希。</summary>
    public const int MaxStampUtf8Bytes = 1024;

    /// <summary>收紧内容戳长度：短戳原样返回，超长/超字节戳改为确定性的 SHA-256 哈希。</summary>
    /// <param name="stamp">原始内容戳。</param>
    /// <returns>不超过字符/字节上限的内容戳。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stamp"/> 为 <see langword="null"/>。</exception>
    public static string BoundStamp(string stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        // UTF-16 每 char 最多 3 个 UTF-8 字节；
        // 1024 / 3 = 341 字符内的串必然满足字节上限，走常数时间快路径。
        if (stamp.Length <= MaxStampUtf8Bytes / 3)
        {
            return stamp;
        }

        if (stamp.Length <= MaxStampLength
            && Encoding.UTF8.GetByteCount(stamp) <= MaxStampUtf8Bytes)
        {
            return stamp;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(stamp));
        return "hash:" + Convert.ToHexString(hash);
    }
}

/// <summary>UI 已算好的行签名：记录、文件清单与描述都未变时才可复用。</summary>
/// <param name="Mod">算签名时的记录实例。</param>
/// <param name="Files">算签名时的文件清单实例（整体替换时引用会变化）。</param>
/// <param name="Description">算签名时的描述文本。</param>
/// <param name="Signature">该状态下的文本签名。</param>
public readonly record struct KnownTextSignature(
    ModRecord Mod,
    IReadOnlyList<string> Files,
    string Description,
    string Signature)
{
    /// <summary>从记录与签名创建快照（描述取当前值）。</summary>
    /// <param name="mod">记录实例。</param>
    /// <param name="signature">已算好的签名。</param>
    /// <returns>签名快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mod"/> 为 <see langword="null"/>。</exception>
    public static KnownTextSignature From(ModRecord mod, string signature)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return new KnownTextSignature(mod, mod.RelativeFiles, mod.Metadata.Description ?? string.Empty, signature);
    }
}
