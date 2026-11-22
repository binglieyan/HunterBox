# HunterBox 架构设计

> 目标：一个**可长期维护**的怪物猎人 MOD 管理器。工程标准优先于短期功能速度。
> 行为规格见 [SPEC.md](SPEC.md)；实现细节、模块职责与流程见 [Wiki](wiki/README.md)。

## 1. 设计原则

| 原则 | 落地方式 |
|---|---|
| 依赖只能指向内层 | Domain 无项目引用；Application 只依赖 Domain；Infrastructure 依赖 Domain + Application（实现其接口）；App 是组合根，依赖三层 |
| 领域逻辑纯函数化 | 放置/冲突/备份/命名/模型身份决策全部是「输入状态 → 输出计划」，可单测、无 IO |
| 副作用集中在边界 | 文件读写只在 Infrastructure 或 Application 的 `*Service` 里发生；UI 只编排用例 |
| 不可变优先 | 决策结果用 `sealed record`；只有聚合根（`ModRecord`）是可变对象 |
| 失败要显式 | 业务失败抛具体异常类型；不用返回值表示错误；UI 统一捕获转中文提示 |
| 数据安全默认开启 | 路径一律经 `BoxPaths.ResolveInside` / `NormalizeRelative`（zip-slip、`..`、绝对路径拒绝）；索引/INI 原子写；启用/禁用/安装/重装均带撤销或换入机制；损坏索引保留原件 |
| 事务性操作 | 安装先写 `Mods/.staging` 再改名换入；重装旧目录先改名保留；启用/禁用/清理记录撤销日志逆序回滚；失败时内存 `Enabled` 与磁盘保持一致 |
| 可测试性 | 外部依赖（压缩库、存储、pak 读写、路径表、进程检测）通过接口注入，测试用临时目录 + 假实现 |
| 可诊断性 | 文件日志、崩溃报告、操作失败状态栏 + 日志双写；`LoadWarning` 等恢复提示不静默 |

## 2. 分层与依赖方向

```
┌───────────────────────────────────────────────────────────────┐
│ HunterBox.App (WinUI 3)                                       │
│ 组合根：App 启动装配 + AppServices（含 GameDiscoveryService）    │
│ 只做：视图、命令编排、依赖装配、中文提示                         │
└───────────────┬───────────────────────────────┬───────────────┘
                │ 依赖                          │ 依赖
┌───────────────▼───────────────┐   ┌───────────▼───────────────┐
│ HunterBox.Application         │   │ HunterBox.Infrastructure  │
│ 用例：安装/启用/清理/模型/存档/  │   │ 实现：JSON 存储、SharpCompress│
│ pak 挂载（只依赖 Domain 抽象）  │   │ 解压、INI、pak 读写、日志、   │
│                               │   │ Steam 定位、旧数据迁移        │
└───────────────┬───────────────┘   └───────────┬───────────────┘
                │ 依赖                          │ 依赖
                └───────────────┬───────────────┘
┌───────────────────────────────▼───────────────────────────────┐
│ HunterBox.Domain                                              │
│ 模型 + 纯决策 + 接口（无项目引用、无第三方包）                    │
└───────────────────────────────────────────────────────────────┘
```

依赖规则（评审时逐条检查）：

1. **Domain 不得引用** Application/Infrastructure，不得引入第三方包；只允许 `System.*` 基础类型与
   `System.Text.RegularExpressions` 等 BCL。
2. Application 通过接口使用存储/解压/pak/指纹，**不得**出现 `SharpCompress`、`JsonSerializer`、`IniSettings` 字样。
3. Infrastructure 只实现接口与外设适配，不新增业务判断；唯一项目引用是 Domain 与 Application
   （`ILegacySaveImporter` 在 Application 定义、由 Infrastructure 实现）。
4. App 层不得自行实现文件复制/解压/JSON 序列化等业务性 IO：存储/配置/日志走 Infrastructure 的边界服务
   （`ModRepository`、`IniSettings`、`AppLog`）或 Application 用例；App 只保留 UI、进程/链接等 OS 交互与组合根职责。
5. 跨项目契约必须 `public` 且带 XML 注释；只服务单个程序集的实现默认 `internal`（个别为测试暴露的入口通过
   `InternalsVisibleTo` 或测试用公开静态方法处理，新增公开 API 需在评审时确认必要性）。

## 3. 模块职责

### Domain（`HunterBox.Domain`，net10.0）

