# 09 构建、测试与运行

## 1. 环境要求

| 项 | 要求 |
|---|---|
| OS | Windows 10 1809+ / Windows 11（WinUI 3） |
| SDK | .NET 10 SDK（含 Windows 桌面工作负载） |
| 架构 | 仅 x64（解决方案把 Any CPU/x64 都映射到项目 x64） |
| 网络 | 首次还原 NuGet：Windows App SDK 2.7.0、SharpCompress 0.50.9、xUnit |
| 系统工具 | Windows PowerShell 5.1+（行尾门禁脚本）；目录重解析点回退读取标签依赖系统自带的 `fsutil.exe` |
| 运行目标机 | .NET 10 桌面运行时 + Windows App Runtime 2.7.x（框架依赖发布） |

## 2. 构建

```powershell
dotnet build HunterBox.slnx -c Release -p:Platform=x64
```

- `Directory.Build.props` 开启 `Nullable`、`ImplicitUsings`、`TreatWarningsAsErrors`，
  构建必须 **0 警告 0 错误**。
- 四个项目均开启 `GenerateDocumentationFile`：public 成员缺 XML 注释会以 CS1591 编译失败。
- 解决方案 [HunterBox.slnx](../../HunterBox.slnx) 已为每个项目做了 `Any CPU`/`x64` → `x64` 的平台映射。

单项目构建：

```powershell
dotnet build src\HunterBox.Domain\HunterBox.Domain.csproj -c Release
dotnet build src\HunterBox.App\HunterBox.App.csproj -c Release -p:Platform=x64
```

## 3. 运行（调试）

```powershell
dotnet run --project src\HunterBox.App\HunterBox.App.csproj -c Debug -p:Platform=x64
```

注意：调试版程序目录是 `src\HunterBox.App\bin\...`，会在该目录生成 `Mods/`、`hunterbox.ini`、
`logs/` 等运行期数据。想验证「首次启动 / 旧数据迁移」，建议把 `dotnet publish` 产物复制到独立空目录运行。

运行后检查：

- 主窗口标题为 `HunterBox v1.5.207 - <当前游戏名>`；
- 首次启动自动探测 Steam 路径（可用时），创建 `Mods/index.json`、`hunterbox.ini`、`logs/`；
- 程序目录不可写时，日志出现在 `%LOCALAPPDATA%/HunterBox/logs`。

## 4. 测试

```powershell
dotnet test HunterBox.slnx -c Release -p:Platform=x64
```

- 测试工程：[tests/HunterBox.Tests](../../tests/HunterBox.Tests)，xUnit，目标 `net10.0`。
- 当前规模：160 个测试源文件、`[Fact]` 1191 / `[Theory]` 63 / 派生 `[Fact]` 78
  （`SourceTreeFact` 46、`FileSymlinkFact` 4、`DirectoryLinkFact` 10、`AclFact` 10、`FileAclFact` 1、`DirectoryAttributeAclFact` 3、`LongPathFact` 2、`SourceTreeDirectoryLinkFact` 2），共 1332 个测试方法；
  `[Theory]` 展开后的用例数、单次运行的通过/跳过数不再写入仓库文档（CHANGELOG 已移除），
  需要时直接看 `dotnet test` 输出（跳过的是本机无符号链接/ACL 权限的用例）；全量墙钟约 11–18 秒（随冷热启动波动），
  其中 `VerifyTestCountsScriptTests` 因类内串行执行多次 msbuild 冷启动常成为最长路径，
  行尾门禁脚本用例已拆类并行。
- Release 全量测试默认生成 TRX，并在 `AfterTargets="VSTest"` 自动运行 `tools/verify-test-counts.ps1`：
  按最新修改时间选择 TRX、校验新鲜度（拒绝上一次运行的旧结果）、`Counters.total` 与结果数；
  仓库已移除 CHANGELOG，脚本不再比对文档计数（显式传 `-ChangelogPath` 时仍按传入文件比对），
  完整性判据不符仍会让 `dotnet test` 失败。
- 仅 **Release 全量**运行自动校验；Debug、`--filter`、`--list-tests` 由 csproj 直接跳过且不启动 PowerShell，
  非 trx 的自定义 logger 仍会启动脚本并由脚本输出跳过原因；`trx`（含 `LogFileName` 参数）与
  多记录器列表中的 trx 继续校验。
