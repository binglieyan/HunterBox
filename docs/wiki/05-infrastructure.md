# 05 Infrastructure 层（`HunterBox.Infrastructure`）

## 1. 职责与目录

Infrastructure 只做「实现端口 + 适配外设」，不新增业务判断。项目引用 Domain 与 Application
（后者仅用于实现 `ILegacySaveImporter`），第三方包只有 SharpCompress。

| 命名空间 | 目录 | 实现内容 |
|---|---|---|
| `Storage` | `Storage/` | `ModRepository`：`Mods/index.json` 持久化 |
| `Archives` | `Archives/` | `ArchiveExtractor`：SharpCompress 解压 |
| `Settings` | `Settings/` | `IniSettings`：`hunterbox.ini` |
| `Paks` | `Paks/` | RE PAK 解析/编解码/加密/校验和/改名/打包/解包/前像/路径清单 |
| `Mods` | `Mods/` | 内嵌名称表、模型候选目录、路径→名字解析 |
| `Games` | `Games/` | Steam VDF/库目录/安装目录/存档目录定位 |
| `Logging` | `Logging/` | 文件日志与崩溃报告 |
| `Migration` | `Migration/` | 旧HunterBox数据检测与只读复制导入 |
| `Saves` | `Saves/` | 旧存档 zip 导入 |
| `Startup` | `Startup/` | 单实例守卫 |
| `IO` | `IO/` | `SystemFileSystem`（`IFileSystem` 实现）与 `CorruptFileQuarantine`（损坏文件隔离） |
| `Models` | `Models/` | `JsonModelTextCacheStore`：模型文本缓存基础文件 + 增量日志 |

## 2. `Storage`：ModRepository

文件：[ModRepository.cs](../../src/HunterBox.Infrastructure/Storage/ModRepository.cs)，实现 `IModStore`。

### 2.1 文件结构

`Mods/index.json`（PascalCase，`ModKind` 按数字）：

```json
{
  "SchemaVersion": 1,
  "NextId": 3,
  "Mods": [
    {
      "Id": 1,
      "GameId": "mhws",
      "Index": 1,
      "Enabled": false,
      "Metadata": {
        "Name": "示例MOD",
        "Category": "模型替换",
        "Version": "1.0",
        "Author": "someone",
        "NexusId": 12345,
        "HomeUrl": "https://...",
        "Description": "..."
      },
      "Kind": 6,
      "TargetDirectory": null,
      "RelativeFiles": ["natives/stm/..."],
      "PackageFingerprint": "SHA256...",
      "InstallSource": "D:\\mods\\example.zip",
      "InstalledAt": "2026-11-16T12:00:00+00:00"
    }
  ]
}
```

`ModKind` 数字顺序：`Unknown=0`、`LooseFiles=1`、`RefScripts=2`、`RefPlugins=3`、`PakFiles=4`、
`RefFrameworkItself=5`、`Fomod=6`。`CurrentSchemaVersion = 1`。

### 2.2 加载

```csharp
public static ModRepository Load(BoxPaths paths);
```

- 文件不存在 → 返回空仓库（`NextId` 从 1 开始）。
- JSON 解析失败/结构不对 → **不抛异常**：把原文件改名为 `index.json.bad-<yyyyMMddHHmmss>` 保留，
  返回空仓库并设置 `LoadWarning`；改名失败也不阻断启动，只给 warning。
- 读取 IO 异常（无权限/被占用等非损坏）→ 直接向上抛 `IOException`，绝不静默当空仓库
  （否则下一次保存会覆盖读不出来的数据）。
- 兼容早期「只有数组」的索引格式；缺失新字段按 `null` 处理。

### 2.3 保存

```csharp
public void Save();
```

1. 在 `_gate` 内取「记录列表 + NextId」一致快照；
2. 序列化与落盘在锁外完成，并发保存由 `_saveGate` 串行化；
3. 写入同目录 GUID 临时文件 → `FileStream.Flush(true)` 刷盘；
4. 替换前把上一版留成 `index.json.bak`，再用 `File.Replace` 原子替换；
5. 损坏索引无法先备份时拒绝覆盖并抛 `IOException`。

