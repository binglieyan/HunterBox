using HunterBox.Application.Common;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Logging;

namespace HunterBox.Infrastructure.Paks;

/// <summary>
/// 整包解包 / 重打包实现：支持 feature 0（明文 TOC）与 feature 8 / 24（加密 TOC）。
/// </summary>
/// <remarks>
/// <para>
/// 解包时先按包头解密 TOC（feature 8/24），再逐条按 attributes 里的压缩/加密方式还原内容；
/// 路径清单未收录的条目写成 <c>__Unknown/&lt;16 位大写哈希&gt;</c>，重打包时按文件名还原哈希。
/// </para>
/// <para>
/// 打包时按 <see cref="PakRepackProfile"/> 生成包头与条目表；TOC 需要加密时复用/生成 128 字节密钥块，
/// 用同一套 XOR 混淆重新加密；目标 pak 通过临时文件 + 原子替换写入，失败不会留下半个文件。
/// </para>
/// <para>
/// <see cref="Capture"/> 只能表达「整包一致」的条目属性：原包压缩/加密方式混合时，
/// profile 会降级为不压缩/不加密，并写入警告日志（<see cref="AppLog"/>）。
/// 加密方式只在压缩方式可用时保留；「未压缩但加密」的包同样会降级为明文并写入警告。
/// </para>
/// </remarks>
public sealed class PakRepacker : IPakRepacker
{
    private readonly PakNameIndex? _defaultNameIndex;
    private readonly IAppLogger _logger;

    /// <summary>装配重打包器。</summary>
    /// <param name="defaultNameIndex">
    /// 解包时未显式传入路径清单所使用的默认清单（哈希 → 路径）；为 <see langword="null"/> 时未收录条目按未知条目处理。
    /// </param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    public PakRepacker(PakNameIndex? defaultNameIndex = null, IAppLogger? logger = null)
    {
        _defaultNameIndex = defaultNameIndex;
        _logger = logger ?? AppLogAdapter.Instance;
    }