| 命名空间 | 主要内容 | 说明 |
|---|---|---|
| `.Layout` | `BoxPaths` | HunterBox磁盘布局、相对路径规范化、越界拒绝、游戏 ID 安全校验 |
| `.Games` | `GameProfile` | 单款游戏静态配置（ID/显示名/pak 前缀与基础编号/Steam AppId/主程序/默认目录名）与目录校验 |
| `.Mods` | `ModRecord`、`ModMetadata`、`ModKind`、`PackageInspector`、`PackageInspection`、`ModInfoParser`、`LooseFilePlanner`、`MountContext`/`FileOperation`、`IModStore`、`ModLinks`、`FomodConfig`/`FomodParser`、`FomodSelectionState`/`FomodDependencyEvaluator` | MOD 模型与聚合根、包识别、元数据解析、散装文件放置决策、FOMOD 语义（步骤/分组/插件/依赖/路径安全） |
| `.Paks` | `PakSource`、`PakPlacementOptions`、`PakPlan`、`PakPlanner`、`PakProfile`、`PakNamingMode`、`PakNameIndex`、`IContentFingerprint`、`PakConflictException` | pak 命名策略（编号/原名）、冲突判定、内容指纹抽象、名称索引 |
| `.Models` | `ModelIdentity`、`ModelTypes`、`ModelPathRecognizer`、`ModelIdRewriter`、`IModelCatalog` | 完整模型身份（护甲/猫/武器）、路径识别、路径改写纯逻辑、模型候选清单抽象 |
| `.Archives` | `ArchiveFormat`、`ArchiveFormatDetector`、`ArchiveExtractionOptions`、`ArchiveProgress`、`IArchiveExtractor` | 压缩格式纯字节探测、解压抽象与进度/密码/嵌套策略 |

### Application（`HunterBox.Application`，net10.0）

| 命名空间 | 主要内容 | 说明 |
|---|---|---|
| `.Install` | `ModInstaller`、`InstallStaging`/`InstallPlan`/`InstallCommitter`、`InstallSourceArchiver`/`InstallSourceLocator`、`PackageFilters`、`PackageVariantScanner`、`InstallEntryClassifier`、`InstallFileRules` | 解压→识别→暂存→原子提交；重装/更新；安装源归档；排除清单与包指纹；多组件变体；拖拽条目分类；安装树默认勾选规则 |
| `.Migration` | `LegacyModRecordFactory` | 旧数据导入的记录构建规则（统一未启用，供 `LegacyMigrationService` 复用） |
| `.Activation` | `ModActivationService`、`ActivationOptions`、`ActivationReport`、`GameRunningGuard`、`IGameProcessDetector` | 启用/禁用/重新复制的编排、事务补偿、游戏运行前置守卫、pak 内部文件重叠提示 |
| `.Mount` | `MountService`、`MountReport` | 执行散装文件计划：复制、备份、还原、删除、清空目录、重叠文件重拷贝与撤销 |
| `.Paks` | `PakMountService`、`PakMountReport`、`PakArchiveInfo`/`PakEntryInfo`、`IPakArchiveReader`/`IPakEntryReader`、`IPakRepacker`、`PakRepackProfile`/`PakRepackProgress`、`PakFingerprintCache` | `pak_mods` 的实际写入/删除：编号稳定化计划、撤消日志、源缺失容忍；pak 档案信息与 I/O 端口（A-25）；进程级内容指纹缓存 |
| `.Models` | `ModelDetector`、`ModelAutoClassifier`、`ModelTextPrefetchService`/`KnownTextSignature`、`ModelTextCache`/`ModelTextCacheSnapshot`、`ModelTextCachePersistScheduler`、`ModelIdentityCache`/`ModelIdentitySnapshot`/`ModelDetailsCacheEntry`/`ModelUsedIdsCacheEntry`、`ModelIdentityStampCache`、`ModelStampRefresh`、`ModelFolderEditor`、`ModelDetail`、`ModelEditorPlanner`、`ModelRewriteCoordinator`、`PakModelRewriteService`、`LooseModelRewriteService`、`ModelRewriteSummary`/`ModelRewriteReport`、`IPakEntryPathReader`/`IModelNameResolver`/`IModelTextCacheStore`/`ModelTextCacheEntry` | 模型行检测、安装/加载后自动分类、未安装文件夹扫描改写、编辑器候选逻辑、pak TOC 路径哈希改写、加密 TOC 解包重打包回退、散装改名、批量汇总；`ModelTextCache` 是 `ModelPanel` 持有的线程安全内存缓存（按游戏 + MOD 隔离，版本号用于跳过无变化写盘），`ModelIdentityCache` 是带锁的明细/已用身份两级 LRU（内容戳校验、按游戏隔离），`ModelIdentityStampCache` 提供身份戳的会话内缓存（10 分钟绝对 TTL、按 MOD 分散 3–5 分钟、不可读结果 30 秒退避、按 MOD 精确失效，同记录实例不重复全库 stat，目标 MOD 每次打开强制重算），`ModelStampRefresh` 负责过期戳的时间预算轮换（至少推进一项、取消与单项异常隔离），`IModelTextCacheStore` 是组合根持有的跨会话持久化端口（`TryLoad` 区分「没有缓存」与「读取失败」，`Save` 返回成败供调度器重试），`ModelTextCachePersistScheduler` 提供去抖/重试状态机 |
| `.Saves` | `SaveBackupService`、`SaveBackupStore`、`SaveBackupRestorer`、`SaveBackupFormatter`、`SaveBackupManifest`、`SaveBackup`、`VerifiedDirectoryCopy`、`ILegacySaveImporter`、`SavePathSelection` | 存档目录备份/还原/重命名/删除、哈希清单与快照清理、旧 zip 导入抽象；存档路径候选的歧义策略；备份列表体积使用进程级有界 LRU 缓存（单锁 `SizeCacheGate` + `Dictionary`/`LinkedList` 索引，`MaxSizeCacheEntries` 512、单游戏 `MaxSizeCacheEntriesPerGame` 256，按游戏计数双层淘汰；`NegativeSizeCacheLifetime` 2 分钟；键统一 `GetFullPath` 并去尾分隔符，按游戏分组递增失效版本，`ResetSizeCache` 递增代次阻止在途扫描回写；`BackupSizeDirectoryScanCount`/`BackupSizeManifestReadCount`/`BackupSizeMetadataProbeCount` 与 `SizeCacheEntryCount`/`SizeCacheContains`/`ResetSizeCache` 仅供测试） |
| `.Cleanup` | `ModCleanupService`、`CleanupScope` | 「删除全部MOD文件」两个安全范围 |
| `.Mods` | `GameScope`、`GameAssignmentService` | 游戏归属守卫与「未归属 MOD 归到当前游戏」用例 |
| `.Common` | `IUndoScope`/`IUndoScopeFactory`、`FileUndoJournal`/`FileUndoScopeFactory`、`IAppLogger`、`IClock`、`CloseWaitBudget`、`OperationError`、`SafeFileTree`、`FileCopyBuffer` | 文件事务补偿抽象与默认实现、日志端口（含 `WarnBatch` 批量写入）、可替换时钟、关窗墙钟预算 |
| `.Settings` | `ISettings`、`SettingsCoordinator`/`GameSettingsSnapshot`、`GameSession`、`GameDiscoveryService`、`IGameInstallProbe` | 配置端口、设置键名/默认值/批量保存协调、当前游戏会话 |

