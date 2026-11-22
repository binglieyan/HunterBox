# 06 App 与 UI（`HunterBox.App`）

## 1. 启动链路

```text
HunterBox.App.exe
  → App 构造函数：挂三类未处理异常钩子
  → App.OnLaunched
      → SingleInstanceGuard：已有实例则提示并退出
      → AppLog.Initialize（程序目录 logs，失败降级 %LOCALAPPDATA%）
      → AppServices(paths, ModRepository.Load(paths), settings) 唯一装配
      → services.LoadTheme()：读取 [settings] theme
      → new MainWindow(services) + Activate
      → 启动异常 → HandleStartupFailure：关窗、写 crash 报告、提示检查权限

MainWindow 构造
  → 从 AppServices 取 BoxPaths / IModStore / ISettings（LoadWarning 非空则稍后提示）
  → new OperationRunner / ModelPanel / InstallWorkflow / MaintenanceWorkflow / StartupCoordinator
  → 注册 AppWindow.Closing 关闭保护

MainWindow.OnRootLoaded（首帧后）
  → 让出消息循环绘制首帧
  → 后台 StartupCoordinator.Prepare：
        GameDiscoveryService.DetectAllMissing（三作补齐缺失路径）
        LegacyPluginPathRepair.Repair（一次性修复旧安装前缀）
        ModFileRescanner.Rescan（按磁盘修正 RelativeFiles）
        有改动则 store.Save()
  → 模型文本预热（后台）
  → ReloadMods（统一刷新列表/标题/状态栏）
  → 启动提示（索引损坏、旧数据迁移、设置只读等）
```

`App.xaml.cs` 的细节：

- `App.AppVersion` 直接读取程序集版本（唯一来源是 csproj 的 `Version`），窗口标题由
  `MainWindow.UpdateWindowTitle` 拼成 `HunterBox v{版本} - <当前游戏名>`；日志与崩溃报告复用同一来源，
  不再维护第二份常量。
- 全局异常三类都写 `logs/crash-*.txt`：`UnhandledException`（XAML）会尝试用
  `MainWindow.TryShowCrashNoticeAsync` 提示；`AppDomain.UnhandledException` 同步落盘并阻塞提示；
  `TaskScheduler.UnobservedTaskException` 只落盘。
- 主题：`Application.RequestedTheme` 只能在启动时设置，运行期通过根 `FrameworkElement.RequestedTheme`
  （`AppServices.ApplyTheme`）切换；`AppTheme` 负责 `system/light/dark` 与 `ElementTheme` 的转换。
  主题偏好本身存放在 `AppServices` 实例上（A-23），App 不再持有可变静态主题/设置。

## 2. 组合根 AppServices

