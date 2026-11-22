# 08 关键业务流程

本页把 UI 入口、Application 用例与磁盘副作用串起来，重点说明**顺序保证与失败语义**。
类型签名见 [04 Application 层](04-application.md)，UI 细节见 [06 App 与 UI](06-app-ui.md)。

## 1. 启动

```text
App.OnLaunched
  → SingleInstanceGuard 抢互斥体（失败：提示已有实例并退出）
  → AppLog.Initialize
  → 读取主题
  → BoxPaths(AppContext.BaseDirectory) + ModRepository.Load(paths)（损坏时保留 .bad-* 并记 LoadWarning）
  → AppServices(paths, store, settings) → new MainWindow(services)

MainWindow 构造
  → 从 AppServices 取 BoxPaths / IModStore / ISettings
  → OperationRunner / ModelPanel / InstallWorkflow / MaintenanceWorkflow / StartupCoordinator
  → 注册关闭保护

OnRootLoaded（首帧后）
  → 让出消息循环
  → 后台 StartupCoordinator.Prepare(token)
       1. GameDiscoveryService.DetectAllMissing(GameCatalog.All)
       2. LegacyPluginPathRepair.Repair(...)
       3. ModFileRescanner.Rescan(...)
       4. 有改动 → store.Save()
  → 模型文本预热（后台，可取消）
  → ReloadMods
  → 启动提示：索引 LoadWarning / 旧数据迁移 / 设置只读 / 启动准备异常
```

失败语义：

- 启动准备异常只提示、不崩溃；取消时已完成项保留，仍会刷新列表。
- `ModRepository.Load` 的损坏索引不会阻断启动，但会提示 `.bad-*` 保留位置。
- 单实例守卫抢不到互斥体时，不创建窗口。

## 2. 安装

```text
用户点击「添加MOD」/ 拖入压缩包、单文件、文件夹
  → InstallEntryClassifier.Classify：压缩包/目录/裸 .pak/.lua/.dll/不支持
  → OperationRunner.RunAsync（后台 + BusyRing + 取消）
  → ModInstaller.Stage
       1. Temp 解压/复制
       2. 剥单层外壳（natives/reframework 等内容根不剥）
       3. PackageInspector.Inspect（FOMOD → pak → 内容根 → 仅 Lua → 仅 DLL → Unknown）
       4. 读取 modinfo.ini、预览图、多组件变体、FOMOD 配置
       5. PackageFilters.ComputeFingerprint → 同游戏重复安装检测
  → InstallDialog（UI 线程）
       - 名称必填且同游戏不能重名
       - 分类/版本/作者/NexusId/来源/描述
       - FOMOD 按步骤分组（必选锁定/推荐/单选/至少选一/依赖可见性）
       - 多组件变体单选
       - 手动文件树（多根 + 安装前缀）
  → CaptureCommitRequest（UI 线程取快照）
  → OperationRunner（后台）→ InstallStaging.Commit
       1. 选中文件写入 Mods/.staging/<guid>/files
       2. 全部成功后改名为 Mods/<id>/files
       3. 写 ModRecord + store.Save
       4. InstallSourceArchiver 归档安装源（失败只 warning）
       5. ModelAutoClassifier.Apply 自动分类/备注
  → ReloadMods + 模型预热
```

失败语义：

- 解压/识别失败：提示「无法识别的 MOD 格式」，不产生记录/目录。
- 重复包（同游戏文件清单 + 大小指纹相同）：拒绝并提示。
- 名称非法/重名：`ValidateName` 返回错误，不提交。
- 提交阶段取消：删除暂存内容，不写记录、不推进落盘 ID。
- 提交中途失败：只删暂存，不留半成品；ID 已推进但不复用（`IModStore` 约定）。
- 安装源归档失败不中断已成功的安装，只提示 warning。

## 3. 重装 / 用新包更新

```text
右键「重装MOD」或「用新包更新」
  → 已启用？提示先禁用（InvalidOperationException）
  → InstallSourceLocator.FindExisting(record, fileSystem) 优先用记录里的安装源
        缺失 → 让用户重新选包
  → ModInstaller.StageReinstall(id, source)
  → 同一 InstallDialog 确认
  → Commit（IsReinstall=true）：
       1. 新文件写入 .staging
       2. 旧 Mods/<id>/files 改名 .old-<guid>
       3. 新目录改名为 Mods/<id>/files
       4. 更新记录（保留 Id/Index/GameId，更新 Metadata/RelativeFiles/InstallSource/InstalledAt）
       5. Save
       6. 失败：恢复旧目录与旧记录
  → 模型缓存失效 + 重新分类/备注 + 刷新
```

「用新包更新」与「重装」共用同一流程，区别只在 UI 文案与默认安装源选择。

