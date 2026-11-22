# 04 Application 层（`HunterBox.Application`）

## 1. 职责

Application 编排用例：校验输入、调用 Domain 纯决策、通过端口执行文件/存储操作、维护事务与回滚、
把结果汇总成可展示的报告。它只依赖 Domain，不出现 `SharpCompress`、`JsonSerializer`、`IniSettings`
等实现字样。

| 命名空间 | 目录 | 职责 |
|---|---|---|
| `Install` | `Install/` | 安装/重装/更新/卸载、暂存与原子提交、FOMOD/变体、安装源归档、条目分类 |
| `Activation` | `Activation/` | 启用/禁用/重新复制编排、批量动作、游戏运行守卫 |
| `Mount` | `Mount/` | 散装文件真实挂载（复制/备份/还原/删除/撤销） |
| `Paks` | `Paks/` | `pak_mods` 实际写入/删除、内容指纹缓存、挂载端口、pak 档案信息与读写/重打包端口 |
| `Models` | `Models/` | 模型检测/自动分类/编辑器规划/文件夹改写/pak 与散装改写 |
| `Cleanup` | `Cleanup/` | 「删除全部MOD文件」两个安全范围 |
| `Saves` | `Saves/` | 存档目录备份/还原/重命名/删除、旧 zip 导入端口 |
| `Mods` | `Mods/` | 跨游戏归属校验、旧安装路径修复、文件清单重扫 |
| `Common` | `Common/` | 撤销作用域抽象与文件级实现、尽力删除、时钟端口 |
| `Settings` | `Settings/` | `ISettings` 设置端口、设置读写协调器、当前游戏会话 |

## 2. 安装：`Install`

### 2.1 ModInstaller

文件：[ModInstaller.cs](../../src/HunterBox.Application/Install/ModInstaller.cs)。

```csharp
public sealed class ModInstaller
{
    public ModInstaller(BoxPaths paths, IModStore store, IArchiveExtractor extractor, string gameId);

    public InstallStaging Stage(string sourcePath,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default);

    public InstallStaging StageReinstall(int id, string sourcePath,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default);

    public bool Uninstall(int id);

    internal (string WorkDir, string ContentRoot, IReadOnlyList<string> Files, PackageInspection Inspection)
        StageCore(string sourcePath, ...);
    internal static FomodConfig? ReadFomod(string contentRoot, IReadOnlyList<string> files);
    internal static ModMetadata ReadSuggestedMetadata(string contentRoot, IReadOnlyList<string> files);
    internal static string? ExtractUrl(string? text);
}
```

`Stage` 流程：`Temp` 解压/复制 → 剥掉单层外壳目录（`natives`/`reframework` 等内容根不剥）→
`PackageInspector.Inspect` → 返回 `InstallStaging` 等待 UI 确认。来源不存在抛 `FileNotFoundException`，
无法识别抛 `InvalidDataException`。

`StageReinstall` 额外校验：MOD 存在、属于当前游戏、已禁用（`InvalidOperationException`），
然后复用同一暂存与选择逻辑，提交时替换已有记录。

`Uninstall` 的顺序是「墓碑改名 → `Remove` → `Save` → 删除墓碑目录」：
先改名而不是先递归删除，索引保存失败时目录还能原位恢复；只有记录移除与保存都成功才删除墓碑。

### 2.2 InstallStaging / InstallPlan / InstallCommitter

文件：[InstallStaging.cs](../../src/HunterBox.Application/Install/InstallStaging.cs)、
[InstallPlan.cs](../../src/HunterBox.Application/Install/InstallPlan.cs)、
[InstallCommitter.cs](../../src/HunterBox.Application/Install/InstallCommitter.cs)。

A-12 把「安装向导状态」与「原子提交」拆开：

| 类型 | 职责 |
|---|---|
| `InstallPlan` | 解压结果、识别结论、元数据、变体、FOMOD、预览图；`ResolveContentFile`/`ValidateName`；把用户选择规划成 `InstallCopyPlan` |
| `InstallCommitter` | `InstallCopyPlan` 的原子提交：暂存 → 改名换入 → 写记录；指纹去重、重装替换、安装源归档 |
| `InstallStaging` | 门面，保持原有 API：构造时组合 Plan + Committer，`Commit*` 先规划再提交 |