### 2.4 排序号与游戏归属

| 成员 | 行为 |
|---|---|
| `NextId()` | 只读高水位，单调不减；与 `Add` 不是原子对 |
| `NextIndex()` | 全局最大 `Index + 1` |
| `NextIndexFor(gameId)` | 指定游戏内最大 `Index + 1`（该游戏无记录时为 1） |
| `Add(record)` | `Index<=0` 时分配（所属游戏末尾；未归属用全局序号）；ID 重复抛 `InvalidOperationException` |
| `Remove(id)` | 移除记录，ID 不复用；此前引用变孤儿 |
| `AssignGame(id, gameId)` | 用 `WithGame` 整体复制并替换，保留安装源等字段；已归属则抛异常 |
| `ReorderForGame(gameId, idsInOrder)` | 只重排指定游戏，`Index` 改为 1..N，整次在同一把锁内完成 |

### 2.5 线程模型

- 全部公开成员在 `_gate` 内读写 `_mods`/`_nextId`；`Save` 的序列化与 fsync 在锁外。
- 列表成员返回新快照，但元素是仓储内活 `ModRecord`；`ModRecord` 字段不受锁保护。
- 少量成员在锁内重入其它公开成员（`Add` → `NextIndex` 等），依赖 `lock` 可重入；
  不要把 `_gate` 换成 `SemaphoreSlim` 等不可重入原语。
- 不负责多实例/跨进程并发写同一索引（App 有单实例保护，且只在启动时加载一次）。

## 3. `Archives`：ArchiveExtractor

文件：[ArchiveExtractor.cs](../../src/HunterBox.Infrastructure/Archives/ArchiveExtractor.cs)。

```csharp
public sealed class ArchiveExtractor : IArchiveExtractor
{
    public IReadOnlyList<string> Extract(
        string archivePath,
        string destinationDirectory,
        ArchiveExtractionOptions? options = null);
}
```

能力：

- 按魔数探测 zip / 7z / rar / tar / gz / bz2 / xz / lzip / zstd（`ArchiveFileProbe` 读文件头 +
  Domain `ArchiveFormatDetector.Detect`）；
- GBK(936) 文件名编码兜底；
- 密码：`options.Password` 优先，否则按文件名「解压码/密码」猜；
- 递归解压嵌套压缩包，默认最大深度 3（防压缩炸弹）；
- 进度回调按「百分比变化或距上次 ≥100ms」节流，结束后补一次完成事件；
- 每个条目经 `BoxPaths.NormalizeRelative` + `ResolveInside`，越界直接抛 `InvalidDataException`；
  条目大小写冲突、重复条目也拒绝；
- 返回解压出的文件相对路径列表。

异常契约：路径空 → `ArgumentException`；包不存在 → `FileNotFoundException`；
损坏/密码错误/条目越界/大小写冲突 → `InvalidDataException`；格式不支持 → `NotSupportedException`；
取消 → `OperationCanceledException`；写入失败 → `IOException`/`UnauthorizedAccessException`。

## 4. `Settings`：IniSettings

文件：[IniSettings.cs](../../src/HunterBox.Infrastructure/Settings/IniSettings.cs)，实现 `ISettings`。

### 4.1 文件布局

```ini
[settings]
game=mhws
theme=system
install_option=0
enable_fix_pak_num=true
check_pak_files=false
check_game_running=true
legacy_imported=true

[mhws]
gamepath=D:\SteamLibrary\steamapps\common\MonsterHunterWilds
savepath=D:\Steam\userdata\123\2246340\remote
```

当前使用的键：

