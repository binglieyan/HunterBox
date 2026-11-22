# HunterBox 行为规格（当前实现）

> 本文档描述当前实现的行为契约，以 `src/` 代码为准。

## 1. 目标与边界

- 支持游戏：怪物猎人荒野（`mhws`）、怪物猎人崛起（`mhr`）、怪物猎人世界（`mhw`）。
- 只做 MOD 管理：安装 / 启用 / 禁用 / 重装 / 更新 / 排序 / 清理 / 模型改写 / 存档备份；不做下载、不做联网更新检查。
- 不读取游戏数据文件来建立模型表：荒野的模型名称表与 pak 路径清单以**内嵌资源**形式随程序发布
  （`HunterBox.Infrastructure` 的 `Paks/FileLists/mhws.models.list.gz`、`Mods/NameTables/mhws_*.csv.gz`）；
  三作模型改写各自使用内嵌名称表与路径规则（§9）。
- 兼容一次性导入旧第三方HunterBox（v3.10.2 历史版本）数据（§12），不修改/不删除旧目录。

## 2. 游戏清单

| ID | 显示名 | Steam AppId | 主程序 | pak 放置 | 编号前缀 | 基础编号 | 默认目录名 |
|---|---|---|---:|---|---|---:|---|
| `mhws` | 怪物猎人荒野 | 2246340 | `MonsterHunterWilds.exe` | `pak_mods/` | `re_chunk_000.pak.sub_000.pak.patch_` | 6 | `MonsterHunterWilds` |
| `mhr` | 怪物猎人崛起 | 1446780 | `MonsterHunterRise.exe` | `pak_mods/` | `re_chunk_000.pak.patch_` | 1 | `MonsterHunterRise` |
| `mhw` | 怪物猎人世界 | 582010 | `MonsterHunterWorld.exe` | 不使用（pak 当散装文件） | — | — | `Monster Hunter World` |

- 用于目录校验的「主程序」缺失时，只要目录存在即视为有效游戏目录（`GameDirectoryProbe.IsGameDirectory` 的降级规则）。
- 游戏路径/存档路径缺省时通过 Steam 探测：注册表 `HKCU\Software\Valve\Steam` / `HKLM\SOFTWARE\Valve\Steam`、
  `libraryfolders.vdf`、`appmanifest_<appid>.acf`、`userdata/<账号>/<appid>/remote`。
- NexusMods 链接当前按荒野站点路径生成：`https://www.nexusmods.com/monsterhunterwilds/mods/<NexusId>`。

## 3. 磁盘布局

```
<程序目录>
├─ Mods\
│  ├─ index.json                    # MOD 记录主存储：{ "SchemaVersion": 1, "NextId": n, "Mods": [...] }
│  ├─ index.json.bak                # 每次保存前保留的上一版索引
│  ├─ <id>\files\...                # MOD 文件本体（相对路径正斜杠、安装时统一小写）
│  ├─ .staging\<guid>\files\        # 安装原子暂存（成功后目录改名为 Mods\<id>\files）
│  ├─ .backup\<gameId>\<相对路径>    # 被 MOD 覆盖的游戏原文件（按游戏隔离）
│  └─ .cleanup-quarantine\<gameId>\<时间戳>\  # 清理时无法区分归属的残留隔离区
├─ Download\                        # 安装源归档（安装选项为复制/移动时）
├─ Saves\<gameId>\<时间戳>\          # 整目录存档备份（时间戳 yyyyMMdd-HHmmss，可重命名）
│   └─ hunterbox-backup-hashes.json  # SHA-256 哈希清单（备份时生成，不还原到游戏目录）
├─ Temp\<guid>\                     # 解压等临时目录（失败/完成即清理）
├─ logs\                            # hunterbox-<yyyyMMdd>.log、crash-<yyyyMMdd-HHmmss>.txt
└─ hunterbox.ini                    # 全局设置（§11）
```