### Infrastructure（`HunterBox.Infrastructure`，net10.0）

| 命名空间 | 主要内容 | 说明 |
|---|---|---|
| `.Storage` | `ModRepository` | `Mods/index.json` 持久化：SchemaVersion、NextId 高水位、原子写、`.bak`、损坏保留、旧数组格式兼容、按游戏查询/重排/归属 |
| `.Archives` | `ArchiveExtractor` | SharpCompress 实现：按魔数解 zip/7z/rar/tar/gz 等、GBK 编码、密码猜测、嵌套解压、进度、zip-slip 防护 |
| `.Settings` | `IniSettings` | `hunterbox.ini` 读写：`[settings]`/`[<gameId>]`、旧键别名、原子写、损坏降级（旧 `sjmodbox.ini` 迁移在 `.Migration`） |
| `.Paks` | `RePakReader`、`RePakEntryReader`（测试工具）、`RePakWriter`（测试工具）、`RePakRenamer`、`Murmur3`、`PakPathHasher`、`FileContentFingerprint`、`EmbeddedPakNameIndex` | RE PAK v2/v4 解析、TOC 哈希改名、路径哈希、内容指纹、内嵌荒野路径清单；条目读取/等长写回仅作为测试校验工具 |
| `.Mods` | `EmbeddedNameTables`、`ModelCatalog`、`ModelNameResolver` | 内嵌名称表加载、荒野模型候选目录、路径→可读名字 |
| `.Games` | `SteamGameProbe`、`SteamGameLocator`、`SteamLibraryIndex`、`SteamConfigIndex`、`SteamVdf`、`SteamSavePathLocator`、`GameProcessDetector` | Steam 注册表/userdata 探测与纯逻辑定位；缺省才写回规则在 Application `GameDiscoveryService`；进程名检测 |
| `.Logging` | `AppLog`、`AppLogAdapter`、`CrashReporter` | 文件日志（按日、可降级目录）、`IAppLogger` 适配器（A-23 组合根注入）、崩溃报告（版本/系统/路径/异常链） |
| `.Migration` | `LegacyMigrationService` | 旧 `Mods_<appid>`/`info.xml`/`Backup_<appid>` 检测与只读复制导入 |
| `.Saves` | `SharpCompressSaveImporter` | 旧 `Saves_<appid>/*.zip` 解压为目录备份 |
| `.IO` | `SystemFileSystem`、`CorruptFileQuarantine` | `IFileSystem` 实现（删除/链接安全、目录元数据一次读取、长路径前缀）；损坏文件 `.bad-*` 统一命名、保留份数与清理 |
| `.Models` | `JsonModelTextCacheStore` | 模型文本缓存：基础文件（版本 3 + 世代号）+ `.journal` 增量日志；本次保存的变更总量估算字节达到 max(1MB, 基础/4) 时直接整份写，日志达到同一阈值时压缩，小体积的大量变化继续走日志；损坏日志先写基础后隔离，失败则重写合法前缀或保留原日志等待重放；混世代/混版本只跳过对应行；除每会话第一次整份写外，真实整份写按单调时钟节流（至少 1 秒、不超过 8MB/s、单次 Save 累计等待默认封顶 10 秒且可注入），仅追加不等待，零变化但日志达到压缩阈值仍会等待 |

