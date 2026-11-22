# 02 整体架构

## 1. 分层与依赖方向

```text
┌─────────────────────────────────────────────────────────────────────┐
│ HunterBox.App（WinUI 3，组合根）                                     │
│  App / MainWindow / 对话框 / Workflow / AppServices / 启动协调        │
│  只做：视图、命令编排、依赖装配、中文提示、进程与链接等 OS 交互          │
└───────────────┬─────────────────────────────────┬───────────────────┘
                │ 依赖                            │ 依赖
┌───────────────▼───────────────────┐   ┌─────────▼───────────────────┐
│ HunterBox.Application             │   │ HunterBox.Infrastructure     │
│ 用例：安装/启用/清理/模型/存档       │   │ 实现：JSON/压缩/INI/pak/日志  │
│ 只依赖 Domain 的抽象与纯规则         │   │ /迁移/Steam 定位              │
└───────────────┬───────────────────┘   └─────────┬───────────────────┘
                │ 依赖                            │ 依赖
                └───────────────┬─────────────────┘
┌───────────────────────────────▼─────────────────────────────────────┐
│ HunterBox.Domain                                                    │
│ 模型 + 纯决策 + 端口接口（无项目引用、无第三方 NuGet 包）              │
└─────────────────────────────────────────────────────────────────────┘
```

依赖规则（评审时逐条检查，摘自 [ARCHITECTURE.md](../ARCHITECTURE.md) 第 2 节）：

1. **Domain 不引用** Application/Infrastructure，不引入第三方包；只用 BCL。
2. Application 通过接口使用存储/解压/pak/指纹，**不得**出现 `SharpCompress`、`JsonSerializer`、`IniSettings` 字样。
3. Infrastructure 只实现接口与外设适配，不新增业务判断；项目引用只有 Domain 与 Application
   （`ILegacySaveImporter` 定义在 Application、由 Infrastructure 实现）。
4. App 不自行实现业务性文件 IO：存储/配置/日志走 `ModRepository`、`IniSettings`、`AppLog`
   或 Application 用例；App 只保留 UI、进程/链接等 OS 交互与组合根职责。
5. 跨项目契约必须 `public` 且带 XML 注释；只服务单个程序集的实现默认 `internal`，
   测试需要时通过 `InternalsVisibleTo` 暴露（Domain 与 Infrastructure 已对 `HunterBox.Tests` 开放）。

## 2. 项目与包依赖

| 项目 | 目标框架 | 项目引用 | NuGet |
|---|---|---|---|
| `HunterBox.Domain` | net10.0 | 无 | 无 |
| `HunterBox.Application` | net10.0 | Domain | 无 |
| `HunterBox.Infrastructure` | net10.0 | Domain、Application | SharpCompress 0.50.9 |
| `HunterBox.App` | net10.0-windows10.0.19041.0 | Domain、Application、Infrastructure | Microsoft.WindowsAppSDK 2.7.0 |
| `HunterBox.Tests` | net10.0 | Domain、Application、Infrastructure | xUnit、Test SDK、Runner |

`HunterBox.Infrastructure` 对 Application 的引用只用于实现 `ILegacySaveImporter` 端口，方向仍是外层→内层。

## 3. 端口与适配器

### 3.1 Domain 定义的端口

| 端口 | 关键成员 | 实现 | 主要消费者 |
|---|---|---|---|
| `IModStore` | `Mods`、`ModsFor`、`NextId`、`Find`、`Add`、`Remove`、`Save`、`LoadWarning`、`UnassignedMods`、`AssignGame`、`ReorderForGame` | `ModRepository` | 全部 Application 用例、App 窗口/工作流/启动协调器 |
| `IArchiveExtractor` | `Extract(...)` | `ArchiveExtractor` | `ModInstaller` |
| `IContentFingerprint` | `GetFingerprint(fullPath)` | `FileContentFingerprint`；`PakFingerprintCache`（Application 包装） | `MountService`、`PakMountService`、`PakPlanner` |
| `IModelCatalog` | `WeaponTargets`、`CatTargets` 等 | `ModelCatalog` | `ModelEditorDialog` |

