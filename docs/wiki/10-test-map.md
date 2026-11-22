# 10 测试地图

## 1. 测试分层

| 层次 | 方式 | 特点 |
|---|---|---|
| Domain 纯逻辑 | 纯单测，假指纹/假上下文 | 无磁盘、无第三方依赖，覆盖边界与冲突 |
| Application 用例 | 临时目录集成测试 | 真解压、真复制、真备份还原、真 pak 计划 |
| Infrastructure | 临时目录 + 真实 zip/pak 读写 | 覆盖损坏恢复、迁移、并发、日志 |
| 安全 / 多游戏 | 跨游戏隔离矩阵、P0 回归 | 勾选回滚、清理范围、FOMOD 越界、索引损坏、pak 哈希 |
| UI | 手动验收 + 运行期证据 | 截图/UI Automation 保存在本地验收目录 |

运行：

```powershell
dotnet test HunterBox.slnx -c Release
```

当前 160 个测试源文件、`[Fact]` 1191 / `[Theory]` 63 / 派生 `[Fact]` 78（共 1332 个测试方法）；
当前版本 v1.5.207 的运行计数不再写入仓库文档（CHANGELOG 已移除），需要时直接看 `dotnet test` 输出。

## 2. 测试支撑设施

| 文件 | 作用 |
|---|---|
| `TempDir.cs` | 自动清理的临时目录：`Create()`、`Dir(...)`、`File(relative, content)` |
| `TestSupport/TestBox.cs` | 建 `BoxPaths` + 游戏根目录 + `ModRepository`；`GameFile`、`SourceFile` 生成路径 |
| `TestSupport/TestGames.cs` | 测试用游戏配置（复用 Domain `GameCatalog`，只保留测试前缀常量） |
| `TestSupport/DirectoryReadAttributesDeny.cs` | 临时给目录加「拒绝读取属性」ACL，验证缺失/无权限语义；无权限环境由 `DirectoryAttributeAclFact` 跳过 |
| `TestSupport/LongPathFact.cs` | 长路径探测：无法创建超过 260 字符目录时由 `LongPathFact` 在发现阶段跳过 |
| `TestSupport/FakeModStore.cs` | 内存 `IModStore`，用于不落盘的用例 |
| `TestSupport/ZipBuilder.cs` | 构造 zip；`Create` / `CreateAt` |
| `ZipTestTools.cs` | 从字节建 zip、GBK 条目名重写、加密 zip、CRC32 |
| `TestSupport/PakCodecTestTools.cs` | 构造 v2/v4 pak、TOC 密钥块、TOC 加密、资源加密、DEFLATE、条目读取、包头解析 |
| `PakCodecTestTools.HashPath` | 复用生产 `PakPathHasher.HashPath` 计算测试哈希 |
| `TestSupport/SourceLineEndingChecks.cs` | 行尾判定（混合 CRLF/LF、尾随换行/空行）与 `FindNonCrlfFiles` 文件集合过滤；C#/XAML 与仓库级行尾门禁共用 |
| `TestSupport/TextFileExtensions.cs` | 从 `tools/text-file-extensions.txt` 读取声明扩展名；编码门禁、行尾门禁与 `.gitattributes` 一致性用例共用 |
| `TestSupport/SourceTreeLocator.cs` | 从 `tools/gate-pruned-directories.txt` 读取剪枝目录，枚举时跳过链接；与构建期行尾脚本共用边界 |
| `TestSupport/LineEndingGateRunner.cs` | 行尾门禁用例的进程运行、临时仓库夹具复制与 BOM+CRLF 断言；接线用例与脚本用例共用 |
| `TestSupport/CommandLineGuard.cs` | targets/hook 的命令提取与禁用开关前缀判定；接线用例共用，单引号/XML 注释/子元素/续行可独立单测 |
| `TestSupport/EditorConfigEvaluator.cs` | EditorConfig 段解析、段匹配与最终值求值；分层守卫与行尾门禁共用，避免测试类互相引用 |

## 3. 测试文件 → 被测对象

> 以下按被测模块分组；同一文件可能覆盖多个相邻目标。测试命名约定 `Method_Scenario_Expectation`。

### Domain