- `ModRecord.Id`：自增正整数，`NextId` 高水位保证删除后不复用。
- `ModRecord.Index`：排序号，与 ID 解耦；**按游戏独立**从 1 开始，重排后归一为 1..N。
- `ModRecord.GameId`：游戏归属；旧索引没有该字段时读为 `null`（不出现在任何游戏列表，启动时引导归属）。
- 多游戏共用同一 `Mods/index.json`；列表、启用、禁用、清理、备份、重复安装检测全部按当前游戏隔离。
- 索引写入：先写 `index.json.tmp`，用 `File.Replace` 原子替换并生成 `.bak`；读不出/半截/结构不对时，
  把原文件改名为 `index.json.bad-<yyyyMMddHHmmss>` 保留，返回空仓库并给出 `LoadWarning`，后续保存不会覆盖原件。
  若加载时连改名都失败、之后用户又手工把 `.bak` 恢复成 `index.json`，保存会识别出有效索引并拒绝覆盖、提示重启。
- `info.json`：当前实现不再生成，元数据只存在 `index.json`；旧版本残留的 `info.json` 不会被读取或改写。

## 4. MOD 包识别

`PackageInspector` 按顺序尝试，先命中先用：

| 顺序 | 条件 | 结论 |
|---:|---|---|
| 1 | 任意层级有 `moduleconfig.xml`（标准 `fomod/ModuleConfig.xml` 或根级） | `Fomod`：走 FOMOD 向导 |
| 2 | 任意层级含 `*.pak` | `PakFiles` |
| 3 | 任意层级出现已知内容根目录（`natives` / `reframework` / `nativePC` / `pl` / `wp` / `plugins` / `autorun` / `weapon` / `player` / `art` / `gamedesign` 等） | `LooseFiles` 散装 |
| 4 | 含 `*.lua` | `RefScripts` 按包内路径安装 |
| 5 | 含 `*.dll` | `RefPlugins` 按包内路径安装 |
| 6 | 都不命中 | `Unknown` → 拒绝安装并提示「无法识别的 MOD 格式」 |

补充规则：

- **不推断安装前缀**：包内的相对路径就是最终落点，安装时按原路径复制到游戏根目录；旧版本自动补
  `nativePC` / `natives` / `reframework` 前缀的逻辑已移除，避免把世界 `nativePC/plugins/...` 塞进
  `reframework/plugins/...`，也避免把根目录 DLL 塞进错误的子目录。
- 压缩包外壳剥离：解压后若「只有一个顶层目录且没有文件」，把该目录当作内容根——除非目录名本身就是
  `natives` / `reframework` / `nativePC` / `pl` / `wp` / `plugins` / `autorun` / `weapon` / `player` /
  `art` / `gamedesign` 或游戏名目录。
- 安装排除清单（普通安装与重装共用）：任意层级的 `modinfo.ini`、FOMOD 配置及 `fomod/` 目录、
  包内第一张预览图/根级 `screenshot.*`、`preview.*` 图片不进入 `files`（只用于元数据与预览）。
- 识别结果带 `Warnings`（如 REFramework 本体、FOMOD 配置问题），安装向导顶部 InfoBar 展示，不静默丢弃。

## 5. 安装

### 5.1 安装源与分类

| 拖入/选择的内容 | 处理 |
|---|---|
| 压缩包（按内容魔数识别 zip / 7z / rar / tar / gzip / bzip2 / xz / lzip / zstd） | 解压安装 |
| 文件夹 | 作为安装源复制后安装（不做「文件夹=模型编辑」语义） |
| 裸 `.pak` / `.lua` / `.dll` 单文件 | 直接作为单文件包安装 |
| 其它文件 | 提示「不支持的文件类型」并跳过；一次拖入多个时合法项逐个进入安装向导 |

- 解压细节：GBK(936) 文件名编码优先、失败回退 UTF-8；文件名含「解压码/密码」时自动猜密码并重试，
  仍失败给出中文提示（建议手动解压或改名重试）；包内嵌套压缩包递归解压（最多 3 层，内层解压到
  `<包名>files/` 后删除原包；内层失败保留原文件继续安装）；zip-slip / 绝对路径 / `..` 条目直接拒绝。
- 解压、识别在后台线程执行，状态栏显示百分比；GUI 保持响应（尚无可取消按钮，见 §14）。

### 5.2 暂存与原子提交

1. 解压到 `Temp/<guid>/`，剥外壳并识别；失败只删临时目录，不推进 ID、不写记录。
2. 安装向导确认后，把选中文件完整写入 `Mods/.staging/<guid>/files`。
3. 成功后整目录改名为 `Mods/<id>/files`，再写记录并保存索引；任一步失败删除暂存目录并恢复原状态。
4. 重装/更新使用同一暂存机制：旧 `files` 先改名为 `.old-<guid>`，新目录换入成功后才更新记录；
   失败恢复旧目录与旧记录，`Id` / `Index` / `GameId` 不变。

