using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HunterBox.Application.Common;
using HunterBox.Application.Migration;
using HunterBox.Application.Settings;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.Logging;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Settings;
namespace HunterBox.Infrastructure.Migration;

/// <param name="AppId">旧目录里的 Steam AppId（如 2246340）。</param>
/// <param name="GameId">新HunterBox稳定游戏 ID（如 mhws）。</param>
/// <param name="ModsDirectory">旧 MOD 根目录（如 <c>Mods_2246340</c>）。</param>
/// <param name="ModCount">其中带 <c>info.xml</c>的 MOD 数。</param>
/// <param name="HasBackup">是否存在对应的 <c>Backup_&lt;appid&gt;</c> 目录。</param>
public sealed record LegacyGameInventory(int AppId, string GameId, string ModsDirectory, int ModCount, bool HasBackup)
{
    /// <summary>
    /// <see cref="LegacyMigrationService.Detect"/> 已扫描到的候选 MOD 目录（带 <c>info.xml</c>），
    /// 供 <see cref="LegacyMigrationService.Import"/> 复用，避免再次枚举旧库。
    /// </summary>
    public IReadOnlyList<string> ModDirectories { get; init; } = [];
}

/// <param name="Games">检测到的旧游戏数据。</param>
/// <param name="LegacyIniPath">旧 <c>sjmodbox.ini</c> 路径；不存在时为 <see langword="null"/>。</param>
/// <param name="LegacySaveDirectories">旧存档备份目录（<c>Saves_&lt;appid&gt;</c>）。</param>
/// <param name="LegacyBackupDirectories">旧原文件备份目录（<c>Backup_&lt;appid&gt;</c>）。</param>
/// <param name="LegacyDownloadDirectory">旧安装包归档目录（<c>Download</c>）。</param>
public sealed record LegacyInventory(
    IReadOnlyList<LegacyGameInventory> Games,
    string? LegacyIniPath,
    IReadOnlyList<string> LegacySaveDirectories,
    IReadOnlyList<string> LegacyBackupDirectories,
    string? LegacyDownloadDirectory)
{
    /// <summary>
    /// 检测时未能完整读取的旧路径（目录、<c>info.xml</c> 或 Download 目录）：可能还有未发现的 MOD/备份，
    /// 导入时必须提示用户。
    /// </summary>
    public IReadOnlyList<string> UnreadablePaths { get; init; } = [];

    /// <summary>是否检测到旧 MOD/备份/配置数据（含探测失败的不可读路径）。</summary>
    /// <remarks>只有不可读路径时也算「有数据」：必须让用户看到「可能不完整」的提示。</remarks>
    public bool HasAnything =>
        Games.Count > 0
        || LegacyIniPath is not null
        || LegacySaveDirectories.Count > 0
        || LegacyBackupDirectories.Count > 0
        || UnreadablePaths.Count > 0;
}

/// <param name="ImportedMods">本次导入的 MOD 数（不含之前已导入过、本次跳过的）。</param>
/// <param name="SkippedMods">跳过的数量：缺 <c>info.xml</c>/目录无效/已导入过/复制失败等。</param>
/// <param name="ImportedBackupFiles">本次新增迁移的原文件备份数。</param>
/// <param name="ImportedSettings">保留字段；配置由 <see cref="LegacyMigrationService.MigrateSettings"/> 迁移，当前恒为 0。</param>
/// <param name="Warnings">需要提示用户的问题。</param>
public sealed record LegacyImportResult(
    int ImportedMods,
    int SkippedMods,
    int ImportedBackupFiles,
    int ImportedSettings,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// <see cref="SkippedMods"/> 中因「该旧目录之前已成功导入过」而跳过的部分（已包含在 <see cref="SkippedMods"/> 内）。
    /// </summary>
    /// <remarks>
    /// 去重记录持久化在 <see cref="LegacyMigrationService.ImportMarkerPath"/>；为 0 表示本次没有重复项，
    /// 大于 0 表示重复点击导入/失败后重试时命中了已完成的旧 MOD，未再重复复制。
    /// 诊断/测试断言用。
    /// </remarks>
    internal int SkippedDuplicates { get; init; }
}

/// <summary>
/// 从旧数据目录（历史版本）导入数据。
/// </summary>
/// <remarks>
/// <para>只读旧数据、复制到新HunterBox目录，**不移动、不删除**旧文件，用户可随时回退。</para>
/// <para>识别的旧布局：<c>Mods_&lt;appid&gt;/&lt;id&gt;/{info.xml,files}</c>（崛起为 <c>Mods/</c>）、
/// <c>sjmodbox.ini</c>、<c>Saves_&lt;appid&gt;/*.zip</c>、<c>Backup_&lt;appid&gt;</c>、<c>Download/</c>。
/// 旧 <c>info.xml</c>的启用状态不直接沿用（新HunterBox要求文件实际落盘才为启用），导入后统一为未启用。</para>
/// <para>去重：每个成功导入的旧 MOD 目录（绝对路径）与分配到的 MOD ID 会记录到
/// <see cref="ImportMarkerPath"/>；同目录再次导入会被跳过，避免重复点击/失败重试把记录与磁盘占用成倍放大。
/// 记录与 MOD 索引一起落盘：正常完成、取消或导入中途异常退出时，已完成项都会尽力保存，重试不会重复；
/// 记录写入失败会在结果 <see cref="LegacyImportResult.Warnings"/> 中明确提示。</para>
/// </remarks>
public sealed class LegacyMigrationService
{
    /// <summary>旧数据导入去重记录文件名；存放在HunterBox <c>&lt;Root&gt;/Mods</c> 下。</summary>
    internal const string ImportMarkerFileName = ".legacy-import.json";