| 测试文件 | 覆盖对象 |
|---|---|
| `BoxPathsTests` | `BoxPaths` 布局/路径安全（含保留设备名与分段规范化） |
| `ModInfoParserTests` | `ModInfoParser` 字段/缺失/异常输入 |
| `PackageInspectorTests` | `PackageInspector.Inspect/Normalize` 判定顺序与告警 |
| `LooseFilePlannerTests` | `LooseFilePlanner.PlanEnable/PlanDisable` 决策表 |
| `PakPlannerTests`、`PakNumberingTests`、`PakNumberingRegressionTests`、`PakNumberingStrategyRegressionTests`、`PakNumberStabilityTests` | `PakPlanner`、`PakNumbering` 编号/冲突/稳定性 |
| `FomodParserTests`、`FomodSemanticsTests`、`FomodPathSecurityTests` | `FomodParser`、`FomodSelectionState`、依赖求值与路径安全 |
| `PackageVariantTests` | `PackageVariantScanner` |
| `InstallEntryTests` | `InstallEntryClassifier` |
| `InstallSourceTests` | `InstallSourceSettings/Archiver/Locator` |
| `ModLinksTests` | `ModLinks` 链接生成 |
| `ModelGameDataTests`、`ModelGameRulesTests` | `ModelGameInfo`、三作路径规则与数据 |
| `ModelIdentityTests` | `ModelIdentity` 工厂/解析/兼容 |
| `ModelIdRewriterTests` | `ModelIdRewriter`（旧接口与完整身份） |
| `ModelPathRecognizerTests` | `ModelPathRecognizer` 识别/备注/游戏判定 |

### Application：安装

| 测试文件 | 覆盖对象 |
|---|---|
| `MainWindowStateTests` | 主窗口纯视图状态：按钮可用性、可见选中写回、取消状态、状态前缀替换（追加内容保留、空前缀、完全等于前缀） |
| `ModInstallerTests` | `ModInstaller.Stage/StageReinstall/Uninstall` |
| `InstallAtomicityTests` | 暂存 → 改名换入 → 写记录的原子性与回滚 |
| `InstallStagingTests`、`InstallStagingRootTests`、`InstallStagingCancellationTests` | `InstallStaging.Commit/CommitVariant/CommitFomod`、多根、取消 |
| `DuplicateInstallTests` | 包指纹去重、同游戏内重名 |
| `FomodInstallTests` | FOMOD 安装落盘、必装/条件/隐藏步骤 |

### Application：通用/会话/后台任务

| 测试文件 | 覆盖对象 |
|---|---|
| `BackgroundTaskHostTests` | 串行后台宿主：排队、空闲等待、异常观察 |
| `FileUndoScopeFactoryTests` | 撤销作用域工厂的创建/释放语义 |
| `GameSessionTests` | 当前游戏/游戏目录会话状态与切换 |
| `OperationErrorTests` | 操作错误分类与用户文案 |
| `ModListCoordinatorTests` | 列表序号归一/移动/写回 |
| `SettingsCoordinatorTests` | 设置键、默认值与批量保存 |

### Application：启用/挂载/清理

| 测试文件 | 覆盖对象 |
|---|---|
| `ModActivationServiceTests` | 启用/禁用/重新复制、跨链路补偿 |
| `ActivationSafetyTests` | 已启用状态与磁盘一致性、失败恢复 |
| `ActivationBatchSaveTests` | `BeginSaveBatch` 批量落盘 |
| `ActivationPortRollbackTests` | 注入假 `ILooseFileMountService`/`IPakMountService` 验证补偿调用 |
| `ModBatchActivatorTests` | 批量启用/禁用/重新复制的失败与取消语义 |
| `GameRunningGuardTests` | 游戏运行守卫 |
| `MountServiceTests` | 复制/备份/还原/删除/重叠文件重拷贝 |
| `PakMountServiceTests` | `pak_mods` 计划/写入/删除、源缺失容忍 |
| `PakInternalConflictTests` | 启用前 pak 内部文件重叠提示 |
| `MultiGameIsolationTests` | 跨游戏记录/文件/备份隔离 |
| `ModCleanupServiceTests`、`ModCleanupCancellationTests` | 两种清理范围、取消、残留排除 |
| `FileUndoJournalTests` | 四种登记方式、逆序回滚、撤销目录保留 |
| `ActivationOptionsTests` | `ActivationOptions` 组合校验（检查 pak 内部文件时必须提供读取器） |
| `PakOwnershipQuarantineTests` | 同名异内容 pak 的隔离目录、撤销与告警 |
| `DirectoryPrunerLinkTests` | 空目录清理的链接安全与上界保护 |
| `TempHousekeepingTests` | 启动 Temp 过期清理：只读条目、取消、链接目标保护、断链清理、扫描失败 fail-closed |

