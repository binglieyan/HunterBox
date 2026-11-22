# 03 Domain 层（`HunterBox.Domain`）

## 1. 职责与约束

Domain 是纯规则层：**无项目引用、无第三方包、无文件 IO**。所有决策都是「输入状态 → 输出计划」，
因此可以用假实现做无磁盘单测；文件头探测也拆成「Domain 判字节 + Application 读文件」（A-03）。

目录与命名空间：

| 命名空间 | 目录 | 职责 |
|---|---|---|
| `HunterBox.Domain.Layout` | `Layout/` | HunterBox磁盘布局、相对路径规范化、越界拒绝、游戏 ID 安全校验 |
| `HunterBox.Domain.Games` | `Games/` | 单款游戏静态配置与目录/exe 校验 |
| `HunterBox.Domain.Mods` | `Mods/` | MOD 聚合根、元数据、包识别、散装放置决策、FOMOD、存储端口 |
| `HunterBox.Domain.Paks` | `Paks/` | pak 命名/放置计划、冲突、名称索引 |
| `HunterBox.Domain.Models` | `Models/` | 模型身份、路径识别/改写规则、模型候选清单端口 |
| `HunterBox.Domain.Archives` | `Archives/` | 压缩格式探测、解压选项、解压端口 |

## 2. `Layout`：BoxPaths

文件：[BoxPaths.cs](../../src/HunterBox.Domain/Layout/BoxPaths.cs)。只计算路径与做安全校验，
不创建目录、不读写文件。

### 2.1 路径属性

| 成员 | 结果 |
|---|---|
| `Root` | 构造时 `Path.GetFullPath(root)` |
| `ModsDirectory` | `<Root>/Mods` |
| `BackupRootDirectory` | `<Root>/Mods/.backup` |
| `StagingDirectory` | `<Root>/Mods/.staging` |
| `TempDirectory` | `<Root>/Temp` |
| `DownloadDirectory` | `<Root>/Download` |
| `SettingsFile` | `<Root>/hunterbox.ini` |
| `ModDirectory(int id)` | `<Root>/Mods/<id>` |
| `FilesDirectory(int id)` | `<Root>/Mods/<id>/files` |
| `BackupDirectory(string gameId)` | `<Root>/Mods/.backup/<gameId>`（游戏 ID 会校验） |
| `BackupFile(string gameId, string relativePath)` | 备份目录下的规范化相对路径 |

### 2.2 关键静态方法

```csharp
public static string NormalizeGameId(string gameId);
public static string NormalizeRelative(string relativePath);
public static string[] NormalizeRelativeSegments(string relativePath);
public static string ResolveInside(string root, string relativePath);
```

路径拒绝规则（均抛 `ArgumentException`）：

1. 空/空白；绝对路径（前导 `/` 或含 `:` 盘符）。
2. 任一段为 `..`；规范化后没有有效段。
3. 任一段以点或空格结尾（Win32 会静默截断，造成撞名/设备名）。
4. 含 `Path.GetInvalidFileNameChars()` 中的字符。
5. 段名的 stem（`Split('.')[0].TrimEnd(' ')`）是 Windows 保留设备名：
   `CON/PRN/AUX/NUL/CONIN$/CONOUT$/COM1-9/LPT1-9`。
6. `NormalizeGameId` 额外拒绝 `.`、`..` 与 `/ \ : * ? " < > |`。

`ResolveInside` 先规范化相对路径，再 `Path.GetFullPath`，最后用「根目录 + 分隔符」前缀做
`OrdinalIgnoreCase` 比较，确保结果没有逃出根目录（zip-slip 的最后一道闸）。

## 3. `Games`：GameProfile

文件：[GameProfile.cs](../../src/HunterBox.Domain/Games/GameProfile.cs)。

```csharp
public sealed record GameProfile(
    string Id,
    string DisplayName,
    string? PakPatchPrefix,
    int PatchNumberFloor,
    int SteamAppId,
    string ExecutableName,
    string DefaultFolderName,
    string NexusGamePath);
```

| 成员 | 说明 |
|---|---|
| `UsesPakModsFolder` | `!string.IsNullOrEmpty(PakPatchPrefix)`；世界为 `false` |
| `HasExecutableName` | 是否配置了主程序名 |
| `CreatePakProfile(mode)` | 生成 `PakProfile(PatchPrefix, PatchNumberFloor, mode)`；不使用 `pak_mods` 时抛 `InvalidOperationException` |