- 每个测试运行前还会以 `-PruneOnly` 预清理旧 TRX，测试失败时 `AfterTargets` 不执行也不会无限累积。
- 需要手动复核时执行 `powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-test-counts.ps1`。
- 门禁接线（`AfterTargets="VSTest"`、`Exec` 参数、开始时间条件、跳过提示、预清理目标）由
  `VerifyTestCountsScriptTests` 通过 `dotnet msbuild` 求值属性并执行目标做行为断言，
  删除属性或接线会让测试失败。
- 构建后与 VSTest 前都会比较源码与部署副本脚本的 SHA-256；源码/副本缺失或哈希漂移直接失败，
  漂移行为用例会断言 msbuild 退出码。
- TRX 修改时间晚于当前时间 30 秒以上视为不可信：容差覆盖 FAT 时间戳 2 秒粒度、网络盘/CI 时钟漂移
  与文件服务器缓存；脚本先解析校验、后在 `finally` 中清理，失败退出也不会在读取前删掉正在校验的 TRX。
- VSTest 之前会预清理旧 TRX（失败运行也有界，最多保留 5 份旧 + 1 份本次）；
  成功后的 AfterTargets 再收敛到最近 5 份。默认 `TestResults/` 与显式传入的 `--results-directory`
  都按目录清理全部 `*.trx`；显式 `-TrxPath` 只有位于结果目录内才清理，避免删除共享目录中他人的文件。
- 脚本的相对路径（`-TrxDirectory`/`-TrxPath`/`-ChangelogPath`）统一按仓库根解析，与调用方当前目录无关；`global.json` 声明最低 SDK `10.0.100` 并允许上移到更高主版本。
- 门禁假设仓库只有一个测试工程且使用默认 trx logger；自定义 `.runsettings`、额外 logger 或多测试工程时，
  需要为每个工程分别生成 TRX 并运行校验脚本。
- 每次提交前必须 0 失败；新增行为必须有回归测试。
- 覆盖率目标：Domain 与 Application 分支覆盖 ≥ 80%（`dotnet test --collect:"XPlat Code Coverage"`），
  当前未作为 CI 门禁，属于人工抽检。
- 真实游戏端到端（加载/模型显示/存档）属于发布验收，单元/集成测试不能替代。

### 门禁状态表

