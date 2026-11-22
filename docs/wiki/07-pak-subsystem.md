# 07 PAK 子系统

> RE Engine 的 pak **只存路径哈希，不存文件名**，因此「给 pak 里的模型改名」等价于
> 「用新路径的哈希替换条目表里的 8 字节哈希」。本页说明 HunterBox 如何安全地做这件事。
> 行为与风险清单另见 [docs/PAK.md](../PAK.md)。

## 1. 格式速览

### 1.1 包头（16 字节）

| 偏移 | 大小 | 字段 |
|---|---|---|
| 0 | 4 | 魔数 `KPKA` |
| 4 | 1 | major（2 或 4） |
| 5 | 1 | minor（0 或 1） |
| 6 | 2 | feature（0 / 8 / 24） |
| 8 | 4 | 条目数 `count` |
| 12 | 4 | fingerprint |

### 1.2 条目布局（TOC）

| 版本 | 单条大小 | 字段顺序 |
|---|---|---|
| v2 | 24 B | `offset(8)`、`decompressedSize(8)`、`hashLower(4)`、`hashUpper(4)` |
| v4 | 48 B | `hashLower(4)`、`hashUpper(4)`、`offset(8)`、`compressedSize(8)`、`decompressedSize(8)`、`attributes(8)`、`checksum(8)` |

**哈希字段位置随版本不同**：v4 在条目起始 `+0`，v2 在 `+16`。任何「只改哈希」的写回都必须通过
`PakEntryLayout.HashFieldOffset` 定位，不能假设哈希在条目开头（历史 bug 曾把 v2 的 offset 写坏）。

### 1.3 属性位

- `attributes` 低 4 位：压缩方式（`0` 原样、`1` DEFLATE、`2` ZSTD）；
- `attributes` 16–23 位：加密方式（`0` 不加密、`1–5` 资源加密）；
- 资源加密条目在数据前有 8 字节明文长度 + N×128 字节密文块。

### 1.4 路径哈希与校验和

```text
HashPath(path) = (HashString(path.ToUpper()) << 32) | HashString(path.ToLower())
HashString     = MurmurHash3 x86_32(UTF-16LE bytes, seed = 0xFFFFFFFF)

Checksum(content) = (XXH64(content, seed=0xFFFFFFFF) << 32)
                  | XXH32(littleEndianBytes(XXH64), seed=0xFFFFFFFF)
```

引擎约定低 32 位是**小写路径**的哈希，调用方必须先归一为小写再计算。

### 1.5 TOC 加密

| feature | 含义 |
|---|---|
| 0 | 明文 TOC |
| 8 | TOC 加密；TOC 后跟 128 字节密钥块 |
| 24 | TOC 加密；TOC 与密钥块之间有 4 字节附加数据（feature `0x10`） |

`PakCipher.CryptToc` 用固定模数与公钥指数做 `BigInteger.ModPow` 还原 128 位密钥，再生成 XOR 密钥流；
加密与解密是同一操作。资源加密同理，是「公开可逆混淆」，不需要私钥，因此可以重新加密 TOC/条目。

## 2. 组件地图

```text
                        ┌──────────────────────────┐
                        │ PakModelRewriteService    │  Application
                        │ 路径还原 → 身份替换 → 哈希 │
                        └───────┬──────────┬───────┘
                                │          │
              ┌─────────────────▼──┐   ┌───▼──────────────────┐
              │ PakNameIndex       │   │ RePakRenamer          │
              │ hash → path        │   │ 只改 TOC 哈希（批量）  │
              └─────────▲──────────┘   └───┬──────────┬───────┘
                        │                  │          │
        ┌───────────────┴────┐   ┌─────────▼──────┐ ┌─▼────────────────┐
        │ PakPathHasher      │   │ PakTocPreImage │ │ PakPathLock      │
        │ Murmur3 + 内置清单  │   │ pending/bak    │ │ 进程内路径互斥    │
        └───────────────▲────┘   └────────────────┘ └──────────────────┘
                        │
        ┌───────────────┴──────────────────────────────────────────────┐
        │ PakPlainToc / PakEntryLayout / PakArchiveCodec                │
        │ 包头、TOC 定位、条目解码、TOC 解密、条目解密/解压              │
        └───────▲───────────────────────▲──────────────────▲───────────┘
                │                       │                  │
        ┌───────┴───────┐     ┌─────────┴──────┐   ┌───────┴────────┐
        │ RePakReader   │     │ PakRepacker    │   │ PakUnpacker    │
        │ 只读/宽松      │     │ Inspect/Capture│   │ 事务化解包      │
        └───────────────┘     │ Pack           │   └────────────────┘
                              └────────────────┘
```

