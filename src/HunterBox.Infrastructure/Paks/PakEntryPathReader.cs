using System.Diagnostics.CodeAnalysis;
using HunterBox.Application.Common;
using HunterBox.Application.Models;
using HunterBox.Application.Paks;
using HunterBox.Domain.Models;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Logging;

namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// <see cref="IPakEntryPathReader"/> 的实现：读 RE PAK 的条目表，按内置路径清单把条目哈希还原成路径。
/// </summary>
/// <remarks>
/// <para>
/// 明文 TOC 直接用 <see cref="RePakReader"/> 读；TOC 加密（feature 8/24）或结构超出
/// <see cref="RePakReader"/> 支持范围的包，用 <see cref="PakRepacker.Inspect"/> 先解密 TOC 再拿条目哈希，
/// 因此「TOC 加密」不再被当成扫描失败。
/// </para>
/// <para>
/// 单包达到 100MB 视为整包资源，不逐条扫描（返回 <see langword="null"/> 并给出原因）；
/// 扫描失败的原因通过 <c>reason</c> 返回，由调用方决定是否/如何记录。
/// 本类只在「TOC 前像恢复」与「整包解密解析」内部通过注入的日志端口记录恢复/降级过程。
/// </para>
/// <para>
/// 预检（大小 + 包头）与随后的明文解析复用同一个已打开的流，因此明文正常路径只打开一次；
/// 加密 TOC 会在解析前释放预检句柄，由整包解析器重新打开；
/// 明文条目表结构损坏触发前像恢复时同样先释放句柄，恢复成功后重新打开。
/// </para>
/// </remarks>
public sealed class PakEntryPathReader : IPakEntryPathReader
{
    /// <summary>pak 逐条扫描上限（达到 100MB 视为整包资源/大 pak，不逐条扫描）。</summary>
    private const long MaxPakScanSize = 104_857_600;

    private readonly IAppLogger _logger;

    /// <summary>装配条目路径读取器。</summary>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    public PakEntryPathReader(IAppLogger? logger = null)
    {
        _logger = logger ?? AppLogAdapter.Instance;
    }

    /// <inheritdoc />
    public PakNameIndex LoadNameIndex(ModelGame game) =>
        game == ModelGame.Unknown ? EmbeddedPakNameIndex.LoadWilds() : EmbeddedPakNameIndex.Load(game);