| 场景 | 触发方式 | 结果 |
|---|---|---|
| Release 全量 `dotnet test` | 自动：VSTest 前 Record/Prune；VSTest 后 Verify | 校验 TRX 新鲜度、`Counters.total` 与结果数一致、失败数为 0；显式提供 `-ChangelogPath` 时另比对计数 |
| Debug / `--filter` / `--list-tests` | csproj 默认值跳过 | 输出「跳过计数门禁」及原因 |
| 自定义非 trx logger | 启动脚本后由脚本跳过 | 输出「跳过计数门禁：自定义测试记录器」 |
| 手动 `verify-test-counts.ps1` | 手动 | 校验 TRX 完整性；显式提供 `-ChangelogPath` 时才比对计数；未传 `-NotBeforeUtc` 时不校验新鲜度 |
| 构建 / `--no-build` | 自动：Build 后与 VSTest 前 | 比较源码/部署脚本 SHA-256，缺失或漂移直接失败 |
| 门禁跳过提示 | 自动：VSTest 后 Report（仅门禁未启用时输出） | 输出「跳过计数门禁」；门禁启用时不执行 |
| 结果目录保留 | 自动：PruneOnly / Verify 的 `finally` | 默认与显式结果目录保留最近 5 份；`-TrxPath` 不在目录内不清理 |
| 编码 / 行尾门禁 | 自动：全量测试 `CommentHygieneTests` / `EncodingConsistencyTests` | `.cs`/`.xaml`/`.md`/`.csproj`/`.props`/`.targets`/`.slnx`/`.ps1`/`.txt`/`.pubxml` 的 BOM 与 CRLF；`.json`（无 BOM + LF）、`.editorconfig`/`.gitattributes`/`.gitignore`/`app.manifest`（无 BOM）明确为例外；`.codegraph` 等非源码目录不参与扫描。`.gitattributes` 的 `eol=crlf` 只影响新检出，存量工作区由门禁兜底 |
| 行尾构建门禁 | 自动：`Directory.Build.targets` → `tools/verify-line-endings.ps1` | 锚定 `HunterBox.Domain`，构建期扫描共享扩展名清单（`tools/text-file-extensions.txt`，含 `.pubxml`），LF/混合行尾直接失败；扫描 0 个文件也失败；提交前可用 `.githooks/pre-commit` 同脚本拦截（`git config core.hooksPath .githooks`）。**门禁边界**：剪枝目录见 `tools/gate-pruned-directories.txt`（脚本与测试枚举共用）；链接判定先看 `Get-Item` 的 `LinkType`/`Target`，目录再回退 `fsutil` 重解析标签（**十六进制字符串比较**，PS 5.1 会把 `0xA0000003` 当 Int32 负数）；只放行已知非链接标签（云占位 CLOUD 家族、WOF/WCI、AppExecLink、StorageSync），名称代理标签（mount point/symlink/global reparse/LX symlink）一律跳过，未知标签 fail-closed 跳过；文件重解析点不查标签、无法确认即跳过并告警，因此 WOF/WCI 压缩的声明文件不在门禁中解压校验。`.githooks/pre-commit` 按 `.gitattributes` 保持 LF，不在扫描范围；共享清单自身由测试断言 BOM+CRLF。单次构建开销本机实测：冷缓存约 1.6 秒、热缓存约 0.5 秒（脚本自身 455–543ms，其中 PowerShell 冷启动约 212ms；新增 `.pubxml` 影响 <0.2%），属设计取舍；依赖 PowerShell 5.1+。**同步点**：两者都只跳过真链接，但判据不同——脚本按标签白名单（`$script:LinkTags`/`$script:PlainTagPatterns`），C# 按 `IFileSystem.IsReparseLink` 的 `LinkTarget`；改动任一侧需同步另一侧与 `VerifyLineEndingsScript_TagWhitelistMatchesWindowsTags`。**编辑期边界**：`.editorconfig` 的 `[*]` 已声明 CRLF + UTF-8 BOM，但只对遵守 EditorConfig 的编辑器生效；例外为 `.json`（无 BOM + LF）、`.editorconfig`/`.gitattributes`/`.gitignore`/`app.manifest`（无 BOM）与 `.githooks/*`（无 BOM + LF）。构建期门禁与一键修复命令 `powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-line-endings.ps1 -Fix` 仍是唯一权威兜底：`-Fix` 只尝试修复行尾违规文件（调整行尾并补齐末尾换行、重写时补 UTF-8 BOM）；无法严格 UTF-8 解码（GBK/ANSI、含 NUL 的无 BOM UTF-16/32）、带 UTF-16/32 BOM、不可读或写入失败时保持原样并退出 1，部分成功时仍列出已修复文件 |

缓存诊断计数（`BaseWriteAttempts`、`JournalAppendAttempts`、`JournalSerializeBatchCount`、`LastJournalSerializedBytes`、
`JournalTruncatedBatchCount`、`FileExistsProbeCount`、`BaseDestinationProbeCount`、
`BackupSizeDirectoryScanCount`、`BackupSizeManifestReadCount`、`BackupSizeMetadataProbeCount`）
只供同层测试通过 `InternalsVisibleTo` 断言，不属于发布门禁。
备份体积缓存另有测试钩子 `SizeCacheEntryCount`（条目数）、`SizeCacheContains`（键是否存在）与
`ResetSizeCache`（隔离进程级静态缓存）；生产代码不得调用，只用于容量、LRU 与跨实例用例。

测试地图见 [10 测试地图](10-test-map.md)。

## 5. 发布

一条命令：

```powershell
dotnet publish src\HunterBox.App\HunterBox.App.csproj -p:PublishProfile=FolderProfile
```

发布配置：[FolderProfile.pubxml](../../src/HunterBox.App/Properties/PublishProfiles/FolderProfile.pubxml)。