主程序/目录存在性检查已下沉到 Infrastructure 的 `GameDirectoryProbe`（A-03），Domain 不再做文件系统探测。

`PatchNumberFloor` 只是**扫描不到任何已有 patch 时的下界兜底**；实际编号以扫描游戏根目录 +
`pak_mods` 为准，不需要随游戏更新/DLC 维护。当前兜底值：荒野 15、崛起 1、世界 0（未使用）。

## 4. `Mods`

### 4.1 聚合根 ModRecord

文件：[ModRecord.cs](../../src/HunterBox.Domain/Mods/ModRecord.cs)。允许可变（启用状态/排序/元数据），
`Id` 是 `init` 不可改。

| 字段 | 类型 | 含义 |
|---|---|---|
| `Id` | `int` | 持久 ID，自增且不复用 |
| `GameId` | `string?` | 所属游戏；`null` 表示旧数据未归属（不出现在任何列表、不允许启用/清理） |
| `Index` | `int` | 列表顺序，从 1 开始，与 `Id` 解耦 |
| `Enabled` | `bool` | 文件是否已铺到游戏目录 |
| `Metadata` | `ModMetadata` | 可编辑元数据 |
| `Kind` | `ModKind` | 安装时识别的包种类 |
| `TargetDirectory` | `string?` | 相对游戏根目录的额外落点前缀；新安装恒为 `null` |
| `RelativeFiles` | `List<string>` | MOD 内全部文件，相对 `files/`、正斜杠、小写 |
| `PackageFingerprint` | `string?` | 包内容指纹（文件清单 + 大小），用于同游戏去重 |
| `InstallSource` | `string?` | 原始安装包/文件夹（或归档后的 `Download` 路径） |
| `InstalledAt` | `DateTimeOffset` | 安装时间（UTC），重装时更新 |
| `DisplayName` | 计算属性 | `Metadata.Name` 为空时回退 `MOD #<Id>` |

`WithGame(gameId, index)` 用 `MemberwiseClone` 整体复制后只覆盖 `GameId` 与 `Index`；
`BelongsTo(gameId)` 忽略大小写比较，未归属记录不属于任何游戏。

### 4.2 元数据

[ModMetadata.cs](../../src/HunterBox.Domain/Mods/ModMetadata.cs) 全部字段可空：
`Name`、`Category`、`Version`、`Author`、`NexusId`（`int?`）、`HomeUrl`、`Description`。
`Description` 是列表「备注信息」列在没有模型识别结果时的回退文本。

`ModInfoParser.Parse(string? content)` 解析包内 `modinfo.ini`：支持 `key=value`、`;`/`#` 注释、
无意义段落头、键名大小写不敏感、值两侧成对引号；同一键以第一次为准；解析失败不抛异常，缺失字段为 `null`。

`ModLinks` 把元数据转成可打开的 NexusMods 地址 / 来源地址 / 文件路径。

### 4.3 包识别

[ModKind.cs](../../src/HunterBox.Domain/Mods/ModKind.cs)：

| 值 | 含义 |
|---|---|
| `Unknown` | 无法识别，安装应拒绝 |
| `LooseFiles` | 带已知内容根（`natives`/`reframework`/`nativepc`/`pl`/`wp` 等）的散装 MOD |
| `RefScripts` | 只含 `.lua`，按包内路径安装 |
| `RefPlugins` | 只含 `.dll`，按包内路径安装 |
| `PakFiles` | 包内含 `.pak`；根层 pak 交给 pak 放置规则，其余按散装处理 |
| `RefFrameworkItself` | 历史分类；新包按普通散装文件安装 |
| `Fomod` | FOMOD 包，需要用户选择组件 |

[PackageInspector.Inspect(relativePaths, gameId?)](../../src/HunterBox.Domain/Mods/PackageInspector.cs)
判定顺序固定为：

```text
FOMOD（fomod/ModuleConfig.xml 或根 ModuleConfig.xml）
  → 包内任意 .pak
  → 已知内容根（natives / reframework / nativepc / pl / wp / plugins / autorun）
  → 含 .lua
  → 含 .dll
  → Unknown
```

