# AGENTS.md

本文件面向在本仓库工作的 AI 编码代理（以及人类贡献者），说明项目结构、构建方式、代码约定与交付流程。
若与其他文档冲突：行为细节以 `docs/SPEC.md` 为准，架构决策以 `docs/ARCHITECTURE.md` 为准，本文件负责「怎么改」。

## 1. 项目概述

HunterBox 是一个 Monster Hunter 系列 MOD 管理器（独立第三方工具），使用 .NET 10 + WinUI 3，
unpackaged、仅 x64。核心能力：压缩包/文件夹安装、FOMOD、pak 编号与 `pak_mods` 挂载、散装文件启用、
存档备份与旧数据迁移、模型 ID 改写、PAK 读写与重打包。

## 2. 仓库结构

| 路径 | 职责 | 允许依赖 |
|---|---|---|
| `src/HunterBox.Domain` | 领域模型、纯决策、端口接口；不引用任何项目与第三方包 | BCL |
| `src/HunterBox.Application` | 用例编排：安装/重装/卸载、启用/禁用、清理、模型改写、存档备份 | Domain |
| `src/HunterBox.Infrastructure` | 适配器：JSON 存储、SharpCompress 解压、INI 配置、日志、Steam 定位、PAK 读写 | Domain、Application |
| `src/HunterBox.App` | WinUI 3 视图、对话框、命令编排、组合根、启动协调 | Domain、Application、Infrastructure |
| `tests/HunterBox.Tests` | xUnit 单元/集成测试与测试支撑设施 | Domain、Application、Infrastructure |
| `tools/` | 校验脚本：全量测试 TRX 完整性（缺失/截断/筛选/新鲜度）与行尾门禁 | — |
| `docs/` | `SPEC.md`（规格）、`ARCHITECTURE.md`（架构）、`PAK.md`（pak 格式）、`wiki/`（代码 Wiki） | — |

依赖方向固定为 `App → Application/Infrastructure → Domain`，禁止反向引用。
跨项目契约 `public` 且带 XML 注释；仅服务单程序集的实现默认 `internal`，测试通过 `InternalsVisibleTo` 访问。

## 3. 常用命令

要求 Windows 10 1809+ / Windows 11 与 .NET 10 SDK（含 Windows 桌面工作负载）。

```powershell
# 构建（必须 0 警告 0 错误）
dotnet build HunterBox.slnx -c Release -p:Platform=x64

# 全量测试（仅 Release 全量自动生成 TRX 并校验 TRX 完整性；
# Debug、--filter、--list-tests 由 csproj 直接跳过且不启动 PowerShell；
# 自定义的非 trx --logger 仍启动脚本后由脚本跳过；两种情况都会输出「跳过计数门禁」；
# TRX 修改时间晚于当前时间 30 秒以上视为不可信并直接失败）
dotnet test HunterBox.slnx -c Release -p:Platform=x64

# 手动复核 TRX 完整性（缺失/截断会失败；显式传 -ChangelogPath 时才比对计数漂移；不传 -NotBeforeUtc 时不校验新鲜度）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-test-counts.ps1

# 一键修复行尾（只尝试修复 LF/裸 CR 违规文件：调整行尾并补齐末尾换行、补 UTF-8 BOM，不改其它正文；
# 行尾违规且无法严格 UTF-8 解码（GBK/ANSI、含 NUL 的无 BOM UTF-16/32）、带 UTF-16/32 BOM、
# 不可读或写入失败时保持原样并退出 1；合规但缺 BOM 不处理）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-line-endings.ps1 -Fix

# 运行（调试版会在程序目录生成 Mods/、hunterbox.ini、logs/）
dotnet run --project src\HunterBox.App\HunterBox.App.csproj -c Debug -p:Platform=x64
```

- 解决方案把 `Any CPU`/`x64` 都映射到项目 `x64`，不要新增其它平台。
- `Directory.Build.props` 已开启 `Nullable`、`ImplicitUsings`、`TreatWarningsAsErrors`、`InvariantGlobalization`。
- 四个项目开启 `GenerateDocumentationFile`：public 成员缺 XML 注释会以 CS1591 编译失败。
- `global.json` 声明最低 SDK `10.0.100` 且 `rollForward=latestMajor`：允许使用更高的主版本（例如本机 11.x），
  避免只装了新 SDK 的机器无法构建；升级 SDK 时同步该文件与本文档。

## 4. 编码约定