模型文本缓存的职责边界：`BaseWriteAttempts`、`JournalAppendAttempts`、`JournalSerializeBatchCount`、`LastJournalSerializedBytes`、`JournalTruncatedBatchCount`、`ChangeDiffCount`、`FileExistsProbeCount`、`BaseDestinationProbeCount` 是 `internal`
诊断计数，仅供同层测试工程（`InternalsVisibleTo`）断言写盘、追加、截断与探测次数，不暴露给生产调用方；
日志容量不足的告警按存储实例（一次会话）限频一次；
清理退避与整份写节流（至少 1 秒且不超过 8MB/s，单次 Save 累计等待默认封顶 10 秒、可注入）是存储对自身磁盘资源的保护，
不改变调用方的保存语义。10 秒与 App 的启动保存/单次缓存空闲等待同值；关窗总预算是 60 秒墙钟上限，
因此单次保存不会独自耗尽关窗预算。跳过剩余节流（`allowEarly`）有两类场景：启动装载修复损坏日志，
以及单次 Save 累计等待预算耗尽后的强制写入（后者从直接写、追加失败回退与压缩三个调用点传入）；
本次会话第一次整份写本身免等待（每会话至多一次，通常发生在后台保存线程，首写全程持锁的取舍见代码注释）。
保存失败后的重试调度（次数、间隔、是否放弃）仍由 Application 的 `ModelTextCachePersistScheduler` 决定。

### App（`HunterBox.App`，net10.0-windows10.0.19041.0，win-x64）

| 文件/类 | 职责 |
|---|---|
| `App.xaml.cs` | 日志初始化、三类全局异常钩子、启动失败兜底 |
| `MainWindow.xaml(.cs)` + `MainWindow.*.cs` | 主窗口：列表/游戏切换/过滤搜索/命令编排/右键菜单/启动提示；按职责拆为多个 partial |
| `MainWindowViewModel` | 主窗口界面状态：忙碌/选中/列表非空、按钮可用性、忙碌环、取消按钮与状态栏文本（x:Bind 订阅） |
| `OperationRunner` / `InstallRefreshCoordinator` | 长任务忙碌门禁与取消；安装/重装后的刷新、状态与选中 |
| `InstallDialog` / `ModInfoDialog` / `ModelEditorDialog` / `MoveToDialog` / `SettingsDialog` / `SaveManagerDialog` / `LegacyMigrationDialog` | 各功能对话框 |
| ~~`GameDetector`~~ | 已移除：注册表探测在 Infrastructure `SteamGameProbe`，缺省才写回与失败隔离在 Application `GameDiscoveryService` |
| `AppModelNameResolver` | 把 Infrastructure 名称表适配为 Application 的 `IModelNameResolver` 端口 |
| `FileTreeItem` / `DialogPickers` | 安装文件树节点、文件/目录选择器 |
| `HunterBox.Publish.targets` + `Properties/PublishProfiles/FolderProfile.pubxml` | 发布目标：清目录、补 pri、精简语言/组件、剔 pdb（压缩包由手动打包） |

### 测试（`tests/HunterBox.Tests`，net10.0，xUnit）