注意：`Inspect` 只判断「包内是否出现某类文件」，不区分层级；「根层 `.pak` 进 `pak_mods`、
其余 `.pak` 按散装处理」的分流发生在启用阶段（`ModActivationService.SplitFiles`）。

它只用于展示/策略提示，**不推断安装前缀**：包内路径就是最终落点，`PackageInspection.TargetDirectory`
新安装恒为 `null`。`PackageInspector.Normalize` 输出「小写、正斜杠、无前导 `./`」的路径。

### 4.4 散装文件放置计划

[LooseFilePlanner.cs](../../src/HunterBox.Domain/Mods/LooseFilePlanner.cs) 只有纯决策，不碰磁盘。

`FileOperationKind`：`Copy`、`BackupThenCopy`、`Delete`、`RestoreBackup`。
`MountContext` 由 Application 填实现：

```csharp
Func<string, bool> ExistsInGame;            // 游戏根目录是否已有该文件
Func<string, int?> OwnerOf;                 // 当前占用该路径的已启用 MOD
Func<string, bool> HasBackup;               // HunterBox里是否已有该文件的原文件备份
Func<string, bool> ClaimedByOtherEnabledMod;// 除当前 MOD 外是否还有启用中的 MOD 需要它
```

启用计划 `PlanEnable`：

| 现场状态 | 操作 |
|---|---|
| 游戏目录不存在该文件 | `Copy` |
| 已存在，且已有备份 **或** 已有HunterBox占用方 | `Copy`（不重复备份） |
| 已存在，无备份、无占用方（游戏原文件） | `BackupThenCopy` |

禁用计划 `PlanDisable`：

| 现场状态 | 操作 |
|---|---|
| 游戏目录不存在 | 不产生操作 |
| 还有其它启用中 MOD 需要该文件 | 不产生操作（保留胜出方内容） |
| 有备份 | `RestoreBackup` |
| 无备份 | `Delete` |

输入路径统一经 `NormalizeRelative` + `PackageInspector.Normalize` 校验并去重；
空路径、绝对路径、含 `..` 直接抛 `ArgumentException`，不静默丢弃。

### 4.5 IModStore 契约

文件：[IModStore.cs](../../src/HunterBox.Domain/Mods/IModStore.cs)。接口注释本身就是一份并发契约：

1. 仓储实例自身线程安全；容器、自增 ID、落盘快照必须串行化。
2. `Mods`/`ModsFor` 返回**列表快照**，但元素是仓储内的**活 `ModRecord`**。
3. `ModRecord` 本身不线程安全；调用方必须保证同一时刻只有一个写线程。
4. `Add`/`Remove`/`Save` 单次原子；`Save` 写一致快照，并发保存由实现串行化。
5. `NextId` 与 `Add` 不是原子对；并发取号必须由调用方串行化，重复 ID 必须拒绝。
6. `Save` 期间持有实现内部锁，不应在 UI 线程逐条保存。
7. ID 单调递增、不复用已删除的 ID；损坏索引的保留/降级策略由实现决定。

### 4.6 FOMOD

文件：[FomodConfig.cs](../../src/HunterBox.Domain/Mods/FomodConfig.cs)、
[FomodSelectionState.cs](../../src/HunterBox.Domain/Mods/FomodSelectionState.cs)。

数据模型：

| 类型 | 说明 |
|---|---|
| `FomodGroupType` | `SelectExactlyOne`、`SelectAtMostOne`、`SelectAll`、`SelectAny`（对齐 Nexus `GroupTypeEnum`） |
| `FomodPluginType` | `Required`、`Recommended`、`Optional`、`NotUsable`、`CouldBeUsable` |
| `FomodFileMapping(Source, Destination, Priority)` | 包内源路径 → 游戏根相对路径；`Priority` 越小越先 |
| `FomodPlugin(Name, Description, Files, Type, Image, ConditionFlags)` | 一个可选项及其标记 |
| `FomodGroup(Name, Type, Plugins)` | 一组互斥/全选/任选选项 |
| `FomodInstallStep(Name, Visible, Groups)` | 安装步骤 + 可见性依赖 |
| `FomodDependency` | `Operator`（And/Or）+ `Files` + `Flags` + `Nested` |
| `FomodInstallPattern` | 依赖满足时安装的文件 |
| `FomodConfig` | `ModuleName`、`Groups`（展开视图）、`InstallSteps`、`RequiredInstallFiles`、`ConditionalInstalls`、`Warnings` |