### 3.2 Application 定义的端口

| 端口 | 实现 | 消费者 |
|---|---|---|
| `IPakArchiveReader` | `RePakReader` | `ModActivationService`（pak 内部文件提示）、`PakModelRewriteService` |
| `IPakEntryReader` | 测试专用实现（不参与组合根） | 测试工具（构造/读取 pak 条目） |
| `IPakEntryRenamer` | `RePakRenamer` | `PakModelRewriteService` |
| `IPakPathHasher` | `PakPathHashProvider` | `PakModelRewriteService`、路径索引构建 |
| `IPakRepacker` | `PakRepacker` | `PakModelRewriteService`（加密 TOC）、`ModelAutoClassifier`（无模型提示） |
| `ISettings` | `IniSettings` | App 启动、`SettingsCoordinator`、`MainWindow`、设置对话框 |
| `IFileSystem` | `SystemFileSystem` | 安装/挂载/存档/清理用例的删除与失败清理 |
| `IPakEntryPathReader` | `PakEntryPathReader` | `ModelAutoClassifier`、`ModelFolderEditor` |
| `IModelNameResolver` | `AppModelNameResolver`（App 适配 Infrastructure 的 `ModelNameResolver`） | 模型分类/编辑器/备注文本 |
| `ILooseFileMountService` | `MountService` | `ModActivationService` |
| `IPakMountService` | `PakMountService` | `ModActivationService`、`ModCleanupService` |
| `IBatchPakEntryRenamer` | `RePakRenamer` | `PakModelRewriteService`（一次 TOC 批量写） |
| `IPakTocRecovery` | `RePakRenamer` | 读取/改写前自动恢复前像 |
| `ILegacySaveImporter` | `SharpCompressSaveImporter` | `SaveManagerDialog` |
| `IClock` | `SystemClock` | `SaveBackupService`（备份时间戳） |
| `IGameProcessDetector` | `GameProcessDetector`（Infrastructure） | `GameRunningGuard` |

端口存在的意义：让 Application 用例可以在不接触真实文件系统/pak/名称表的前提下被测试，
并把「注册表读取、WinUI 选择器」等 OS 交互留在 App；进程枚举实现在 Infrastructure。

## 4. 组合根

App 层不引入 DI 容器，唯一组合根是 [AppServices](../../src/HunterBox.App/AppServices.cs)：

| 成员 | 装配内容 | 生命周期 |
|---|---|---|
| `PakEntryPathReader` | `new PakEntryPathReader()` | 单例（无状态） |
| `ModelNames` | `new AppModelNameResolver()` | 单例（名称表只读） |
| `PakRepacker` | `new PakRepacker()` | 单例（无状态） |
| `PakFingerprints` | `new PakFingerprintCache(new FileContentFingerprint(), FileSystem)` | **进程级单例**（跨多次启用/禁用/清理复用整文件哈希，容量 4096；与模型文本缓存的 5000 上限是两套缓存） |
| `CreateInstaller(game)` | `ModInstaller(paths, store, new ArchiveExtractor(), game.Id)` | 瞬态（一次安装流程） |
| `CreateInstallContext(game, gameRoot)` | `ModInstallContext(paths, store, installer, gameRoot)` | 瞬态 |
| `CreateActivation(game, gameRoot)` | `ModActivationService`：注入 `FileContentFingerprint`、`GameRunningGuard`、`check_pak_files`、`RePakReader`、进程级 `PakFingerprints` | 瞬态（一次批量/单项操作） |
| `CreateCleanup(game, gameRoot, pakMode)` | `ModCleanupService(paths, store, gameRoot, game, PakFingerprints, FileSystem, pakMode, guard)` | 瞬态 |
| `CreateSaveBackup()` | `SaveBackupService(paths, FileSystem, JsonBackupManifestStore)` | 每次对话框打开 |
| `CreateLegacySaveImporter()` | `SharpCompressSaveImporter(paths, FileSystem, SaveBackupService, Logger)` | 每次对话框打开 |
| `CreatePakRewrite()` | `PakModelRewriteService(RePakReader, RePakRenamer, PakPathHashProvider, PakRepacker)` | 每次改写 |
| `CreateLegacyMigration()` | `LegacyMigrationService(paths, store, FileSystem, Logger)` | 每次导入 |