```csharp
public sealed class InstallStaging : IDisposable
{
    internal static InstallStaging Create(...);
    public bool IsReinstall { get; }
    public string? ResolveContentFile(string? relativePath);
    public string? ValidateName(string? name);

    public InstallResult Commit(...);        // 选择文件 / 根目录 / 安装前缀
    public InstallResult CommitVariant(PackageVariant variant, ...);
    public InstallResult CommitFomod(FomodSelectionState selection, ...);
}
```

提交语义：

1. 选中文件先完整写进 `Mods/.staging/<guid>/files`；
2. 全部成功后目录改名为 `Mods/<id>/files`；
3. 再写 `ModRecord` 并 `Save`；
4. 失败只删暂存目录，不写记录、不留半成品；ID 只在复制成功后分配（失败不回退 `NextId`，但也不落盘）。

重装模式（`IsReinstall`）：旧 `files` 先改名 `.old-<guid>` 保留，新目录换入成功后才更新记录；
失败恢复旧目录与旧记录，保留 `Id`/`Index`/`GameId`。

名称校验：必填且当前游戏内不能重名。安装选项（`InstallSourceMode`）：
`None`/`Copy`/`Move`，对应 INI `[settings] install_option` 的 0/1/2。

### 2.3 包级规则与变体

| 类型/方法 | 说明 |
|---|---|
| `PackageFilters.ShouldExclude(path, previewPath, allowPreview)` | 排除 `modinfo.ini`、FOMOD 配置、预览图/截图（用户显式勾选时允许图片作为真实资源） |
| `PackageFilters.ComputeFingerprint(contentRoot, files, fileSystem)` | 「相对路径 + 文件大小」SHA-256，用于同游戏重复安装检测 |
| `PackageVariantScanner.Scan(files, readFile, gameId)` | 根下多个子目录各是一套安装：优先看子目录 `modinfo.ini`，否则看子目录能否被 `PackageInspector` 识别；至少两个变体才返回 |
| `InstallEntryClassifier.Classify(path, fileSystem)` | 目录 → 目录流程；压缩包 → 安装；裸 `.pak`/`.lua`/`.dll` → 单文件；其它 → 不支持 |
| `InstallSourceArchiver.Archive(paths, fileSystem, sourcePath, displayName, mode)` | 把原始安装包复制/移动到 `<Root>/Download/<显示名>.<ext>`；重名追加 `(2)(3)`；`.tmp` + 改名，失败只 warning |
| `InstallSourceLocator.FindExisting(record, fileSystem)` | 重装优先用记录里的安装源；缺失再让用户选包 |

## 3. 启用/禁用：`Activation`

### 3.1 ModActivationService

文件：[ModActivationService.cs](../../src/HunterBox.Application/Activation/ModActivationService.cs)。

```csharp
public sealed record ActivationReport(MountReport Loose, PakMountReport? Pak);

public interface IGameProcessDetector { bool IsRunning(string executableName); }

public sealed class GameRunningGuard
{
    public GameRunningGuard(GameProfile game, bool enabled, IGameProcessDetector detector);
    public void EnsureGameNotRunning();   // 开启且运行中 → InvalidOperationException
}

public sealed class ModActivationService
{
    public ModActivationService(
        BoxPaths paths, IModStore store, string gameRoot, GameProfile game,
        IContentFingerprint fingerprint, GameRunningGuard? gameRunningGuard = null,
        bool checkPakInternalFiles = false, IPakArchiveReader? pakReader = null,
        Func<ILooseFileMountService>? mountServiceFactory = null,
        Func<IPakMountService>? pakServiceFactory = null);

    public IDisposable BeginSaveBatch();
    public IReadOnlyList<string> FindLooseFileConflicts(ModRecord mod);
    public ActivationReport Enable(ModRecord mod, PakNamingMode pakMode, CancellationToken cancellationToken = default);
    public ActivationReport Disable(ModRecord mod, PakNamingMode pakMode, CancellationToken cancellationToken = default);
    public ActivationReport Recopy(ModRecord mod, PakNamingMode pakMode, CancellationToken cancellationToken = default);
}
```

启用顺序保证：