`FomodParser.Parse(xml, packageFiles)` 的路径安全：`source`/`destination` 必须是目标根内的安全相对路径；
绝对路径（盘符/UNC/前导分隔符）、含 `..`、空路径的映射会被跳过并记录 `Warnings`，不静默丢弃；
`<folder>` 映射在未传 `packageFiles` 时跳过并 warning。没有任何安装内容或 XML 非法时返回 `null`。
`PackageInspector.IsFomodConfig` 识别 `fomod/ModuleConfig.xml` 与根级 `ModuleConfig.xml`（唯一实现）。

`FomodSelectionState`：

- 默认值：`Required` / `SelectAll` 强制选中并锁定；`Recommended` 默认选中；
  `SelectExactlyOne` 在没有其它默认值时选中第一个可用项；其余默认不选。
- `NotUsable` 不可选、不参与校验与安装；`CouldBeUsable` 按可选处理。
- `SetSelected(entry, selected)` 按分组类型做互斥/锁定。
- `Validate()` 返回中文错误列表（空集合通过）。
- 步骤可见性每次勾选后按 flag/file 依赖做**不动点迭代**重算；被隐藏步骤的勾选与标记不生效。
- `ResolveFiles()` = 必装文件 + 可见步骤选中项 + 条件安装求值，按 `Priority` 排序。

## 5. `Paks`

### 5.1 计划模型

文件：[PakPlanner.cs](../../src/HunterBox.Domain/Paks/PakPlanner.cs)。

```csharp
public sealed record PakSource(int ModId, string ModName, string FileName, string FullPath);
public enum PakNamingMode { PatchNumbered, KeepOriginalName }
public sealed record PakPlacementOptions {
    required PakNamingMode Mode;
    required string PatchPrefix;
    int PatchNumberFloor = 1;
    IReadOnlyList<string> ExistingGameRootFileNames = [];
}
public sealed record PakAssignment(string FinalName, PakSource Source);
public sealed record PakConflict(string FileName, PakSource Incoming, PakSource Owner);
public sealed record PakPlan(IReadOnlyList<PakAssignment> Assignments, IReadOnlyList<PakConflict> Conflicts) {
    bool CanEnable => Conflicts.Count == 0;
}
public interface IContentFingerprint { string GetFingerprint(string fullPath); }
```

`PakPlanner.Plan(enabledInOrder, options, fingerprint)`：

- **编号模式**：调用 `PakNumbering.AssignPatchNumbers`，编号起点为
  `max(PatchNumberFloor, 游戏根目录已有 patch 号) + 1`，纯规划不复用已有内容；
  `PatchNumberFloor` 仅作下界兜底，实际部署由 `PakMountService` 扫描 `pak_mods` 与游戏根目录后取最小可用号。
- **保留原名模式**：同名且内容相同 → 复用同一 `FinalName`；同名内容不同 → 记 `PakConflict`；
  不同名 → 原名分配。

### 5.2 编号规则

文件：[PakNumbering.cs](../../src/HunterBox.Domain/Paks/PakNumbering.cs)。规则只有一份实现，
`PakPlanner`（纯规划）与 `PakMountService`（部署）共用：

- 文件名格式 `<prefix><NNN>.pak`，编号至少 3 位，可解析 3–5 位，上限 `99_999`。
- `NextPatchNumber(patchNumberFloor, existingFileNames, patchPrefix)`：取已有编号与下界的较大者再 `+1`。
- `AssignPatchNumbers(sources, options, contentOf)`：
  1. 按启用顺序遍历源文件；
  2. 命中 `ExistingNamesByContent` 且 `ReuseExistingContent` → 沿用已有文件名；
  3. 同一批内相同内容 → 共用同一个分配（`PakAssignment with { Source = ... }`）；
  4. 其余从 `FirstCandidateNumber` 起取**最小未占用**编号（`OccupiedNumbers` 会被跳过）。
- `PakNumberingOptions` 让两处调用方参数化差异：规划器不跳过 `pak_mods` 已占用号、不复用内容；
  部署服务传入实际已占用号与内容映射。