关键点：工厂返回的服务带缓存/批量落盘状态，**不要跨操作复用**；例外是 `PakFingerprints`
（进程级指纹缓存，由工厂显式注入各用例）与 `IModelTextCacheStore`/`ModelTextCache`：
前者是持久化端口（跨会话 JSON 存储），后者是 `ModelPanel` 持有的线程安全内存缓存（按游戏 + MOD 隔离），
它们不随瞬态服务销毁。`BoxPaths`、`ModRepository` 与 `ISettings` 由 `App.OnLaunched` 在进入主窗口前
装配进 `AppServices`（`ModRepository.Load` 只在这里调用），`MainWindow` 与各对话框只消费注入的实例，
避免多份内存状态互相覆盖。

## 5. 横切设计

### 5.1 不可变决策 + 可变聚合根

- 决策结果（`PakPlan`、`FileOperation`、`PackageInspection`、`ModelRewriteSummary` 等）全部是
  `sealed record`，输入→输出可单测、无 IO。
- 唯一可变聚合根是 `ModRecord`（启用状态、排序、元数据），但不允许改 `Id`；分配游戏用
  `MemberwiseClone` 的 `WithGame` 整体复制，避免新增字段漏拷。

### 5.2 失败显式

- 业务失败抛具体异常（`InvalidDataException`、`PakConflictException`、`FileNotFoundException`、
  `InvalidOperationException`、`IOException` 等），不用错误码返回值。