### 5.3 安装向导

- 字段：预览图（FOMOD `moduleImage` 优先，否则包内第一张图）、MOD 名称（必填、当前游戏内不能重名）、
  分类、版本、NexusId、作者、MOD 来源（网址）、描述；元数据默认值来自 `modinfo.ini`，其次 FOMOD 元数据，
  最后用包名兜底；踩蘑菇包名 `cmg_<帖子ID>_<标题>` 会拆出标题并补来源链接。
- 摘要：「识别为 <类型>，共 N 个文件」「安装到 <TargetDirectory>」「FOMOD 名/必装文件数」「重装模式」等。
- 普通包/手动模式：文件树勾选、可设多个根目录（根外文件不安装）、安装前缀（再套一层目录）、
  全选/全不选/已选计数（固定在滚动区外）；底部 InfoBar 显示错误。
- 取消安装需要二次确认（对话框内遮罩，不用嵌套 ContentDialog）。

### 5.4 FOMOD 完整语义

- 解析 `InstallSteps`（步骤 + `visible` 依赖 + `optionalFileGroups`）、`requiredInstallFiles`、
  `conditionalFileInstalls`、`moduleImage`。
- 分组类型：`SelectExactlyOne`（必选一个）、`SelectAtMostOne`（最多一个）、`SelectAtLeastOne`（至少一个）、
  `SelectAll`（全选锁定）、`SelectAny`（任意）。
- 插件类型：`Required`（必选锁定）、`Recommended`（默认选中）、`Optional`（可选）、
  `NotUsable` / `CouldBeUsable`（解析保留）。
- 依赖：`fileDependency`（Active/Inactive/Missing）、`flagDependency`（插件 `conditionFlags` 的 flag/value）、
  嵌套 `dependencies`；操作符 `And` / `Or`；`gameDependency` / `fommDependency` 保守视为满足。
- 默认值：`SelectAll` 分组全选；`Required` 与 `Recommended` 默认选中；`SelectExactlyOne` 无默认选中时选第一项。
  锁定项（Required / SelectAll）不可取消；`SelectExactlyOne` / `SelectAtMostOne` 分组自动互斥。
- 每次勾选后重算 flag 与步骤可见性；隐藏步骤的勾选不参与安装。提交前 `Validate` 校验可见步骤的分组规则
  （必选一个/至少一个/最多一个），不合法则拒绝提交并给出中文原因。
- `source` / `destination` 必须是安全相对路径（拒绝绝对路径、盘符、UNC、`..`）；越界映射记 warning 并跳过。
- 最终安装文件 = `requiredInstallFiles` + 可见步骤中选中插件的文件 + 满足依赖的 `conditionalFileInstalls` 文件，
  按 `priority` 稳定排序；落盘前再走一次路径安全与排除清单校验（纵深防御）。

### 5.5 变体 / 多根 / 手动模式

- 多组件包（如 Shop Tweaks）：当包内至少两个子目录能各识别为一套安装（各自带 `modinfo.ini`，或子目录结构可被
  识别为 MOD）时，向导出现「选择变体」单选，提交时去掉变体根目录前缀安装；FOMOD 包优先走 FOMOD。
- 有分组/变体时可在「按分组/向导安装」与「手动选择文件」之间切换。

### 5.6 去重、元数据与失败处理

- 包指纹 = 包内全部文件「相对路径 + 大小」的 SHA256；**只在当前游戏内**去重，同一包可在不同游戏各装一份。
- 安装完成后写 `Kind`（新安装 `TargetDirectory` 恒为 `null`，按包内路径安装）；对模型类 MOD 自动补分类「模型替换」与模型明细备注（§9）。
- `modinfo.ini` 在识别时读取（名称、分类、版本、作者、NexusId、HomeUrl、描述等）。

### 5.7 安装源归档（设置「安装选项」，§11）

| 取值 | 行为 |
|---:|---|
| 0 无操作（默认） | 只把原始安装源路径写入 `ModRecord.InstallSource` |
| 1 复制到HunterBox | 把压缩包复制到 `Download/<MOD显示名><原扩展名>`（重名追加 ` (2)`/` (3)`，先写 `.tmp` 再改名） |
| 2 移动到HunterBox | 同「复制」，成功后删除原文件；删除失败只给 warning，不中断安装 |