见 [02 整体架构 §4](02-architecture.md#4-组合根)。App 层只有这一处集中装配；
`App.OnLaunched` 创建一次并注入 `MainWindow`，`MaintenanceWorkflow` 与各对话框共用它。
工厂返回的服务带缓存/批量状态，是瞬态对象，不要跨操作复用；例外是进程级 `PakFingerprints`、
持久化端口 `IModelTextCacheStore`（跨会话 JSON 存储）与 `ModelPanel` 持有的内存缓存 `ModelTextCache`
（按游戏 + MOD 隔离，不随瞬态服务销毁）。

## 3. 游戏配置与探测

### Games

游戏清单已下沉到 [HunterBox.Domain/Games/GameCatalog.cs](../../src/HunterBox.Domain/Games/GameCatalog.cs)：
三作 `GameProfile`、界面顺序 `All`、`Find` / `FindIdByAppId`。数值见
[01 项目概览 §2](01-overview.md#2-支持的游戏)。

### SteamGameProbe / GameDiscoveryService

注册表探测在 Infrastructure 的 `SteamGameProbe`（实例缓存），「缺省才写回」规则在 Application 的
`GameDiscoveryService`：

```csharp
// Infrastructure
public string? FindGameRoot(GameProfile game);
public IReadOnlyList<string> FindSaveRootCandidates(GameProfile game); // 只枚举候选，歧义策略在 Application
public void Invalidate(string gameId, bool gameRoot, bool saveRoots);  // 按缓存项分别失效

// Application
public string? FindGameRoot(GameProfile game);
public string? FindSaveRoot(GameProfile game);        // 由 SavePathSelection 从候选中选单一路径
public bool EnsureDetected(GameProfile game);
public int DetectAllMissing(IEnumerable<GameProfile> games);
public void Invalidate(string gameId, bool gameRoot, bool saveRoots);
```

- `SteamGameProbe`：读 `HKCU\Software\Valve\Steam` 的 `SteamPath`、
  `HKCU\Software\Valve\Steam\Apps\<appid>\Installed`、`HKLM\...\Uninstall\Steam App <appid>\InstallLocation`，
  交给 `SteamGameLocator.Locate` 完成定位；缓存随组合根实例创建/释放。
- 缓存策略：安装目录命中永久缓存、未找到负缓存 2 分钟；存档候选非空永久缓存、
  空结果负缓存 30 秒；设置页只在用户点「重新识别缺失路径」时按缺失项调用 `Invalidate`，打开对话框本身不重复探测。
- `GameDiscoveryService`：只补缺失值，已设置的路径一律保留；单款游戏写配置失败不影响其它游戏。
- `CreateGameRunningGuard` 读取 `[settings] check_game_running`（默认 `true`），关闭时返回 `null`。
- `CreatePakInternalInspector` 读取 `[settings] check_pak_files`（默认 `false`），开启时返回 `new RePakReader()`。
- 进程检测实现 `GameProcessDetector` 已下沉到 Infrastructure（`HunterBox.Infrastructure.Games`），
  由 `CreateGameRunningGuard` 装配；失败返回 `false`，不抛异常。

## 4. 主窗口结构

[XAML](../../src/HunterBox.App/MainWindow.xaml) 布局：

```text
┌ 顶部：HunterBox 标题 | 游戏下拉 | 游戏路径文本 | 刷新 ──────────────┐
├ 左侧：列头（勾选/顺序/MOD名字/备注信息/分类/当前版本/NexusMods）     │
│       + ListView（Extended 多选、拖拽、右键菜单）                    │
├ 右侧按钮列（可滚动）：游戏设置 / 过滤+搜索 / 快速移动 / 添加MOD /    │
│      重装MOD / 用新包更新 / 卸载MOD / 上移 / 下移 / 启用选中MOD /     │
│      禁用全部MOD / 删除全部MOD文件 / 重新复制MOD                     │
└ 底部：状态文本 | 存档 | 启动游戏(Steam) | BusyRing | 取消 ───────────┘
```

行模板使用无状态勾选字形按钮（避免原生 CheckBox 在虚拟化列表里偶发不刷新），
`EnabledGlyph` 单向绑定，启用/禁用成功后由 `ModRow.NotifyEnabledChanged` 通知界面。

### MainWindow 的 partial 拆分

| 文件 | 职责 | 关键入口 |
|---|---|---|
| `MainWindow.xaml.cs` | 字段、构造、依赖装配、`Context`、`IsFilterMode` | `MainWindow(AppServices)` |
| `MainWindow.StartupUi.cs` | 首帧加载、DPI/窗口尺寸、标题、图标、标题栏主题 | `OnRootLoaded`、`UpdateWindowTitle`、`ApplyWindowIcon` |
| `MainWindow.GameSwitch.cs` | 初始游戏选择、切换写设置、刷新列表与标题 | `SelectInitialGame`、`GameCombo_SelectionChanged` |
| `MainWindow.ListUi.cs` | 刷新/重建/筛选/选中/按钮联动/索引整理 | `ReloadMods`、`ApplyFilter`、`UpdateActionButtons`、`NormalizeIndexes` |
| `MainWindow.ListCommands.cs` | 上移/下移/快速移动/右键菜单/打开链接/文件夹 | `MoveAsync`、`QuickMoveAsync`、`ModsList_RightTapped` |
| `MainWindow.InstallCommands.cs` | 添加/重装/用新包更新、暂存与安装向导 | `InstallCoreAsync`、`ReinstallCoreAsync`、`UpdateModCoreAsync` |
| `MainWindow.ActivationCommands.cs` | 单项启用/禁用、批量启用/禁用全部 | `SetEnabledAsync`、`EnableSelectedCoreAsync`、`DisableAllCoreAsync` |
| `MainWindow.MaintenanceCommands.cs` | 卸载/清理/存档/启动游戏/重新复制 | `UninstallCoreAsync`、`CleanupCoreAsync`、`SaveCoreAsync`、`RecopyCoreAsync` |
| `MainWindow.ModelCommands.cs` | 模型编辑、MOD 信息、模型文本预热 | `RewriteModelsAsync`、`EditInfoAsync`、`WarmMissingModelTextsAsync` |
| `MainWindow.DragDropCommands.cs` | 拖入压缩包安装、拖入文件夹改模型 | `DropCoreAsync`、`EditModelFolderAsync` |
| `MainWindow.Dialogs.cs` | 启动提示、旧数据导入、对话框串行门、崩溃提示 | `ShowDialogAsync`、`TryShowCrashNoticeAsync` |
| `MainWindow.OperationHelpers.cs` | busy/取消/进度包装、UI 动作门禁、选中滚动 | `RunOperationAsync`、`RunBackgroundAsync`、`CancelOperation_Click` |
| `MainWindow.WindowClose.cs` | 关闭保护：取消长任务并异步等待 | `OnAppWindowClosing`、`CloseAfterOperationAsync` |
| `MainWindow.SettingsCommands.cs` | 打开设置对话框并应用主题 | `SettingsCoreAsync` |

## 5. UI 基础设施

### OperationRunner

[OperationRunner.cs](../../src/HunterBox.App/OperationRunner.cs) 是长任务的统一门禁：

```csharp
public OperationRunner(
    Action<bool> setBusyUi, Action<string> setStatus,
    Action updateActionButtons, Action refreshAfterOperation);

public Task<(bool Ok, string? Error)> RunAsync(
    string message, Func<CancellationToken, Task> action);

public Task<(T? Value, string? Error)> RunBackgroundAsync<T>(
    string message, Func<CancellationToken, T> action, bool showStatus = true);

public void Cancel();
public IProgress<T> CreateProgress<T>(int epoch, Func<T, string> format);
public static bool IsCanceled(string? error);
```

- 同一时刻只允许一个长任务；重复进入返回固定 `BusyReentryMessage`；
- 后台线程执行，异常写日志并转成可读错误；取消返回固定 `CanceledMessage`；
- 进度用「代次（epoch）」过滤过期回调，避免旧任务进度覆盖新任务文案；
- 结束后按需刷新列表并恢复按钮可用性。

### 其他 UI 支撑

| 类型 | 作用 |
|---|---|
| `ModListView` | 纯状态/投影：行集合、选中 ID、筛选、行签名（固定长度文本签名，不再内嵌文件清单） |
| `ModRow` | `ModRecord` → 绑定属性（`Index`/`Name`/`ModelText`/`Category`/`Version`/`NexusId`/`EnabledGlyph`/`ToolTipText`）；`UpdateModelText` 原地回填备注列 |
| `ModelPanel` | 模型文本缓存（`ModelTextCache`，内部锁保护）、身份缓存（`ModelIdentityCache`，内部锁 + 内容戳校验）与后台准备；`Prewarm`/`PrewarmMissing`/`BuildPreparation`/`StorePreparation`；文本缓存/戳/游戏标记由 `ModelTextCache` 的内部锁保护，UI 线程与后台预热共用；身份缓存按 `(游戏, MOD)` 隔离，后台用 `IdentitySnapshot` 一次物化并逐 MOD 校验内容戳 |
| `ModelIdentityCache` | 身份明细与已用身份的两级 LRU（Application 层，线程安全、可单测）：按游戏隔离、内容戳命中校验、按存活集合裁剪与粗粒度失效 |
| `ModelIdentityStampCache` | 身份内容戳的会话内缓存（Application 层，10 分钟绝对 TTL、按 MOD 分散 3–5 分钟、不可读结果 30 秒退避，目标 MOD 每次打开强制重算）：同一记录实例二次打开不重复全库 stat；不可读期间不重复扫描/写日志 |
| `FileTreeItem` | 安装文件树节点：`SelectedFiles`、`RefreshFromChildren`、`SetRootRecursive`、`CollectRoots`（多根安装） |
| `LruCache<TKey,TValue>` | 容量上限 LRU（位于 `Application/Common`，供 UI 与其它层复用）；非线程安全，跨线程读取先取快照 |
| `ModelPickerView` | 目标模型候选面板（UserControl + Flyout）：搜索框按名称/完整 ID 过滤、单击选中、非空查询回车选第一项、列表底部计数；主题随宿主对话框 |
| `DialogPickers` | 文件/目录选择器封装，绑定窗口句柄；`PickModArchivesAsync`、`PickSingleArchiveAsync`、`PickFolderAsync` |
| `WindowMinSize` | 子类化 `WM_GETMINMAXINFO`，给 WinUI 3 窗口加最小尺寸 |
| `AppModelNameResolver` | 把 Infrastructure 的 `ModelNameResolver` 适配成 Application 的 `IModelNameResolver` |
| `ModInstallContext` | 安装暂存阶段的服务集合（`Paths`、`Store`、`Installer`、`GameRoot`） |
| `InstallWorkflow` | 无 UI 的安装后台服务：`Stage`、`Commit`（提交后自动分类并保存一次） |
| `MaintenanceWorkflow` | 无 UI 的维护后台服务：`Uninstall`、`Cleanup`、`Recopy`、`Launch` |
| `StartupCoordinator` | 启动数据准备（路径探测、旧路径修复、文件重扫） |

### 模型预热

`ModelPanel.Prewarm` 在首帧后后台扫描当前游戏 MOD，给未分类 MOD 补自动分类并预计算「备注信息」列；
取消不抛 `OperationCanceledException`，而是返回已完成项 + `Canceled=true`，调用方仍须写回缓存并保存。
`PrewarmMissing` 只处理调用方点名的缺失项，避免每次刷新全量扫 pak。
`BuildPreparation` 为模型编辑器准备「目标 MODEL 的模型行 + 当前游戏已用模型身份」，
`StorePreparation` 回 UI 线程写回缓存。

模型文本缓存文件损坏或超过 128MB 时会被改名成 `<缓存文件>.bad-<时间戳>` 保留（只保留最近 3 份），
本次会话重新生成；只有改名也失败（文件被占用）时才禁用本会话写盘。

## 6. 对话框

| 对话框 | 职责 | 关键点 |
|---|---|---|
| `InstallDialog` | 安装向导：预览图、名称/分类/版本/作者/NexusId/来源/描述、FOMOD 按步骤分组、多组件变体、手动文件树（多根/前缀） | 名称必填且同游戏不重名；`CaptureCommitRequest` 在 UI 线程取快照，`CommitCaptured` 可在后台执行；取消需二次确认 |
| `ModInfoDialog` | 编辑名称/分类/版本/作者/NexusID/来源/备注 | `BuildResult()` 返回新的 `ModMetadata` |
| `ModelEditorDialog` | 每个检测到的模型一行，选择目标模型 | `Changes` 返回 `(From, To)` 完整身份路径形式；已启用时行只读；当前模型强制加回候选；支持「仅显示未使用的ID」；目标用按钮弹出 `ModelPickerView` 面板（Flyout，顶部带搜索），单击即选中 |
| `MoveToDialog` | 快速移动到指定位置 | `Position` 1 起始 |
| `SettingsDialog` | 游戏路径（exe 校验）、存档路径、安装选项、自动修正PAK、检查PAK内部文件、检测游戏运行、主题 | 保存前统一校验；风险项用「仍然保存」二次确认；`UpdateBatch` 一次落盘 |
| `SaveManagerDialog` | 备份列表 + 备份/恢复/重命名/删除/打开目录/导入旧存档 zip | 内联确认/输入面板（避免嵌套 ContentDialog）；忙碌时实现 `IBusyCloseGuard` |
| `LegacyMigrationDialog` | 展示检测到的旧数据并执行只读复制导入 | 可取消；导入结果显示 warning |

`ContentDialog` 不能同时打开两个，主窗口用 `_dialogGate`（`ShowDialogAsync`）串行化；
崩溃提示也走同一道门，超时则回退阻塞式系统消息框，不静默吞掉。

## 7. 线程、忙碌与关闭保护

- **UI 动作门禁**：`_uiActionGate` 串行化会打开对话框/写状态的 UI 动作；
  **对话框门**：`_dialogGate` 串行化 `ContentDialog`。
- **长任务**：全部走 `OperationRunner`（后台线程 + BusyRing + 取消按钮 + 状态栏），
  UI 线程只做控件更新与结果提示。
- **取消语义**：安全点协作取消——批量项与项之间、安装复制阶段、清理 MOD 之间可取消；
  单个 MOD 的启用/禁用/清理事务内部不可中断（保持事务与回滚语义）。
- **关闭保护**（`MainWindow.WindowClose.cs`）：
  - 有长任务：取消这次关闭（`args.Cancel = true`），请求协作取消，异步等待任务退出后再关窗；
  - 忙碌中的 `SaveManagerDialog` 打开时，主窗口 `AppWindow.Closing` 通过 `IBusyCloseGuard` 拦截 Alt+F4；
  - 等待覆盖后台宿主里的索引保存与模型文本缓存写盘（含失败重试，`ModelPanel.WhenCacheIdleAsync`），
    持续续期的写盘请求也受 60 秒总预算约束；
  - 只做「取消 + 异步等待」，**不在 UI 线程同步阻塞**；空闲时关闭原样放行。
- 后台操作结果统一回 UI 线程刷新（`refreshAfterOperation`），列表刷新区分轻量/整表。

## 8. 设置应用与界面状态

`SettingsDialog.SaveCore` 一次 `UpdateBatch` 写入：

```text
[<gameId>] gamepath, savepath
[settings] install_option, enable_fix_pak_num, check_pak_files, check_game_running, theme
清理旧键：file_list / check_pak_inner / install_optn / ignore_caimogu / enable_filter_mhmodel
```

主窗口在设置对话框关闭后立即把主题应用到根元素，并刷新按钮可用性与列表（目录变化会影响启用/存档入口）。
`UpdateActionButtons` 根据选中数量、是否启用、是否过滤模式、是否有长任务等统一决定按钮可用性。