| 项 | 值 | 说明 |
|---|---|---|
| Configuration / Platform | Release / x64 | 仅 x64 |
| TargetFramework | net10.0-windows10.0.19041.0 | WinUI 3 |
| RuntimeIdentifier | win-x64 | 桌面 x64 |
| SelfContained | false | .NET 框架依赖，目标机需 .NET 10 桌面运行时 |
| WindowsAppSDKSelfContained | false | Windows App SDK 框架依赖，目标机需 Windows App Runtime 2.7.x |
| PublishReadyToRun | true | 开启 R2R 加快首屏启动；产物体积由约 37 MB 增至约 53 MB（+42%） |
| PublishDir | `FolderProfile.pubxml` 中的值（当前为仓库内 `dist\HunterBox`，已在 `.gitignore` 中忽略） | 发布目录；不自动生成压缩包；换机器时按需改为 `..\..\dist\HunterBox\` |

产物：

```text
dist/HunterBox/                 约 22 个文件 / 17 个 DLL / 53 MB（R2R）
```

发布目标（`src/HunterBox.App/HunterBox.Publish.targets`）自动执行：

1. 清空输出目录（被占用时提示先关闭 `HunterBox.App.exe`）；
2. 补 `HunterBox.App.pri`（缺失会导致 unpackaged 启动 `0xC000027B`）；
3. 精简语言目录（保留 `zh-CN` / `zh-TW` / `en-us` / `ja-JP`；`-p:HunterBoxKeepAllLocales=true` 跳过）；
4. 剔除未使用的 WinAppSDK/Windows SDK 组件、AI/ML、WebView2、多语言 mui 与 XML 文档；
5. 剔除 pdb。

发布不再自动打压缩包；需要分发时手动把 `dist\HunterBox\` 压成 zip 即可（压缩包内建议只有一层 `HunterBox\`）。

必须保留：`Microsoft.InteractiveExperiences.Projection.dll`、`WinUIEdit.dll`、
`Microsoft.Windows.SDK.NET.dll`、`Microsoft.WinUI.dll`、`Microsoft.ui.xaml*.dll`、`Assets\hunterbox.ico`。

本页同时是构建、测试、运行与发布的唯一操作说明。

## 6. 版本号

版本只在一处定义：[HunterBox.App.csproj](../../src/HunterBox.App/HunterBox.App.csproj) 的
`Version` / `AssemblyVersion` / `FileVersion`。窗口标题、日志与崩溃报告都读取
`App.AppVersion`（程序集版本），发版时只需改 csproj 并同步 README/docs 里的版本字段，不会再出现标题版本漂移。

## 7. 常见问题

| 现象 | 原因 / 处理 |
|---|---|
| exe 启动即退出，事件查看器 `0xC000027B` | 缺 `HunterBox.App.pri`，或误删 `Microsoft.InteractiveExperiences.Projection.dll` |
| 发布失败 `MSB3231 文件被占用` | 先关闭正在运行的 `HunterBox.App.exe`；输出目录无法清空也会同样提示 |
| 目标机无法启动（缺 .NET） | 安装 .NET 10 桌面运行时 |
| 目标机无法启动（缺 Windows App Runtime） | 安装 Windows App Runtime 2.7.x（Windows 不自带） |
| 语言目录被删导致界面异常 | 用 `-p:HunterBoxKeepAllLocales=true` 重新发布 |
| 启动后提示索引损坏 | 查看 `logs/` 与 `Mods/index.json.bad-*`；可从 `index.json.bak` 恢复后重启 |
| 设置修改没有保存 | 检查 `hunterbox.ini` 是否只读/被占用；界面会提示 `IsReadOnly` |
| 启用失败提示游戏运行中 | 关闭游戏，或在设置里关闭「检测游戏运行」 |
| pak 冲突 | 同名不同内容；改用「自动修正PAK」编号模式，或禁用冲突 MOD |
| 模型改写提示不支持 | 该 pak 的 TOC 加密且当前未配置重打包实现，或条目不在内置路径清单 |

## 8. 本地发布校验

```powershell
dist\HunterBox\HunterBox.App.exe
```

- 主窗口正常显示，标题含版本与游戏名；
- `dist\HunterBox\HunterBox.App.pri` 存在、`Assets\hunterbox.ico` 存在、无 `*.pdb`；
- 在独立空目录运行可验证首次启动（自动探测、创建 `Mods/index.json`、`hunterbox.ini`、`logs/`）。

发布端到端实测（2026-11-20 本机实测；产物目录为本机 `FolderProfile.pubxml` 的
`F:\work\nhw\HunterBox\dist\HunterBox`，随该配置变化）：

```text
dotnet publish src\HunterBox.App\HunterBox.App.csproj -p:PublishProfile=FolderProfile
→ 产物：HunterBox.App.exe、HunterBox.App.pri、Assets\hunterbox.ico、若干 WinAppSDK DLL
→ 校验：*.pdb 数量为 0；不再生成任何 zip（需要分发时手动把 dist\HunterBox\ 压缩）
```

发布配置 `FolderProfile.pubxml` 的 `PublishDir` 当前指向仓库内 `dist\HunterBox`（已在 `.gitignore` 中忽略）；
换机器或换目录发布时改成 `$(MSBuildProjectDirectory)\..\..\dist\HunterBox\` 即可。
