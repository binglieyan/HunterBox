# HunterBox Code Wiki

> 代码基线：`v1.5.207`（版本号定义在 [HunterBox.App.csproj](../../src/HunterBox.App/HunterBox.App.csproj)）。
> 本 Wiki 面向**维护者/评审者**，从代码结构出发说明项目如何组织、每个模块负责什么、关键类型与函数如何协作、如何构建与运行。
> 行为细节以 [SPEC.md](../SPEC.md) 为准，架构决策以 [ARCHITECTURE.md](../ARCHITECTURE.md) 为准，pak 格式与改写能力以 [PAK.md](../PAK.md) 为准。

## 项目一句话

HunterBox 是一个面向《怪物猎人：荒野 / 崛起 / 世界》的第三方 MOD 管理器：安装压缩包/文件夹/裸文件，
把 MOD 安全地铺进游戏目录（散装文件 + `pak_mods`），支持启用/禁用/清理/存档备份/旧数据迁移，
并提供按「完整模型身份」改写散装路径与 pak 条目表（TOC）路径哈希的模型替换功能。

技术栈：.NET 10 + WinUI 3（Windows App SDK 2.7.0）+ SharpCompress，unpackaged、仅 x64。

## 文档地图

| 文档 | 回答的问题 |
|---|---|
| [01 项目概览](01-overview.md) | 项目做什么、支持哪些游戏、目录/运行期磁盘布局、技术栈与版本 |
| [02 整体架构](02-architecture.md) | 分层与依赖方向、项目/NuGet 依赖、端口与适配器、组合根、横切设计 |
| [03 Domain 层](03-domain.md) | 纯规则层有哪些类型；路径安全、包识别、放置决策、pak 编号、FOMOD、模型身份 |
| [04 Application 层](04-application.md) | 安装/启用/清理/模型改写/存档等用例服务的方法签名、事务与回滚语义 |
| [05 Infrastructure 层](05-infrastructure.md) | JSON 存储、SharpCompress 解压、INI、日志、迁移、Steam 定位、pak 读写实现 |
| [06 App 与 UI](06-app-ui.md) | 启动链、组合根、MainWindow partial 拆分、对话框、忙碌门禁与关窗保护 |
| [07 PAK 子系统](07-pak-subsystem.md) | RE PAK 格式、编解码/加密/校验和、条目改名、前像与崩溃恢复、重打包 |
| [08 关键业务流程](08-flows.md) | 安装、启用/禁用、清理、模型改写、存档、迁移的端到端时序与失败处理 |
| [09 构建、测试与运行](09-build-test-run.md) | 环境要求、构建/测试/调试/发布命令、发布产物与常见故障 |
| [10 测试地图](10-test-map.md) | 测试分层、测试支撑设施、测试文件与被测对象的对应关系 |

## 规范文档

| 文档 | 定位 |
|---|---|
| [SPEC.md](../SPEC.md) | 行为规格（磁盘布局/安装/启用/pak/模型/UI/设置键），**行为以它为准** |
| [ARCHITECTURE.md](../ARCHITECTURE.md) | 架构原则、依赖规则、编码/注释标准、里程碑 |
| [PAK.md](../PAK.md) | pak 格式、备份语义与改写能力矩阵 |

本 Wiki 只讲“代码如何组织与实现”，规范冲突时以 SPEC/ARCHITECTURE/PAK 为准；构建、测试、运行与发布统一见 [09](09-build-test-run.md)。

推荐阅读路径：

- **第一次接手**：01 → 02 → 03 → 04 → 05 → 06。
- **改某个功能**：先看 04 中对应用例，再看 03 的决策函数，最后看 06 的 UI 入口。
- **排查数据/文件问题**：08 → 05（存储/日志）→ 07（pak）。
- **准备发布**：09（构建、测试、运行、发布已合并到这一页）。

## 解决方案布局

```text
HunterBox.slnx
├─ src/HunterBox.Domain/          纯规则：路径/包识别/放置决策/pak 计划/模型身份/FOMOD（无项目引用、无第三方包）
├─ src/HunterBox.Application/     用例编排：安装/启用/清理/模型改写/存档（只依赖 Domain）
├─ src/HunterBox.Infrastructure/  实现：JSON 存储、SharpCompress、INI、pak 读写、日志、迁移、Steam 定位
├─ src/HunterBox.App/             WinUI 3 外壳与组合根（MainWindow + 对话框 + 游戏配置）
└─ tests/HunterBox.Tests/         xUnit 测试（Domain / Application / Infrastructure）
```

依赖方向只能指向内层：`App → Application/Infrastructure → Domain`。详见 [02 整体架构](02-architecture.md)。

## 术语表

| 术语 | 含义 |
|---|---|
| HunterBox / Box | 管理器自身的运行目录，运行期数据都在这里（`Mods/`、`Saves/`、`hunterbox.ini` 等） |
| 散装文件 | 不属于 pak 的普通文件（`natives/`、`reframework/`、`nativePC/` 等），启用时复制到游戏根目录 |
| `pak_mods` | REFramework 新版加载目录；荒野/崛起把 MOD pak 放这里，世界不使用 |
| pak 型 MOD | MOD 文件里含根层 `.pak` 的 MOD；荒野/崛起走 `pak_mods` 编号链，世界按普通文件处理 |
| TOC | pak 的条目表（Table of Contents）；RE PAK 只存路径哈希，不存文件名 |
| 前像 / pre-image | 改写 pak 条目表前保存的原始 TOC（`.toc.pending` / `.toc.bak`），用于回滚与崩溃恢复 |
| 完整模型身份 | `ModelIdentity`：游戏 + 类别 + 类型 + 两段 ID；改写以它为输入/输出单位 |
| FOMOD | Nexus Mods 的安装向导格式（`fomod/ModuleConfig.xml`），按步骤/分组/依赖选择文件 |
| 端口 / Port | 内层定义的接口（如 `IModStore`、`IArchiveExtractor`、`IPakEntryPathReader`），外层实现 |
| 组合根 | App 层的 `AppServices`，唯一集中装配各服务的地方 |
| 忙碌门禁 | `OperationRunner`：同一时刻只允许一个长任务，提供进度、取消与关闭保护 |