    /// <inheritdoc />
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "TryPreflight 返回的流由本方法的 try/finally 统一释放。")]
    public IReadOnlyList<string>? TryReadEntryPaths(
        string pakPath,
        string pakName,
        PakNameIndex index,
        out string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentNullException.ThrowIfNull(index);

        reason = null;
        try
        {
            // 上次原地写 TOC 被中断：必须在打开预检流之前恢复。
            // 先恢复再打开，保证随后复用的句柄读到的一定是恢复后的条目表。
            PakTocPreImage.TryRecoverPending(pakPath, out _, _logger);

            if (!TryPreflight(pakPath, out FileStream? stream, out bool encryptedToc, out string? rejectReason))
            {
                reason = rejectReason;
                return null;
            }

            FileStream? opened = stream;
            try
            {
                if (encryptedToc && opened is not null)
                {
                    // 加密 TOC 由整包解析器重新打开并可能触发恢复改写；先释放预检句柄，
                    // 避免自身共享锁阻塞恢复流程。
                    opened.Dispose();
                    opened = null;
                }

                PakArchiveInfo archive;
                try
                {
                    archive = ReadArchive(pakPath, opened, _logger, encryptedToc);
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    // 前像恢复会以独占写打开 pak，必须先把预检/解析仍持有的共享读句柄释放，
                    // 否则恢复被自身共享锁拒绝（明文路径此前未处理）。
                    opened?.Dispose();
                    opened = null;

                    // 结构损坏时再尝试完整恢复（未提交日志或已提交前像），然后重试一次。
                    if (!PakTocPreImage.TryRecover(pakPath, out _, _logger))
                    {
                        throw;
                    }

                    // 恢复可能重写/替换文件：恢复成功后重新打开后读取。
                    // 加密 TOC 仍由整包解析器自行打开（它可能再次触发恢复），不能保留共享读句柄。
                    if (encryptedToc)
                    {
                        archive = ReadArchive(pakPath, stream: null, _logger, encryptedToc: true);
                    }
                    else
                    {
                        opened = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        archive = ReadArchive(pakPath, opened, _logger, encryptedToc: false);
                    }
                }

                List<string> names = [];
                foreach (PakEntryInfo entry in archive.Entries)
                {
                    string? name = index.TryResolve(entry.Hash);
                    if (name is not null)
                    {
                        names.Add(name);
                    }
                }

                return names;
            }
            finally
            {
                opened?.Dispose();
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            reason = "无法解析：" + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 单次打开完成存在性、大小与包头预读：成功时把仍打开的流交给调用方复用。
    /// </summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <param name="stream">仍打开的只读流；拒绝继续解析时为 <see langword="null"/>。</param>
    /// <param name="encryptedToc">包头显示条目表加密（feature 8/24）。</param>
    /// <param name="rejectReason">无需继续解析时的原因（文件不存在/达到扫描上限/无法打开）。</param>
    /// <returns>可以继续解析时返回 <see langword="true"/>。</returns>
    private static bool TryPreflight(
        string pakPath,
        out FileStream? stream,
        out bool encryptedToc,
        out string? rejectReason)
    {
        stream = null;
        encryptedToc = false;
        rejectReason = null;

        FileStream opened;
        try
        {
            opened = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            rejectReason = "文件不存在";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            rejectReason = "无法解析：" + ex.Message;
            return false;
        }

        try
        {
            if (opened.Length >= MaxPakScanSize)
            {
                rejectReason = "达到 100MB，未逐条扫描";
                opened.Dispose();
                return false;
            }

            try
            {
                PakPlainHeader header = PakPlainToc.ReadHeader(opened);
                encryptedToc = PakFeature.IsTocEncrypted(header.Feature);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // 预读失败交给解析器/恢复流程给出具体错误（与旧 HasEncryptedToc 一致）。
            }

            // 解析器从当前位置开始读：预读把位置推进到包头之后，这里统一重置到文件开头。
            opened.Position = 0;
            stream = opened;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            opened.Dispose();
            rejectReason = "无法解析：" + ex.Message;
            return false;
        }
    }

    /// <summary>读条目表：明文复用预检流；已有包头提示 TOC 加密或结构超出简化读取器支持时用整包解析器解密。</summary>
    /// <param name="pakPath">pak 文件路径。</param>
    /// <param name="stream">预检阶段打开、待复用的只读流；加密 TOC 路径为 <see langword="null"/>。</param>
    /// <param name="logger">日志端口。</param>
    /// <param name="encryptedToc">预读得到的「条目表加密」标记。</param>
    /// <returns>解析后的包头与条目。</returns>
    private static PakArchiveInfo ReadArchive(
        string pakPath,
        FileStream? stream,
        IAppLogger logger,
        bool encryptedToc)
    {
        // 与并发改名/恢复串行化，避免读到写了一半的条目表。
        using PakPathLock.Scope scope = PakPathLock.Acquire(Path.GetFullPath(pakPath));

        // 包头已提示 TOC 加密：直接走整包解析器，不再先被 RePakReader 读一遍（P-15）。
        if (encryptedToc)
        {
            return new PakRepacker(logger: logger).Inspect(pakPath);
        }

        PakArchiveInfo archive;
        try
        {
            stream!.Position = 0;
            archive = RePakReader.Read(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            // 加密 TOC 的 pak 用整包重打包器读取（解密 TOC 后拿条目哈希）。
            return new PakRepacker(logger: logger).Inspect(pakPath);
        }

        // RePakReader 刻意宽松，半写 TOC 的条目字段（负值/越界）不会报错；
        // 这里补一次最小范围校验，让损坏的明文 TOC 也进入前像恢复流程。
        EnsureEntryRangesSane(archive, stream!.Length);
        return archive;
    }

    /// <summary>
    /// 明文条目的最小范围校验：哈希不得为空，偏移/大小不得为负，正向大小不得超出数据区。
    /// </summary>
    /// <param name="archive">解析出的包头与条目。</param>
    /// <param name="fileLength">pak 文件长度。</param>
    /// <exception cref="InvalidDataException">存在明显损坏的条目（半写 TOC 的典型形态，含清零型）。</exception>
    private static void EnsureEntryRangesSane(PakArchiveInfo archive, long fileLength)
    {
        PakPlainHeader header = new(
            archive.Major,
            archive.Minor,
            archive.Feature,
            archive.Entries.Count,
            archive.Fingerprint);
        long dataStart = PakTocPreImage.DataStart(header);
        foreach (PakEntryInfo entry in archive.Entries)
        {
            // 真实路径哈希不可能为 0：清零型半写 TOC 的条目全部为 0，必须按损坏处理。
            if (entry.HasEmptyPathHash)
            {
                throw new InvalidDataException("pak 条目的路径哈希为空（TOC 可能被清零写坏）");
            }

            if (entry.Offset < 0 || entry.CompressedSize < 0 || entry.DecompressedSize < 0)
            {
                throw new InvalidDataException($"pak 条目 {entry.Hash:X16} 的偏移或大小非法");
            }

            long stored = entry.CompressedSize > 0 ? entry.CompressedSize : entry.DecompressedSize;
            if (stored > 0 && (entry.Offset < dataStart || entry.Offset > fileLength - stored))
            {
                throw new InvalidDataException($"pak 条目 {entry.Hash:X16} 超出 pak 数据区范围");
            }
        }
    }
}