| 段 | 键 | 含义 / 默认 |
|---|---|---|
| `[settings]` | `game` | 上次选择的游戏 ID（默认 `mhws`） |
| `[settings]` | `theme` | `system` / `light` / `dark`（默认 `system`） |
| `[settings]` | `install_option` | 0 无操作 / 1 复制 / 2 移动（默认 0） |
| `[settings]` | `enable_fix_pak_num` | 自动修正PAK（默认 `true`） |
| `[settings]` | `check_pak_files` | 检查PAK内部文件（默认 `false`） |
| `[settings]` | `check_game_running` | 检测游戏运行（默认 `true`） |
| `[settings]` | `legacy_imported` | 旧数据迁移提示是否已处理 |
| `[<gameId>]` | `gamepath` | 游戏根目录 |
| `[<gameId>]` | `savepath` | 游戏存档目录 |

旧键读取兼容：`check_pak_inner` → `check_pak_files`、`install_optn` → `install_option`；
保存时清理旧键。已移除的死设置：`file_list`、`ignore_caimogu`、`enable_filter_mhmodel`。

### 4.2 迁移与降级

- 旧 `sjmodbox.ini` 的迁移归 `LegacyMigrationService.MigrateSettings`（A-13），`IniSettings` 只做读写：
  迁移安装选项/检测游戏运行/检查PAK内部文件/自动修正PAK/上次游戏/各游戏 `gamepath`、`savepath`，
  旧文件的 AppId 段映射到稳定游戏 ID（2246340→`mhws` 等）；启动与设置页打开时触发，
  不覆盖新文件已有值、可重复执行（幂等）；旧文件只读、不修改。
- 保存采用「先写 `.tmp` 再原子替换」；失败抛 `IOException`，不会留下半截 INI。
- 读取失败（占用/无权限）→ `IsReadOnly=true`、`LoadWarning` 提示；`Set/SetBool/Remove/UpdateBatch`
  只改内存、不写盘、不抛异常，避免大量无 try/catch 的调用点崩溃。
- `UpdateBatch` 合并多次修改，结束后只落盘一次；中途异常也会尝试落盘已有修改。

## 5. `Paks`：pak 实现栈

完整格式与算法见 [07 PAK 子系统](07-pak-subsystem.md)，这里只列组件职责：

| 组件 | 职责 |
|---|---|
| `PakPlainToc` | 明文包头/TOC 定位读取/条目布局解码的公共层 |
| `PakEntryLayout` | v2(24B)/v4(48B) 条目字段偏移与读写；哈希字段位置按版本区分 |
| `RePakReader` | 只读解析（不校验、不解密）；TOC 加密时返回密文原样结果 |
| `PakArchiveCodec` | 解密 TOC、校验条目范围与属性 |
| `RePakEntryReader` | 按条目信息读内容（测试校验工具；未加密；DEFLATE） |
| `RePakWriter` | 等长内容替换（测试校验工具；v4 重算 checksum；首次 `.bak`；TOC 加密拒绝） |
| `RePakRenamer` | 只重写 TOC 的 8 字节路径哈希；批量一次写；`.toc.pending`/`.toc.bak`；实现 `IBatchPakEntryRenamer`、`IPakTocRecovery` |
| `PakTocPreImage` | 前像文件格式、写入/提交/恢复、指纹校验、失败语义 |
| `PakPacker` / `PakUnpacker` / `PakRepacker` | 流式打包、事务化解包、整包 Capture/Inspect |
| `PakCipher` | TOC 密钥流混淆与资源加密（RSA 还原密钥，公开可逆） |
| `PakChecksum` / `PakChecksumAccumulator` | XXH64 + XXH32 条目校验和；流式累加 |
| `PakEntryCodec` | DEFLATE/ZSTD 压缩/解压；输出上限与 bounded stream |
| `Murmur3` / `PakPathHasher` / `PakNameIndex` | 路径哈希（UTF-16LE + Murmur3，seed 0xFFFFFFFF）与索引 |
| `PakPathSafety` | 条目路径落盘安全校验：委托 `BoxPaths.NormalizeRelative`，失败包装为 `InvalidDataException` |
| `PakPathLock` | 同一 pak 路径的进程内引用计数互斥锁 |
| `FileContentFingerprint` | 文件内容 MD5，用于 pak 去重 |
| `PakEntryPathReader` | `IPakEntryPathReader` 实现：条目哈希 → 路径；TOC 加密时先解密；>100MB 整包不逐条扫描 |
| `EmbeddedPakNameIndex` | 内置荒野/崛起模型路径清单（gzip 资源） |