### 5.3 名称索引与冲突

`PakNameIndex` 是「路径哈希 → 路径」对照表；RE PAK 只存哈希，必须靠外部清单还原路径。
`PakConflictException` 携带 `IReadOnlyList<PakConflict> Conflicts`，供 UI 列出冲突双方。

pak 的档案信息（`PakArchiveInfo` / `PakEntryInfo`）、读写端口（`IPakArchiveReader` /
`IPakEntryReader`，后者主要用于测试工具）与整包解包/重打包端口（`IPakRepacker`、
`PakRepackProfile`、`PakRepackProgress`）描述的是二进制格式与 I/O，自 A-25 起统一放在
`Application/Paks`，Domain 不再承载；详见 [04-application.md](04-application.md) 第 5 节。

`IPakEntryRenamer` / `IPakPathHasher` 同属改写用例的 I/O 端口，一并位于 Application/Paks（A-19）。

## 6. `Models`

### 6.1 游戏与身份

文件：[ModelGame.cs](../../src/HunterBox.Domain/Models/ModelGame.cs)、
[ModelIdentity.cs](../../src/HunterBox.Domain/Models/ModelIdentity.cs)。

`ModelGame`：`Unknown=0`、`Wilds=1`、`Rise=2`、`World=3`；
`ModelGameInfo` 提供稳定 ID（`mhws`/`mhr`/`mhw`）、显示名与 `TryParseId`。

`ModelKind`：`Armor`、`CatEquip`、`Weapon`。
`ModelTypes` 常量：0 大剑 … 11 弓、12 重弩、13 轻弩、14 猎虫，15/16 男女护甲、17 随从猫；
另提供 `GetTableName`、`GetDisplayName`、`IsWeapon`、`IsArmorSex`、`PathType`（荒野猎虫归一到操虫棍 10）。

```csharp
public sealed partial record ModelIdentity(ModelKind Kind, int Type, int FirstId, int SecondId)
{
    ModelGame Game { get; init; } = ModelGame.Wilds;
    int PathType;        // 荒野武器把猎虫归一到操虫棍
    string Code;         // 完整模型 ID（显示/去重键）
    string PathForm;     // 完整身份路径形式（改写输入/输出）
    string TypeDisplayName;
}
```

工厂：`Armor(sex, fid, sid)`、`Cat(fid, sid)`、`Weapon(pathType, AA, BBBB)`、
`RiseArmor(id, female)`、`RiseWeapon(type, id)`、`WorldArmor(sex, fid, sid)`、
`WorldWeapon(type, fullId)` / `WorldWeapon(type, fid, withBs, opId)`；
解析：`TryParse`（身份文本）、`TryParsePath`（真实路径）。

关键语义：两段 ID 的含义按游戏不同——荒野护甲/猫 `fid/sid`，荒野武器 `AA/BBBB`；
崛起护甲/武器均为 `id/0`；世界护甲 `fid/sid`，世界武器 `fid/fullId`
（`fullId = fid*10000 + op*10 + bs`）。

### 6.2 路径识别与改写

文件：[ModelPathRules.cs（+ Rise/Wilds/World partial）](../../src/HunterBox.Domain/Models/ModelPathRules.cs)、
[ModelPathRecognizer.cs](../../src/HunterBox.Domain/Models/ModelPathRecognizer.cs)、
[ModelIdRewriter.cs](../../src/HunterBox.Domain/Models/ModelIdRewriter.cs)。

`ModelPathRules`（纯字符串处理）主要方法：

| 方法 | 作用 |
|---|---|
| `TryParsePath(path)` / `TryParsePath(path, game, out identity)` | 从真实游戏路径解析完整身份 |
| `TryParseForm(text, out identity)` | 解析完整身份路径形式（`ch03/012/003`、`rise:pl/f/060`、`world:wp/bow001`） |
| `FormatCode(identity)` | 完整模型 ID（`012_003`、`pl060`、`bs_bow001`） |
| `FormatPathForm(identity)` | 身份路径形式（改写/对话框提交用） |
| `BuildKeys(identity)` | 路径里的身份片段（internal，测试/诊断用） |
| `TryRewritePath(...)` | 按完整身份替换路径片段；`.tex.` 跳过 |
| `IsTexturePath(path)` | 是否贴图路径 |
| `TryParseWorldWeaponOpPart` / `FormatWorldWeaponOpCode` | 世界武器 op 部件 |
| `IsRowTriggerPath(path, game)` | 是否应触发模型行 |
| `EnsureCompatible(from, to)` | 同游戏、同类别、同类型；护甲不允许换性别 |

