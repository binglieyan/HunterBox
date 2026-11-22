using System.Buffers;
using System.Security.Cryptography;
using HunterBox.Application.Common;
using HunterBox.Application.Saves;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Logging;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Readers;

namespace HunterBox.Infrastructure.Saves;

/// <summary>
/// 旧存档 zip 存档备份导入实现：读 <c>Saves_&lt;appid&gt;/*.zip</c>（崛起为 <c>Saves/</c>），
/// 解压到新格式 <c>Saves/&lt;gameId&gt;/&lt;时间戳&gt;/</c> 目录。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>只读取旧 zip 与HunterBox根目录，不删除/不修改旧文件（用户可回退）。</item>
/// <item>压缩包条目经 <see cref="BoxPaths.NormalizeRelative"/> + <see cref="BoxPaths.ResolveInside"/> 校验，
/// zip-slip 越界条目直接拒绝。</item>
/// <item>加密 zip 按文件名规则解析密码（文件名含「解压码xxx」/「密码xxx」），解析不到时按无密码尝试，
/// 失败给出明确提示。</item>
/// </list>
/// </remarks>
public sealed class SharpCompressSaveImporter : ILegacySaveImporter
{
    /// <summary>崛起（MHR）的 Steam AppId：它对存档备份使用无后缀的 <c>Saves/</c> 目录。</summary>
    private static readonly int RiseAppId = GameCatalog.Rise.SteamAppId;

    /// <summary>旧存档 zip 单条目解压上限（2 GiB）：存档文件远小于该值，超出视为异常包。</summary>
    internal const long MaxEntryBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>旧存档 zip 解压总量上限（8 GiB），防止压缩炸弹或损坏包头写满系统盘。</summary>
    internal const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;

    private readonly BoxPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly SaveBackupService _saves;
    private readonly IAppLogger _logger;

