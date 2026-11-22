# RE Engine PAK 读写与模型改写能力

> 目标：为「模型 ID 修改」提供 pak 解析、条目表路径哈希重写，以及整张 TOC 加密 pak 的就地改名。
> 实现位于 `src/HunterBox.Infrastructure/Paks/`、`src/HunterBox.Application/Models/`，Domain 只保留接口与纯逻辑。

## 1. 格式要点（摘要）

- 包头 16 字节：magic `KPKA`、major/minor、feature（0/8/24）、条目数、fingerprint；v4 条目 48 字节、v2 条目 24 字节。
- 条目 = 路径哈希（64 位，低 32 位 = hash(小写路径)、高 32 位 = hash(大写路径)）+ offset + 压缩前后大小 +
  attributes（压缩类型/加密类型）+ checksum；TOC 后紧跟数据区。
- 路径哈希 = UTF-16LE + Murmur3 x86_32（seed 0xFFFFFFFF），大小写各算一次。
- feature 8/24 表示整张 TOC 加密（RSA + 密钥流混淆）；v4 支持条目 DEFLATE/ZSTD 压缩与资源加密。
- 本实现的 `PakRepacker` 可解密 TOC、解压/解密条目，并按原 profile 重新打包；需要产出普通 mod pak 时输出 feature=0。

## 2. 能力矩阵

| 能力 | 状态 | 实现/说明 |
|---|---|---|
| 解析包头 + 条目表 + 路径表 | ✅ | `RePakReader` → `PakArchiveInfo`（v2/v4）；`PakRepacker.Inspect` 可读 TOC 加密 pak |
| 条目读取 | ✅ | `RePakEntryReader`（测试校验工具）：DEFLATE；`PakRepacker.Unpack` 支持 DEFLATE/ZSTD/资源加密 |
| 条目改名 | ✅ | `RePakRenamer`：只覆盖条目表 8 字节哈希，数据区不动（仅重写条目表；加密 TOC 就地解密/回加密） |
| 路径哈希/路径清单 | ✅ | `PakPathHasher` + 三作内嵌清单（荒野/崛起；世界无 pak） |
| 明文 TOC pak 模型改写 | ✅ | `PakModelRewriteService.RewritePaths`：按完整身份替换路径段，只改 TOC 哈希 |
| 散装模型改写 | ✅ | `LooseModelRewriteService`：只 `File.Move` 改名，不改文件内容 |
| 整张 TOC 加密 pak | ✅ | 有 `IPakRepacker` 时先 `Inspect` 解密条目表，再由 `RePakRenamer` 就地改哈希并用同一密钥块加密写回 |
| 整包重打包（增删/变长条目） | ✅ | `PakPacker` 按目录生成 header/TOC/数据，临时文件 + 原子替换 |
| TOC 加密重打包（保持 feature 8/24） | ✅ | `PakRepackProfile` 保存 TOC 密钥块/附加数据，可重新加密；改写默认输出 feature=0 |

## 3. 模型改写规则

1. `ModelRewriteCoordinator` 对 MOD 的每个根层 pak 调用 `PakModelRewriteService.RewritePaths`：
   - 用当前作内嵌路径清单把条目哈希还原成真实路径（清单未命中 → 跳过并汇总 warning，最多 100 条）；
   - 按各作完整模型身份替换路径（荒野 `ch0X/fid/sid`、猫、`itTT/AA/BBBB`；崛起 `player/mod`、`weapon` 等；
     世界仅散装不改 pak）；
   - 变化条目只重写 TOC 的 8 字节路径哈希（新路径统一转小写后计算），数据区、偏移、压缩/加密属性与 checksum
     一律不动；首次写回前生成 `<pak>.toc.bak`。
2. 明文 TOC 与压缩/加密**条目**都可直接改名（只动 TOC）。
3. 整张 TOC 加密（feature 8/24）的 pak：
   - 有 `IPakRepacker` 时：`Inspect` 解密条目表还原路径，再由 `RePakRenamer` 就地改哈希、
     用包内同一密钥块重新加密写回，多个 change 一次完成（O(TOC)，不再整包重打包）。
   - 没有重打包实现时：抛 `NotSupportedException`，协调器记录到 `UnsupportedPaks` 并继续其它 pak。