    private const int ImportMarkerVersion = 1;

    /// <summary>旧配置文件名（与 <c>hunterbox.ini</c> 同目录）。</summary>
    private const string LegacyIniFileName = "sjmodbox.ini";

    /// <summary>从旧 <c>sjmodbox.ini</c> 迁移到新配置的 [settings] 键。</summary>
    private static readonly (string NewKey, string LegacyKey)[] LegacySettingKeys =
    [
        ("install_option", "install_option"),
        (SettingsKeys.CheckGameRunning, SettingsKeys.CheckGameRunning),
        (SettingsKeys.CheckPakFiles, SettingsKeys.CheckPakFiles),
        ("enable_fix_pak_num", "enable_fix_pak_num"),
    ];

    /// <summary>按 appid 分段里的路径键（迁移到按游戏 ID 分段）。</summary>
    private static readonly string[] PathKeys = [SettingsKeys.GamePath, SettingsKeys.SavePath];

    /// <summary>「上次游戏」取值（appid 或稳定 ID）→ 稳定游戏 ID。</summary>
    private static readonly Dictionary<string, string> LegacyGameIds = BuildLegacyGameIds();

    private static Dictionary<string, string> BuildLegacyGameIds()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameProfile game in GameCatalog.All)
        {
            map[game.Id] = game.Id;
            if (game.SteamAppId > 0)
            {
                map[game.SteamAppId.ToString(CultureInfo.InvariantCulture)] = game.Id;
            }
        }

        return map;
    }

    private static readonly JsonSerializerOptions ImportMarkerJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // 旧目录名多是中文/带空格：不转义，方便用户与排障人员直接读去重记录。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly int[] KnownAppIds =
        [.. GameCatalog.All.Where(game => game.SteamAppId > 0).Select(game => game.SteamAppId)];

    private readonly BoxPaths _paths;
    private readonly IModStore _store;
    private readonly IFileSystem _fileSystem;
    private readonly IAppLogger _logger;

    /// <summary>装配迁移服务。</summary>
    /// <param name="paths">新HunterBox磁盘布局。</param>
    /// <param name="store">新HunterBox MOD 记录存储。</param>
    /// <param name="fileSystem">文件系统端口（回滚/清理已复制目录的唯一入口，A-03）。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public LegacyMigrationService(
        BoxPaths paths,
        IModStore store,
        IFileSystem fileSystem,
        IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _paths = paths;
        _store = store;
        _fileSystem = fileSystem;
        _logger = logger ?? AppLogAdapter.Instance;
    }

    /// <summary>把旧 <c>sjmodbox.ini</c> 的设置迁移到新配置（幂等，只补缺失值）。</summary>
    /// <remarks>
    /// 旧文件只读、不修改；新配置里已有非空值时跳过该键。迁移失败只记录日志并保留本次会话内的内存值，
    /// 不让设置页/启动失败。只读降级期间直接跳过：内存里是空配置，此时再写盘会整文件覆盖读不出来的用户原配置。
    /// </remarks>
    /// <param name="paths">新HunterBox磁盘布局（决定新旧 INI 的位置）。</param>
    /// <param name="settings">要写入的新配置端口。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    /// <returns>发生迁移（写入了新值）时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static bool MigrateSettings(BoxPaths paths, ISettings settings, IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        logger ??= AppLogAdapter.Instance;

        if (settings.IsReadOnly)
        {
            logger.Warn($"配置文件为只读降级状态，已跳过旧配置迁移以保护原文件：{paths.SettingsFile}");
            return false;
        }

        string legacyPath = Path.Combine(paths.Root, LegacyIniFileName);
        if (!File.Exists(legacyPath))
        {
            return false;
        }

        IniSettings legacy;
        try
        {
            legacy = new IniSettings(legacyPath, logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.Warn($"读取旧配置失败，跳过迁移：{legacyPath}（{ex.Message}）");
            return false;
        }

        List<(string Section, string Key, string Value)> pending = [];
        void AddIfMissing(string legacySection, string legacyKey, string section, string key, string? forcedValue = null)
        {
            if (settings.Get(section, key).Length > 0)
            {
                return;
            }

            string value = forcedValue ?? legacy.Get(legacySection, legacyKey);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            pending.Add((section, key, value));
        }

        foreach ((string newKey, string legacyKey) in LegacySettingKeys)
        {
            AddIfMissing(SettingsKeys.Section, legacyKey, SettingsKeys.Section, newKey);
        }

        string legacyGame = legacy.Get(SettingsKeys.Section, SettingsKeys.Game).Trim();
        if (LegacyGameIds.TryGetValue(legacyGame, out string? gameId))
        {
            AddIfMissing(SettingsKeys.Section, SettingsKeys.Game, SettingsKeys.Section, SettingsKeys.Game, gameId);
        }

        foreach ((string legacyId, string stableId) in LegacyGameIds)
        {
            if (!int.TryParse(legacyId, out _))
            {
                continue;
            }

            foreach (string key in PathKeys)
            {
                AddIfMissing(legacyId, key, stableId, key);
            }
        }

        if (pending.Count == 0)
        {
            return false;
        }

        try
        {
            settings.UpdateBatch(batch =>
            {
                foreach ((string section, string key, string value) in pending)
                {
                    batch.Set(section, key, value);
                }
            });
            logger.Info($"已把旧配置 {legacyPath} 迁移到 {paths.SettingsFile}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去也保留内存值：本次会话的设置读写仍然可用。
            logger.Warn($"旧配置迁移结果无法写盘（本次会话仍生效）：{ex.Message}");
        }

        return true;
    }

    /// <summary>旧数据导入去重记录文件：<c>&lt;Root&gt;/Mods/.legacy-import.json</c>。</summary>
    /// <remarks>
    /// 内容是带版本号的 JSON（已导入旧目录的绝对路径、分配到的 MOD ID、导入时间），随HunterBox数据目录一起保存；
    /// 损坏时会被改名成 <c>.bad-&lt;时间戳&gt;</c> 保留并在导入结果里告警，而不是阻断导入。
    /// </remarks>
    public string ImportMarkerPath => Path.Combine(_paths.ModsDirectory, ImportMarkerFileName);

    /// <summary>扫描旧数据（不写盘）。</summary>
    /// <returns>检测结果；没有旧数据时各集合为空。</returns>
    /// <remarks>扫描是只读的，不会因为「已导入过去重记录」而隐藏旧数据；去重只在 <see cref="Import"/> 里生效。</remarks>
    public LegacyInventory Detect()
    {
        List<LegacyGameInventory> games = [];
        List<string> saveDirs = [];
        List<string> backupDirs = [];
        List<string> unreadableDirs = [];
        foreach (int appId in KnownAppIds)
        {
            string gameId = MapGameId(appId);
            string modsDir = Path.Combine(_paths.Root, $"Mods_{appId}");
            (bool modsPresent, bool modsUnreadable) = ProbeDirectory(modsDir);
            if (string.Equals(gameId, "mhr", StringComparison.OrdinalIgnoreCase) && (!modsPresent || modsUnreadable))
            {
                // 旧崛起版本的 MOD 目录是 ./Mods（仅在不存在别的布局时当作崛起旧数据）。
                string legacyRise = Path.Combine(_paths.Root, "Mods");
                (bool risePresent, bool riseUnreadable) = ProbeDirectory(legacyRise);
                if (risePresent && !riseUnreadable)
                {
                    // 回退成功时不再报告原 Mods_<appid> 不可读，否则会误导用户「可能不完整」。
                    modsDir = legacyRise;
                    modsPresent = true;
                    modsUnreadable = false;
                }
                else if (riseUnreadable)
                {
                    // 回退目标本身不可读时保留两个不可读路径（原路径 + 回退路径）。
                    unreadableDirs.Add(legacyRise);
                }
            }

            if (modsUnreadable)
            {
                // 目录存在但 ACL 拒绝：Directory.Exists 返回 false，必须显式记录。
                unreadableDirs.Add(modsDir);
            }

            int modCount = 0;
            List<string>? modDirectories = null;
            if (modsPresent && !modsUnreadable)
            {
                try
                {
                    foreach (string dir in Directory.EnumerateDirectories(modsDir))
                    {
                        // 每个 MOD 目录最多一次系统调用组；文件不存在不再靠异常控制流。
                        bool infoPresent;
                        try
                        {
                            infoPresent = Directory.EnumerateFiles(dir, "info.xml", SearchOption.TopDirectoryOnly).Any();
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            unreadableDirs.Add(dir);
                            continue;
                        }

                        if (infoPresent)
                        {
                            // 属性可读但内容被 ACL 拒绝时，检测阶段也要标记该文件不可读，
                            // 否则对话框摘要看不到「可能不完整」（导入阶段另有一次 ReadInfo 分类）。
                            string infoPath = Path.Combine(dir, "info.xml");
                            if (!CanReadFile(infoPath))
                            {
                                unreadableDirs.Add(infoPath);
                            }

                            modCount++;
                            modDirectories ??= [];
                            modDirectories.Add(dir);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个游戏目录不可枚举时按 0 计，不能让旧数据检测整体失败。
                    // 但残缺清单必须标记，导入时明确提示，避免用户以为迁移完整。
                    unreadableDirs.Add(modsDir);
                    _logger.Warn($"检测旧 MOD 目录失败，已跳过 {modsDir}：{ex.Message}");
                }
            }

            string backupDir = Path.Combine(_paths.Root, $"Backup_{appId}");
            (bool backupPresent, bool backupUnreadable) = ProbeDirectory(backupDir);
            if (backupUnreadable)
            {
                unreadableDirs.Add(backupDir);
            }

            bool hasBackup = backupPresent && !backupUnreadable;
            if (hasBackup)
            {
                backupDirs.Add(backupDir);
            }

            string saveDir = Path.Combine(_paths.Root, $"Saves_{appId}");
            (bool savePresent, bool saveUnreadable) = ProbeDirectory(saveDir);
            if (saveUnreadable)
            {
                unreadableDirs.Add(saveDir);
            }
            else if (savePresent)
            {
                saveDirs.Add(saveDir);
            }

            if (modCount > 0 || hasBackup)
            {
                games.Add(new LegacyGameInventory(appId, gameId, modsDir, modCount, hasBackup)
                {
                    ModDirectories = modDirectories ?? [],
                });
            }
        }

        string ini = Path.Combine(_paths.Root, "sjmodbox.ini");
        string download = Path.Combine(_paths.Root, "Download");
        (bool downloadPresent, bool downloadUnreadable) = ProbeDirectory(download);
        if (downloadUnreadable)
        {
            unreadableDirs.Add(download);
        }

        return new LegacyInventory(
            games,
            ProbeFile(ini).Present ? ini : null,
            saveDirs,
            backupDirs,
            downloadPresent && !downloadUnreadable ? download : null)
        {
            UnreadablePaths = unreadableDirs,
        };
    }

    /// <summary>探测目录是否存在且可枚举；ACL 拒绝时返回「存在但不可读」。</summary>
    /// <param name="path">目录绝对路径。</param>
    /// <returns>(存在, 不可读)。</returns>
    private static (bool Present, bool Unreadable) ProbeDirectory(string path)
    {
        try
        {
            // Directory.Exists 对 ACL 拒绝也可能返回 true；这里实际枚举一次判断可读性。
            using IEnumerator<string> enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            enumerator.MoveNext();
            return (true, false);
        }
        catch (DirectoryNotFoundException)
        {
            return (false, false);
        }
        catch (UnauthorizedAccessException)
        {
            return (Directory.Exists(path), true);
        }
        catch (IOException)
        {
            return (false, false);
        }
    }

    /// <summary>探测文件是否可读取内容（只打开句柄，不解析）；用于检测阶段标记不可读的 info.xml。</summary>
    /// <param name="path">文件绝对路径。</param>
    /// <returns>能打开读取时返回 <see langword="true"/>。</returns>
    private static bool CanReadFile(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>探测文件是否存在；权限拒绝时带回错误说明。</summary>
    /// <param name="path">文件绝对路径。</param>
    /// <returns>(存在, 读取错误)；错误非空表示无法判定。</returns>
    private static (bool Present, string? Error) ProbeFile(string path)
    {
        try
        {
            File.GetAttributes(path);
            return (true, null);
        }
        catch (FileNotFoundException)
        {
            return (false, null);
        }
        catch (DirectoryNotFoundException)
        {
            return (false, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, ex.Message);
        }
        catch (IOException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>导入旧数据（MOD + 原文件备份；配置由 <see cref="MigrateSettings"/> 迁移，存档由存档模块单独导入）。</summary>
    /// <param name="inventory">由 <see cref="Detect"/> 得到的检测结果。</param>
    /// <param name="cancellationToken">取消令牌：在 MOD/备份文件之间检查，已完成的部分会保留。</param>
    /// <param name="progress">导入进度：已处理项/总项（MOD 目录 + 备份文件）。</param>
    /// <returns>导入结果与提示；之前已导入过的旧 MOD 计入 <see cref="LegacyImportResult.SkippedDuplicates"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inventory"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="OperationCanceledException">令牌被触发；已导入的记录与去重标记会保存。</exception>
    /// <exception cref="IOException">读取去重记录/枚举旧目录/保存 MOD 索引失败。</exception>
    /// <exception cref="UnauthorizedAccessException">没有读取去重记录或写入HunterBox数据的权限。</exception>
    public LegacyImportResult Import(
        LegacyInventory inventory,
        CancellationToken cancellationToken = default,
        IProgress<(int Done, int Total)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        // 导入前清理历史遗留的多余去重记录备份，与文本缓存/索引行为一致。
        CorruptFileQuarantine.Prune(ImportMarkerPath, _fileSystem, keep: null, _logger.Warn);

        List<string> warnings = [];
        // 检测阶段未能完整枚举时，导入按残缺清单执行，必须让用户知道可能少导入。
        if (inventory.UnreadablePaths.Count > 0)
        {
            warnings.Add(
                $"旧数据检测时有 {inventory.UnreadablePaths.Count} 个路径未能完整读取，本次导入可能不完整："
                + string.Join("、", inventory.UnreadablePaths.Take(5))
                + (inventory.UnreadablePaths.Count > 5 ? "…" : string.Empty));
        }

        ImportMarker marker = LoadImportMarker(warnings);
        List<ImportedModInRun> importedThisRun = [];
        int importedMods = 0;
        int skippedMods = 0;
        int skippedDuplicates = 0;

        // 本次实际新增的备份数量：复制阶段顺带累计，不再迁移前后各做一次全树计数。
        int importedBackups = 0;

        // 旧备份文件只枚举一次：总进度与导入阶段共用同一份清单（P-14）。
        Dictionary<string, (string[]? Files, string? Error)> legacyBackups = [];
        int totalWork = 0;
        foreach (LegacyGameInventory game in inventory.Games)
        {
            // 复用 Detect 的扫描结果，不再重复枚举旧 MOD 目录。
            totalWork += game.ModCount;

            if (game.HasBackup)
            {
                try
                {
                    // 安全枚举：不跟随链接，含链接的备份目录只统计真实文件；
                    // 链接条目必须提示，避免用户以为备份已完整迁移。
                    string backupRoot = Path.Combine(_paths.Root, $"Backup_{game.AppId}");
                    (string[] files, bool containsLink, int linkCount) =
                        SafeFileTree.EnumerateFilesAndCheckLinks(_fileSystem, backupRoot);
                    if (containsLink)
                    {
                        warnings.Add(
                            $"旧备份目录包含 {linkCount} 个符号链接/junction，已跳过这些链接及其目标：{backupRoot}");
                    }

                    legacyBackups[game.GameId] = (files, null);
                    totalWork += files.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 同上：无列举权限/根目录是链接时按 0 计，实际导入阶段会跳过并记 warning。
                    legacyBackups[game.GameId] = (null, ex.Message);
                }
            }
        }

        int done = 0;
        void Advance()
        {
            done++;
            progress?.Report((done, totalWork));
        }

        try
        {
            foreach (LegacyGameInventory game in inventory.Games)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string modsRoot = game.ModsDirectory;
                bool modsPresent = Directory.Exists(modsRoot);
                if (!modsPresent)
                {
                    // 只有 Backup_<appid> 的旧HunterBox：MOD 阶段直接跳过，备份仍要迁移（C-02）。
                    warnings.Add($"旧 MOD 目录不存在，跳过 MOD 导入（备份仍会迁移）：{modsRoot}");
                }

                // 优先复用 Detect 的目录清单；仅在清单为空（Detect 未枚举成功或目录为空）时兜底枚举一次。
                bool fromDetection = game.ModDirectories.Count > 0;
                string[] modDirectories = [.. game.ModDirectories];
                if (modsPresent && modDirectories.Length == 0)
                {
                    try
                    {
                        modDirectories = [.. Directory.EnumerateDirectories(modsRoot)];
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 目录存在但无列举权限：跳过该游戏的 MOD 阶段，备份仍继续。
                        warnings.Add($"旧 MOD 目录无法枚举，跳过该游戏的 MOD 导入：{modsRoot}（{ex.Message}）");
                    }
                }

                foreach (string modDir in modDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string infoPath = Path.Combine(modDir, "info.xml");
                    (bool infoPresent, string? infoError) = ProbeFile(infoPath);
                    if (infoError is not null)
                    {
                        // info.xml 存在但不可读时不能静默跳过。
                        skippedMods++;
                        warnings.Add($"跳过无法读取的 info.xml：{infoPath}（{infoError}）");
                        continue;
                    }

                    if (!infoPresent)
                    {
                        if (fromDetection)
                        {
                            // Detect 曾看到 info.xml，导入时却读不到：说明检测后文件消失，必须提示。
                            skippedMods++;
                            warnings.Add($"旧 MOD 在检测后消失或缺少 info.xml，已跳过：{modDir}");
                        }

                        continue;
                    }

                    int newId;
                    string? targetFiles = null;
                    bool recordAdded = false;
                    try
                    {
                        if (marker.IsImported(modDir))
                        {
                            int? importedId = marker.FindImportedModId(modDir);
                            if (importedId is { } existingId && _store.Find(existingId) is not null)
                            {
                                // 该旧目录之前已成功导入且记录仍在：直接跳过，绝不重复分配 ID/复制文件。
                                skippedMods++;
                                skippedDuplicates++;
                                continue;
                            }

                            // 记录已被用户删除：去重标记失效，允许重新导入（N-24）。
                            marker.RemoveImported(modDir);
                        }

                        (LegacyModInfo? info, bool infoUnreadable) = ReadInfo(infoPath);
                        if (info is null)
                        {
                            skippedMods++;
                            warnings.Add(infoUnreadable
                                ? $"跳过无法读取（权限不足）的 info.xml：{infoPath}"
                                : $"跳过无法解析的 info.xml：{infoPath}");
                            continue;
                        }

                        string filesDir = Path.Combine(modDir, "files");
                        string[] scanned = [];
                        if (_fileSystem.DirectoryExists(filesDir))
                        {
                            if (SafeFileTree.IsReparseLink(_fileSystem, filesDir))
                            {
                                // files 根本身是链接时不能跟随目标，整条记录跳过。
                                skippedMods++;
                                warnings.Add($"旧 MOD 的 files 目录是符号链接/junction，已跳过（不复制链接目标）：{modDir}");
                                continue;
                            }

                            // 一次全树枚举同时得到文件清单与「是否含链接」，
                            // 复制阶段沿用同一份清单；扫描后消失的文件跳过并告警。
                            (scanned, bool containsLink, _) = SafeFileTree.EnumerateFilesAndCheckLinks(_fileSystem, filesDir);
                            if (containsLink)
                            {
                                warnings.Add($"旧 MOD 目录包含符号链接/junction，已跳过链接及其目标：{modDir}");
                            }
                        }

                        if (scanned.Length == 0)
                        {
                            skippedMods++;
                            warnings.Add($"跳过没有文件的旧 MOD：{modDir}");
                            continue;
                        }

                        newId = _store.NextId();
                        targetFiles = _paths.FilesDirectory(newId);
                        (List<string> relativeFiles, int missingFiles) = CopyScannedFiles(
                            scanned,
                            filesDir,
                            targetFiles,
                            cancellationToken);
                        if (missingFiles > 0)
                        {
                            warnings.Add($"旧 MOD 在导入期间有 {missingFiles} 个文件消失，已从记录中剔除：{modDir}");
                        }

                        if (relativeFiles.Count == 0)
                        {
                            skippedMods++;
                            warnings.Add($"跳过没有文件的旧 MOD：{modDir}");
                            continue;
                        }

                        PackageInspection inspection = PackageInspector.Inspect(relativeFiles);

                        // 旧 MOD 归属到识别出的游戏；记录构建规则在 Application（A-03）。
                        ModRecord record = LegacyModRecordFactory.Create(
                            newId,
                            game.GameId,
                            new LegacyModDescriptor(
                                info.Name,
                                info.ModuleName,
                                info.ModType,
                                info.Version,
                                info.Author,
                                info.NexusId,
                                info.HomeUrl,
                                info.Description),
                            inspection,
                            relativeFiles,
                            DateTimeOffset.UtcNow);
                        _store.Add(record);
                        recordAdded = true;
                        string source = marker.RecordImport(modDir, newId);
                        importedThisRun.Add(new ImportedModInRun(source, newId, _paths.ModDirectory(newId)));
                        importedMods++;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        skippedMods++;
                        warnings.Add($"导入失败（{modDir}）：{ex.Message}");
                    }
                    finally
                    {
                        // 复制失败/取消都不能留下 Files/<newId> 半成品（N-24/）；记录已加入时不动它。
                        if (targetFiles is not null && !recordAdded)
                        {
                            _fileSystem.TryDeleteDirectory(targetFiles);
                        }

                        Advance();
                    }
                }

                if (game.HasBackup)
                {
                    string backupRoot = Path.Combine(_paths.Root, $"Backup_{game.AppId}");
                    if (!Directory.Exists(backupRoot))
                    {
                        warnings.Add($"旧备份目录不存在，跳过备份迁移：{backupRoot}");
                        continue;
                    }

                    // 复用扫描阶段枚举出的清单，不再重复整目录枚举（P-14）。
                    legacyBackups.TryGetValue(game.GameId, out (string[]? Files, string? Error) scan);
                    if (scan.Files is null)
                    {
                        // 备份目录无列举权限：跳过该游戏的备份阶段，不影响其它游戏。
                        warnings.Add(
                            $"旧备份目录无法枚举，跳过该游戏的备份迁移：{backupRoot}（{scan.Error ?? "未知错误"}）");
                        continue;
                    }

                    foreach (string source in scan.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string relative = Path.GetRelativePath(backupRoot, source);
                        string target = Path.Combine(_paths.BackupDirectory(game.GameId), relative);
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            // 复制阶段顺带统计新增备份，避免迁移后再做一次全树计数。
                            bool isNew = !_fileSystem.FileExists(target);
                            File.Copy(source, target, overwrite: true);
                            if (isNew)
                            {
                                importedBackups++;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            warnings.Add($"备份迁移失败（{relative}）：{ex.Message}");
                        }
                        finally
                        {
                            Advance();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消/异常退出也要把已完成项（索引 + 去重记录）尽力落盘，回复后重试不会重复。
            PersistCompletedBestEffort(marker, importedThisRun, importedMods);
            throw;
        }
        catch
        {
            PersistCompletedBestEffort(marker, importedThisRun, importedMods);
            throw;
        }

        if (importedMods > 0)
        {
            try
            {
                _store.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 索引没落盘就不能留下「内存记录 + 已复制文件 + 去重标记」的半套状态，
                // 否则同一次会话里重试会重复分配 ID。回滚本次新增项，让重试从干净状态开始。
                RollbackImportedMods(marker, importedThisRun, "MOD 索引保存失败");
                throw;
            }
        }

        TrySaveImportMarker(marker, warnings);

        if (skippedDuplicates > 0)
        {
            warnings.Add(
                $"检测到 {skippedDuplicates} 个旧 MOD 目录之前已成功导入过，本次已跳过（去重记录：{ImportMarkerPath}）。");
            _logger.Info($"旧数据导入跳过重复项：{skippedDuplicates} 个（去重记录：{ImportMarkerPath}）");
        }

        _logger.Info(
            $"旧数据导入完成：导入 {importedMods} 个 MOD、{importedBackups} 个原文件备份；"
            + $"跳过 {skippedMods} 个（其中重复 {skippedDuplicates} 个）。");

        // 配置（sjmodbox.ini）由 MigrateSettings 在启动/设置页触发，这里不重复处理。
        return new LegacyImportResult(importedMods, skippedMods, importedBackups, 0, warnings)
        {
            SkippedDuplicates = skippedDuplicates,
        };
    }

    /// <summary>把绝对路径规范化成去重键（保留大小写；比较时忽略大小写）。</summary>
    private static string NormalizeSourcePath(string path) => Path.GetFullPath(path);

    /// <summary>读取并校验去重记录；记录不存在时返回空记录。</summary>
    /// <remarks>内容损坏（JSON 无法解析/版本不认识）时保留原件为 <c>.bad-*</c> 并告警后按空记录继续；
    /// 读取文件本身没有权限/被占用则向上抛，避免在「不知道已导入什么」的情况下重复复制。</remarks>
    private ImportMarker LoadImportMarker(List<string> warnings)
    {
        ImportMarker marker = new();
        string path = ImportMarkerPath;
        if (!File.Exists(path))
        {
            return marker;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            ImportMarkerDocument? document = JsonSerializer.Deserialize<ImportMarkerDocument>(stream, ImportMarkerJsonOptions);
            if (document is null || document.Version != ImportMarkerVersion)
            {
                PreserveBrokenImportMarker(path, warnings, "结构或版本无法识别");
                return marker;
            }

            foreach (ImportMarkerModEntry? entry in document.ImportedMods ?? [])
            {
                if (entry is null)
                {
                    // JSON 数组里可能有 null 元素：跳过而不是让每次导入都 NRE。
                    continue;
                }

                string? source = TryNormalizeSourcePath(entry.Source);
                if (source is null || !marker.Sources.Add(source))
                {
                    continue;
                }

                marker.Mods.Add(new ImportMarkerModEntry
                {
                    Source = source,
                    ModId = entry.ModId,
                    ImportedAt = entry.ImportedAt,
                });
                marker.IndexLoaded(source, entry.ModId);
            }

            marker.Dirty = false;
            _logger.Info($"已加载旧数据导入去重记录：{marker.Mods.Count} 个旧 MOD（{path}）");
            return marker;
        }
        catch (FileNotFoundException)
        {
            // 检查存在之后被删：按空记录处理即可。
            return marker;
        }
        catch (JsonException)
        {
            PreserveBrokenImportMarker(path, warnings, "JSON 无法解析");
            return marker;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // 结构异常同样走损坏保留路径，让用户能自愈而不是每次导入都失败。
            PreserveBrokenImportMarker(path, warnings, "结构无法识别：" + ex.Message);
            return marker;
        }
    }

    private static string? TryNormalizeSourcePath(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        try
        {
            return NormalizeSourcePath(source.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>把损坏的去重记录改名保留（不覆盖已有 <c>.bad-*</c>），并在导入结果里告警。</summary>
    private void PreserveBrokenImportMarker(string path, List<string> warnings, string reason)
    {
        string? badPath = CorruptFileQuarantine.TryMoveAside(path, _fileSystem, out Exception? error);
        if (badPath is not null)
        {
            // 迁移去重件的隔离文件同样限制保留份数。
            CorruptFileQuarantine.Prune(
                path,
                _fileSystem,
                badPath,
                _logger.Warn);
            _logger.Warn($"旧数据导入去重记录{reason}，已保留原件：{badPath}（原路径 {path}）");
            warnings.Add(
                $"旧数据导入去重记录{reason}，已保留为：{badPath}；本次导入可能重复导入之前导入过的旧 MOD。");
            return;
        }

        _logger.Error($"旧数据导入去重记录{reason}，且无法改名保留：{path}", error);
        warnings.Add(
            $"旧数据导入去重记录{reason}，且无法改名保留：{path}；本次导入可能重复导入之前导入过的旧 MOD。");
    }

    /// <summary>原子写去重记录（GUID 临时文件 + 刷盘 + 同目录改名替换）。</summary>
    /// <exception cref="IOException">写入/替换失败。</exception>
    /// <exception cref="UnauthorizedAccessException">没有写权限。</exception>
    private void SaveImportMarker(ImportMarker marker)
    {
        string path = ImportMarkerPath;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(
            new ImportMarkerDocument
            {
                Version = ImportMarkerVersion,
                ImportedMods = [.. marker.Mods],
            },
            ImportMarkerJsonOptions);

        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            _fileSystem.TryDeleteFile(temp);
            throw;
        }

        marker.Dirty = false;
        _logger.Info($"已保存旧数据导入去重记录：{marker.Mods.Count} 个旧 MOD（{path}）");
    }

    /// <summary>尽力保存去重记录：失败只告警（导入本身已完成），绝不把「去重记录写不进去」升级成导入失败。</summary>
    private void TrySaveImportMarker(ImportMarker marker, List<string> warnings)
    {
        if (!marker.Dirty)
        {
            return;
        }

        try
        {
            SaveImportMarker(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"保存旧数据导入去重记录失败：{ImportMarkerPath}", ex);
            warnings.Add(
                $"保存旧数据导入去重记录失败（{ImportMarkerPath}）：{ex.Message}；"
                + "下次导入可能重复导入本次已完成的旧 MOD。");
        }
    }

    /// <summary>导入被取消/中途异常时保存已完成项：先索引、成功后再写去重记录，避免标记指向未保存的记录。</summary>
    private void PersistCompletedBestEffort(ImportMarker marker, List<ImportedModInRun> importedThisRun, int importedMods)
    {
        if (importedMods > 0)
        {
            try
            {
                _store.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error("保存已完成的旧数据导入记录失败，重试可能重复导入本次已完成项", ex);
                RollbackImportedMods(marker, importedThisRun, "中断保存失败");
                return;
            }
        }

        if (marker.Dirty)
        {
            try
            {
                SaveImportMarker(marker);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error($"保存旧数据导入去重记录失败：{ImportMarkerPath}", ex);
            }
        }
    }

    /// <summary>回滚本次运行新增但没能落盘的 MOD：删记录、删已复制目录、移除去重项。</summary>
    private void RollbackImportedMods(ImportMarker marker, List<ImportedModInRun> importedThisRun, string reason)
    {
        foreach (ImportedModInRun item in importedThisRun)
        {
            _store.Remove(item.ModId);
            marker.Remove(item.Source);
            if (!_fileSystem.TryDeleteDirectory(item.Directory))
            {
                _logger.Warn($"回滚旧数据导入失败（{reason}）：无法删除目录 {item.Directory}，请手工清理");
            }
        }

        _logger.Warn($"旧数据导入未完成（{reason}），已回滚本次新增的 {importedThisRun.Count} 个 MOD，避免重复导入。");
    }

    /// <summary>读取旧 <c>info.xml</c>；返回内容与「是否因权限不可读」。</summary>
    /// <param name="infoPath">info.xml 路径。</param>
    /// <returns>解析结果；无法解析或不可读时 <c>Info</c> 为 <see langword="null"/>。</returns>
    private static (LegacyModInfo? Info, bool Unreadable) ReadInfo(string infoPath)
    {
        try
        {
            XDocument document = XDocument.Load(infoPath);
            XElement? root = document.Root;
            if (root is null)
            {
                return (null, false);
            }

            string? Value(string name) =>
                root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

            return (new LegacyModInfo
            {
                ModuleName = Value("moduleName"),
                Name = Value("name"),
                Version = Value("version"),
                Author = Value("author"),
                NexusId = int.TryParse(Value("nexusId"), out int nexus) ? nexus : 0,
                ModType = Value("modType"),
                HomeUrl = Value("HomeUrl"),
                Description = Value("description"),
            }, false);
        }
        catch (System.Xml.XmlException)
        {
            return (null, false);
        }
        catch (IOException)
        {
            return (null, false);
        }
        catch (UnauthorizedAccessException)
        {
            // 属性可读但内容被 ACL 拒绝时，分类为「不可读」而不是「解析失败」。
            return (null, true);
        }
    }

    /// <summary>按已扫描的清单复制（扫描后消失的文件跳过并计数），返回记录用的相对清单。</summary>
    private (List<string> RelativeFiles, int MissingFiles) CopyScannedFiles(
        string[] scanned,
        string sourceRoot,
        string destination,
        CancellationToken cancellationToken)
    {
        _fileSystem.CreateDirectory(destination);
        List<string> relativeFiles = new(scanned.Length);
        int missingFiles = 0;
        foreach (string file in scanned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_fileSystem.FileExists(file))
            {
                // 扫描后被删除/移走：从记录中剔除，保证 RelativeFiles 与磁盘一致。
                missingFiles++;
                continue;
            }

            string relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            string target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            _fileSystem.CreateDirectory(Path.GetDirectoryName(target)!);
            _fileSystem.CopyFile(file, target, overwrite: true);
            relativeFiles.Add(relative);
        }

        relativeFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return (relativeFiles, missingFiles);
    }

    private static string MapGameId(int appId) => GameCatalog.FindIdByAppId(appId) ?? "unknown";

    /// <summary>本次运行新导入的一条 MOD（回滚用）。</summary>
    private sealed record ImportedModInRun(string Source, int ModId, string Directory);

    /// <summary>去重记录的内存视图：加载后始终保留本次运行新增但尚未落盘的条目。</summary>
    private sealed class ImportMarker
    {
        /// <summary>已记录的旧 MOD 目录（按绝对路径、忽略大小写去重）。</summary>
        public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>来源目录 → MOD ID 索引：避免每次按目录查 ID 都线性扫 <see cref="Mods"/>（P-14）。</summary>
        private readonly Dictionary<string, int> _modIdsBySource = new(StringComparer.OrdinalIgnoreCase);

        public List<ImportMarkerModEntry> Mods { get; } = [];

        /// <summary>是否有尚未落盘的改动（显式 dirty：Remove+Add 数量不变的场景也必须落盘）。</summary>
        public bool Dirty { get; set; }

        /// <summary>旧 MOD 目录是否已经成功导入过。</summary>
        public bool IsImported(string legacyDirectory) =>
            Sources.Contains(NormalizeSourcePath(legacyDirectory));

        /// <summary>取某个旧目录导入后分配的 MOD ID；没有记录时为 <see langword="null"/>。</summary>
        public int? FindImportedModId(string legacyDirectory)
        {
            string source = NormalizeSourcePath(legacyDirectory);
            return _modIdsBySource.TryGetValue(source, out int modId) ? modId : null;
        }

        /// <summary>把从磁盘加载的条目登记进 ID 索引。</summary>
        /// <param name="source">已规范化的来源目录。</param>
        /// <param name="modId">导入时分配的 MOD ID。</param>
        public void IndexLoaded(string source, int modId) => _modIdsBySource[source] = modId;

        /// <summary>移除某个旧目录的导入标记（用户删除导入的 MOD 后允许重新导入）。</summary>
        public void RemoveImported(string legacyDirectory) => Remove(NormalizeSourcePath(legacyDirectory));

        /// <summary>记录一个旧 MOD 目录已导入（内存）；返回规范化后的去重键。</summary>
        public string RecordImport(string legacyDirectory, int modId)
        {
            string source = NormalizeSourcePath(legacyDirectory);
            if (Sources.Add(source))
            {
                Mods.Add(new ImportMarkerModEntry
                {
                    Source = source,
                    ModId = modId,
                    ImportedAt = DateTimeOffset.UtcNow,
                });
                _modIdsBySource[source] = modId;
                Dirty = true;
            }

            return source;
        }

        /// <summary>回滚一个尚未落盘的条目。</summary>
        public void Remove(string source)
        {
            bool removedSource = Sources.Remove(source);
            bool removedIndex = _modIdsBySource.Remove(source);
            int removedEntries = Mods.RemoveAll(
                entry => string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase));
            if (removedSource || removedIndex || removedEntries > 0)
            {
                Dirty = true;
            }
        }
    }

    /// <summary>去重记录文件结构（JSON，带版本）。</summary>
    private sealed class ImportMarkerDocument
    {
        public int Version { get; set; }

        public List<ImportMarkerModEntry>? ImportedMods { get; set; }
    }

    /// <summary>一条已导入的旧 MOD 记录（诊断用：旧目录、新 MOD ID、导入时间）。</summary>
    private sealed class ImportMarkerModEntry
    {
        public string Source { get; set; } = string.Empty;

        public int ModId { get; set; }

        public DateTimeOffset ImportedAt { get; set; }
    }

    private sealed class LegacyModInfo
    {
        public string? ModuleName { get; init; }

        public string? Name { get; init; }

        public string? Version { get; init; }

        public string? Author { get; init; }

        public int NexusId { get; init; }

        public string? ModType { get; init; }

        public string? HomeUrl { get; init; }

        public string? Description { get; init; }
    }
}