- 文件夹安装不做归档，只记录来源；安装源本身已在 `Download/` 内时不重复归档；归档失败只提示，不影响安装结果。

### 5.8 重装 / 用新包更新

- 两者都要求 MOD **未启用**，且都走**安装向导**（可重新选组/变体/文件/元数据）。
- 重装MOD：优先使用 `InstallSource` 记录的安装源；记录值失效时提示，再让用户选包。
- 用新包更新：总是让用户另选本地压缩包。
- 提交后保留 `Id` / `Index` / `GameId`；文件清单、包指纹、`InstalledAt`、元数据与安装源按本次向导填写/新包内容更新；
  失败恢复旧文件与旧记录。

## 6. 启用 / 禁用

启用时按 `ModRecord.TargetDirectory` 把 MOD 内相对路径映射为游戏根目录相对路径（新安装为 `null`，直接使用包内路径；旧记录由启动迁移修正），再按文件类型分两路。
「检测游戏状态」开启（默认）时，游戏正在运行会拒绝启用/禁用/清理/重新复制。

### 6.1 散装文件

- 复制到游戏根目录；目标已存在且不属于本HunterBox记录的占用时，先把游戏原文件备份到
  `Mods/.backup/<gameId>/<相对路径>`（先写 `.tmp` 再改名，避免半截备份），再覆盖。
- 禁用：有备份则还原备份并删除备份文件，无备份则删除HunterBox复制的文件；随后向上清理因此变空的目录，
  绝不动游戏根目录本身。
- 重叠文件：同一游戏路径被多个启用 MOD 提供时，按列表顺序靠后的生效；禁用其中一个时，如果仍有其它启用 MOD
  提供该文件，会从胜出 MOD 的源文件重新复制，备份保留到最后一个占用方禁用时才还原。
- 源文件缺失：启用时在改动任何文件之前整体失败并提示（防半套）；其它启用 MOD 的源缺失只跳过并 warning；
  备份文件缺失时降级为删除并 warning。
- 文件复制带 3 次重试；启用/禁用均记录撤销日志，中途失败逆序回滚，并把 `Enabled` 恢复为操作前状态。

### 6.2 pak 文件

- 只有 **根层** `.pak`（MOD 内相对路径不含 `/`）参与 pak 放置；嵌套子目录里的 pak 按散装文件处理。
- 荒野 / 崛起：pak 放入 `<游戏目录>/pak_mods/`，目录不存在则创建。
- 世界：不使用 `pak_mods`，pak 按普通散装文件复制到游戏根目录。
- 启用前先计算 pak 计划（冲突/源缺失在动散装文件之前抛出），再落散装文件，最后写 pak 与记录；
  任一步失败回滚已写的 pak 与散装文件。
- 启用不会清理计划外的 pak（可能来自旧本或用户手动放置；不替用户删文件）；禁用只撤出本 MOD 占用的 pak。

### 6.3 pak 命名策略

设置「自动修正PAK」开启（默认）时使用 **patch 编号（稳定化）**：

- 编号格式 `<前缀><NNN>.pak`（至少 3 位；解析接受 3–5 位，格式化上界 99999，保证编号往返闭环）。
- 可复用性：先扫描 `pak_mods/` 里已存在的编号文件，按内容指纹建立「内容 → 现有文件名」映射；
  内容相同的 pak 直接沿用现有编号，因此反复启用/禁用不会让编号漂移；新内容只占用**最小可用编号**
  （起点 = max(基础编号, 游戏根目录已有 patch 号) + 1，跳过已占用编号），而不是简单 max+1。
- 同一内容在一批里的多个 pak 源共用同一个编号；不同 MOD 内容相同时只占一个文件。
- 禁用后**不重排、不压紧**其它 MOD 的编号；只删除本 MOD 占用且未被剩余启用 MOD 计划保留的文件。
- 本 MOD 的 pak 源在禁用时缺失：保留原名模式按记录文件名定位删除；编号模式在「其余启用 MOD 源都完整」时
  按内容指纹扫描 `pak_mods` 中疑似残留（带编号、未被保留、内容不匹配任何剩余源）删除并提示；
  其余源也不完整时保守保留并提示。

设置关闭时使用 **保留原文件名**：