- C# 使用最新语言版本；优先清晰、直白的实现，避免过度抽象与无需求驱动的配置项。
- 代码注释、XML 文档、日志与用户可见文案统一使用中文；不要使用表情符号。
- 注释必须是有信息量的完整句子；`CommentHygieneTests` 会扫描 `src/` 与 `tests/` 的 C# 注释，
  禁止残句、损坏的注释模板（如空 `<summary>`、乱码、占位符）。
- 文本文件统一使用 UTF-8 BOM，范围是 `.cs`/`.md`/`.csproj`/`.props`/`.targets`/`.xaml`/`.slnx`/`.ps1`/`.txt`/`.pubxml`
  （`EncodingConsistencyTests`）；无 BOM（或 LF）例外的完整清单只在下方「行尾统一 CRLF」小节枚举。
- 行尾统一 CRLF：`.gitattributes` 的 `eol=crlf` 只保证新检出，工作区由 `CommentHygieneTests` 的
  `SourceLineEndingChecks` 门禁覆盖 `.cs`/`.xaml`/`.md`/`.csproj`/`.props`/`.targets`/`.slnx`/`.ps1`/`.txt`/`.pubxml`；扩展名唯一来源是 `tools/text-file-extensions.txt`。
  `Directory.Build.targets` 在构建期运行 `tools/verify-line-endings.ps1`，编辑器/外部工具写入 LF 会直接构建失败；
  提交前钩子启用一次即可：`git config core.hooksPath .githooks`。
  门禁剪枝目录唯一来源是 `tools/gate-pruned-directories.txt`（脚本与测试枚举共用）；
  链接判定先看 `Get-Item` 的 `LinkType`/`Target`，目录再回退 `fsutil` 重解析标签并用十六进制字符串比较
  （PS 5.1 把 `0xA0000003` 当 Int32 负数）；只放行已知非链接标签，未知标签跳过并告警；
  文件重解析点不查标签、无法确认即跳过并告警，避免云占位逐文件进程与下载；
  `.githooks/pre-commit` 按 `.gitattributes` 保持 LF，不参与扫描；
  `tools/text-file-extensions.txt` 与 `tools/gate-pruned-directories.txt` 自身由测试断言 BOM+CRLF；
  单次构建开销本机实测：冷缓存约 1.6 秒、热缓存约 0.5 秒（脚本自身 455–543ms），属设计取舍。
  `.editorconfig` 的 `[*]` 已声明 `end_of_line = crlf` 与 `charset = utf-8-bom`，
  例外只有：`.json`（无 BOM + LF）、`.editorconfig`/`.gitattributes`/`.gitignore`/`app.manifest`（无 BOM）、
  `.githooks/*`（无 BOM + LF）；编辑期保护只对遵守 EditorConfig 的编辑器生效，构建期门禁仍是唯一权威兜底。
  git 因 `text=auto` 规范化不显示纯 LF 差异，不要用该设置掩盖行尾漂移。