## 3. 读取路径

| 场景 | 使用 | 行为 |
|---|---|---|
| 普通解析 | `RePakReader.Read(pakPath)` | 只读包头 + TOC；不校验 feature 白名单、不逐条校验；TOC 加密时返回密文原样结果（`IsTocEncrypted=true`，offset/大小/哈希不可信） |
| 模型识别/路径还原 | `PakEntryPathReader.TryReadEntryPaths(...)` | 明文 TOC 用 `RePakReader`；TOC 加密或结构超出支持范围时先用 `PakRepacker.Inspect` 解密；>100MB 整包资源不逐条扫描；失败返回原因，不写日志 |
| 需要校验的完整解析 | `PakArchiveCodec.Read(stream)` | 支持 feature 0/8/24，解密 TOC，校验条目范围、大小、加密/压缩方式；未知 feature/压缩/加密抛 `NotSupportedException` |
| 条目内容 | `PakUnpacker` + `PakEntryCodec` + `PakCipher` 流式路径 | 资源解密 + DEFLATE/ZSTD 解压 + 长度校验；输出有上限 |
| 测试/诊断读取 | `RePakEntryReader.ReadEntry`（测试工具） | 只支持未加密条目；压缩 0/1（DEFLATE） |

## 4. 改名（只动 TOC）

### 4.1 RePakRenamer

```csharp
public sealed class RePakRenamer : IPakEntryRenamer, IBatchPakEntryRenamer, IPakTocRecovery
{
    public void RenameEntry(string pakPath, int entryIndex, ulong newHash, bool backup = true);
    public void RenameEntries(string pakPath, IReadOnlyList<PakEntryRename> renames, bool backup = true);
    public bool TryRecoverPending(string pakPath, out string message);
    public bool TryRecover(string pakPath, out string message);
    public IDisposable? AcquireReadLock(string pakPath);
}
```

- 只读一次 TOC、内存改完所有目标哈希、一次写回、一次 flush（批量是单条的超集）；
- 不再复制整包：I/O 从 O(pak) 降到 O(TOC)，GB 级 pak 不再每个 change 复制一次；
- TOC 加密 pak：用包内 128 字节密钥块解密 → 改哈希 → 用同一密钥块重新加密写回；
- 数据区、偏移、压缩/加密属性、checksum 一律不动；
- 同一 pak 的并发改名/恢复由 `PakPathLock` 串行化（路径级 `Monitor`，引用计数，可重入；
  读锁与写锁共用同一把排他锁，当前不做读写分离）。

### 4.2 前像协议（崩溃恢复）

两个文件：

| 文件 | 含义 |
|---|---|
| `<pak>.toc.pending` | 本次写入的未提交日志；写前生成，成功后提交/删除 |
| `<pak>.toc.bak` | 最近一次成功改名的前像（可回滚） |

前像头部 48 字节（小端）：

```text
magic "HBTOCPRE"(8) | version(4) | pakLength(8) | tocLength(4)
| major(1) | minor(1) | feature(2) | count(4) | dataSample(8) | reserved(8)
| 原始 TOC 字节（加密 pak 为密文）
```

`dataSample` 是数据区「起点/中点/末尾各最多 4KB」的 FNV-1a 64 采样哈希。
恢复前逐项比对，重装/替换后的旧前像不会误套到新 pak 上。

恢复时机：`PakEntryPathReader`、`PakRepacker.Inspect`、`PakUnpacker`、`RePakRenamer`、
模型改写服务在读取/解包/改名/规划前都会发现 `.toc.pending` 并自动写回。

恢复结果三态：