`ModelPathRecognizer` 从 `resolvedPaths`（pak 条目已还原成路径）识别：
`FindModelIds`、`FindIdentities`（护甲 + prefab 合并为同一身份，去重保序）。

`ModelIdRewriter`：

- `TryRewritePath(...)`：旧的三/四位 ID 兼容接口，只改身份槽位；
- `TryRewriteIdentityPath(path, from, to, out rewritten)`：推荐接口，按完整身份分派到各作规则；
- `ValidateIdentityPair(from, to)`：校验身份对是否可改写（同游戏、同类别、同类型）。

旧的内容等长替换接口已删除：改写只改路径，不修改文件内容。

改写只改路径，不修改文件内容；`.tex.` 贴图路径一律跳过（否则模型引用对不上会白模）。
无法识别或目标身份缺少对应路径角色时返回 `false` 并原样返回，不静默乱改。

`ModelPfbType` 用于区分 prefab 来源：`None`、`Weapon`、`Female`、`Male`。

### 6.3 模型候选清单

[IModelCatalog.cs](../../src/HunterBox.Domain/Models/IModelCatalog.cs)：

```csharp
public sealed record ModelChoice(string Code, string Name, bool HasChain = false, ModelIdentity? Identity = null);
public interface IModelCatalog {
    ModelGame Game { get; }
    IReadOnlyList<ModelChoice> ArmorTargets { get; }
    IReadOnlyList<ModelChoice> WeaponTargets(int weaponType);
    IReadOnlyList<ModelChoice> CatTargets(int? secondId = null);
}
```

`Code` 是显示/去重键，**不是路径片段**，不能直接拼进路径；改写要用 `Identity` + `PathForm`。

## 7. `Archives`

文件：[ArchiveFormats.cs](../../src/HunterBox.Domain/Archives/ArchiveFormats.cs)、
[IArchiveExtractor.cs](../../src/HunterBox.Domain/Archives/IArchiveExtractor.cs)。

- `ArchiveFormat`：`Unknown`、`Zip`、`SevenZip`、`Rar`、`Tar`、`GZip`、`BZip2`、`Xz`、`LZip`、`Zstandard`。
- `ArchiveFormatDetector.Detect(ReadOnlySpan<byte>)`：按文件头字节魔数判断，**不依赖扩展名**；
  文件读取在 Application 的 `ArchiveFileProbe`（读 512 字节后转交本方法）；
  文件不存在/无法识别返回 `Unknown`。
- `GuessPasswordFromFileName(fileName)`：从「xxx解压码123.zip」「xxx密码abc(1).zip」等形式猜密码。
- `ArchiveProgress(EntryPath, BytesTransferred, TotalBytes, Percent)`。
- `ArchiveExtractionOptions`：`Password`（`null` 时按文件名猜）、`Progress`、`CancellationToken`、
  `ExtractNestedArchives`（默认 `true`）、`MaxNestedDepth`（默认 3，防压缩炸弹）。
- `IArchiveExtractor.Extract(sourcePath, destinationDirectory, options)`：实现必须拒绝任何逃出目标目录的条目，
  并在解压前创建目标目录。

## 8. 异常契约（Domain）

| 场景 | 异常 |
|---|---|
| 路径为空/绝对/含 `..`/尾随点空格/非法字符/保留设备名 | `ArgumentException` |
| 游戏 ID 非法 | `ArgumentException` |
| 世界调用 `CreatePakProfile` | `InvalidOperationException` |
| `IModStore.Add` ID 重复 | `InvalidOperationException` |
| pak 同名冲突（构造 `PakConflictException`） | `ArgumentNullException` / `ArgumentException` |
| 身份不兼容（游戏/类别/类型不一致） | `ArgumentException` |
| 模型 ID 非 3–4 位/位数不一致 | `ArgumentException` |
| 目标目录不存在（`GameDirectoryProbe` 等） | 返回 `null`/`false`，不抛异常 |