### Application：模型改写

| 测试文件 | 覆盖对象 |
|---|---|
| `ModelDetectorTests` | 模型行检测/去重/行触发 |
| `ModelAutoClassifierTests` | 自动分类、备注文本、无模型提示、不支持 pak 列表 |
| `ModelEditorPlannerTests` | 候选过滤、强制加回、自撞检测（按路径形式）、目标身份拼装、关键词搜索、逐行记忆键与空查询回车判定 |
| `ModelFolderEditorTests` | 未安装文件夹扫描/改写 |
| `ModelRewriteCoordinatorTests` | 批量编排、汇总、失败继续 |
| `PakModelRewriteServiceTests` | `RewritePaths`、TOC 哈希、索引未命中 warning |
| `PakRenameTests` | pak 型 MOD 改模型 ID 只重写 TOC 8 字节哈希，数据区/压缩/加密属性不动 |
| `PakModelTocEncryptedRewriteTests` | TOC 加密 pak 的模型改写 |
| `EncryptedPakModelRewriteIntegrationTests` | 加密 pak 端到端（构造 → 改写 → 校验） |
| `ModelIdentityPakRewriteTests` | 完整身份在 pak 中的替换 |
| `ModelGameRewriteTests` | 三作改写规则 |
| `LooseModelRewriteServiceTests`、`WorldLooseModelRewriteTests` | 散装 `File.Move`、世界路径规则 |
| `ModelSummaryTextTests` | 备注列文本合并/回退 |
| `ModelTextPrefetchServiceTests` | 分类/文本/负缓存判定、缓存容量与 pak 内容戳 |
| `ModelTextCacheTests` | 文本缓存并发、按游戏隔离、大小写一致性与 LRU 淘汰 |
| `ModelIdentityCacheTests` | 身份缓存按游戏隔离、内容戳校验、裁剪/失效与大库容量 |
| `ModelIdentityStampCacheTests` | 身份内容戳会话缓存：同实例复用、绝对 TTL/3–5 分钟分散、不可读退避与驱逐、失效/记录替换重算、按游戏裁剪 |
| `ModelStampRefreshTests` | 过期内容戳的时间预算、至少推进一项、负游标回绕与跨轮轮换 |
| `ModelTextCachePersistSchedulerTests` | 写盘去抖、失败重试上限、独立重试与脏标记 |
| `CloseWaitBudgetTests` | 关窗墙钟预算只减不增、不可被收尾期任务重置 |

### Application：存档/迁移

| 测试文件 | 覆盖对象 |
|---|---|
| `SaveBackupServiceTests` | 备份/列目录/槽创建/清单 |
| `SaveBackupManagementTests` | 还原/重命名/删除边界 |
| `SaveBackupRestoreFaultTests` | 还原两阶段失败回滚/校验失败 |
| `SaveBackupLinkSafetyTests` | 备份目录链接安全与越界防护 |
| `SaveBackupRestoreSnapshotTests` | 还原快照/清单一致性 |
| `LegacySaveImporterTests` | 旧 zip 列表/导入/密码 |
| `LegacyModRecordFactoryTests` | 旧记录构建：未启用/元数据映射 |
| `JsonModelTextCacheStoreTests` | 跨会话模型文本缓存：版本、原子写、增量日志/压缩、损坏回退、宽松 UTF-8 编码与 `.bad-*` 上限 |
| `LegacyMigrationTests` | 旧 MOD/备份检测、只读导入、去重记录 |
| `LegacyPluginPathRepairTests` | 旧安装前缀修复 |
| `ModFileRescannerTests` | 启动重扫文件清单 |

### Infrastructure：存储/设置/日志/Steam