| 结果 | 含义 | 处理 |
|---|---|---|
| `NotApplicable` | 没有前像，或指纹不匹配 | 不恢复 |
| `Restored` | 已写回原始 TOC | 可继续操作 |
| `IoError` | pak 或前像被占用/无权限 | **保留** `.toc.pending` 并抛可读 `IOException` |

已提交的 `.toc.bak` 只在当前 TOC 结构（偏移/大小）确实损坏时才自动恢复，
正常 pak 不会被回退；半写但仍可解析的 TOC 依赖 `.toc.pending`（已知边界）。

### 4.3 改写调用链

```text
ModelRewriteCoordinator.Rewrite(mod, changes, index, game)
  → 对每个根层 pak：
      PakModelRewriteService.RewritePaths(pakPath, changes, index, backup)
        1. 先尝试恢复 pending
        2. 用 PakNameIndex 把条目哈希还原成路径
        3. ModelPathRules.TryRewritePath 按完整身份替换路径
        4. 新路径小写 → PakPathHashProvider.HashPath → 8 字节哈希
        5. RePakRenamer.RenameEntries（一次写回，前像 .toc.pending/.toc.bak）
  → 散装文件：LooseModelRewriteService.Rewrite（File.Move，不改内容）
  → 汇总 PaksScanned/EntriesChanged/FilesMoved/Warnings/UnsupportedPaks
```

整张 TOC 加密（feature 8/24）时：有 `IPakRepacker` 就先 `Inspect` 解密条目表，再由改名器
按同一密钥块重新加密写回；没有重打包实现时抛 `NotSupportedException`，协调器记录到
`UnsupportedPaks` 并继续其它 pak，不中断整批。

## 5. 内容替换（测试工具）

`RePakWriter.ReplaceEntry(pakPath, entry, data, backup)`：

- 只支持「未加密 + 未压缩」且**长度必须一致**的条目；
- v4 内容变了必须同步重算 `checksum`；v2 没有 checksum 字段，按布局跳过；
- 写入事务化：原 pak 复制到同目录临时副本 → 写数据与 checksum → 落盘 → 同卷 rename 原子替换；
- `.bak` 只在首次写回前生成，后续替换保留最初原始备份；
- TOC 加密时无法只写明文 checksum 字段，抛 `NotSupportedException`，应改用解包→重打包。

`RePakWriter`/`RePakEntryReader` 只作为测试工具保留（构造/读取 pak 条目做断言），
不在组合根装配、不参与生产改写。旧的 `PakModelRewriteService.Rewrite(pakPath, fromId, toId)`
内容等长替换 API 已删除：它不改变条目路径、对 pak 型 MOD 改模型 ID 不生效，且可能破坏资源；
生产改写统一走 `RewritePaths` + `RePakRenamer`。

## 6. 打包 / 解包

### PakUnpacker

```csharp
public void Unpack(string pakPath, string destinationDirectory,
    PakNameIndex? nameIndex = null,
    IProgress<PakRepackProgress>? progress = null,
    CancellationToken cancellationToken = default);
```

- 先解到目标父目录下的同卷暂存目录，全部条目成功后才整体替换目标目录；
- 目标已存在：旧目标改名同卷回滚目录 → 暂存改名目标；改名失败恢复旧目标；
- 中途失败/取消只清暂存，目标保持原样；
- 路径清单未收录的条目写成 `__Unknown/<16 位大写哈希>`；
- 支持 TOC 解密、条目资源解密、DEFLATE/ZSTD 解压；
- 解包期间持有 `PakPathLock` 读锁（实为排他锁），避免与改名交错。

### PakPacker

```csharp
public static void Pack(string sourceDirectory, string pakPath,
    PakRepackProfile profile, IProgress<PakRepackProgress>? progress = null,
    CancellationToken cancellationToken = default);
```

- 条目按 64 位组合哈希升序写入 TOC；
- 默认原样存放；配置了压缩方式且确实变小时才压缩（避免压缩后更大）；
- 全流程流式分块：原样条目一次遍历完成「写数据 + 算 checksum」；压缩条目先流式压到临时条目文件，
  同时算原内容 checksum，只有变小时才写入输出；
