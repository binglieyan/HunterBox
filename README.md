# HunterBox

怪物猎人 MOD 管理器（荒野 / 崛起 / 世界）。.NET 10 + WinUI 3，unpackaged 单目录发布，仅 x64。

本项目是独立开发的第三方工具：功能规格见 [docs/SPEC.md](docs/SPEC.md)，实现与资源均为项目自有，
不包含任何第三方程序的代码、名称、图标或美术资源。

> 当前版本 `1.5.207`（窗口标题 `HunterBox v1.5.207 - <当前游戏名>`）；变更历史以 git 提交记录为准（仓库不再维护 CHANGELOG）。

## 功能

- **安装**：zip / 7z / rar / tar / gz 等压缩包（按内容魔数识别、不依赖扩展名）、文件夹、裸
  `.pak` / `.lua` / `.dll` 单文件；支持多选与拖拽。解压在后台线程完成，弹出安装向导：
  预览图、名称/分类/版本/作者/NexusId/来源/描述，文件树 + 多根 + 安装前缀，
  FOMOD 按安装步骤分组（必选/推荐/单选/至少选一/依赖可见性），多组件包单选变体，也可切「手动选择文件」。
  安装包按「文件清单 + 大小」指纹在当前游戏内去重；名称必填且同游戏内不能重名。
- **启用 / 禁用**：散装文件（`natives`、`reframework`、`nativePC` 等）复制到游戏目录，被覆盖的游戏原文件
  按游戏隔离备份到 `Mods/.backup/<gameId>/`，禁用时还原；多个 MOD 争用同一文件时按列表顺序重新复制胜出方内容。
  pak 型 MOD（荒野 / 崛起）放进 `<游戏目录>/pak_mods`；世界不使用 `pak_mods`，pak 按普通散装文件处理。
- **pak 命名**：默认「自动修正PAK」编号方案——沿用 `pak_mods` 里已有 pak 的编号，新 pak 只占用最小可用号，
  禁用后不重排、不压紧其它 MOD 的编号；关闭后保留原文件名，同名且内容不同会拒绝启用并弹窗列出冲突。
- **模型改写**（荒野 / 崛起 / 世界三作）：按各作的完整模型身份改写散装文件路径与 pak 内条目路径；
  散装只改名、不改文件内容，pak 只重写条目表（TOC）路径哈希、数据区不动，首次写回前生成 `<pak>.bak`。
  整张 TOC 加密（feature 8/24）的 pak 会解包→改名→重打包（输出明文 TOC 的普通 mod pak）。
  编辑器支持 MOD 名字只读行、`(文件数)类型|名字[pfb][pak]`、pak ToolTip、仅未使用 ID 过滤、
  候选强制包含当前源；拖 MOD 文件夹到列表可直接改该文件夹的模型 ID。
- **清理（删除全部MOD文件）**：二选一——「仅删HunterBox安装的」= 安全禁用当前游戏全部 MOD 并还原原文件；
「清理HunterBox记录残留」= 在前者基础上按 MOD 记录删除游戏目录里仍存在的HunterBox文件；不整目录删除、不删备份、
不删用户自己的非HunterBox文件。
- **其它操作**：重装MOD（优先用记录的安装源，缺失再选包，走安装向导）、用新包更新（本地新包替换，
  保留序号）、重新复制MOD、上移/下移/快速移动（顶/中/底或指定位置）、多选 + 启用选中MOD（按列表顺序批量启用）、
  卸载（总是二次确认；已启用则先安全禁用）、MOD 信息编辑、右键打开 NexusMods /来源网址/查看文件。
- **存档**：把游戏存档目录整目录备份到 `Saves/<gameId>/<时间戳>/`，备份时生成 SHA-256 哈希清单；
  还原前校验备份文件、写入后再校验目标文件，不一致会拒绝还原并提示。存档管理器支持备份/恢复/重命名/删除/
  打开目录，并可导入旧存档 `Saves_<appid>/*.zip`（旧格式崛起为 `Saves/`；导入后同样生成哈希清单）。
- **游戏与设置**：三作切换、游戏路径/存档路径（Steam 注册表与 `userdata` 自动探测 + 手动指定 + exe 校验）、
  安装选项（无操作/复制/移动安装包到 `Download/`）、自动修正PAK、检查PAK内部文件（启用前比对 pak 条目重叠并提示）、
  检测游戏状态（运行中拦截启用/禁用/清理）、Steam 启动游戏、外观主题（跟随系统/浅色/深色）。
- **旧数据迁移**：首次启动检测旧数据目录 `Mods_<appid>/<id>/{info.xml,files}`、`Backup_<appid>`、`Saves_<appid>`、
  `Download` 与 `sjmodbox.ini`，一次性只读复制导入（不移动/删除旧文件）；旧配置自动迁移到
  `hunterbox.ini`（不覆盖新值、可重复执行）。