- 覆盖 Domain 纯逻辑、Application 临时目录集成、Infrastructure 真实文件/zip/pak 读写与损坏恢复。
- 重点回归：P0 安全（勾选回滚/清理范围/跨游戏隔离/FOMOD 越界/索引损坏/pak TOC 哈希）、安装原子性、
  pak 编号稳定性、模型身份与加密 pak 拒绝、INI 迁移、存档管理、丢弃旧数据迁移。
- 门禁脚本一致性：构建后与 VSTest 前比较源码与部署副本的 SHA-256，缺失或漂移直接失败。
- 测试方法规模由源码统计（`[Fact]` 1191 / `[Theory]` 63 / 派生 `[Fact]` 78，共 1332 个测试方法）；运行计数以 `dotnet test HunterBox.slnx -c Release` 输出为准，当前版本 v1.5.207 不再写入仓库文档。

## 4. 关键流程

### 4.1 安装 / 重装

```
用户选择包/文件夹/裸文件/拖拽
  → InstallEntryClassifier 分类（压缩包/目录/单文件/不支持）
  → [后台] ModInstaller.StageCore：Temp 解压 → 剥单层外壳 → PackageInspector.Inspect
  → InstallStaging（预览图/元数据/变体/FOMOD 解析，均只读暂存）
  → InstallDialog 用户确认（名称去重、文件选择、FOMOD/变体选择、多根/前缀）
  → 写 Mods/.staging/<guid>/files → 改名为 Mods/<id>/files → 写记录 → 保存索引
  → 按安装选项归档安装源 → ModelAutoClassifier 自动分类
```

重装/用新包更新复用同一暂存与向导：旧 `files` 先改名 `.old-<guid>`，换入成功后更新记录（保留
`Id`/`Index`/`GameId`），失败恢复旧目录与旧记录。

### 4.2 启用 / 禁用

```
ModActivationService.Enable/Disable
  → GameScope 校验归属 + GameRunningGuard（设置开启时）
  → SplitFiles：根层 .pak → pak 链；其余 → 散装链
  → [pak] PakMountService.PlanEnable（先算计划，冲突/源缺失直接抛）→ 写 pak_mods
  → [散装] MountService：LooseFilePlanner.PlanEnable/PlanDisable →
       Copy / BackupThenCopy / RestoreBackup / Delete / 重叠文件重拷贝
  → 两链路各自撤销日志；跨链路失败由 ModActivationService 补偿
  → 最后写 ModRecord.Enabled + 保存索引；失败恢复状态并抛可读异常
```

### 4.3 pak 编号稳定化

1. 收集当前游戏全部启用 MOD 的根层 pak 源（源缺失：本 MOD 启用前直接失败，其它 MOD 跳过并 warning）。
2. 扫描 `pak_mods/` 已有 patch 编号与内容指纹，建立「内容 → 现有文件名」映射；扫描游戏根目录 patch 号参与编号起点。
3. 已有内容沿用原编号；新内容占用「最小可用编号」；同批同内容的多个源共用同一编号。
4. 禁用时只删除本 MOD 占用且不被剩余启用 MOD 计划保留的编号文件；不重排其它 MOD 的编号。

### 4.4 模型改写

```
ModelAutoClassifier.DetectDetails（散装文件 + 根层 pak 条目路径还原）
  → ModelEditorDialog（完整身份、同类候选、仅显示未使用、已启用只读、必选加回当前模型；
      目标用按钮弹出带搜索的候选面板，单击选中、有查询时回车选第一项）
  → ModelRewriteCoordinator
      → PakModelRewriteService.RewritePaths：路径清单还原 → 完整身份替换 → 只改 TOC 8 字节哈希（先 .bak）
      → LooseModelRewriteService.Rewrite：模型路径 File.Move（不改内容）
  → 汇总 Warnings / UnsupportedPaks；重算分类与备注
```

整张 TOC 加密（feature 8/24）的 pak：`PakRepacker` 解包→改名→重打包为明文 TOC 的 mod pak；失败不覆盖原文件。

### 4.5 存档与旧数据迁移

- 存档：`Saves/<gameId>/<yyyyMMdd-HHmmss>/` 目录备份；恢复覆盖同名文件；重命名/删除只允许操作
  `Saves/<gameId>/<backup>` 这一层；旧 zip 由 `ILegacySaveImporter` 解压进新建备份槽。
- 旧数据：`LegacyMigrationService.Detect` 扫描旧目录（不写盘）→ 用户确认 → `Import` 只读复制 MOD 与原文件备份；
  `LegacyMigrationService.MigrateSettings` 迁移旧配置；旧启用状态不沿用（统一为未启用），导入后写 `legacy_imported` 标记。