- 行尾报错排查：构建报 `MSB3073`（`VerifyTextFileLineEndings` 退出码 1）时，
  先运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-line-endings.ps1 -Fix`
  重写行尾违规文件，再重新构建；脚本只调整行尾并补齐末尾换行（补齐末尾换行会改变文件内容），
  重写时补 UTF-8 BOM；行尾违规且无法严格 UTF-8 解码（GBK/ANSI、含 NUL 的无 BOM UTF-16/32、带 UTF-16/32 BOM）、
  不可读或写入失败时会保持原样并退出 1，需先自行转码为 UTF-8 或关闭占用；
  若文件内容与 HEAD 一致、`git diff` 为空但 `git status` 显示 M，
  多半是纯行尾漂移（差异被规范化隐藏），可用 `git checkout -- <文件>` 兜底恢复。
- 命名空间与目录结构一致；文件名即类型名（每个文件一个主要类型，测试支撑类除外）。
- 产品名统一写作 `HunterBox`，与中文之间不强制加空格（避免全仓重排）；
  新增文案按所在段落既有风格保持一致，同一段落内不要混用两种写法。
- 不要吞异常：可恢复的清理失败返回布尔值并记录日志；影响用户数据的失败必须抛出带上下文的异常。
- 避免 `DateTimeOffset.Now` 直接进入业务逻辑，使用 `IClock`。

## 5. 架构约束（评审逐条检查）

1. Domain 只用 BCL，不引入第三方包，不做 IO。
2. Application 只通过端口接口使用存储/解压/pak/指纹；不得出现 `SharpCompress`、`JsonSerializer`、`IniSettings` 等实现细节字样。
3. Infrastructure 只做适配，不新增业务判断。
4. App 不自行实现业务性文件 IO：存储/配置/日志分别走 `ModRepository`、`IniSettings`、`AppLog` 或 Application 用例。
5. 所有解压条目路径必须经 `BoxPaths.NormalizeRelative` + `BoxPaths.ResolveInside` 校验，越界直接失败，不静默跳过。

## 6. 文件系统与数据安全

- 删除/清理统一使用 `IFileSystem`（实现 `Infrastructure/IO/SystemFileSystem`）：`TryDeleteFile`/`TryDeleteDirectory`
  为尽力删除，`TryDeleteFileForce`/`TryDeleteDirectoryForce` 会先清只读属性并支持取消。
- 链接（junction/symlink）必须只删除链接本身；子树含链接或安全扫描失败时整体跳过（fail-closed），
  绝不允许递归进入链接目标。
- 安装/重装/卸载采用「暂存目录 → 改名换入 → 写记录」；失败必须回滚并保留旧数据。
- 启用/禁用使用 `FileUndoJournal` 记录撤销信息，逆序回滚。
- 临时目录由 `TempHousekeeping.PurgeStale` 在启动时清理超期条目（默认 7 天），不得在业务代码里删除整个 Temp。

## 7. 测试约定

- 框架：xUnit 2.9.3；测试工程目标 `net10.0`，仅在本机 Windows 上运行。
- 新行为、缺陷修复必须附带回归测试；测试名描述行为（`Method_Scenario_Expectation`）。
- 使用测试支撑设施：`TempDir`（临时目录）、`ZipBuilder`/`ZipTestTools`（构造压缩包）、
  `PakCodecTestTools`（构造 pak）、`TestSupport/TestGame*`（游戏配置）。
- 测试不得触碰真实游戏目录、真实存档或用户配置；一律使用临时目录。
- 环境相关用例（符号链接、目录 ACL）必须使用发现阶段跳过特性（`FileSymlinkFact`、`DirectoryLinkFact`、
  `AclFact`），无权限时显示 Skipped，不能静默返回。
- 提交前必须全量测试通过；不得为通过测试而放宽断言或删除用例。

## 8. 版本与文档同步

版本号唯一来源：`src/HunterBox.App/HunterBox.App.csproj` 的 `Version` / `AssemblyVersion` / `FileVersion`
（主窗口标题读取该版本）。

每次发布/修复轮次需要同步：

1. `README.md` 版本号；
2. `docs/SPEC.md`、`docs/wiki/09-build-test-run.md`、`docs/wiki/01-overview.md` 的版本号与标题示例；
3. `docs/ARCHITECTURE.md`、`docs/wiki/09-build-test-run.md`、`docs/wiki/10-test-map.md` 的测试计数
   （`[Fact]`/`[Theory]`/派生 `[Fact]` 数量与测试方法总数）。

仓库不再维护 `CHANGELOG.md`：变更历史以 git 提交记录为准，运行计数直接看 `dotnet test` 输出。

## 9. 评审修复流程

- 评审报告位于 `.local/review/CODE-REVIEW-vX.Y.Z-RN.md`（`.local/` 为本地数据，已被 `.gitignore` 忽略）。
- 按编号逐项修复，每轮产出两个本地提交：
  1. `fix(...)/test(...)`：代码或测试修复；
  2. `chore(release): vX.Y.Z (RN fixes)`：版本号与文档同步。
- 修复必须对应评审的验收标准；无法修复的项要说明原因或改为文档化取舍。

## 10. Git 约定

- 提交信息使用 `type(scope): summary`（如 `fix(fs): ...`、`test(fs): ...`、`chore(release): ...`）。
- 行尾钩子：每个克隆执行一次 `git config core.hooksPath .githooks`，`.githooks/pre-commit` 会用
  `tools/verify-line-endings.ps1` 拦截 LF 行尾。
- 默认只做本地提交，**不要 `git push`**，除非用户明确要求。
- 不要提交构建产物、`dist/`、`.local/`、日志或用户数据；不要用 `--no-verify` 绕过检查，不要改写已推送历史。

## 11. 文档地图

- 构建/测试/运行：[docs/wiki/09-build-test-run.md](docs/wiki/09-build-test-run.md)
- 测试地图：[docs/wiki/10-test-map.md](docs/wiki/10-test-map.md)
- 分层与依赖：[docs/wiki/02-architecture.md](docs/wiki/02-architecture.md)
- PAK 子系统：[docs/PAK.md](docs/PAK.md)、[docs/wiki/07-pak-subsystem.md](docs/wiki/07-pak-subsystem.md)
- 关键流程（安装/启用/回滚）：[docs/wiki/08-flows.md](docs/wiki/08-flows.md)