## 6. `Mods`：名称表与模型目录

| 类型 | 说明 |
|---|---|
| `EmbeddedNameTables` | 加载内嵌 `mhws_*`/`mhr_*`/`mhw_*` CSV.gz；`FindName(rows, id)`、`ParseTable(content, game, kind)` |
| `ModelCatalog` | 用名称表构建各作候选：`ArmorTargets`、`WeaponTargets(type)`、`CatTargets(sid)` |
| `ModelNameResolver` | 把模型路径映射成名称表键并查名字；`Resolve(path)`、`Resolve(identity)` |

名称表 ID 形态：

| 游戏 | 护甲 | 武器 | 猫装备 |
|---|---|---|---|
| 荒野 | `003_000`（`ch0X/FFF/SSS`） | `00_0000`（`itTT/AA/BBBB`） | `003_0000` |
| 崛起 | `pl001`（`player/mod/{f,m}/pl{id}`） | 类型 + 3 位序号（`G_Swd001` 等） | — |
| 世界 | `pl001_0000`（`nativepc/pl/{f,m}_equip/pl{fid}_{sid}`） | `bs_bow001` 等（`nativepc/wp/...`） | — |

### 6.1 数据来源与更新办法（游戏大版本后怎么补表）

名称表与路径清单都不是人工整理的，而是从游戏数据文件解析出来的。原始实现见
[MH-IDEditor](https://gitee.com/kenan223/mh-ideditor) 的 `DataParseTool`；本仓库的表与其产物同源
（表头注释 `#weapon id`/`#armor id`/`#cat armor id`、机械武器手写行、猫装备 `(身)/(头)/(未知)` 后缀、
护甲固定行都逐字一致，且比其当前 master 多出 `10_xxxx`「原始」机械武器行，说明取自更新一版的产物）。

名称表链路：

```text
游戏数据 .user.3（数据表，SQLite 格式）+ .msg（文本表）
  → dtlnor/MHWs-in-json 转成 JSON（社区项目，随游戏版本更新）
  → DataParseTool 按 GUID 关联「名字 + ID」，生成 mhws_{armor,cat_armor,weapon}.csv
  → 本仓库 gzip 成 Mods/NameTables/*.csv.gz
```

路径清单链路：

```text
dtlnor/MonsterHunterWildsModding 的 files/MHWs.list
  → 只保留模型路径（ch00/ch02/ch03/ch05、it00–it14），补 .chain2.13/.chain2.14 成对条目
  → 压缩成 Paks/FileLists/mhws.models.list.gz
```

荒野各表的字段来源（名字取 `content[13]`，与数据表靠 `guid` 关联）：

| 表 | 名字（`GameDesign/Text/Excel_Equip/` 下的 msg 文本表） | ID（`GameDesign/` 下的数据表） |
|---|---|---|
| 护甲 | `ArmorSeries.msg.23.json` | `Common/Equip/ArmorSeriesData`：`_ModId` → fid，`_ModSubMaleId`/`_ModSubFemaleId` → sid |
| 猫装备 | `OtomoEquipSeries.msg.23.json` | `Otomo/DataParam/OtomoEquipSeriesData`：`_ModelId` → `###_0000/1000/2000` |
| 武器 | `<武器类型>.msg.23.json` | `Common/Weapon/<武器类型>.user.3`：`_ModelId` → `AA_BBBB`（÷10000、%10000）；猎虫用 `RodInsectData` |

生成约定（改表时必须遵守，否则与代码规则对不上）：同一 ID 的多个变体用 `/` 合并、去掉 Ⅰ–Ⅴ 罗马数字；
男女款共用一个 fid、用 `(男款)`/`(女款)` 后缀区分（`ModelCatalog.InferArmorSex` 据此推断性别）；
机械（Artian）武器 14 行在生成器里手写；护甲表固定插入 `000_900,骨骼` 与 `002_0xx/002_1xx/002_2xx,内衣`。

更新步骤：

1. 等上游 `MHWs-in-json` / `MHWs.list` 更新后重新生成 CSV；
2. 与现有表 diff，只追加新行——路径清单少一条，对应 pak 条目只能被跳过并记 warning；
3. gzip 替换资源后 `dotnet build` + `dotnet test`；只换数据不增删用例，文档里的用例计数无需改；
4. 若游戏改了 ID 编码（如 sid 位宽），属于 `ModelPathRules`/`ModelIdentity` 的代码改动，必须补回归测试。

来源与许可：表内容源自 Capcom 游戏数据，生成链路参考 AGPL-3.0 的 MH-IDEditor，本仓库只保留生成后的 CSV、
不包含其代码。崛起/世界的 CSV 与 `mhrisePC.list` 在该项目中只有成品、没有解析代码，来源仍待补。

## 7. `Games`：Steam 定位

| 类型 | 说明 |
|---|---|
| `SteamVdfParser` | KeyValues 文本解析：带引号/不带引号的键值、`{}` 嵌套、`//` 注释、`\\`/`\"` 转义；失败返回 `null` |
| `SteamLibraryIndex.ParseEntries(vdf)` | 解析 `libraryfolders.vdf`，支持新版（`path` + `apps` 段）与旧版（数字键直接是路径）；按路径去重并合并 AppIds |
| `SteamConfigIndex.IsAppInstalled(configVdf, appId)` | 从 `config.vdf` 判断 AppId 是否记录为已安装；内容损坏返回 `null`（未知） |
| `SteamGameLocator.Locate(request)` | 完整定位链：HKCU Apps Installed → Steam 标准目录 → `libraryfolders.vdf` 各库（含 apps 映射）→ `config.vdf` 兜底 → HKLM Uninstall 兜底；每一步都校验主程序存在 |
| `SteamGameLocator.ReadInstallDir(steamApps, appId)` | 读 `appmanifest_<appid>.acf` 的 `installdir` |
| `SteamSavePathLocator.FindCandidates(steamRoot, appId)` | 枚举 `<Steam>/userdata/<账号>/<appid>/remote`（没有 `remote` 时退回 appid 目录）候选；多账号歧义由 Application `SavePathSelection.SelectSingle` 决定 |

注册表读取在 Infrastructure：`SteamGameProbe` 读取 HKCU/HKLM 后填 `SteamLocateRequest`，
探测缓存按实例持有；Application 的 `GameDiscoveryService` 只决定「设置缺省才写回」与单款失败隔离。
`SteamLocateRequest` 字段：`AppId`、`ExecutableName`、`DefaultFolderName`、`SteamRoot`、
`AppInstalled`、`UninstallLocation`、`LibraryFoldersVdf`、`ConfigVdf`（后两者可用于测试注入）。

## 8. `Logging`

### AppLog

文件：[AppLog.cs](../../src/HunterBox.Infrastructure/Logging/AppLog.cs)。

- 目录选择：显式目录（只建目录）→ 程序目录 `logs/` → `%LOCALAPPDATA%/HunterBox/logs`；
  逐个实际建目录 + 写探测文件验证可写。
- 按日文件 `hunterbox-yyyyMMdd.log`；常驻 `StreamWriter` 自动 flush；单文件超过
  `MaxLogFileBytes = 8MB` 滚动为 `hunterbox-yyyyMMdd.N.log`。
- 行格式：`2026-11-15 01:50:00.123 [INF] 内容`；级别 INF/WRN/ERR。
- 写日志失败不抛异常，原因见 `LastError`；进程退出时 `AppDomain.ProcessExit` 兜底 flush。

### CrashReporter

```csharp
public sealed class CrashReporter
{
    public CrashReporter(string? logDirectory = null, string? fallbackDirectory = null);
    public string? WriteReport(string source, Exception? exception, CrashContext context);
}
```

写 `logs/crash-<yyyyMMdd-HHmmss>.txt`，含版本、系统、程序/当前目录、启动参数、异常与内部异常堆栈；
首选目录失败降级 `%LOCALAPPDATA%`；都失败返回 `null` 并记录 `LastError`（不抛异常）；
同一秒多次崩溃追加 `-1`、`-2`，绝不覆盖。

## 9. `Migration`：LegacyMigrationService

文件：[LegacyMigrationService.cs](../../src/HunterBox.Infrastructure/Migration/LegacyMigrationService.cs)。

```csharp
public sealed class LegacyMigrationService
{
    public LegacyMigrationService(BoxPaths paths, IModStore store);
    public LegacyInventory Detect();
    public LegacyImportResult Import(LegacyInventory inventory,
        CancellationToken cancellationToken = default,
        IProgress<(int Done, int Total)>? progress = null);
}
```

- 检测的旧布局：`Mods_<appid>/<id>/{info.xml,files}`（崛起为 `Mods/`）、`sjmodbox.ini`、
  `Backup_<appid>`、`Saves_<appid>`、`Download/`。
- 只读扫描 + 只读复制导入，**不移动、不删除**旧文件。
- 旧启用状态不沿用（新HunterBox要求文件实际落盘才为启用），导入后统一未启用。
- 去重记录：`Mods/.legacy-import.json`（`LegacyMigrationService.ImportMarkerPath`），
  同一旧目录再次导入会被跳过；正常完成、取消或中途异常退出时已完成项都会尽力保存。
- 结果：`LegacyImportResult.ImportedMods/SkippedMods/ImportedBackupFiles/Warnings`；
  `SkippedDuplicates`（internal，含在 `SkippedMods` 内）表示因已导入过而跳过的数量；
  `ImportedSettings` 恒为 0（配置由 `LegacyMigrationService.MigrateSettings` 在启动/设置页迁移）。

## 10. `Saves`：SharpCompressSaveImporter

```csharp
public sealed class SharpCompressSaveImporter : ILegacySaveImporter
{
    public SharpCompressSaveImporter(BoxPaths paths, IFileSystem fileSystem, SaveBackupService saves, IAppLogger? logger = null);
    public static string LegacySaveDirectory(BoxPaths paths, GameProfile game);
    public IReadOnlyList<LegacySaveArchive> List(GameProfile game);
    public SaveBackup Import(GameProfile game, LegacySaveArchive archive);
    public static string? PasswordFromFileName(string? fileName);
}
```

- 旧目录：`Saves_<appid>`（崛起为 `Saves`）；只读 zip，不删除/修改源文件。
- 条目经 `NormalizeRelative` + `ResolveInside`，zip-slip 越界拒绝。
- 密码从文件名「解压码/密码」解析；失败给明确提示。
- 解压到 `SaveBackupService.CreateSlot` 新建的槽；`ExtractZipWithHashes` 在分块写入时同步计算 SHA-256，
  再由 `FinalizeImportedBackup` 一次性写出哈希清单（不再二次读盘）。

## 11. `Startup`：SingleInstanceGuard

文件：[SingleInstanceGuard.cs](../../src/HunterBox.Infrastructure/Startup/SingleInstanceGuard.cs)。

- 命名互斥体 `Local\HunterBox-<sessionId>-<userSid>`（`BuildName` 纯逻辑可单测）；
- 用 `WaitOne(0)` 判断是否第一个实例，并在进程存活期间持有所有权；
  只依赖 `createdNew` 会在「A 运行 → B 提示 → A 退出 → C 启动」时误判；
- 上个实例异常退出时 `AbandonedMutexException` 也视为第一个实例，不会「崩溃一次后再也打不开」；
- `Dispose` 释放互斥体。

## 12. 内嵌资源

`HunterBox.Infrastructure.csproj` 的 `EmbeddedResource`：

```text
Paks/FileLists/mhws.models.list.gz     荒野模型路径清单
Paks/FileLists/mhr.models.list.gz      崛起模型路径清单
Mods/NameTables/mhws_weapon.csv.gz     荒野武器名称表
Mods/NameTables/mhws_armor.csv.gz      荒野护甲名称表
Mods/NameTables/mhws_cat_armor.csv.gz  荒野猫装备名称表
Mods/NameTables/mhr_weapon.csv.gz      崛起武器名称表
Mods/NameTables/mhr_armor.csv.gz       崛起护甲名称表
Mods/NameTables/mhw_weapon.csv.gz      世界武器名称表
Mods/NameTables/mhw_armor.csv.gz       世界护甲名称表
```

资源缺失时各加载器返回空表/空索引，不崩溃；当前不支持外部 `file.list` 覆盖（设置项已移除）。
数据来源与游戏大版本后的更新办法见 §6.1。

## 13. `IO`：SystemFileSystem 与损坏文件隔离

文件：[SystemFileSystem.cs](../../src/HunterBox.Infrastructure/IO/SystemFileSystem.cs)、
[CorruptFileQuarantine.cs](../../src/HunterBox.Infrastructure/IO/CorruptFileQuarantine.cs)。

- `SystemFileSystem` 实现 Application 的 `IFileSystem` 端口，是删除/清理的唯一实现；
- `OpenRead`（`FileMode.Open` + `FileShare.Read`）与 `OpenWrite`（`FileMode.Create` + `FileShare.None`）
  供存档备份/还原与包指纹等流式复制使用；
- `TryDeleteFile`/`TryDeleteDirectory` 尽力删除，失败（占用/权限/安全策略）返回 `false`；
- `TryDeleteFileForce`/`TryDeleteDirectoryForce` 先清只读属性并支持取消；
- 链接（含断链）只删链接本身；目录子树含 junction/symlink 或扫描失败时整体跳过（fail-closed）；
- `GetDirectoryExistenceAndLastWriteTimeUtc` 用一次 `GetFileAttributesEx` 返回存在性与写入时间，
  缺失与无权限分离，超过 260 字符的路径补 `\\?\` 前缀；
- `CorruptFileQuarantine` 统一生成不覆盖已有备份的 `.bad-时间戳[-序号]` 名称，改名与删除都走
  `IFileSystem`；按文件名时间戳只保留最近 3 份，刚隔离的文件一定保留；
- 无状态，`Instance` 供组合根与 Infrastructure 内部复用。

## 14. 失败与降级矩阵

| 场景 | 行为 |
|---|---|
| 索引损坏 | 保留 `.bad-*`，返回空仓库 + `LoadWarning`；保存前尽量再备份，失败则拒绝覆盖 |
| 索引读取 IO 错误 | 向上抛 `IOException`，不静默当空仓库 |
| INI 读取失败 | `IsReadOnly=true` + `LoadWarning`，写入只改内存 |
| INI 保存失败 | 抛 `IOException`，磁盘原值不变 |
| 压缩包损坏/密码错误 | `InvalidDataException`（含中文原因） |
| 压缩格式未知 | `NotSupportedException` |
| pak 结构损坏 | `InvalidDataException` |
| pak TOC 加密且不支持改写 | `NotSupportedException`，由上层记录为 UnsupportedPaks |
| pak 被占用 | 可读 `IOException`（提示关闭占用程序） |
| 日志目录不可写 | 降级 `%LOCALAPPDATA%`；再失败则静默忽略 |
| 崩溃报告写入失败 | 返回 `null` + `LastError`，不抛异常 |
| 旧数据导入失败 | 已导入项保留，warning 汇总；去重记录避免重复 |

## 15. `Models`：模型文本缓存

文件：[JsonModelTextCacheStore.cs](../../src/HunterBox.Infrastructure/Models/JsonModelTextCacheStore.cs)。

- 基础文件 `model-text-cache.json` 带 `Version=3` 与随机 `Generation`；装载后再次保存只向
  `model-text-cache.json.journal` 追加变化行（JSON Lines，每行最多 100 条变化），单 MOD 变化写盘量为 O(该条)；
- 本次保存的变更总量估算字节达到压缩阈值 max(1MB, 基础文件/4)，或「已有日志 + 本批」会跨过阈值时，
  直接整份写，避免同一批数据先写日志再立即压缩；日志达到同一阈值时压缩回基础文件并删除日志；
  小体积的大量删除继续写日志，不为约 200KB 的变化重写 2–21MB；
- 条目字段统一经 `NormalizeEntry`：文本按 2048 字符/4096 字节截断；签名/内容戳/游戏 ID 在
  **字符数超过 512 或 UTF-8 字节数超过 1024**（任一条件，例如 400 个中文字符虽不足 512 字符但超过 1024 字节）
  时替换为确定性的 `hash:<SHA-256>`。哈希只用于把损坏字段收敛到有界长度，合法短值不会触发；
  哈希后与原值不再相等，会按缓存未命中重算；
- 日志按 100 条/256KB 分块，按 UTF-8 最坏 6 字节/字符估算，单行有确定上界；
- 日志行携带写入时的基础世代号；基础文件被外部/压缩重写后，旧世代日志不会回放；
- 日志损坏或末尾半截时保留已回放部分：先写基础文件，成功后才隔离日志；写基础失败则把合法前缀重写成干净日志；
  两级都失败时保留原日志等待下次启动重放，删除项不会复活；
- 基础文件整份写成功后清理旧日志分三级：删除 → 改名隔离 → 截断为 0；三者都失败时记入清理待办，
  按指数退避（30 秒起、上限 5 分钟）重试，每次进入待办只告警一次。追加失败只是本次保存的瞬时结果，
  下一次保存仍会重新尝试追加；锁释放后立即恢复增量写；
  待办期间追加成功的当前世代行只存在于日志，会保留到下一次整份写把快照下沉后才清理；
- 除本次会话第一次整份写外，只有真实整份写按「至少 1 秒且不超过 8MB/s」节流
  （按本次预计写入字节与基础文件的较大值折算），单次 Save 累计等待默认封顶 10 秒、可注入
  （与 App 的启动保存/单次缓存空闲等待同值；关窗总预算 60 秒，不会被单次保存耗尽）；
  首次整份写免等待；仅追加日志不等待，零变化但日志达到压缩阈值仍会整份写并等待。
  等待在存储锁外进行，达到预算后强制写入；
  跳过剩余节流（`allowEarly`）有两类场景：启动装载修复损坏日志，以及预算耗尽后的强制写入
  （后者从直接写、追加失败回退与压缩三个调用点传入）；
- 混世代/混版本日志只跳过对应行继续回放，不会用旧行阻断当前世代变化；
- 压缩失败按指数退避（30 秒起、上限 5 分钟），期间继续把变化写入日志而不是反复整份重写；
  压缩前探测基础文件是否被独占，基础文件存在时用 `File.Replace` 允许共享读句柄，日志追加受 32MB 硬上限约束；
- 日志接近 32MB 时按剩余容量限量序列化；容量不足以完整容纳本批时**整批不写入**并回退整份写，
  `Save` 不会用截断日志假成功；
  容量不足告警按存储实例（一次会话）限频一次；
  基础文件换入失败（探测后目标被外部创建/删除）会重新探测并重试一次，第二次仍失败才返回失败；
- 基础文件超 128MB/无法解析时隔离为 `.bad-*` 并重新生成，最多保留 3 份。