### 4.6 日志与异常

- `AppLog.Initialize()` 选择可写日志目录（程序目录 → `%LOCALAPPDATA%`）；INFO/WRN/ERR 分级、按日一个文件。
- `App.OnLaunched` 初始化日志并挂三类异常钩子；`CrashReporter` 写 `crash-*.txt`，写失败返回 `null` 并带 `LastError`。
- 存储/解压/pak/设置边界统一记录关键路径日志，失败路径带完整异常（便于用户反馈）。

## 5. 错误模型

| 场景 | 异常 | 上层处理 |
|---|---|---|
| 来源不存在 | `FileNotFoundException` | 提示用户选的路径失效 |
| 包无法识别 | `InvalidDataException` | 提示「无法识别的 MOD 格式」并说明支持的结构 |
| 压缩包损坏/加密密码错误 | `InvalidDataException`（含中文原因） | 提示手动解压或改名重试 |
| 压缩条目越界 / 路径非法 | `InvalidDataException` / `ArgumentException` | 拒绝安装并记录日志 |
| FOMOD 映射越界/缺失 | 解析 warning + 提交前二次校验 | 提示并跳过非法映射；不越界读写 |
| pak 同名内容冲突 | `PakConflictException` | 弹窗列出冲突双方，拒绝启用 |
| pak 源缺失（启用本 MOD） | `FileNotFoundException` | 在改动任何文件前失败并提示重装 |
| pak TOC 加密 | 解包重打包回退 | 输出明文 TOC 的 mod pak；失败时保留原文件并说明 |
| 游戏运行中操作（设置开启） | `InvalidOperationException` | 提示先退出游戏 |
| 已启用就卸载/重装 | `InvalidOperationException` | 提示先禁用 |
| 索引读取失败 | `IOException` | 启动失败兜底提示（不静默空列表） |
| 索引损坏 | 无异常；`LoadWarning` | 保留 `.bad-*` 原件并提示从 `.bak` 恢复 |
| INI 保存失败 | `IOException`（含路径） | 设置页显示保存失败，磁盘原值不变 |
| 卸载删除失败 | `IOException`（含重试说明） | 记录与目录保留，可关闭占用后重试 |

不用错误码返回值；UI 层统一捕获并转成中文提示，同时写日志。

## 6. 数据格式与兼容

- `Mods/index.json`：`{ SchemaVersion, NextId, Mods[] }`（PascalCase 字段，`ModKind` 按数字序列化以兼容旧索引）；
  读取兼容早期「只有数组」的格式；缺失 `GameId`/`InstallSource` 等新字段按 `null` 处理。
- `hunterbox.ini`：`[settings]` + `[<gameId>]` 两段；旧键名读取兼容、旧键保存时清理；旧 `sjmodbox.ini` 由迁移服务只读导入。
- 旧数据导入：只读源目录；写入新HunterBox格式（`Mods/index.json`、`Mods/.backup/<gameId>`、`Saves/<gameId>/...`）。
- 原子写策略：索引/INI 均「先写 `.tmp` 再替换」；清单元数据与文件落盘顺序保证失败不留半成品。

## 7. 编码规范

1. **命名**：类型/方法/属性 PascalCase；私有字段 `_camelCase`；常量 PascalCase；接口 `I` 前缀。
2. **文件**：一个文件一个公开类型；文件名与类型名一致。
3. **长度**：方法 > 40 行要拆分；类型 > 400 行考虑拆分职责（超大服务按链路拆成 Planner/Service）。
4. **可变性**：默认 `sealed`；`record` 用于值语义；`class` 仅用于有身份或需要可变的聚合根。
5. **空值**：`Nullable` 全开；对外 API 不接受 `null` 表示「没有」，用空集合或 `TryXxx`。
6. **异常**：只抛具体类型；不使用 `Exception` 基类；不吞异常（日志 + 上抛或显式降级）。
7. **异步**：IO 密集的公开 API 用 `async Task`；纯计算保持同步；UI 侧长操作走后台线程并保持 BusyRing 门禁。
8. **测试**：每个公开用例至少一个测试；测试命名 `Method_Scenario_Expectation`；不测实现细节。
9. **依赖注入**：外部能力通过接口传入用例构造函数；默认参数只用于可选能力（如 `pakReader` / 守卫）。
10. **工程约束**：`Directory.Build.props` 开启 `Nullable`、`ImplicitUsings`、`TreatWarningsAsErrors`；
    四个项目开启 `GenerateDocumentationFile`。

## 8. 注释标准（Javadoc 风格）