1. `GameScope` 校验归属（内部 `GameScope.EnsureBelongsTo`）；
2. `GameRunningGuard`（设置开启时）；
3. `SplitFiles(mod)`：根层 `.pak` → pak 链，其余 → 散装链；
4. 可选「检查PAK内部文件」：展开已有启用 pak 与本次 pak 条目，生成重叠提示 warning；
5. **先算 pak 计划**（`PlanEnable`，冲突/本 MOD 源缺失直接抛，此时散装一个都没动）；
6. 写 pak（`PakMountService.Enable`）；
7. 写散装（`MountService.Enable`）；
8. 任一步失败：撤销散装、删除本次写入的 pak，`ModRecord.Enabled` 保持 `false`；
9. 成功后写 `Enabled = true` 并保存（批量作用域内延迟到 `Dispose()` 统一保存一次）。

禁用是反向流程：先撤散装、再撤 pak，最后写 `Enabled = false`；失败尽量恢复为「仍启用」。
`BeginSaveBatch()` 可嵌套，只有最外层释放时落盘；批量结束保存失败会从 `Dispose` 抛出，
调用方必须如实报告（文件已写入、索引未更新）。

### 3.2 ModBatchActivator

```csharp
public sealed record BatchActivationResult(
    int Processed, int Total, bool Canceled, IReadOnlyList<string> Failures);

public sealed class ModBatchActivator
{
    public ModBatchActivator(ModActivationService activation);
    public BatchActivationResult Enable(IReadOnlyList<ModRecord> mods, PakNamingMode pakMode,
        CancellationToken cancellationToken = default, IProgress<(int Done, int Total)>? progress = null);
    public BatchActivationResult Disable(...);
    public BatchActivationResult Recopy(...);
}
```

- 逐项调用 `ModActivationService`，项与项之间检查取消并上报进度；
- 取消时已处理项保持各自完成后的状态（不回滚）；
- 批量启用/禁用：单项失败不中断其它项，逐条汇总 `Failures`；
- 重新复制：单项失败立即停止（与 UI 原行为一致）；
- 整批共用一个 `BeginSaveBatch` 作用域，结束（含取消/失败）统一保存一次。

## 4. 散装挂载：`Mount`

文件：[MountService.cs](../../src/HunterBox.Application/Mount/MountService.cs)、
[ILooseFileMountService.cs](../../src/HunterBox.Application/Mount/ILooseFileMountService.cs)。

```csharp
public sealed record MountReport(int Copied, int BackedUp, int Restored, int Deleted);

public sealed class MountService : ILooseFileMountService
{
    public MountService(BoxPaths paths, string gameRoot, string gameId,
        IReadOnlyList<ModRecord> allMods, IContentFingerprint? fingerprint = null);

    public static string MapToGamePath(ModRecord mod, string fileInMod);
    public IReadOnlyList<string> FindLooseFileConflicts(ModRecord mod);
    public MountReport Enable(ModRecord mod, IEnumerable<string>? files = null);
    public MountReport Disable(ModRecord mod, IEnumerable<string>? files = null);
}
```

- 只执行 `LooseFilePlanner` 产出的计划；「是否需要备份/是否该删」由 Domain 决定。
- `MapToGamePath`：`TargetDirectory` 非空时加前缀，否则沿用 MOD 内相对路径。
- 启用：执行前预校验源文件（缺失在动任何文件之前抛 `FileNotFoundException`）；复制/备份后复制；
  失败用 `IUndoScope`（默认 `FileUndoJournal`）逆序回滚并抛 `IOException`。
- 禁用：优先还原备份；若还有其它启用 MOD 提供同一文件，从该 MOD 重新复制（胜出方按列表顺序）；
  备份保留到最后一个占用方禁用时才还原；失败尽量回滚。
- 重叠文件查找使用「游戏路径 → (MOD, MOD 内文件)」的映射（`OwnerEntry`），忽略大小写。

### IUndoScope 与 FileUndoJournal

文件：[IUndoScope.cs](../../src/HunterBox.Application/Common/IUndoScope.cs)、
[IUndoScopeFactory.cs](../../src/HunterBox.Application/Common/IUndoScopeFactory.cs)、
[FileUndoJournal.cs](../../src/HunterBox.Application/Common/FileUndoJournal.cs)。

```csharp
public interface IUndoScope : IDisposable
{
    void BackupFile(string destination);      // 覆盖前把现有文件复制进撤销目录
    void RecordCreated(string destination);   // 本次新建：回滚时删除
    void MoveAside(string original);          // 移入撤销目录：回滚时移回
    void RecordRestore(string destination, string preImage, bool deletePreImageOnRollback = false);
    void Rollback(List<string> warnings);     // 逆序回滚，单项失败只追加告警
    void Commit();                            // 成功提交：撤销目录不再需要
}

public interface IUndoScopeFactory
{
    IUndoScope Create(string undoRoot, Action<string>? onRollbackApplied = null);
}
```

