# CLAUDE.md

本仓库的完整协作约定见 [AGENTS.md](AGENTS.md)。本文件是给 Claude Code 的速查版，两者冲突时以 AGENTS.md 为准。

## 项目

HunterBox：.NET 10 + WinUI 3（unpackaged、仅 x64）的 Monster Hunter MOD 管理器。
分层：`Domain`（模型/规则/端口，仅 BCL）← `Application`（用例）← `Infrastructure`（适配器）← `App`（WinUI）。
依赖只能向内，禁止反向引用；Domain 不得引入第三方包。

## 常用命令

```powershell
dotnet build HunterBox.slnx -c Release -p:Platform=x64   # 必须 0 警告 0 错误
dotnet test HunterBox.slnx -c Release -p:Platform=x64   # 提交前必须全部通过；Release 自动校验 TRX 完整性（显式提供 CHANGELOG 时才比对计数）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-test-counts.ps1   # 手动复核 TRX 完整性（可选；显式提供 CHANGELOG 时才比对计数）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-line-endings.ps1 -Fix   # 只修行尾违规（补末尾换行/BOM）；无法严格 UTF-8 解码（含 NUL）、带 UTF-16/32 BOM、不可读或写入失败保持原样并退出 1；行尾合规但缺 BOM 不处理
dotnet run --project src\HunterBox.App\HunterBox.App.csproj -c Debug -p:Platform=x64
```

## 硬性约束

- `TreatWarningsAsErrors`：任何警告都是构建失败。
- public 成员必须有中文 XML 注释（缺注释触发 CS1591）。
- 注释、日志、界面文案用中文，不用表情符号；`CommentHygieneTests` 会扫描注释质量。
- 文本文件统一 UTF-8 BOM + CRLF（含 `.txt`、`.pubxml`）；例外：`.json` 无 BOM + LF，
  `.editorconfig`/`.gitattributes`/`.gitignore`/`app.manifest` 无 BOM，`.githooks/*` 无 BOM + LF；
  `.gitattributes` 只影响新检出，`CommentHygieneTests` 扫描工作区，`dotnet build` 与 pre-commit 钩子
  （`git config core.hooksPath .githooks`）会在写入 LF 时直接失败，git 不显示纯行尾差异。
- 门禁剪枝目录唯一来源 `tools/gate-pruned-directories.txt`（脚本与测试枚举共用）；
  链接判定用 `LinkType`/`Target`，目录回退 `fsutil` 标签并做**字符串**比较（PS 5.1 的十六进制常量是 Int32 负数）；
  未知标签与无法确认的文件重解析点（含云占位）跳过并告警。
- 解压条目路径必须经 `BoxPaths.NormalizeRelative` + `BoxPaths.ResolveInside` 校验。
- 删除/清理走 `IFileSystem`（`SystemFileSystem` 实现）；链接只删链接本身，子树含链接或扫描失败时整体跳过（fail-closed）。
- 新行为/缺陷修复必须带回归测试；测试只用 `TempDir` 等临时目录，不碰真实游戏目录。
- 计数门禁只在 Release 全量测试自动执行；Debug、`--filter`、`--list-tests` 由 csproj 直接跳过，
  自定义的非 trx `--logger` 由脚本跳过，两种情况都会打印跳过原因。
- 平台相关测试用发现阶段跳过特性（`FileSymlinkFact`/`DirectoryLinkFact`/`AclFact`），不要静默 return。

## 版本与交付

- 版本号唯一来源：`src/HunterBox.App/HunterBox.App.csproj`。
- 每轮修复产出两个提交：`fix(...)`/`test(...)` + `chore(release): vX.Y.Z (RN fixes)`，
  并同步 `README.md`、`docs/SPEC.md`、`docs/ARCHITECTURE.md` 与 `docs/wiki/` 的版本和测试计数。
- 评审报告在 `.local/review/`（本地目录，不入库）。
- 默认**不要 `git push`**，除非用户明确要求；不要提交构建产物、`dist/`、`.local/`。

## 参考

- 构建/测试/运行：`docs/wiki/09-build-test-run.md`
- 测试地图：`docs/wiki/10-test-map.md`
- 架构与依赖：`docs/ARCHITECTURE.md`、`docs/wiki/02-architecture.md`
- 规格：`docs/SPEC.md`；PAK 格式：`docs/PAK.md`