## 4. 启用 / 禁用

### 单项

```text
行勾选 / 右键启用
  → MainWindow.SetEnabledAsync（OperationRunner 门禁）
  → AppServices.CreateActivation(game, gameRoot)
  → ModActivationService.Enable(mod, pakMode, token)
       1. GameScope 校验归属
       2. GameRunningGuard（设置开启时）
       3. 散装文件冲突提示（FindLooseFileConflicts）
       4. 可选：检查 pak 内部文件重叠（warning）
       5. SplitFiles：根层 .pak → pak 链；其余 → 散装链
       6. pak 计划先算（PlanEnable）——冲突/本 MOD 源缺失直接失败，散装尚未动
       7. PakMountService.Enable：写 pak_mods（撤销日志）
       8. MountService.Enable：LooseFilePlanner → Copy/BackupThenCopy（FileUndoJournal）
       9. ModRecord.Enabled = true → Save
      10. 任一步失败：撤销散装、删本次写入的 pak、Enabled 保持 false
  → ModRow.NotifyEnabledChanged + 状态栏
```

禁用是逆过程：散装撤出（有备份还原，有其它占用方则从其内容重新复制）→ pak 撤出
（只删除能证明归属的文件）→ `Enabled=false` → Save；失败尽量恢复为仍启用。

### 批量

```text
「启用选中MOD」/「禁用全部MOD」
  → ModBatchActivator.Enable/Disable
       - 按列表顺序逐项调用 ModActivationService
       - 项与项之间检查取消并上报进度
       - 启用/禁用：单项失败不中断，逐条汇总 Failures
       - 整批共用一个 BeginSaveBatch，结束统一 Save 一次
  → 结束后展示批量警告（pak 内部冲突、源缺失等）
```

取消时：已处理 MOD 保持各自完成后的状态，未处理项保持原状；不会出现「一个 MOD 半启用」。

## 5. 重新复制

```text
「重新复制MOD」
  → MaintenanceWorkflow.Recopy → ModBatchActivator.Recopy
  → 对每个已启用 MOD 重跑 MountService.Enable + PakMountService.Enable
  → 不改变 Enabled 状态
  → 单项失败立即停止（与原行为一致）
```

用途：游戏目录里的 pak 编号被外部改动后，重新建立「MOD ↔ pak_mods 文件」的关联。

## 6. 清理

```text
「删除全部MOD文件」→ 选择范围
  → ModCleanupService.Cleanup(scope, token, progress)
       1. 安全禁用当前游戏全部启用中的 MOD（还原原文件/删除复制文件/Enabled=false）
       2. BoxInstalledOnly：到此结束
       3. Everything：按 MOD 记录删除「能证明归属且没有原文件备份」的残留
          - 排除禁用阶段刚还原/保留的路径
          - 不整目录删除、不删备份、不删用户自己的非HunterBox文件
          - pak 型根层文件交给 pak 链处理
  → 返回 CleanupReport（Disabled/Total/Canceled/Warnings）
```

取消：已完成的安全禁用保持生效；`Everything` 的残留删除不再执行；未处理项保持原状。

## 7. 模型改写

### 已安装 MOD（右键「MOD文件修改」）

```text
RewriteModelsAsync
  → 已启用？提示先禁用
  → ModelPanel.BuildPreparation（后台）
       - ModelAutoClassifier.DetectDetails：散装 + pak 条目 → 模型行
       - 其它 MOD 的已用模型身份（增量缓存）
  → ModelEditorDialog
       - 每行：MOD 名字（只读）、(文件数)类型|名字[pfb][pak]、目标选择按钮（点击弹出候选面板）、pak ToolTip
       - 候选强制包含当前模型；「仅显示未使用的ID」过滤
       - 面板（ModelPickerView，Flyout 承载）：顶部搜索框按名称/完整 ID 模糊过滤（ModelEditorPlanner.Search），
         单击列表项即选中并写回该行；有搜索词时回车选取第一个命中项，空查询回车不改目标，Esc 关闭
       - 同一 MOD 其它行占用的 ID 也会过滤；选择自撞时提示
  → Changes（完整身份路径形式）
  → ModelRewriteCoordinator.Rewrite(mod, changes, index, game)
       - 根层 pak：PakModelRewriteService.RewritePaths（只改 TOC 哈希）
       - 散装：LooseModelRewriteService.Rewrite（File.Move，不改内容）
       - 汇总 Warnings / UnsupportedPaks
  → 重算分类/备注，失效模型缓存，ReloadMods
```

失败/跳过：