- `FileUndoJournal` 是默认实现，`FileUndoScopeFactory` 负责按操作创建；
  `MountService` / `PakMountService` 构造时可注入工厂，测试可观察补偿动作（A-10）；
- 撤销目录按需创建（只有备份/移入需要），`Dispose` 递归清理；
- 回滚有失败项、或既未提交也未回滚时**保留**撤销目录（可能是游戏原文件唯一前像）；
- 仅在单线程内使用，与两个挂载服务约定一致。

### SettingsCoordinator

文件：[SettingsCoordinator.cs](../../src/HunterBox.Application/Settings/SettingsCoordinator.cs)、
[GameSettingsSnapshot.cs](../../src/HunterBox.Application/Settings/GameSettingsSnapshot.cs)。

- 把「游戏设置」的 INI 键名、默认值、批量保存与旧键清理收敛到 Application；
- `Read(game)` 返回 `GameSettingsSnapshot`，`Save(game, snapshot)` 一次 `UpdateBatch` 落盘；
- `ResolveCurrentGame()` 统一「上次游戏/未知回退荒野」的解析；
- 设置对话框只负责控件值与快照之间的映射（A-06）。

## 5. pak 挂载：`Paks`

文件：[PakMountService.cs](../../src/HunterBox.Application/Paks/PakMountService.cs)。

```csharp
public sealed record PakMountReport(int Written, int Removed);

public sealed class PakMountService : IPakMountService
{
    public PakMountService(BoxPaths paths, string gameRoot, string gameId,
        IReadOnlyList<ModRecord> allMods, IContentFingerprint fingerprint);

    public PakPlan PlanEnable(ModRecord mod, PakProfile profile, ICollection<string>? warnings = null);
    public PakMountReport Enable(ModRecord mod, PakProfile profile);
    public PakMountReport Disable(ModRecord mod, PakProfile profile);
}
```

稳定性约定：

| 场景 | 行为 |
|---|---|
| 本 MOD 的 pak 源缺失（启用） | 动任何文件前抛 `FileNotFoundException`，提示重装 |
| 其它启用中 MOD 的源缺失 | 跳过并写 warning，不让整个启用/禁用卡死 |
| 同名且内容不同 | `PakConflictException`，UI 列出冲突双方 |
| 禁用时源缺失 | 只删记录能证明归属的文件名；编号模式无法证明归属时全部保留并警告 |
| `pak_mods` 里未参与本次计划的文件 | 不删除（可能是已禁用 MOD 或用户手放的） |

写/删都带 `FileUndoJournal`，失败恢复已覆盖/已移动的文件。
`PakFingerprintCache` 由组合根持有为进程级缓存（`AppServices.PakFingerprints`），启用/禁用/清理共享同一实例；
旧存档导入（`SharpCompressSaveImporter`）只注入 `IFileSystem`/`SaveBackupService`，不接收指纹缓存。
命中前比对「长度 + 最后写入时间」，文件改写后自动重算。

### 5.1 pak 端口与档案信息（A-25）

文件：[PakArchiveInfo.cs](../../src/HunterBox.Application/Paks/PakArchiveInfo.cs)、
[IPakAccess.cs](../../src/HunterBox.Application/Paks/IPakAccess.cs)、
[IPakRepacker.cs](../../src/HunterBox.Application/Paks/IPakRepacker.cs)。

```csharp
public sealed record PakEntryInfo(
    uint HashLower, uint HashUpper, long Offset, long CompressedSize,
    long DecompressedSize, long Attributes, ulong Checksum);

public sealed record PakArchiveInfo(
    byte Major, byte Minor, short Feature, uint Fingerprint,
    IReadOnlyList<PakEntryInfo> Entries) {
    bool IsTocEncrypted;                  // feature 8/24
    static bool IsSupportedMajorMinor(byte major, byte minor); // 2.0/2.1/4.0/4.1
    static bool IsSupportedFeature(short feature);             // 0/8/24
}

public interface IPakArchiveReader { PakArchiveInfo Read(string pakPath); }
// IPakEntryReader：RePakEntryReader 实现仅作为测试校验工具，生产不实例化。
public interface IPakEntryReader   { byte[] ReadEntry(string pakPath, PakEntryInfo entry); }
public interface IPakRepacker {
    PakArchiveInfo Inspect(string pakPath);
    PakRepackProfile Capture(string pakPath);
    void Unpack(string pakPath, string destinationDirectory, PakNameIndex? nameIndex,
                IProgress<PakRepackProgress>? progress, CancellationToken cancellationToken);
    void Pack(string sourceDirectory, string pakPath, PakRepackProfile profile,
              CancellationToken cancellationToken);
}
```