4. 改写后调用方重算模型分类/备注；多 pak、pak+散装混装一次处理并汇总「成功 / 跳过 / 不支持」。

## 4. 备份语义

- **改条目路径（改名，只动 TOC）**：`RePakRenamer` 原地重写条目表，成本 O(TOC)，GB 级 pak 不再整包复制。
  - 每次写入前把原始条目表写成 `<pak>.toc.pending`（未提交日志）；写成功后再提升为 `<pak>.toc.bak`
    （最近一次成功改名的前像），`backup: false` 时成功后退化为删除日志。
  - 前像文件带 48 字节头部：魔数、版本、pak 文件长度、TOC 长度、major/minor/feature/条目数与
    **数据区采样哈希**（起点/中点/末尾各最多 4KB）。恢复前逐项比对，重装/替换后的旧前像不会误套到
    新 pak 上。
  - **崩溃自动恢复**：进程被杀/断电导致 TOC 写了一半时，下次读取/解包/改名该 pak
    （`PakEntryPathReader`、`PakRepacker.Inspect`、`PakUnpacker`、`RePakRenamer`、模型改写服务）会在
    解析前发现 `.toc.pending` 并自动写回原始条目表（改名服务保证「先恢复再规划目标条目」）。
    恢复按路径加进程内锁，与并发改名串行化。
  - **恢复失败语义**：前像指纹不符/损坏时丢弃日志；pak 或前像文件被占用 / 无权限时**保留** `.toc.pending`
    并抛出可读的 `IOException`（提示关闭占用程序后重试），不会删掉唯一的恢复来源。
    解锁后再次操作即可自动恢复。
  - 已提交的 `.toc.bak` 只在当前 TOC 结构（偏移/大小）确实损坏时才自动恢复，正常 pak 不会被回退；
    半写但仍可解析的 TOC 依赖 `.toc.pending`（已知边界）。
  - 指纹是成本 O(12KB) 的启发式校验（长度/头部/条目数 + 数据区三段采样）：理论上存在被「结构完全相同
    但 TOC 不同」的 pak 命中的残余风险，正常 MOD 更新几乎不会发生。
  - 写入中途 IO 失败会用内存里的原始条目表立即回滚；回滚也失败时保留 `.toc.pending` 交给下次自动恢复。
  - 数据区从不被改名修改：`.toc.bak` + 当前数据区就是上一次改名前的 pak。
- **改条目内容（替换数据）**：仅 `RePakWriter`（测试校验工具）支持；它仍生成整包 `<pak>.bak`（首次写回前，后续不覆盖），
  因为数据区已经变化，只有整包前像才能还原。生产模型改写只改路径、不改内容。
- **TOC 加密 pak**：与明文一样只生成 `.toc.pending` / `.toc.bak`（保存加密后的原始条目表字节），
  条目不复制整包。
- 如果已有前像损坏，恢复会被拒绝（视为指纹不符）；需要手工恢复时可保留前像文件排查。
- 需要多代备份时请自行复制前像文件，当前实现只保留最近一份。

## 5. 风险与限制

- **真实游戏加载验证未完成**：TOC 哈希与重打包结果已用独立代码校验，但未用真实 MOD 进游戏确认加载。
- 明文 TOC 改写不重排 TOC；若引擎对条目做二分查找，理论上依赖原 TOC 顺序，当前按可用处理。
- 世界不作 pak 模型改写（与游戏加载方式一致）；崛起/荒野的路径清单内嵌且不可外部覆盖。
- 解包/重打包需要临时磁盘空间；单条目 >2GB 不支持；未知 feature/压缩/加密方式明确拒绝，不猜测。
- 任何 pak 写操作都限定在 `Mods/<id>/files/` 内的 MOD 副本上，不直接改游戏目录里的 pak。
- 同一 pak 的并发改名只在**本进程内**串行化；多个 HunterBox 实例/外部工具同时写同一 pak 不在支持范围
  （应用已有单实例保护）。