- 直接使用 MOD 内原文件名；同名且内容相同 → 共用；同名且内容不同 → 拒绝启用并弹窗列出冲突双方。
- 同名冲突的启用/禁用均由计划层判定，避免误删其它 MOD 的文件。

### 6.4 检查 PAK 内部文件（可选设置）

开启后，启用前读取本 MOD 与其它启用 MOD 的根层 pak 条目表（RE pak 只存路径哈希），发现内部文件重叠时
在启用结果中给出提示（**不阻止启用**，仅给出提示）；pak 读不了时记录 warning 并跳过该包的比对。

### 6.5 重新复制

对已启用 MOD 重跑一次复制流程（散装 + pak），不改变启用状态；用于游戏更新后恢复HunterBox文件。
按钮作用于当前游戏全部已启用 MOD，右键菜单作用于单个 MOD；未启用的 MOD 会被拒绝。

## 7. 清理（删除全部MOD文件）

UI 提供二选一，任何范围都**只处理当前游戏**：

| 选项 | 行为 |
|---|---|
| 仅删HunterBox安装的 | 安全禁用当前游戏全部已启用 MOD（有备份还原游戏原文件、删HunterBox文件、记录置为未启用）；不按记录二次扫描删除游戏文件 |
| 清理HunterBox记录残留 | 先做「仅删HunterBox安装的」，再按当前游戏 MOD 记录的相对路径处理游戏目录里仍存在、且没有原文件备份的**散装**残留文件；内容指纹与 MOD 源文件一致的文件复制到 `Mods/.cleanup-quarantine/<gameId>/<时间戳>/` 后再从游戏目录移除（不直接删除）。使用 `pak_mods` 的游戏会跳过记录里的根层 `*.pak`（它们不落在游戏根目录）。不整目录删除，不删除记录之外的用户文件，移除后只清理空父目录 |

- 不整删任何游戏目录，不删除 `Mods/.backup/` 备份，不删除用户自己的非HunterBox文件。
- 移动失败（文件被占用/无权限）保留现场、不中断整体清理。
- 无法证明归属（源文件缺失、内容不一致、指纹失败）的残留文件原地保留并 warning。
- 隔离区用于覆盖「MOD 源文件与游戏原文件字节相同」的情况：即使内容相等，也保留可恢复副本；清理完成提示会给出隔离目录路径，确认无用后可手动删除。

## 8. 其它操作

| 操作 | 行为 |
|---|---|
| 卸载 | 任何情况下都二次确认；已启用则先安全禁用，再把 `Mods/<id>` 改名为 `.old-<guid>` 墓碑，移除记录并保存成功后删除墓碑；保存失败时目录改名回原位、记录加回内存，可重试 |
| 重新复制 | 见 §6.5 |
| 上移 / 下移 | 单个选中项在所属游戏列表内移动一位，重写 `Index` |
| 快速移动 | 按钮弹对话框输入目标位置（1..N）；右键菜单提供「快速移动到顶部/中间/底部」 |
| 排序守卫 | 无选中、搜索/过滤模式、目标 MOD 已启用时拒绝排序 |
| 过滤 / 搜索 | 关键词匹配名称、描述（备注）、分类，实时过滤并计数；输入框非空即「搜索模式」：禁止添加/重排，状态栏提示退出方式；「搜索」按钮应用过滤，关键词为空时清空并退出搜索模式 |
| 多选 | 列表支持多选（Extended）；「启用选中MOD」按列表顺序批量启用，逐条失败汇总、不中断其它项；列表重建后按 ID 恢复选中 |
| 存档 | 存档管理器：备份当前存档（生成 SHA-256 哈希清单）、恢复（二次确认 + 备份源哈希校验，不一致拒绝还原；写入后不读回目标）、重命名、删除（二次确认）、打开目录、导入旧存档 zip（导入后生成清单） |
| 启动游戏 | 校验游戏目录与主程序存在后用 `steam://run/<AppId>` 启动，发起启动后最小化HunterBox窗口 |
| 刷新 | 重新加载当前游戏 MOD 列表并归一序号 |
| 切换游戏 | 写 `[settings] game`，刷新列表、路径显示与窗口标题；各游戏列表/备份/排序互相隔离 |

## 9. 模型改写（荒野 / 崛起 / 世界）

### 9.1 三作模型身份与路径