`PakRepackProfile` 保存重打包要沿用的属性：`Major`（2/4，默认 4）、`Minor`、`Feature`
（0 明文 / 8 加密 TOC / 24 加密 TOC + 4 字节附加数据）、`Fingerprint`、`CompressionType`
（0 原样 / 1 DEFLATE / 2 ZSTD）、`EncryptionType`（0 不加密 / 1–5 资源加密）、128 字节 `TocKeyBlob`、
`Feature24Payload`、`Warnings`；`IsTocEncrypted => (Feature & 8) != 0`。

## 6. 模型子系统：`Models`

### 6.1 检测与分类

| 类型/方法 | 说明 |
|---|---|
| `ModelDetector.Detect(entries, game)` | 从「散装文件 + pak 条目」检测模型行；去重键 = 完整身份 + pak 文件名 |
| `ModelDetector.CollectEntries(relativeFiles, readPakEntries, game)` | 散装原样保留，pak 条目按路径清单还原并记录来源 pak；世界不展开 pak |
| `DetectedModel(Identity, FileCount, PakFile, HasPrefab, HasChain, SamplePath)` | 检测行（不含名称表名字） |
| `ModelDetail.FromDetected(detected, name)` | 编辑器行（含显示名） |
| `ModelAutoClassifier.Apply(paths, record, game, reader, names, warn, recompute)` | 安装后自动分类 + 备注；只改内存，不落盘 |
| `ModelAutoClassifier.Apply(record, entries, game, names, warn, recompute)` | 用已收集条目分类（与备注文本共用一次 pak 扫描） |
| `ModelAutoClassifier.DetectDetails(...)` | 模型编辑器行（含名字/类型/来源 pak） |
| `ModelAutoClassifier.BuildModelText(...)` | 「备注信息」列文本：`名字 (完整ID) [pfb] [pak]` |
| `ModelAutoClassifier.DescribeNoModels(...)` | 没有可改模型时的提示（首行固定「这个MOD没什么模型可以修改」） |
| `ModelAutoClassifier.InspectUnsupportedPaks(...)` | 列出 TOC 加密/无法解析的根层 pak |

行触发规则：荒野由 `art/model` 与 `_prefab` 触发；崛起由 `player/mod`、`weapon`、`player/prefab` 触发；
世界只由 `nativepc/pl|wp` 且含 `/mod/` 的路径触发；骨骼/贴图不单独出行。

### 6.2 编辑器规划

[ModelEditorPlanner.cs](../../src/HunterBox.Application/Models/ModelEditorPlanner.cs) 是纯逻辑：

```csharp
public static IReadOnlyList<ModelChoice> BuildCandidates(
    ModelIdentity current, string currentName, bool currentHasChain,
    IReadOnlyList<ModelChoice> all, IReadOnlySet<string> usedIds,
    bool filterUnused, string? rememberedCode = null);

public static IReadOnlyList<ModelChoice> Search(IReadOnlyList<ModelChoice> choices, string? query);
public static string SelectionKey(string? pakFile, string identityForm);
public static bool CanChooseFirstOnEnter(string? query);
public static string BuildTargetForm(ModelIdentity current, ModelChoice choice);
public static bool IsCurrent(ModelIdentity current, ModelChoice choice);
public static string? DescribeSelfCollision(IEnumerable<ModelIdentity> ownRows,
    ModelIdentity current, ModelChoice choice);
```

- 候选只取同类型；「仅显示未使用的ID」过滤其它 MOD 与同一 MOD 其它行占用的完整 ID
  （宿主把两者合并后传入 `usedIds`）；
- 当前模型无论是否被过滤/是否在名称表里都强制加回；没有已记住目标时默认选中它；
- 候选面板的搜索复用 `ModelEditorPlanner.Search`：按名称/完整 ID 子串匹配，多个词须命中同一候选；
- 候选面板按 `SelectionKey`（PakFile + 完整身份）逐行记住目标，同名模型在多个 pak 中互不覆盖；
- 自撞校验按完整身份路径形式比较，护甲同 `fid_sid` 的男/女款不会误报；
- 提交给改写服务的必须是完整身份路径形式（保留 `ch0X` 性别 / `itTT`）。