- **长任务**：安装/重装、批量启用/禁用、清理等支持状态文字进度与「取消」按钮（安全点协作取消，
  未处理项保持原状）。
- **日志与异常**：文件日志 `logs/hunterbox-<日期>.log`（程序目录不可写时降级 `%LOCALAPPDATA%/HunterBox/logs`）；
  XAML / AppDomain / 未观察任务三类未处理异常写 `logs/crash-<时间戳>.txt` 并提示；`index.json` 损坏时保留
  `.bad-<时间戳>` 原件并提示从 `.bak` 恢复，绝不静默覆盖。
- **列表信息**：「备注信息」列运行时显示被替换模型明细（模型名/完整 ID/`[pfb]`/`[pak]`），无模型时回退用户描述。

## 已知限制

- 模型名称表/pak 路径清单仍为内嵌资源，暂不支持外部表覆盖。
- 明文 TOC pak 只做「条目表路径哈希改写」；TOC 加密（feature 8/24）的 pak 走解包→改名→重打包回退（输出明文 TOC）。
- 尚未完成真实 MOD 的端到端进游戏验证；发布前请自行备份存档与游戏文件。
- 取消为安全点协作取消：单个 MOD 的启用/禁用/清理事务内部不可中途打断（保持事务与回滚语义）。
- 有意精简、不做：联网检查更新、外链/Logo/关于页、MO2 overwrite、AVP 修复、贴图改名、多作模型库。

## 构建与测试

```powershell
dotnet build HunterBox.slnx -c Release -p:Platform=x64
dotnet test  HunterBox.slnx -c Release
```

## 发布

```powershell
dotnet publish src\HunterBox.App\HunterBox.App.csproj -p:PublishProfile=FolderProfile
```

产物目录由 [FolderProfile.pubxml](src/HunterBox.App/Properties/PublishProfiles/FolderProfile.pubxml) 的 `PublishDir`
决定：当前指向仓库内的 `F:\work\nhw\HunterBox\dist\HunterBox`（已在 `.gitignore` 中忽略），
换机器时改成 `$(MSBuildProjectDirectory)\..\..\dist\HunterBox\` 即可。产物为 .NET 10 与 Windows App SDK
框架依赖，约 22 个文件 / 17 个 DLL / 53 MB（开启 R2R 后）。
目标机需要 **.NET 10 桌面运行时 + Windows App Runtime 2.7.x**。发布目标会自动补 `HunterBox.App.pri`、
精简语言目录、剔除未使用的 WinAppSDK 可选组件与 XML 文档、剔除 pdb；R2R 默认开启以加快首屏启动，体积相应增大。
发布不再自动生成压缩包，需要分发时手动把发布目录压缩即可。
详见 [构建、测试与运行](docs/wiki/09-build-test-run.md)。

## 目录结构

```
src/
├─ HunterBox.Domain/          纯规则：路径安全、包识别、放置决策、pak 计划、模型身份（无外部依赖）
├─ HunterBox.Application/     用例：安装/重装、启用禁用、清理、pak 挂载、模型改写、存档
├─ HunterBox.Infrastructure/  实现：JSON 存储、SharpCompress 解压、INI 设置、pak 读写、日志/迁移、Steam 定位
└─ HunterBox.App/             WinUI 3 外壳与组合根（主窗口 + 各对话框 + 游戏配置）
tests/HunterBox.Tests/        xUnit 测试（Domain / Application / Infrastructure）
docs/SPEC.md                  当前行为规格（磁盘布局/安装/启用/pak/模型/UI/设置键）
docs/ARCHITECTURE.md          架构、模块职责、依赖规则、测试策略与里程碑
docs/PAK.md                   pak 读写与模型改写能力现状
docs/wiki/README.md           Code Wiki 索引（架构、模块职责、关键类/函数、流程、构建发布、测试地图）
```

运行期在程序目录下生成：`Mods/index.json`（MOD 记录与 SchemaVersion）、`Mods/<id>/files/`、
`Mods/.staging/`、`Mods/.backup/<gameId>/`、`Download/`、`Saves/<gameId>/<时间戳>/`、`Temp/`、
`logs/`、`hunterbox.ini`。

## 依赖

- .NET 10 桌面运行时 + Windows App Runtime 2.7.x（发布包为框架依赖）
- Windows 10 1809+ / Windows 11（WinUI 3 要求）
- 运行库：SharpCompress（解压）；Windows App SDK 2.7.0 为框架依赖（目标机需 Windows App Runtime 2.7.x）
- 测试：xUnit

## 免责声明

本项目名称、品牌与图标均为项目自有（HunterBox），不包含任何第三方程序的名称、图标或美术资源，
与 Capcom 及任何第三方 MOD 管理器作者均无隶属/授权关系。使用前请自行备份存档与游戏文件；
请支持 MOD 原作者，不要在联机场景使用影响他人的 MOD。