- TOC 需要加密时复用/生成 128 字节密钥块，用同一套 XOR 混淆重新加密；
- 先写临时文件，成功后原子替换目标 pak，失败不留半个文件；
- 峰值内存与单个文件大小无关，只与分块/压缩缓冲有关。

### PakRepacker

```csharp
public PakArchiveInfo Inspect(string pakPath);
public PakRepackProfile Capture(string pakPath);
public void Unpack(...);
public void Pack(...);
```

- `Inspect` 支持 feature 0/8/24，解密 TOC 并返回可信条目；
- `Capture` 记录版本/feature/压缩/加密/密钥块/附加数据；原包条目属性混合时降级为
  「不压缩/不加密」并写 warning（`PakRepackProfile.Warnings` + `AppLog`）；
- 需要产出普通 mod pak 时把 `Feature` 改成 0，输出明文 TOC。

## 7. 路径清单与安全

| 组件 | 说明 |
|---|---|
| `EmbeddedPakNameIndex` | 内置荒野/崛起模型路径清单（`mhws.models.list.gz` / `mhr.models.list.gz`），世界返回空索引 |
| `PakPathHasher.BuildIndex(lines)` | 每行一个路径，跳过空行与 `#`/`;` 注释，构建 `PakNameIndex` |
| `PakPathSafety.SplitSafeSegments(path)` | 委托 `BoxPaths.NormalizeRelative` 校验（绝对路径、`..`、非法字符、保留设备名、尾随点空格），额外拒绝 `.` 段；返回安全路径段 |
| `PakNameIndex` | 只支持「哈希 → 路径」，没有反向查询 |
| `PakPathLock` | 进程内按路径引用计数互斥；多实例/外部工具并发写不在支持范围（应用有单实例保护） |

## 8. 能力矩阵

| 能力 | 状态 | 实现 |
|---|---|---|
| 解析包头/TOC/路径表 | 支持 | `RePakReader`（宽松）、`PakArchiveCodec`（严格） |
| TOC 加密 pak 解析 | 支持 | `PakArchiveCodec` / `PakRepacker.Inspect` |
| 条目读取 | 测试工具 | DEFLATE（`RePakEntryReader`）；DEFLATE/ZSTD/资源加密（`PakUnpacker` 流式路径） |
| 条目改名（只改哈希） | 支持 | `RePakRenamer`（明文与加密 TOC，批量一次写） |
| 明文 TOC 模型改写 | 支持 | `PakModelRewriteService.RewritePaths` |
| 加密 TOC 模型改写 | 支持 | 解密 TOC → 改哈希 → 同密钥块加密写回 |
| 整包解包/重打包 | 支持 | `PakUnpacker` / `PakPacker` / `PakRepacker` |
| 条目内容等长替换 | 测试工具 | `RePakWriter`（测试校验用；v4 重算 checksum） |
| 变长条目/增删条目 | 需重打包 | `PakPacker` |
| 未知 feature/压缩/加密 | 明确拒绝 | `NotSupportedException`，不猜测 |
| 单条目 > 2GB | 不支持 | `NotSupportedException` |

## 9. 已知限制与风险

- **未完成真实 MOD 进游戏验证**：TOC 哈希与重打包结果已用独立代码校验，但未确认真实游戏加载。
- 明文 TOC 改写不重排条目；若引擎依赖原 TOC 顺序做查找，理论上存在风险。
- 世界不做 pak 内改写（与游戏加载方式一致）；荒野/崛起路径清单内嵌且当前不可外部覆盖。
- 解包/重打包需要额外临时磁盘空间；未知 feature/压缩/加密明确拒绝。
- 前像指纹是 O(12KB) 的启发式校验（长度/头部/条目数 + 数据区三段采样），
  理论上存在「结构相同但 TOC 不同」的 pak 命中风险；正常 MOD 更新几乎不会发生。
- 只保留最近一份 `.toc.bak`；需要多代备份请自行复制前像。
- 任何 pak 写操作都限定在 `Mods/<id>/files/` 内的 MOD 副本，不直接改游戏目录里的 pak。