### 6.3 文件夹改写与批量改写

```csharp
public static class ModelFolderEditor
{
    public static FolderModelScan Scan(string folder, ModelGame game,
        IPakEntryPathReader reader, IModelNameResolver names, Action<string>? warn = null);

    public static ModelRewriteSummary Rewrite(string folder,
        IReadOnlyList<(string From, string To)> changes,
        PakModelRewriteService pakService, ModelGame game,
        IPakEntryPathReader reader,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken token = default);
}

public sealed class ModelRewriteCoordinator
{
    public ModelRewriteCoordinator(BoxPaths paths, PakModelRewriteService pakService);
    public ModelRewriteSummary Rewrite(ModRecord mod, IReadOnlyList<(string From, string To)> changes,
        PakNameIndex index, bool backup = true);
    public ModelRewriteSummary Rewrite(ModRecord mod, IReadOnlyList<(string From, string To)> changes,
        PakNameIndex index, ModelGame game, bool backup = true);
}

public sealed record ModelRewriteSummary(
    int PaksScanned, int EntriesChanged, int FilesMoved,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> UnsupportedPaks)
{
    // 文案由 App 的 ModelRewriteText.Build 生成（A-16）
}
```

`ModelRewriteCoordinator` 对 MOD 的每个根层 pak 调用 `PakModelRewriteService.RewritePaths`，
再改写散装文件路径；不支持改写的 pak（TOC 加密且无重打包实现）记录到 `UnsupportedPaks` 并继续，
不中断整批。

### 6.4 pak 改写服务

```csharp
public sealed class PakModelRewriteService
{
    public PakModelRewriteService(
        IPakArchiveReader archiveReader, IPakEntryRenamer? renamer = null,
        IPakPathHasher? pathHasher = null, IPakRepacker? repacker = null);

    public ModelRewriteReport RewritePaths(string pakPath, string from, string to,
        PakNameIndex index, bool backup = true);

    public ModelRewriteReport RewritePaths(string pakPath,
        IReadOnlyList<(string From, string To)> changes,
        PakNameIndex index, bool backup = true);
}
```

`RewritePaths`：用路径索引把条目哈希还原成路径 → 按完整身份替换 → 只重写 TOC 的 8 字节哈希；
数据区、偏移、压缩/加密属性与 checksum 不动。明文 TOC 与「条目压缩/加密」都可直接改名；
整张 TOC 加密（feature 8/24）先用 `IPakRepacker.Inspect` 解密条目表，再由改名器用同一密钥块加密写回。
同一个 pak 的多个 change 聚合为一次 TOC 读改写（O(TOC)），不是每个 change 整包复制。

`LooseModelRewriteService.Rewrite(mod, from, to)` 只做 `File.Move` 改名，不改文件内容；
只处理属于该模型身份的路径，贴图保持原样。

## 7. 清理：`Cleanup`

文件：[ModCleanupService.cs](../../src/HunterBox.Application/Cleanup/ModCleanupService.cs)。

```csharp
public enum CleanupScope { BoxInstalledOnly, Everything }
public sealed record CleanupReport(int Disabled, int Total, bool Canceled) {
    IReadOnlyList<string> Warnings { get; init; }
}

public sealed class ModCleanupService
{
    public ModCleanupService(BoxPaths paths, IModStore store, string gameRoot, GameProfile game,
        IContentFingerprint fingerprint, PakNamingMode pakMode, GameRunningGuard? gameRunningGuard = null);

    public CleanupReport Cleanup(CleanupScope scope,
        CancellationToken cancellationToken = default,
        IProgress<(int Done, int Total)>? progress = null);
}
```

安全模型：

- **永远先安全禁用**当前游戏所有启用中的 MOD（还原原文件、删除复制文件、`Enabled=false`）；
- `BoxInstalledOnly` 到此为止；
- `Everything` 再按 MOD 记录删除「能证明归属且没有原文件备份」的残留；禁用阶段刚还原/保留的路径显式排除；
- 不整目录删除、不删备份、不删用户自己的非HunterBox文件；只处理当前游戏。

## 8. 存档：`Saves`