- 条目不在内置路径清单 → warning（最多 100 条）；
- TOC 加密且无重打包实现 → 记录 `UnsupportedPaks`，继续其它 pak；
- 散装路径无法识别为目标身份角色 → 原样保留 + warning；
- 改写中途失败：pak 用前像/pending 机制恢复，散装 `File.Move` 失败只影响当前文件并汇总。

### 未安装文件夹（拖文件夹到列表）

```text
DropCoreAsync → EditModelFolderAsync
  → ModelFolderEditor.Scan(folder, game, reader, names, fileSystem)
  → ModelEditorDialog（文件夹模式：不显示「仅显示未使用的ID」）
  → ModelFolderEditor.Rewrite(folder, changes, pakService, game, reader, fileSystem, progress, token)
       - 散装：IFileSystem.MoveFile
       - 根层 pak：PakModelRewriteService.RewritePaths
  → 展示汇总
```

## 8. 存档

### 备份

```text
「存档」→ SaveManagerDialog
  → SaveBackupService.Backup(gameId, saveDirectory)
       1. 校验存档目录存在、不能包含HunterBox数据目录
       2. CreateSlot：Saves/<gameId>/<yyyyMMdd-HHmmss>/（同秒冲突追加 -1/-2）
       3. 递归复制存档目录
       4. 生成 SHA-256 哈希清单
       5. 失败删除半成品槽
  → Reload 列表（倒序，自动选中第一项）
```

### 还原

```text
选择备份 → 内联确认
  → SaveBackupService.Restore(backup, saveDirectory)
       1. 校验备份存在、目标不在备份目录内部
       2. 在当前存档同级建影子目录，复制备份
          - 有哈希清单：复制时逐文件对源哈希校验
          - 无清单（旧备份）：只复制，不做哈希比对
       3. 源校验通过后才创建当前存档快照（.restore-undo-*）
       4. 覆盖存档；生产路径写入后不读回目标（仅故障注入测试读回）
       5. 任一步失败：用快照回滚；校验失败抛 SaveBackupVerificationException
  → 返回 SaveBackupVerification（旧备份无清单时 HasManifest=false）
```

峰值额外空间约 2× 存档体积（快照 + 影子），同级目录必须可写。

### 重命名 / 删除 / 打开目录 / 导入旧 zip

- 重命名：只改 `Saves/<gameId>/<backup>` 这一层；拒绝非法字符、`.`/`..`、尾随点空格、同名冲突。
- 删除：递归删除，不可恢复；UI 二次确认；路径必须在 `Saves` 下。
- 导入旧存档：`SharpCompressSaveImporter.List` 扫描 `Saves_<appid>/*.zip`（崛起 `Saves/`）→
  `Import` 解压到 `CreateSlot` 新槽，分块写入时同步计算 SHA-256 →
  `FinalizeImportedBackup` 校验条目并生成清单；源 zip 只读。

## 9. 旧数据迁移

```text
启动时 MainWindow.Dialogs.ShowStartupNoticesAsync
  → 若 [settings] legacy_imported 未设置：
       LegacyMigrationService.Detect 扫描（只读）
       有旧数据 → 提示 → LegacyMigrationDialog
  → Import：
       1. 旧 Mods_<appid>/<id>/{info.xml,files} → 新 Mods/<id>/files + ModRecord（未启用）
       2. 旧 Backup_<appid> → 新 Mods/.backup/<gameId>
       3. 已导入目录写入 Mods/.legacy-import.json，重复导入跳过
       4. 成功/取消/异常都尽力保存已完成项
  → 写 [settings] legacy_imported
```

配置迁移独立于 UI：启动读取主题前与打开设置页时，由 `LegacyMigrationService.MigrateSettings`
从 `sjmodbox.ini` 导入（幂等、不覆盖新值、旧文件只读）。
旧存档 zip 不在此流程，由存档对话框单独导入。

## 10. 卸载

```text
「卸载MOD」→ 二次确认
  → 已启用？先安全禁用（Disable）
  → ModInstaller.Uninstall(id)
       1. Mods/<id> 改名墓碑目录
       2. store.Remove(id) + store.Save()
       3. 都成功 → 删除墓碑目录
       4. 失败 → 恢复目录，记录与目录都保留
  → 失效模型缓存 + ReloadMods
```

## 11. 切换游戏

```text
GameCombo.SelectionChanged
  → 保存 [settings] game
  → 读取 [<gameId>] gamepath；缺失则 GameDiscoveryService.EnsureDetected
  → 校验目录含主程序；失败回滚选择并保留旧游戏配置
  → 更新窗口标题、游戏路径文本、列表（ModsFor(gameId)）
  → 更新按钮可用性与存档入口
```

多游戏隔离由 `ModRecord.GameId` + `GameScope` + 按游戏分目录的备份/存档保证；
切换游戏不会动另一个游戏的记录、文件与备份。