| 作 | 护甲 | 武器 | 其他 |
|---|---|---|---|
| 荒野 | `art/model/character/ch0{2,3}/<fid:3>/<sid:3>`，`fid_sid` | `art/model/item/it<TT>/<AA:2>/<BBBB:4>`，`AA_BBBB`（`it10/03` 归一猎虫） | 猫装备 `ch05/<fid:3>/<sid:4>`；兼容 `_prefab`（男/女、wpTT）、骨骼 |
| 崛起 | `natives/stm/player/mod/{f,m}/pl<id:3>`，`pl<id>` | `natives/stm/weapon/<类型目录>/<liteName><id:3>`，3 位 ID | 兼容 `player/prefab/mod`、`player/prefab/weapon`、骨骼/lua 骨骼 |
| 世界 | `nativepc/pl/{f_equip,m_equip}/pl<fid:3>_<sid:4>`，全 ID `fid*10000+sid` | `nativepc/wp/<类型目录>/<code>/mod|epv`，含 `bs_`/`op_` 叠层变体 | 世界 pak 不参与改写（与游戏加载方式一致，只改散装） |

- 模型行触发/去重按各作规则；行文本形如 `(文件数)类型|名字 (完整ID) [pfb] [pak] (chain)`。
- 名称优先取对应作内嵌名称表，查不到退化为「类型 + 完整 ID」。
- 改写用「源模型身份键模板 → 占位符 → 目标身份键」替换完整路径/文件名；贴图（`.tex.`）一律跳过；
  只改路径，**不改文件内容**。

### 9.2 编辑器（右键「MOD文件修改」）

- MOD 名字只读行；每模型一行（左名字、右目标按钮），候选只列同作同类同类型；
  「仅显示未使用的ID」默认勾选（过滤当前游戏全部 MOD 已使用的完整 ID），当前源模型强制可选；没有已记住目标时默认选中它。
- 目标用「按钮 + 弹出候选面板」选择：面板顶部搜索框按模型名称/完整 ID 模糊过滤
  （空格分隔的多个词须同时命中同一候选，半角/全角空格均可），列表单击即选中；有搜索词时回车选取第一个命中项，
  空查询回车只吞掉按键不改目标，Esc 关闭。
- MOD 已启用时只读打开；应用时提示先禁用。无改动/无目标时明确提示。
- 拖 MOD 文件夹到列表进入「文件夹模式」编辑器（无已用过滤），直接改文件夹内散装路径与根层 pak 路径。
- 无模型时按原因提示：未识别到模型、世界仅 pak、含无法解析的 pak 等。

### 9.3 pak 改写

- 明文 TOC（feature 0）与压缩/加密**条目**：只重写条目 8 字节路径哈希，数据区/偏移/压缩加密属性/checksum 不动；
  首次写回前生成 `<pak>.bak`。
- 整张 TOC 加密（feature 8/24）的 pak：解包（解密 TOC、解压/解密条目）→ 目录内改名 → 重新打包为
  明文 TOC 的普通 mod pak；失败不覆盖原文件，结果中记录原因。
- 条目不在对应作路径清单中 → 跳过并汇总 warning；世界不对 pak 做改写并说明。
- 改写后重算备注/分类；多 pak、pak+散装一起处理并汇总成功/跳过/不支持。

### 9.4 范围与有意移除

- 「修复选中的MOD（AVP 修复）」「贴图改名」为有意移除功能，不在本实现范围。
## 10. 界面结构（WinUI 3）

### 10.1 窗口

- 标题：`HunterBox v1.5.207 - <当前游戏名>`（切换游戏即时更新）；图标 `Assets/hunterbox.ico`（项目自制）。
- 初始尺寸 1240×820 DIP，最小 1080×720 DIP（按 DPI 换算为物理像素）；Mica 背景。
- 主题默认跟随系统深浅色；设置页可手动选择跟随系统/浅色/深色，保存后立即生效；系统标题栏颜色跟随内容主题。

### 10.2 布局

