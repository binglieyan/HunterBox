# 01 项目概览

## 1. 定位与边界

HunterBox 是独立开发的第三方怪物猎人 MOD 管理器，目标是把「下载到的 MOD 包」安全地安装、启用、
禁用、清理，并额外提供模型替换（按完整模型身份改写路径）与存档备份能力。

明确不做（有意精简，见 [README](../../README.md#已知限制) 与 [SPEC](../SPEC.md) 第 14 节）：

- 不做联网检查更新、外链/Logo/关于页；
- 不做 MO2 overwrite、AVP 修复、贴图改名、多作模型库；
- 世界（MHW）不使用 `pak_mods`，pak 按普通散装文件处理；
- 不修改游戏原始文件内容：散装只改名，pak 只重写条目表路径哈希（内容替换 API 仅兼容保留）。

## 2. 支持的游戏

游戏静态配置定义在 [GameCatalog.cs](../../src/HunterBox.Domain/Games/GameCatalog.cs)，类型为
[GameProfile](../../src/HunterBox.Domain/Games/GameProfile.cs)。

| 游戏 ID | 显示名 | pak 前缀 | Steam AppId | 主程序 | 默认目录名 | Nexus 路径段 |
|---|---|---|---|---|---|---|
| `mhws` | 怪物猎人荒野 | `re_chunk_000.pak.sub_000.pak.patch_` | 2246340 | `MonsterHunterWilds.exe` | `MonsterHunterWilds` | `monsterhunterwilds` |
| `mhr` | 怪物猎人崛起 | `re_chunk_000.pak.patch_` | 1446780 | `MonsterHunterRise.exe` | `MonsterHunterRise` | `monsterhunterrise` |
| `mhw` | 怪物猎人世界 | 无（`null`） | 582010 | `MonsterHunterWorld.exe` | `Monster Hunter World` | `monsterhunterworld` |

`PakPatchPrefix == null` 表示该游戏不使用 `pak_mods`：`GameProfile.UsesPakModsFolder` 为 `false`，
`CreatePakProfile` 会抛 `InvalidOperationException`。游戏 ID 同时是 INI 段名与 `ModRecord.GameId` 的取值。

pak 编号以实际扫描结果为准：会扫描游戏根目录与 `pak_mods` 中已有的 patch 编号，再取最小可用编号；
游戏更新或大型 DLC 新增的编号会被自动扫描到，静态配置值只作为下界兜底，不需要人工维护。

## 3. 技术栈与工程约束

| 项 | 值 | 位置 |
|---|---|---|
| 运行时 | .NET 10（Domain/Application/Infrastructure 为 `net10.0`） | 各 `.csproj` |
| UI | WinUI 3，`net10.0-windows10.0.19041.0`，`TargetPlatformMinVersion` 10.0.17763.0 | [HunterBox.App.csproj](../../src/HunterBox.App/HunterBox.App.csproj) |
| 应用模型 | unpackaged（`WindowsPackageType=None`），仅 x64（`RuntimeIdentifier=win-x64`） | 同上 |
| Windows App SDK | 2.7.0 | 同上 |
| 解压 | SharpCompress 0.50.9 | [HunterBox.Infrastructure.csproj](../../src/HunterBox.Infrastructure/HunterBox.Infrastructure.csproj) |
| 测试 | xUnit 2.9.3 + Microsoft.NET.Test.Sdk 17.14.1 | [HunterBox.Tests.csproj](../../tests/HunterBox.Tests/HunterBox.Tests.csproj) |
| 产品版本 | `1.5.207`（窗口标题 `HunterBox v1.5.207 - <游戏名>`） | `HunterBox.App.csproj` 的 `Version` |
| 公共工程约束 | `Nullable`、`ImplicitUsings`、`TreatWarningsAsErrors`、`InvariantGlobalization` | [Directory.Build.props](../../Directory.Build.props) |
| 文档约束 | 四个项目开启 `GenerateDocumentationFile`：public 成员缺 XML 注释即编译失败（CS1591） | 各 `.csproj` |

解决方案 [HunterBox.slnx](../../HunterBox.slnx) 只有 `Any CPU` 与 `x64` 两种平台，并把两种平台都映射到项目 `x64`，
因此 `dotnet build HunterBox.slnx` 不会出现「配置不存在」。

## 4. 源码目录结构

```text
src/
├─ HunterBox.Domain/
│  ├─ Layout/       BoxPaths（磁盘布局 + 路径安全）
│  ├─ Games/        GameProfile（游戏静态配置与目录校验）
│  ├─ Mods/         ModRecord/ModMetadata/包识别/放置计划/FOMOD/存储端口
│  ├─ Paks/         pak 命名与放置计划、pak 读写接口与档案信息
│  ├─ Models/       模型身份、路径识别/改写规则、模型清单端口
│  └─ Archives/     压缩格式探测、解压选项与端口
├─ HunterBox.Application/
│  ├─ Install/      安装/重装/更新/卸载、暂存、FOMOD/变体、安装源归档
│  ├─ Activation/   启用/禁用/重新复制编排、批量动作、游戏运行守卫
│  ├─ Mount/        散装文件真实挂载（复制/备份/还原/删除/撤销）
│  ├─ Paks/         pak_mods 实际写入/删除、指纹缓存、端口
│  ├─ Models/       模型检测/自动分类/编辑器规划/文件夹改写/pak 与散装改写
│  ├─ Cleanup/      「删除全部MOD文件」两个安全范围
│  ├─ Saves/        存档目录备份/还原/重命名/删除、旧 zip 导入端口
│  ├─ Mods/         跨游戏归属校验、旧安装路径修复、文件清单重扫
│  ├─ Common/       撤销日志、尽力删除、时钟端口
│  └─ Settings/     ISettings 设置端口
├─ HunterBox.Infrastructure/
│  ├─ Storage/      ModRepository（Mods/index.json）
│  ├─ Archives/     ArchiveExtractor（SharpCompress）
│  ├─ Settings/     IniSettings（hunterbox.ini）
│  ├─ Paks/         RE PAK 解析/编解码/加密/校验和/改名/打包/解包/前像/路径清单
│  ├─ Mods/         内嵌名称表、模型候选目录、路径→名字解析
│  ├─ Games/        Steam VDF/库目录/安装目录/存档目录定位
│  ├─ Logging/      AppLog、CrashReporter
│  ├─ Migration/    LegacyMigrationService（旧HunterBox数据导入）
│  ├─ Saves/        SharpCompressSaveImporter（旧存档 zip）
│  └─ Startup/      SingleInstanceGuard（命名互斥体）
└─ HunterBox.App/
   ├─ App.xaml(.cs)          入口、日志、全局异常钩子、主题
   ├─ AppServices.cs         唯一组合根
   ├─ MainWindow.*.cs        主窗口按职责拆分的 partial
   ├─ *Dialog.xaml(.cs)      安装/模型/信息/移动/设置/存档/迁移对话框
   ├─ *Workflow.cs           安装与维护的后台无 UI 服务
   └─ ModelPanel/ModListView/OperationRunner/ModRow/FileTreeItem 等 UI 基础设施
tests/HunterBox.Tests/       测试（105 个顶层测试文件 + TestSupport/ 辅助）
docs/                        SPEC/ARCHITECTURE/PAK/BUILD/PUBLISH + 本 Wiki
```

## 5. 运行期磁盘布局

HunterBox根目录是程序所在目录（`AppContext.BaseDirectory`）。布局由
[BoxPaths](../../src/HunterBox.Domain/Layout/BoxPaths.cs) 计算，**只算路径、不创建目录**：

```text
<Root>/
├─ hunterbox.ini                 # 全局设置（[settings] + [<gameId>]）
├─ Mods/
│  ├─ index.json                 # MOD 记录（唯一权威数据源）
│  ├─ index.json.bak             # 上一次成功保存的前一版
│  ├─ index.json.bad-<时间戳>    # 损坏索引保留件（绝不静默覆盖）
│  ├─ .staging/<guid>/files/     # 安装暂存（与 Mods 同卷，成功后改名换入）
│  ├─ .backup/<gameId>/...       # 被覆盖的游戏原文件备份（按游戏隔离）
│  └─ <id>/files/                # 每个 MOD 的文件本体（相对路径与游戏目录一致）
├─ Download/                     # 安装源归档（安装选项=复制/移动时）
├─ Saves/<gameId>/<时间戳>/      # 存档目录备份（目录复制 + 哈希清单）
├─ Temp/                         # 解压等临时目录
└─ logs/
   ├─ hunterbox-<yyyyMMdd>.log   # 按日日志（超过 8MB 滚动为 .N.log）
   └─ crash-<yyyyMMdd-HHmmss>.txt# 未处理异常报告
```

说明：

- `Mods/<id>/info.json` 当前实现**不生成也不读取**，元数据只存在 `Mods/index.json`
  （见 [SPEC](../SPEC.md) 第 3 节）。
- 程序目录不可写时，日志降级到 `%LOCALAPPDATA%/HunterBox/logs`（`AppLog.Initialize`）。
- 旧数据目录（`Mods_<appid>`、`Backup_<appid>`、`Saves_<appid>`、`Download`、`sjmodbox.ini`）只读扫描，
  导入时复制到上述新布局，不移动/删除旧文件。

## 6. 主要功能

| 功能 | 入口 | 核心用例 |
|---|---|---|
| 安装包/文件夹/裸 `.pak`/`.lua`/`.dll` | 主窗口「添加MOD」、拖拽 | `ModInstaller.Stage` + `InstallStaging.Commit` |
| 重装 / 用新包更新 | 右键菜单 / 按钮 | `ModInstaller.StageReinstall`（保留 Id/Index/GameId） |
| 启用 / 禁用 / 重新复制 | 行勾选、按钮、批量 | `ModActivationService` + `ModBatchActivator` |
| pak 编号稳定化 | 「自动修正PAK」设置 | `PakPlanner` / `PakNumbering` / `PakMountService` |
| 模型改写 | 右键「MOD文件修改」、拖文件夹 | `ModelRewriteCoordinator` / `PakModelRewriteService` / `LooseModelRewriteService` |
| 清理 | 「删除全部MOD文件」 | `ModCleanupService`（两种范围） |
| 存档 | 底部「存档」 | `SaveBackupService` + `SharpCompressSaveImporter` |
| 旧数据迁移 | 启动提示 / 「导入数据」 | `LegacyMigrationService` + `IniSettings` 旧键迁移 |
| 设置 | 「游戏设置」 | `IniSettings` + `SettingsDialog` |

## 7. 相关文档

- 行为规格：[SPEC.md](../SPEC.md)
- 架构决策与规范：[ARCHITECTURE.md](../ARCHITECTURE.md)
- pak 能力矩阵与备份语义：[PAK.md](../PAK.md)
- 版本历史：git 提交记录（仓库不再维护 CHANGELOG）