    /// <inheritdoc />
    public PakArchiveInfo Inspect(string pakPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        if (!File.Exists(pakPath))
        {
            throw new FileNotFoundException("pak 文件不存在", pakPath);
        }

        try
        {
            // 上次原地写 TOC 被中断：先恢复再解析，避免读到哈希混合的条目表。
            PakTocPreImage.TryRecoverPending(pakPath, out _, _logger);
            return ReadArchiveLocked(pakPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            // 上次原地写 TOC 被中断时先用前像恢复，再重试一次。
            if (!PakTocPreImage.TryRecover(pakPath, out _, _logger))
            {
                throw;
            }

            return ReadArchiveLocked(pakPath);
        }
    }

    /// <summary>读取并解密 pak 的包头与条目表。</summary>
    /// <param name="pakPath">pak 路径。</param>
    /// <returns>解析结果（含 TOC 加密材料）。</returns>
    private static ParsedPak ReadParsed(string pakPath)
    {
        using FileStream stream = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PakArchiveCodec.Read(stream);
    }

    private static PakArchiveInfo ReadArchive(string pakPath) => ReadParsed(pakPath).Archive;

    /// <summary>在路径锁内读取条目表，避免与并发改名/恢复交错。</summary>
    /// <param name="pakPath">pak 路径。</param>
    /// <returns>解析后的包头与条目。</returns>
    private static PakArchiveInfo ReadArchiveLocked(string pakPath)
    {
        using PakPathLock.Scope scope = PakPathLock.Acquire(Path.GetFullPath(pakPath));
        return ReadArchive(pakPath);
    }

    /// <summary>在路径锁内读出完整解析结果（含 TOC 加密材料）。</summary>
    /// <param name="pakPath">pak 路径。</param>
    /// <returns>解析结果。</returns>
    private static ParsedPak ReadParsedLocked(string pakPath)
    {
        using PakPathLock.Scope scope = PakPathLock.Acquire(Path.GetFullPath(pakPath));
        return ReadParsed(pakPath);
    }

    /// <inheritdoc />
    public PakRepackProfile Capture(string pakPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        if (!File.Exists(pakPath))
        {
            throw new FileNotFoundException("pak 文件不存在", pakPath);
        }

        ParsedPak parsed;
        try
        {
            PakTocPreImage.TryRecoverPending(pakPath, out _, _logger);
            parsed = ReadParsedLocked(pakPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            if (!PakTocPreImage.TryRecover(pakPath, out _, _logger))
            {
                throw;
            }

            parsed = ReadParsedLocked(pakPath);
        }

        PakArchiveInfo archive = parsed.Archive;

        // PakRepackProfile 只能表达「整包一致」的压缩/加密方式：混合属性的包会被降级为不压缩/不加密。
        // 降级会让输出体积甚至加密属性变化，必须留下可检索的记录，不能静默发生。
        int compressionType = GetUniformCompressionType(archive.Entries, out bool mixedCompression);
        // 加密方式独立检测，即使压缩方式混合（compressionType=0）也要报告加密降级。
        int uniformEncryptionType = GetUniformEncryptionType(archive.Entries, out bool mixedEncryption);
        int encryptionType = compressionType == 0 ? 0 : uniformEncryptionType;

        List<string> warnings = [];
        if (mixedCompression)
        {
            string warning =
                $"{pakPath} 的条目压缩方式不一致，重打包只能统一为不压缩；输出体积可能明显增大。";
            warnings.Add(warning);
            _logger.Warn("Capture：" + warning);
        }

        if (mixedEncryption)
        {
            string warning =
                $"{pakPath} 的条目加密方式不一致，重打包只能统一为不加密；原本加密的条目会变成明文。";
            warnings.Add(warning);
            _logger.Warn("Capture：" + warning);
        }

        // 重打包格式不支持「加密但不压缩」，该组合必须显式告知而不是静默转明文。
        if (!mixedEncryption && compressionType == 0 && uniformEncryptionType != 0)
        {
            string warning =
                $"{pakPath} 的条目未压缩但带加密，重打包只支持「压缩 + 加密」组合；整个 pak 会降级为明文（不加密）。";
            warnings.Add(warning);
            _logger.Warn("Capture：" + warning);
        }

        return new PakRepackProfile
        {
            Major = archive.Major,
            Minor = archive.Minor,
            Feature = archive.Feature,
            Fingerprint = archive.Fingerprint,
            EntryCount = archive.Entries.Count,
            CompressionType = compressionType,
            EncryptionType = encryptionType,
            TocKeyBlob = parsed.TocKeyBlock,
            Feature24Payload = parsed.TocFeaturePayload,
            Warnings = warnings,
        };
    }

    /// <inheritdoc />
    public void Unpack(string pakPath, string destinationDirectory, CancellationToken cancellationToken) =>
        Unpack(pakPath, destinationDirectory, nameIndex: null, progress: null, cancellationToken);

    /// <inheritdoc />
    public void Unpack(
        string pakPath,
        string destinationDirectory,
        PakNameIndex? nameIndex,
        IProgress<PakRepackProgress>? progress,
        CancellationToken cancellationToken)
    {
        new PakUnpacker(_defaultNameIndex, _logger)
            .Unpack(pakPath, destinationDirectory, nameIndex, progress, cancellationToken);
    }

    /// <inheritdoc />
    public void Pack(string sourceDirectory, string pakPath, PakRepackProfile profile, CancellationToken cancellationToken) =>
        Pack(sourceDirectory, pakPath, profile, progress: null, cancellationToken);

    /// <inheritdoc />
    public void Pack(
        string sourceDirectory,
        string pakPath,
        PakRepackProfile profile,
        IProgress<PakRepackProgress>? progress,
        CancellationToken cancellationToken)
    {
        PakPacker.Pack(sourceDirectory, pakPath, profile, progress, cancellationToken);
    }

    /// <summary>取全部条目一致的压缩方式；不一致或没有条目时返回 0（原样存放）。</summary>
    /// <param name="entries">条目表。</param>
    /// <param name="mixed">条目之间的压缩方式不一致时为 <see langword="true"/>。</param>
    private static int GetUniformCompressionType(IReadOnlyList<PakEntryInfo> entries, out bool mixed)
    {
        mixed = false;
        if (entries.Count == 0)
        {
            return 0;
        }

        int value = entries[0].CompressionType;
        foreach (PakEntryInfo entry in entries)
        {
            if (entry.CompressionType != value)
            {
                mixed = true;
                return 0;
            }
        }

        return value;
    }

    /// <summary>取全部条目一致的加密方式；不一致或没有条目时返回 0。</summary>
    /// <param name="entries">条目表。</param>
    /// <param name="mixed">条目之间的加密方式不一致时为 <see langword="true"/>。</param>
    private static int GetUniformEncryptionType(IReadOnlyList<PakEntryInfo> entries, out bool mixed)
    {
        mixed = false;
        if (entries.Count == 0)
        {
            return 0;
        }

        int value = entries[0].EncryptionType;
        foreach (PakEntryInfo entry in entries)
        {
            if (entry.EncryptionType != value)
            {
                mixed = true;
                return 0;
            }
        }

        return value;
    }
}