文件：[SaveBackupService.cs](../../src/HunterBox.Application/Saves/SaveBackupService.cs)。
A-11 把实现拆到 `SaveBackupStore`（列目录/备份/槽/重命名/删除）、`SaveBackupRestorer`
（校验/还原/快照清理）、`SaveBackupFormatter`（时间戳/体积）与 `SaveBackupManifest`（哈希清单）；
`SaveBackupService` 保留原公开 API 作为门面。

```csharp
public sealed record SaveBackup(string Name, string Directory, DateTimeOffset CreatedAt) {
    long SizeBytes { get; init; }
}
public sealed record SaveBackupVerification(bool HasManifest, int FileCount, IReadOnlyList<string> Errors) {
    bool IsValid => HasManifest && Errors.Count == 0;
}

public sealed class SaveBackupService
{
    public SaveBackupService(BoxPaths paths, IClock? clock = null);

    public string SavesRoot(string gameId);
    public IReadOnlyList<SaveBackup> List(string gameId);              // 时间倒序
    public SaveBackup Backup(string gameId, string saveDirectory);      // 目录复制 + 哈希清单
    public SaveBackup CreateSlot(string gameId, DateTimeOffset createdAt);
    public SaveBackup FinalizeImportedBackup(SaveBackup backup, IReadOnlyList<BackupManifestEntry> entries);
    public SaveBackupVerification Restore(SaveBackup backup, string saveDirectory);
    public SaveBackup Rename(SaveBackup backup, string newName);
    public void Delete(SaveBackup backup);
    public static bool TryParseTimestamp(string name, out DateTimeOffset createdAt);
    public static string FormatSize(long bytes);
}
```

要点：

- 备份格式是目录复制，不做 zip；路径为 `Saves/<gameId>/<yyyyMMdd-HHmmss>/`，备份时写哈希清单；
- `Backup` 拒绝「存档目录包含HunterBox数据目录」（会边枚举边复制撑爆磁盘）；失败删除半成品槽；
- `CreateSlot` 用 `SlotCreationGate` 原子完成「检查同名 + 创建」，并发调用会拿到不同目录名；
- `Restore` 是两阶段提交：影子目录预复制（有清单时逐文件校验源哈希）→ 源校验通过后创建当前存档快照
  （`.restore-undo-*`）→ 覆盖存档；生产路径写入后不读回目标（仅故障注入测试路径读回）；
  校验失败抛 `SaveBackupVerificationException`，失败用快照回滚；峰值额外空间约 2× 存档体积；
- 没有清单的旧备份 `HasManifest=false`，按「未校验」提示；仍走影子复制与快照回滚，但无法做哈希比对；
- `Rename`/`Delete` 只允许作用于 `Saves/<gameId>/<backup>` 这一层；名称拒绝非法字符、`.`/`..`、尾随点空格；
- 性能取舍：写清单后直接复用总字节刷新体积缓存；导入仍保留少量内存列表复制；非时间戳命名的备份
  每次列表读取一次目录创建时间。

## 9. 通用与多游戏辅助