    /// <summary>装配存档导入器（由组合根统一提供文件系统与存档服务）。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="fileSystem">文件系统端口（失败清理用）。</param>
    /// <param name="saves">存档备份服务（与对话框共用同一组合根装配）。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public SharpCompressSaveImporter(
        BoxPaths paths,
        IFileSystem fileSystem,
        SaveBackupService saves,
        IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(saves);
        _paths = paths;
        _fileSystem = fileSystem;
        _saves = saves;
        _logger = logger ?? AppLogAdapter.Instance;
    }

    /// <summary>计算某游戏的存档备份目录。</summary>
    /// <param name="paths">HunterBox磁盘布局。</param>
    /// <param name="game">游戏。</param>
    /// <returns><c>&lt;Root&gt;/Saves_&lt;appid&gt;</c>；崛起为 <c>&lt;Root&gt;/Saves</c>。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="InvalidOperationException">游戏没有 Steam AppId。</exception>
    public static string LegacySaveDirectory(BoxPaths paths, GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(game);
        if (game.SteamAppId <= 0)
        {
            throw new InvalidOperationException($"{game.DisplayName} 没有 Steam AppId，无法定位旧存档目录");
        }

        return game.SteamAppId == RiseAppId
            ? Path.Combine(paths.Root, "Saves")
            : Path.Combine(paths.Root, $"Saves_{game.SteamAppId}");
    }

    /// <inheritdoc />
    public string LegacySaveDirectory(GameProfile game) => LegacySaveDirectory(_paths, game);

    /// <inheritdoc />
    public IReadOnlyList<LegacySaveArchive> List(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        string directory = LegacySaveDirectory(game);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<LegacySaveArchive> archives = [];
        foreach (string file in Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly))
        {
            try
            {
                FileInfo info = new(file);
                archives.Add(new LegacySaveArchive(
                    info.FullName,
                    info.Name,
                    new DateTimeOffset(info.LastWriteTime),
                    info.Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件读不到信息（被占用/权限）时跳过，不影响其它备份。
                _logger.Warn($"读取旧存档信息失败：{file}（{ex.Message}）");
            }
        }

        return [.. archives.OrderByDescending(a => a.LastWriteTime)];
    }

    /// <inheritdoc />
    /// <param name="game">当前游戏。</param>
    /// <param name="archive">要导入的旧存档 zip。</param>
    /// <param name="cancellationToken">取消令牌：在创建备份槽后、每个条目与复制分块处检查。</param>
    public SaveBackup Import(GameProfile game, LegacySaveArchive archive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(archive);
        if (!File.Exists(archive.FilePath))
        {
            throw new FileNotFoundException("旧存档 zip 不存在", archive.FilePath);
        }

        // 预取消不创建备份槽，不给列表留下空条目。
        cancellationToken.ThrowIfCancellationRequested();

        string? password = PasswordFromFileName(archive.FileName);
        SaveBackup slot = _saves.CreateSlot(game.Id, archive.LastWriteTime);
        try
        {
            (int count, List<BackupManifestEntry> entries) = ExtractZipWithHashes(
                archive.FilePath,
                slot.Directory,
                password,
                MaxEntryBytes,
                MaxTotalBytes,
                cancellationToken);
            if (count == 0)
            {
                throw new InvalidDataException($"旧存档 zip 是空的: {archive.FileName}");
            }

            // 哈希在解压分块时同步算出，收尾不再整目录读一遍。
            slot = _saves.FinalizeImportedBackup(slot, entries);
            _logger.Info($"导入旧存档：{archive.FileName} → {slot.Directory}（{count} 个文件，已生成哈希清单）");
            return slot;
        }
        catch (OperationCanceledException)
        {
            // 取消不是损坏：清理本次半成品槽后按取消语义抛出，界面显示“已取消”。
            _fileSystem.TryDeleteDirectory(slot.Directory);
            _logger.Warn($"导入旧存档已取消：{archive.FileName}");
            throw;
        }
        catch (Exception ex)
        {
            _fileSystem.TryDeleteDirectory(slot.Directory);
            _logger.Error($"导入旧存档失败：{archive.FileName}", ex);
            if (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw;
            }

            throw new InvalidDataException(
                password is null
                    ? $"导入旧存档失败: {archive.FileName}（若为加密压缩包，请在文件名里标注「解压码xxx」）"
                    : $"导入旧存档失败: {archive.FileName}（{ex.Message}）",
                ex);
        }
    }

    /// <summary>
    /// 从旧存档 zip 文件名解析密码（取「解压码」或「密码」之后的文本，去掉结尾括号备注）。
    /// </summary>
    /// <param name="fileName">zip 文件名（可含路径，取文件名部分）。</param>
    /// <returns>解析出的密码；没有标注时返回 <see langword="null"/>。</returns>
    public static string? PasswordFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string name = Path.GetFileNameWithoutExtension(fileName)
            .Replace(":", string.Empty)
            .Replace("：", string.Empty);

        string marker = "解压码";
        int index = name.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            marker = "密码";
            index = name.LastIndexOf(marker, StringComparison.Ordinal);
        }

        if (index < 0)
        {
            return null;
        }

        string password = name[(index + marker.Length)..];
        int open = password.LastIndexOf('(');
        int close = password.LastIndexOf(')');
        if (open > 0 && close > open)
        {
            password = password[..open];
        }

        password = password.Trim();
        return password.Length > 0 ? password : null;
    }

    /// <summary>解压旧存档 zip，并在写入每个分块时同步计算 SHA-256（只读一遍）。</summary>
    /// <param name="archivePath">zip 路径。</param>
    /// <param name="destinationDirectory">备份槽目录（不存在会创建）。</param>
    /// <param name="password">解压密码。</param>
    /// <param name="maxEntryBytes">单条目上限。</param>
    /// <param name="maxTotalBytes">总量上限。</param>
    /// <param name="cancellationToken">取消令牌：每个条目与每个复制分块处检查。</param>
    /// <returns>解出的文件数与可直接写入哈希清单的条目（相对路径使用正斜杠）。</returns>
    /// <exception cref="InvalidDataException">条目越界、重复或超过尺寸上限。</exception>
    /// <exception cref="OperationCanceledException">已取消。</exception>
    internal static (int Count, List<BackupManifestEntry> Entries) ExtractZipWithHashes(
        string archivePath,
        string destinationDirectory,
        string? password,
        long maxEntryBytes = MaxEntryBytes,
        long maxTotalBytes = MaxTotalBytes,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        int count = 0;
        long total = 0;
        HashSet<string> extractedKeys = new(StringComparer.OrdinalIgnoreCase);
        List<BackupManifestEntry> entries = [];

        using IArchive archive = ZipArchive.OpenArchive(archivePath, new ReaderOptions { Password = password });
        foreach (IArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            // 先按声明的尺寸做整包预检，再在复制时按实际写入量复核：损坏包可能谎报尺寸。
            long declared = Math.Max(entry.Size, 0);
            if (declared > maxEntryBytes)
            {
                throw new InvalidDataException(
                    $"旧存档 zip 条目超过单文件上限（{maxEntryBytes} 字节）：{entry.Key}");
            }

            if (declared > maxTotalBytes - total)
            {
                throw new InvalidDataException(
                    $"旧存档 zip 解压总量超过上限（{maxTotalBytes} 字节）：{Path.GetFileName(archivePath)}");
            }

            string relative = SafeKey(entry.Key, destinationDirectory);
            if (!extractedKeys.Add(relative))
            {
                // 大小写不同/重复条目会映射到同一落盘路径；静默覆盖等于丢存档文件（C-11）。
                throw new InvalidDataException($"旧存档 zip 内存在大小写冲突或重复条目：{entry.Key}");
            }

            string target = BoxPaths.ResolveInside(destinationDirectory, relative);
            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            long written = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(HunterBox.Application.Common.FileCopyBuffer.Size);
            try
            {
                using Stream stream = entry.OpenEntryStream();
                using FileStream file = File.Create(target);
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    written += read;
                    if (written > maxEntryBytes || written > maxTotalBytes - total)
                    {
                        throw new InvalidDataException(
                            $"旧存档 zip 实际解压大小超过上限（条目 {maxEntryBytes} 字节 / 总量 {maxTotalBytes} 字节）：{entry.Key}");
                    }

                    hash.AppendData(buffer, 0, read);
                    file.Write(buffer, 0, read);
                }

                entries.Add(new BackupManifestEntry(
                    relative.Replace('\\', '/'),
                    written,
                    Convert.ToHexString(hash.GetHashAndReset())));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            total += written;
            count++;
        }

        return (count, entries);
    }

    private static string SafeKey(string key, string destinationDirectory)
    {
        string normalized;
        try
        {
            normalized = BoxPaths.NormalizeRelative(key);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"旧存档 zip 条目路径非法: {key}", ex);
        }

        _ = BoxPaths.ResolveInside(destinationDirectory, normalized);
        return normalized;
    }

}