| 测试文件 | 覆盖对象 |
|---|---|
| `ModRepositoryTests` | 基本增删改查/排序/游戏归属 |
| `ModRepositoryConcurrencyTests` | 并发读写/保存 |
| `ModRepositoryCorruptionTests` | 损坏索引保留 `.bad-*` 与份数上限、`.bak` 恢复 |
| `ModRepositoryDurabilityTests` | 原子写、落盘一致性 |
| `IniSettingsTests` | 读写/批量/别名 |
| `IniSettingsReadOnlyTests` | 只读降级 |
| `IniSettingsLegacyMigrationTests` | `sjmodbox.ini` 迁移/幂等 |
| `AppLogTests`、`CrashReporterTests` | 日志目录/滚动/崩溃报告失败语义 |
| `SteamDetectionTests`、`SteamGameLocatorTests` | VDF/库目录/安装目录/存档目录 |
| `GameDiscoveryServiceTests` | 游戏路径发现：探测、缺省回退、失效转发 |
| `SystemFileSystemTests` | `IFileSystem` 实现：删除/链接防护/属性/枚举/目录元数据与长路径 |
| `CorruptFileQuarantineTests` | 损坏文件隔离：按文件名时间戳保留、keep 计入上限 |
| `SingleInstanceGuardTests` | 互斥体名字/Get/Dispose |
| `ArchiveExtractorTests`、`ArchiveExtractorFormatTests`、`ArchiveExtractorProgressTests` | 解压格式/嵌套/编码/加密/进度/越界 |
| `SettingsPortTests` | `ISettings` 端口契约 |

### Infrastructure：pak

| 测试文件 | 覆盖对象 |
|---|---|
| `Murmur3Tests` | Murmur3 32 位实现 |
| `PakPathHasherTests` | 路径组合哈希 |
| `PakCipherInteropTests` | TOC/资源加解密互操作 |
| `PakPlainTocParsingRegressionTests` | 明文 TOC 解析回归 |
| `RePakReaderTests`、`RePakEntryReaderTests` | 解析/条目读取 |
| `RePakWriterTests` | 等长替换、checksum、`.bak` |
| `RePakRenamerTests` | 条目改名、批量、加密 TOC |
| `PakPackerPathHashTests`、`PakPackerStreamingTests` | 打包路径哈希/流式 |
| `PakRepackerTests`、`PakRepackerCaptureTests`、`PakRepackerFailureTests` | Capture/Pack/Unpack/失败清理 |
| `PakUnpackerAtomicityTests`、`PakUnpackerPathSafetyTests` | 解包事务/路径安全 |
| `PakEntryPathReaderTests` | 条目哈希 → 路径 |
| `PakFingerprintCacheTests` | 指纹缓存失效 |
| `PakDecompressionLimitTests` | 解压输出上限 |
| `RePakWriterTests` | 内容替换、checksum 与 `.bak` 语义 |
| `EmbeddedPakNameIndexTests` | 内置清单加载 |

### Infrastructure：名称表/模型目录

| 测试文件 | 覆盖对象 |
|---|---|
| `EmbeddedNameTablesTests` | CSV.gz 解析/内嵌资源 |
| `ModelCatalogTests` | 三作候选 |
| `ModelNameResolverTests`、`ModelNameResolverFormatTests` | 路径 → 名称表键 → 名字 |

### 工程规范