- **所有 public 类型、成员必须有 XML 文档注释**（`<summary>`），缺失即编译错误（`CS1591` 视为错误）。
- 带参方法必须写 `<param>`；有返回值写 `<returns>`；可能抛异常写 `<exception>`。
- 注释说明**契约与约束**，不描述实现细节；实现注释只在「为什么这么做」不明显时写
  （例如「先登记再覆盖，回滚才能恢复」这类事务性原因）。
- 注释语言：中文；类型/成员名保持英文。

## 9. 测试策略

| 层次 | 方式 | 代表测试 |
|---|---|---|
| Domain 决策 | 纯单测，假指纹/假上下文，覆盖边界与冲突 | `PakPlannerTests`、`PakNumberStabilityTests`、`LooseFilePlannerTests`、`FomodSemanticsTests`、`FomodPathSecurityTests`、`ModelIdentityTests`、`ModelIdRewriterTests` |
| Application 用例 | 临时目录集成测试：真解压、真复制、真备份还原、真 pak 计划 | `ModInstallerTests`、`InstallAtomicityTests`、`ModActivationServiceTests`、`PakMountServiceTests`、`ModCleanupServiceTests`、`SaveBackupManagementTests`、`ModelIdentityPakRewriteTests` |
| Infrastructure | 临时目录 + 真实 zip/pak 读写、损坏恢复、迁移 | `ModRepositoryCorruptionTests`、`IniSettingsLegacyMigrationTests`、`ArchiveExtractorFormatTests`、`RePak*Tests`、`LegacyMigrationTests`、`SteamDetectionTests`、`AppLogTests`/`CrashReporterTests` |
| 安全/多游戏 | 跨游戏隔离矩阵、P0 回归 | `MultiGameIsolationTests`、`ActivationSafetyTests`、`PakInternalConflictTests`、`PakRenameTests` |
| UI | 发布前手动验收（可选 UI Automation 辅助） | 验收截图/证据按需临时生成，不入库。 |

- 每次提交前跑 `dotnet test HunterBox.slnx -c Release`，必须 0 失败；新增行为必须有回归测试。
- 覆盖率目标：Domain 与 Application 分支覆盖 ≥ 80%（`dotnet test --collect:"XPlat Code Coverage"` 检查；
  当前未作为 CI 门禁，属于人工抽检）。
- 真实游戏端到端（加载/模型显示/存档）属于发布验收，单元/集成测试不能替代。

## 10. 里程碑

| 阶段 | 内容 | 状态 |
|---|---|---|
| M1 | Domain 规则库 + 单元测试（路径安全、包识别、pak 放置、散装规划） | 完成 |
| M2 | Application 用例：安装 / 启用 / 禁用 / 卸载 / 清理 / 存档（文件系统级） | 完成 |
| M3 | WinUI 3 外壳：主窗口、列表、对话框、右键菜单、过滤/排序/状态反馈 | 完成 |
| M4 | 模型编辑器、pak 读写、存档管理、设置页、日志与异常 | 完成 |
| M5 | 功能验收与发布产物切换 | 完成 |

> 版本变更见 `src/HunterBox.App/HunterBox.App.csproj` 的 `Version` 与 git 提交记录（仓库不再维护 CHANGELOG）。

## 11. 架构取舍与后续重构

2026-11-17 架构评审中，下列项需要跨层设计或重写核心数据路径，当前版本以文档化取舍收尾；
后续如启动「文件系统端口化」或 UI ViewModel 改造，按表中前置条件推进：