| 类型/方法 | 说明 |
|---|---|
| `GameScope.EnsureBelongsTo(mod, gameId)` | 内部校验记录属于当前游戏；未归属/跨游戏抛 `InvalidOperationException` |
| `ModFileRescanner.Rescan(paths, mods, fileSystem)` | 启动时按 `Mods/<id>/files` 实际内容修正 `RelativeFiles`，不改 `Kind`/`TargetDirectory` |
| `LegacyPluginPathRepair.Repair(mods, paths, gameRootFor, fileSystem)` | 一次性修复旧版本错误推断的 `nativePC`/`reframework` 前缀，并把已部署文件移到新落点 |
| `IFileSystem.TryDeleteFile/TryDeleteDirectory` | 统一「清理失败不影响主流程」的尽力删除（捕获占用/权限/安全策略异常） |
| `IFileSystem.TryDeleteFileForce/TryDeleteDirectoryForce` | 删除前清除只读属性；支持取消；链接（含断链）只删链接本身，子树含链接或扫描失败时整体跳过（fail-closed） |
| `IFileSystem.FileExists/DirectoryExists/CreateDirectory/Enumerate*` | 读写基础能力；文件枚举、文本读写、复制/移动/删除保持 BCL 异常语义 |
| `IFileSystem.ReadAllText/WriteAllText/GetFileLength/Get*LastWriteTimeUtc` | 文本与元数据读取，供存档清单与临时清理等用例使用 |
| `IFileSystem.OpenRead/OpenWrite` | 流式读写（共享读 / 独占创建），供存档备份还原、包指纹与哈希清单的复制+校验一次读盘使用 |
| `IClock` / `SystemClock.Instance` | 可替换时钟，避免业务代码直接依赖 `DateTimeOffset.Now` |
| `IAppLogger` | 日志端口（Info/Warn/Error），生产实现为 Infrastructure 的 `AppLogAdapter`（A-23） |
| `IUndoScope` / `IUndoScopeFactory` | 文件事务补偿端口：登记前像/新建/移走，失败逆序回滚；工厂负责按操作创建（A-10） |
| `FileUndoJournal` / `FileUndoScopeFactory` | `IUndoScope` 的默认文件级实现与工厂 |
| `ISettings` | 设置端口：`Get/GetBool/Set/SetBool/Remove/UpdateBatch` + `IsReadOnly/LoadWarning` |
| `SettingsCoordinator` / `GameSettingsSnapshot` | 游戏设置的键名、默认值、读取快照与一次批量保存（A-06） |
| `GameSession` | 当前游戏与游戏根目录的唯一状态来源：从设置加载、切换游戏、重读目录、采纳自动探测结果（A-06；窗口不再持有 `_game`/`_gameRoot`） |
| `ModListCoordinator` | 当前游戏的 MOD 顺序读取、序号归一、移动到目标下标与失败回滚（A-06；窗口只保留提示与落盘） |
| `BackgroundTaskHost` / `IBackgroundTaskHost` | 串行登记后台写盘任务，提供 `PendingCount` 与空闲等待 `WhenIdleAsync(timeout)`（A-07；替代窗口自维护的保存链与计数器） |
| `MainWindowState` | 主窗口的纯视图状态：忙碌/选中/列表非空、派生按钮可用性与取消状态；App 的 `MainWindowViewModel` 只做 `Visibility` 适配 |

## 10. 事务与回滚矩阵

| 操作 | 成功路径 | 失败回滚 |
|---|---|---|
| 安装 | `.staging` 写完 → 改名换入 → 写记录 → Save | 删暂存；不写记录；ID 不落盘 |
| 重装/更新 | 旧 `files` 改名 `.old-*` → 新目录换入 → 更新记录 | 恢复旧目录与旧记录 |
| 卸载 | 目录改墓碑 → Remove → Save → 删墓碑 | 保存失败恢复目录；记录与目录都保留 |
| 启用（pak） | 计划 → 写 `pak_mods` → 记录 undo | `FileUndoJournal` 恢复覆盖/移动的文件 |
| 启用（散装） | 计划 → 复制/备份 → 记录 undo | 逆序撤销，抛 `IOException` |
| 启用（跨链路） | pak → 散装 → `Enabled=true` → Save | `ModActivationService` 补偿两条链路 |
| 禁用 | 散装撤出 → pak 撤出 → `Enabled=false` → Save | 尽量恢复为「仍启用」 |
| 清理 | 逐项安全禁用 → 可选残留删除 | 取消时已完成项保持，未处理项原状 |
| pak 改名 | `.toc.pending` → 写 TOC → 提升 `.toc.bak` | 内存原始 TOC 立即回滚；失败保留 pending |
| pak 内容替换 | 临时副本 + 原子替换 | 原 pak 不变，只清临时文件 |
| 存档还原 | 影子复制校验 → 覆盖 → 目标再校验 | 当前存档快照回滚 |
| 索引/INI | `.tmp` + 原子替换 | 原文件不变 |

## 11. 异常契约

| 场景 | 异常 |
|---|---|
| 安装来源不存在 | `FileNotFoundException` |
| 包无法识别/损坏/密码错误/条目越界 | `InvalidDataException` |
| 未选中任何文件 / 重装已启用 MOD / 跨游戏操作 | `InvalidOperationException` |
| pak 同名内容冲突 | `PakConflictException` |
| 本 MOD 的 pak 源缺失 | `FileNotFoundException` |
| 游戏运行中（设置开启） | `InvalidOperationException` |
| 取消 | `OperationCanceledException` |
| 文件复制/删除/改名失败 | `IOException` |
| 存档还原校验失败 | `InvalidDataException` |
| TOC 加密且未注入重打包实现 | `NotSupportedException` |