```
┌ 顶部：HunterBox | 游戏下拉 | 游戏路径文本 | 刷新 ─────────────────────────┐
├ 左：列头（☐/顺序/MOD名字/备注信息/分类/当前版本/NexusMods）          │ 右侧 172px 按钮列：
│    列表（多选、行高 ≥32、字号 13、支持拖拽安装）                       │ 游戏设置
│                                                                      │ ─ 过滤/位置：关键词框 + 计数 + 搜索
│                                                                      │ 快速移动 / 添加MOD / 重装MOD /
│                                                                      │ 用新包更新 / 卸载MOD
│                                                                      │ ─ 上移 / 下移
│                                                                      │ ─ 启用选中MOD / 禁用全部MOD /
│                                                                      │ 删除全部MOD文件 / 重新复制MOD
└ 底部：状态文字 … 存档 | 启动游戏(Steam) | BusyRing ─────────────────────────┘
```

- 列宽（DIP）：勾选 40 / 顺序 52 / 名称 * / 备注 220 / 分类 120 / 当前版本 110 / NexusMods 110。
- 勾选列为只读绑定 + 点击驱动（避免 TwoWay 先写回源导致启用/禁用不执行），失败会回滚勾选状态。
- 按钮可用性：无选中时依赖选中项的按钮灰态；忙碌（`_busy`）时列表与全部操作禁用；有 MOD 时才有
  「禁用全部MOD」「重新复制MOD」。
- 右键菜单只在 MOD 行上弹出，多选时收拢为右键行：MOD信息设置 / MOD文件修改(取消启用MOD才能修改) / —
  / 跳转到NexusMods查看 / 打开MOD来源网址 / 查看MOD的文件 / — / 快速移动到顶部/中间/底部 / —
  / 启用或禁用 / 重装 / 重新复制 / 卸载。

### 10.3 启动流程与提示

1. 加载 `index.json` 与 `hunterbox.ini`，自动补齐三作缺失的游戏/存档路径（Steam 探测）。
2. 按 `[settings] game` 选择游戏，加载列表、归一序号、补齐模型分类。
3. 启动提示依次为：旧数据导入询问（未导入过且检测到旧数据）→ 索引损坏警告（`LoadWarning`）→
   未归属旧记录归游戏引导（`归到当前游戏` / `暂不处理`）。

## 11. 设置

### 11.1 `hunterbox.ini` 键表

`[settings]` 段：

| 键 | 默认 | 消费者/说明 |
|---|---|---|
| `game` | `mhws` | 启动时选择的游戏 |
| `enable_fix_pak_num` | `true` | 开启 = patch 编号稳定化；关闭 = 保留 pak 原文件名 |
| `check_pak_files` | `false` | 启用前展开 pak 条目做内部文件重叠提示（§6.4） |
| `check_game_running` | `true` | 操作前检测游戏进程，运行中拦截启用/禁用/清理/重新复制 |
| `install_option` | `0` | 安装源归档：0 无操作 / 1 复制到 `Download` / 2 移动到 `Download` |
| `legacy_imported` | `false` | 旧数据导入流程已执行标记（仅内部维护，不显示在设置页） |

`[<gameId>]` 段（`mhws` / `mhr` / `mhw`）：`gamepath`、`savepath`，均为可选；缺省时自动探测。

- 兼容旧键名读取：`check_pak_inner` → `check_pak_files`、`install_optn` → `install_option`。
- 保存设置时清理已移除键：`file_list`、`check_pak_inner`、`install_optn`、`ignore_caimogu`、`enable_filter_mhmodel`。
- INI 写入：先写 `.tmp` 再 `File.Replace` 原子替换；读取失败降级为默认值不阻塞启动；保存失败抛 `IOException` 由界面提示。
- 旧配置迁移：启动或打开设置页时若同目录存在旧 `sjmodbox.ini`，由 `LegacyMigrationService.MigrateSettings` 只读解析并迁移
  `install_option` / `check_game_running` / `check_pak_files` / `enable_fix_pak_num`、
  `game`（AppId / `mhr` / `mhw` → 稳定游戏 ID）以及旧 `[<appid>]` 段的 `gamepath` / `savepath` 到 `[<gameId>]`；
  不覆盖新文件里已有的值、不修改旧文件、可重复执行（幂等）。

### 11.2 设置对话框

- 字段：当前游戏；游戏路径（必须包含主程序，可编辑 + 选择目录）；存档路径（可选，留空首次备份时再选）；
  安装选项 3 单选（无操作 / 把安装包复制到HunterBox文件夹 / 把安装包移动到HunterBox文件夹）；
  HunterBox功能设置：自动修正PAK、检查PAK内部文件、检测游戏状态（后两项带说明文字与 ToolTip）。