| 测试文件 | 覆盖对象 |
|---|---|
| `CommentHygieneTests` | 扫描 `src/` 与 `tests/` 的 C# 注释，禁止残句/损坏注释模板；`.cs`/`.xaml` 的 BOM 与 CRLF/尾随空行门禁在本类内校验，其余声明扩展名的 BOM 由 `EncodingConsistencyTests` 校验；`.gitattributes` 按「最后匹配生效」求最终 eol |
| `LineEndingGateTests` | 行尾门禁接线（`Directory.Build.targets`、pre-commit，含 Exec 子元素/变量间接/续行，参数按完整前缀判定不得携带 `-Fix`/`-Extensions`）、单次 msbuild 行为断言（LF 失败且错误包含文件名）、共享清单自身 BOM+CRLF、剪枝清单被脚本从 `$PSScriptRoot` 读取且覆盖关键目录、标签白名单与 Windows 官方标签一致（名称代理标签不得进非链接白名单、CLOUD 正则覆盖范围与未核实标签不放行）、`.editorconfig` 全局 CRLF+BOM、例外段与真实路径最终生效值一致（含行尾与实际字节）、测试侧剪枝/链接枚举 |
| `LineEndingScriptTests` | 校验脚本的退出码与链接/剪枝行为：LF 失败与 CRLF 通过、`-Fix` 把 LF/裸 CR 的合法 UTF-8 修复为 CRLF+单个 BOM（含双 BOM 剥离）、非 UTF-8（GBK/UTF-16/UTF-32 BOM/无 BOM 含 NUL）拒绝改写且字节不变、只读文件归入「未能修复」且无临时残留、占用文件报告后继续、0 文件失败、真实目录链接跳过（覆盖 PS 5.1 链接判定）、共享剪枝清单自定义行为；与接线用例拆类以并行执行 |
| `EditorConfigEvaluatorTests` | EditorConfig 求值器专属单测：后段覆盖、空值不写入、`?`、`{a,b}`、`[!...]` 与 `**` 匹配语义 |
| `LayerSourceGuardTests` | 源码守卫：分层引用方向、Application 禁止 Infrastructure/JSON/静态文件 IO、App 禁止 SharpCompress/JSON/静态文件 IO、拖拽入口先取 deferral；含 EditorConfig 段匹配辅助断言（源码树缺失时发现阶段跳过） |
| `XamlReferenceGuardTests` | XAML 事件处理器双向守卫：XAML 引用必须有对应方法，事件式方法必须被 XAML 引用 |
| `XmlDocumentationContractTests` | Domain/Application 全部 public 类型与成员必须有非空 summary；关键接口校验 param/returns |
| `DuplicateCodeCandidateTests` | 归一化哈希的重复实现候选检测（保留调用目标） |
| `VersionConsistencyTests` | 版本字段/测试计数/测试地图与源码同步门禁 |
| `VerifyTestCountsScriptTests` | 计数脚本综合用例：正例、缺失/陈旧/筛选/截断、四种跳过模式、TRX 保留策略与哈希门禁前置/漂移行为 |
| `EncodingConsistencyTests` | 源码/文档/工程/XAML/脚本统一 UTF-8 BOM；`.json`/`.editorconfig` 明确排除 |
| `FakeModStoreTests` | 内存 `IModStore` 测试支撑的契约行为 |
| `TempDirTests` | 临时目录测试支撑的创建/清理 |

## 4. 重点回归清单（安全相关）

- **勾选/启用回滚**：`ActivationSafetyTests`、`ActivationPortRollbackTests`、`ModActivationServiceTests`。
- **清理范围**：`ModCleanupServiceTests`（不整目录删除、不删备份、不删用户文件）。
- **跨游戏隔离**：`MultiGameIsolationTests`。
- **FOMOD 越界**：`FomodPathSecurityTests`、`FomodSemanticsTests`。
- **索引损坏**：`ModRepositoryCorruptionTests`、`ModRepositoryDurabilityTests`。
- **pak TOC 哈希/前像**：`PakModelRewriteServiceTests`、`PakModelTocEncryptedRewriteTests`、
  `RePakRenamerTests`、`PakRepackerFailureTests`。
- **安装原子性/取消**：`InstallAtomicityTests`、`InstallStagingCancellationTests`。
- **pak 编号稳定**：`PakNumberStabilityTests`、`PakNumbering*RegressionTests`。
- **存档还原校验**：`SaveBackupRestoreFaultTests`、`SaveBackupManagementTests`。

## 5. 新增测试约定

1. 每个公开用例至少一个测试；测试命名 `Method_Scenario_Expectation`，不测实现细节。
2. 涉及文件系统的用例用 `TempDir`，不要写仓库目录；需要业务布局时用 `TestBox`。
3. 需要隔离外部能力时注入端口假实现（`FakeModStore`、假 `IArchiveExtractor`、假挂载服务）。
4. 涉及 pak 的用例优先用 `PakCodecTestTools` 构造最小 pak，避免依赖真实游戏文件。
5. 涉及取消/失败路径的用例必须断言「未处理项保持原状、已完成项可恢复/已保存」。
6. 提交前 `dotnet test HunterBox.slnx -c Release` 必须 0 失败；新增行为必须有回归测试。