- UI 统一捕获并转中文提示，同时写日志；异常清单见 [04 Application 层](04-application.md#11-异常契约) 与
  [ARCHITECTURE.md](../ARCHITECTURE.md) 第 5 节。
- 数据损坏不静默：`ModRepository.LoadWarning`、`IniSettings.LoadWarning`、pak 前像恢复失败提示。

### 5.3 路径安全

所有外部路径在落盘前必须经过：

1. `BoxPaths.NormalizeRelative`：拒绝绝对路径、盘符、`..`、空段、尾随点/空格、Windows 非法字符与保留设备名；
2. `BoxPaths.ResolveInside`：解析后必须仍在目标根目录内（zip-slip / pak 条目越界防护）；
3. `BoxPaths.NormalizeGameId`：游戏 ID 只能作为单级安全目录名。

`pak` 条目路径来自不可信的外部清单，另经 `PakPathSafety.SplitSafeSegments` 二次校验；
该实现委托 `BoxPaths.NormalizeRelative`（不重复维护规则），把 `ArgumentException` 包装为 pak 解析语义的
`InvalidDataException`。

### 5.4 事务与回滚

| 场景 | 机制 |
|---|---|
| 安装 | 先写 `Mods/.staging/<guid>/files`，全部成功后目录改名换入 `Mods/<id>/files`，再写记录 |
| 重装/更新 | 旧 `files` 先改名 `.old-<guid>` 保留，新目录换入成功后才更新记录，失败恢复旧目录与旧记录 |
| 卸载 | 先把 MOD 目录改名墓碑，`Remove` + `Save` 都成功后才删除墓碑 |
| 启用/禁用（散装） | `FileUndoJournal` 登记「备份/新建/移入/外部前像」，失败逆序回滚 |
| 启用/禁用（pak） | 写/删都登记撤销日志，失败恢复已覆盖/已移动文件 |
| 跨链路启用 | `ModActivationService` 在 pak 或索引保存失败时补偿已复制的散装文件与本次写入的 pak |
| pak 条目表改写 | 写前生成 `.toc.pending`，成功后提升为 `.toc.bak`；崩溃后自动恢复 |
| pak 内容替换 | 临时副本 + 原子替换，首次写回前生成整包 `.bak` |
| 存档还原 | 影子目录预复制 + 源校验 → 覆盖 → 目标再校验，失败用当前存档快照回滚 |
| 索引/INI 写入 | `.tmp` + `File.Replace`/原子替换，失败不破坏原文件 |

### 5.5 并发与线程模型

- **单写线程假设**：`IModStore` 的锁只保护容器与落盘，不保护 `ModRecord` 字段；App 通过
  `OperationRunner` 忙碌门禁 + UI 线程保证同一时刻只有一个写线程。
- `ModRepository`：`_gate` 保护记录集合与快照；`_saveGate` 串行化保存；序列化与 fsync 在锁外完成。
- 长任务（安装/批量启用/清理/存档/迁移）在后台线程执行，通过 `IProgress<T>` 回 UI 线程更新状态，
  取消是「安全点协作取消」：单个 MOD 的事务内部不可中断。
- pak 路径锁 `PakPathLock` 是进程内引用计数互斥锁，保证同一 pak 的改名与前像恢复串行；
  不支持多实例/外部工具并发写同一 pak（应用有单实例保护）。
- App 单实例：`SingleInstanceGuard` 用 `Local\` + 用户 SID 命名互斥体，第二个实例提示后退出。

### 5.6 日志与异常兜底

- `AppLog`：按日文件、常驻 `StreamWriter`、8MB 滚动、程序目录不可写时降级 `%LOCALAPPDATA%`；
  业务与 UI 代码统一经 `IAppLogger` 端口（`AppLogAdapter` 适配），仅 `App.xaml.cs` 的初始化与崩溃钩子直接调用静态实现。
- `CrashReporter`：XAML / AppDomain / 未观察 Task 三类未处理异常写
  `logs/crash-<时间戳>.txt`（版本、系统、路径、异常链），写失败返回 `null` 并记录 `LastError`。
- 启动失败：`App.HandleStartupFailure` 关闭窗口、写报告并提示检查权限。

### 5.7 数据兼容

- `Mods/index.json`：`{ SchemaVersion, NextId, Mods[] }`，PascalCase；兼容早期「只有数组」的格式；
  `ModKind` 按数字序列化；缺失 `GameId`/`InstallSource` 按 `null` 处理。
- `hunterbox.ini`：`[settings]` + `[<gameId>]`；旧键名读取兼容、保存时清理；旧 `sjmodbox.ini` 由迁移服务只读导入。
- 旧数据导入：只读源目录，写入新HunterBox格式；`Mods/.legacy-import.json` 记录已导入的旧 MOD 目录，避免重复导入。

### 5.8 编码与注释规范

- 类型/方法 PascalCase，私有字段 `_camelCase`，接口 `I` 前缀；默认 `sealed`，值语义用 `record`。
- `Nullable` 全开；对外 API 不接受 `null` 表示「没有」，用空集合或 `TryXxx`。
- 所有 public 成员必须有中文 XML 注释（`<summary>`/`<param>`/`<returns>`/`<exception>`），
  缺失即编译错误；注释写契约与约束，不描述实现细节。
- 文件名与公开类型名一致；超大服务按链路拆成 Planner/Service。

## 6. 典型调用链

```text
用户点击「添加MOD」
  MainWindow.InstallCommands.InstallCoreAsync
    → InstallWorkflow.Stage（后台：ArchiveExtractor 解压 + PackageInspector 识别）
    → InstallDialog（预览/元数据/FOMOD/变体/文件树）
    → InstallWorkflow.Commit（后台）
        → InstallStaging.Commit（暂存 → 改名换入 → 写记录 → Save）
        → ModelAutoClassifier.Apply（自动分类/备注）
    → MainWindow.ReloadMods（列表刷新 + 模型文本预热）

用户勾选启用
  MainWindow.ActivationCommands.SetEnabledAsync（OperationRunner 忙碌门禁）
    → ModActivationService.Enable
        → SplitFiles：根层 .pak → pak 链；其余 → 散装链
        → PakMountService.PlanEnable（先算计划，冲突/源缺失直接失败）
        → PakMountService.Enable（写 pak_mods，撤销日志）
        → MountService.Enable（LooseFilePlanner 计划 → 复制/备份，FileUndoJournal）
        → ModRecord.Enabled = true + Save（批量时延迟到作用域结束）
```