| 项 | 现状 | 取舍原因 | 前置条件 / 下一步 |
|---|---|---|---|
| A-29 `FileSystemUtil` 归属 | 已完成：删除/清理统一走 `IFileSystem`，实现移到 `Infrastructure/IO/SystemFileSystem`，Application 注入端口 | 链接安全规则只有一份，Infrastructure 不再引用 Application 的具体实现 | 无 |
| A-02 Application 直接文件 IO | 已完成：Application 不再直接调用 `File`/`Directory`，读写、枚举、流式复制与删除全部经 `IFileSystem`；静态入口显式接收端口，组合根统一注入 | 端口为唯一实现边界，用例可用测试替身脱离真实磁盘 | 无 |
| A-10 回滚实现多套 | 部分完成：已抽出 `IUndoScope`/`IUndoScopeFactory`，`MountService` 与 `PakMountService` 的启用/禁用撤销日志统一经工厂创建（默认 `FileUndoJournal`）；目录交换、快照回滚、TOC 前像仍保留各自实现 | 粒度不同（单文件/整目录/整包），强行统一会放大回滚风险 | 评估 `InstallStaging` 目录交换、`SaveBackupService` 快照回滚、`LegacyMigrationService` 与 `PakTocPreImage` 的可迁移性；先补跨实现契约测试 |
| A-06 / A-07 `MainWindow` 上帝类与后台任务 | 部分完成：partial 拆分 + `OperationRunner`；`ModListCoordinator`（顺序）、`GameSession`（当前游戏/根目录）、`SettingsCoordinator`（设置键名/默认值/批量保存）、`InstallRefreshCoordinator`（安装后刷新）、`BackgroundTaskHost`（串行写盘/空闲等待）已迁出窗口；`MainWindowViewModel` 已引入，视图状态下沉为 Application 的 `MainWindowState` 并覆盖 5 项回归测试 | 视图命令与 WinUI 控件仍深度耦合，命令迁移需要 ViewModel 与绑定层设计 | 继续把命令本身与列表投影迁入 `MainWindowState`，逐步移除窗口事件处理器里的业务编排 |
| A-16 剩余用户文案 | 部分完成：存档备份校验已改为 `SaveBackupIssue` 结构化问题 + `SaveBackupVerificationException`，用户文案由 `SaveManagerDialog` 统一映射；安装/模型改写的异常消息仍由 Application 生成 | 异常消息同时承担诊断信息，结构化问题对象需要 UI 全量映射 | 定义 `InstallProblem` 等结果模型，按安装→模型改写→存档的顺序迁移剩余文案 |
| A-23 静态日志/锁表/时钟 | 已完成：`IAppLogger` 端口与 `AppLogAdapter` 覆盖解压、迁移、索引、配置、旧存档导入、Pak 子系统与整个 App 层（主窗口、工作流、对话框、游戏探测）；`AppServices` 是日志端口的唯一装配点。仅 `App.xaml.cs` 保留静态 `AppLog`（日志初始化与崩溃钩子运行在组合根之前，且不能依赖可能已损坏的对象图）。`PakPathLock`、`SystemClock.Instance` 保留进程级静态实例；`SteamGameProbe` 的探测缓存改为实例字段（随组合根生命周期）；`ModelNameResolver` 保留三个静态名称缓存（武器名、名称表、操作码）；`PakFingerprintCache` 由组合根持有并设条目上限；`IModelTextCacheStore`（`model-text-cache.json`）是跨会话持久化端口，`ModelTextCache` 是 `ModelPanel` 持有的线程安全内存缓存（两者职责不同） | 单实例应用下进程级共享是刻意选择：pak 锁必须在所有适配器间共享同一把锁，日志文件、时钟、Steam 探测结果与内嵌名称表也天然是进程级单例；探测缓存按路径键去重，名称缓存按（游戏/类型/ID）键去重，重复解析不会增长 | 无；若未来支持多实例，再把锁表与日志目录提升为显式注入的实例 |
|  Infrastructure 内用户文案与游戏规则 | 部分完成：Steam 多账号歧义策略已上移到 Application `SavePathSelection`（有单测），`SteamSavePathLocator` 只枚举候选；崛起旧存档 `Saves/` 特殊目录与 9 处「请…重试/恢复」类提示仍在 Infrastructure。延伸：旧数据导入对话框仍直接编排 `Detect/Import/MarkLegacyImported`，同属文档化取舍 | 这些字符串就是异常/加载提示的最终用户文案，结构化错误迁移需要 UI 全量映射并新增错误码契约；崛起目录规则需要 `GameProfile` 增加存档布局字段，属跨层数据模型变更 | 定义 `InfrastructureWarning`/错误码与 UI 映射表后，按存档→归档→pak 的顺序迁移文案；`GameProfile` 增加 `LegacySaveDirectoryName` 后移除 `SharpCompressSaveImporter` 中的 AppId 特判 |
| 复审 P2/P3（性能与一致性） | 已完成：非递归空目录删除与短路判空、路径分段复用、刷新签名缓存、进程检查 TTL、pak 计划复用、安装分类单次落盘、备份创建时间、空闲等待复核、只读设置返回值、取消按钮状态 剩余项同样闭环：安装前等待后台识别、保存等待超时、批量占用索引增量维护、按委托读取最新记录、设置探测移出 UI 线程、FOMOD 告警消费、`FakeModStore` 契约、启动切换时序、`MainWindowState` 与 App 状态测试、App 业务 IO 走端口、删除重复 `Verify` | 这些项依赖 UI 线程模型、测试基建或批量作用域设计，按「先状态、再时序、后批量」的顺序逐项落地，避免一次性大改放大回归风险 | 无（后续 UI 命令继续迁入 `MainWindowState` 时沿用同一测试模式） |