- 保存为「保存设置」主按钮：先校验路径（不存在/找不到 exe 拒绝保存，磁盘原值不变），校验通过才写 INI；
  修改「自动修正PAK」或关闭「检测游戏状态」时在对话框内显示风险说明并把按钮改为「仍然保存」，再点一次才落盘
  （规避 WinUI 不允许嵌套 ContentDialog 的限制）。
- 「忽略踩蘑菇」「过滤怪猎模型」「file.list」等旧项已按 D10 移除。

## 12. 旧数据迁移

- 启动时（`legacy_imported` 未置位）检测程序目录下的旧数据：
  `Mods_<appid>/<id>/{info.xml,files}`（旧崛起为 `./Mods`，仅当 `Mods_1446780` 不存在时）、
  `Backup_<appid>`、`Saves_<appid>/*.zip`（旧崛起为 `Saves/`）、`Download/`、`sjmodbox.ini`。
- 有数据时弹「检测到旧数据」，选择导入进入迁移对话框；**只读复制**，不移动/删除旧文件。
- MOD 导入：读取 `info.xml`（`name`/`moduleName` 回退名称、`modType`→分类、版本/作者/nexusId/HomeUrl/描述），
  复制 `files/` 到 `Mods/<新id>/files`，重新识别包类型并写入 `GameId`；**旧启用状态不沿用**，导入后统一为未启用。
- 原文件备份导入：`Backup_<appid>` 下的文件复制到 `Mods/.backup/<gameId>/`。
- 配置由 `LegacyMigrationService.MigrateSettings` 迁移（§11.1）；旧存档 zip 在「存档管理器 → 导入旧存档 zip」中导入为目录备份。
- 选择「暂不导入」不会置位 `legacy_imported`，下次启动仍会提示；只有实际执行导入后才记录。
- 旧记录若缺少 `GameId`（早期索引），启动时单独引导归到当前游戏，不会静默丢弃。

## 13. 日志、错误与恢复

- 文件日志：`logs/hunterbox-<yyyyMMdd>.log`，每行 `yyyy-MM-dd HH:mm:ss.fff [INF|WRN|ERR] 内容`；
  程序目录不可写时自动降级 `%LOCALAPPDATA%/HunterBox/logs`（初始化时探测可写性）。
- 崩溃报告：`logs/crash-<yyyyMMdd-HHmmss>.txt`（同秒冲突追加 `-1`/`-2`，不覆盖），含版本/系统/路径/启动参数/异常与内部异常链。
- 全局异常：XAML `UnhandledException` 写报告并提示后继续（`Handled=true`）；`AppDomain.UnhandledException` 写报告 +
  阻塞式系统消息框后退出；`TaskScheduler.UnobservedTaskException` 写报告并标记已观察。
- 启动构造期失败：写崩溃报告 + 系统消息框说明后退出，不静默消失。
- 索引损坏：保留 `.bad-<时间戳>` + `LoadWarning`，界面提示并从 `.bak` 恢复；INI 损坏：降级默认值；解压/存储失败写日志并上抛中文提示。
- 操作失败时状态栏显示「操作失败：…」，不会因随后的列表刷新被覆盖；后台操作统一 BusyRing + 忙碌门禁。

## 14. 已知限制 / 有意精简

- 长任务（安装/重装、批量启用/禁用、重新复制、清理）显示状态文字进度与「取消」按钮，按安全点协作取消；无独立进度条窗口。
- 模型名称表/路径清单内嵌，暂不支持外部表覆盖。
- 明文 TOC pak 只改 TOC 路径哈希、不增删条目；TOC 加密 pak 走解包→改名→重打包回退；真实游戏加载验证未完成。
- 荒野名称表 / pak 路径清单内嵌且不可外置覆盖；路径清单未命中时只能跳过并提示。
- 世界 pak 按散装文件处理，不落 `pak_mods`。
- 备注列显示运行时模型信息（模型名/完整 ID/`[pfb]`/`[pak]`），无模型时回退用户描述。
- 主题选项位于设置页「外观主题」，保存后立即生效。
- 旧数据导入的提示记忆只覆盖「导入已执行」：选择「暂不导入」会每次启动重复询问（已知观察）。
- 有意不做：13 作支持、联网更新检查、外链/Logo/关于赞助、MO2 overwrite、AVP 修复、贴图改名、
  把 pak 落点改回游戏根目录、还原旧 MetroSet 视觉。
