# 本地存储

iOS App 的所有内部业务数据和结构化缓存使用唯一 SQLite 数据库：Application Support 下 `meowfilm/storage-v1/meowfilm.sqlite3`。`MeowfilmResources` 创建数据库后，将同一 `SQLiteDatabase` 显式交给原生 owner；受信 Node runtime（`apps/ios/runtime/bootstrap.mjs` 显式传入 `target: 'ios'`）通过 App 私密启动描述符和原生异步 SQLite binding（`packages/script-host/native/sqlite-storage.cc`）打开同一路径。两端使用各自的平台 SQLite driver，但共享同一份 iOS schema SQL、identity 和数据根。schema 不匹配、损坏或不支持的当前格式会阻止启动，不会探测、迁移、兼容或删除旧数据。

## 数据模型与 owner

iOS 专属关系模式按职责分层位于 `packages/storage/schema/{core,runtime,music,ios}/`，由 `python3 -I -B scripts/storage/generate-schema.py` 合并生成 `packages/storage/generated/schema-ios.sql` 并镜像到 `native/MeowKit/Sources/MeowStorage/Resources/storage-schema.sql`（161 张 `STRICT` 表、43 个索引，不包含 `windows_*` 表，无 `shape_mask` / `null_mask`）。所有内部业务持久化均由各仓库手写强类型 SQL ↔ Swift 模型直接映射，禁止 `JSONEncoder`/`JSONDecoder` 数据库往返或伪关系 JSON-AST 抽象；仅外部第三方脚本／上游动态扩展字段使用有界 typed value tree（`SQLiteValueTree`）。

目录职责划分：
- `native/MeowKit/Sources/MeowStorage/`：`Engine/`（`SQLiteDatabase.swift`）、`Schema/`（`SQLiteSchema.swift`）、`Files/`（原始资源与文件保护）、`Values/`（`SQLiteValueTree.swift`）、`Debug/`（`SQLiteDebugQuery.swift`）。
- `native/MeowKit/Sources/MeowMusicNativeService/Persistence/`：`Core/`（库事务协调、模型清洗、通用 SQL helper、目录源与播放设置）、`Songs/`（曲目与来源事实）、`Library/`（收藏、歌单、网络歌单引用与同步状态）、`Queue/`（播放队列与相似推荐会话）、`History/`（历史歌单与按日播放记录）、`Discovery/`（多平台来源发现缓存）、`Home/`（首页展示快照）。

| 数据 | 唯一 owner 与存储 |
| --- | --- |
| UI 外观、音量、搜索、播放器与影视偏好 | 对应原生设置 owner 写当前 SQLite 数据库；不使用 UserDefaults JSON 持久化 |
| 影视规则、猫源设置、TMDB／豆瓣设置、网盘账号 | 原生设置 owner 直接提交 typed rows 与 revision；受信 Node 只获得所需设置能力。在线网盘登录／刷新以 provider revision CAS 回写同一账号行 |
| 小说书架、阅读历史、净化规则和影视收藏 | iOS 原生业务 owner 写 SQLite 关系表 |
| 音乐账号、歌曲、来源、队列、历史、匹配、首页与平台状态 | 音乐原生 owner 写同库的音乐关系表；Node 执行外部配方，不持久化第二份账号或歌曲事实 |
| Cat/source/music 脚本安装事实、私有 KV、来源 Cookie、TMDB／豆瓣设置独立入口和影视业务缓存 | 对应 Node owner 使用同一数据库中的专属关系表；App 注入原生设置时，Node 不另存该配置 |
| 真实脚本源码、原始图片／音频／歌词等大文件 | 保留各自 owner 的原始文件或流式资源目录；SQLite 只保存索引、身份和状态 |
| 用户主动导入／导出的交换文件 | 由用户选择的文件流程处理；JSON 是允许的外部交换格式，不是内部数据库 payload |
| 第三方脚本私有卷 | 由脚本及宿主现有文件 capability 管理；格式由第三方决定，应用不扫描或禁止其自管 JSON |

原生设置提交和运行器应用是两个独立结果。设置页写入成功不等待网络、脚本装载或运行器通知；正在运行的 owner 收到不携带值的变更通知后，重新读取同一数据库中的最新行。SQLite 事务串行化物理写入；业务 stale-write 通过 owner/block/provider revision、CAS 与 operation receipt 判定，不以整库内存快照覆盖其他 writer。受信 capability 限制可写 owner 和字段，但 SQLite 本身不提供表级应用 ACL。

凭据与所属业务同库、同事务，以明文 typed fields 保存。当前设计不使用 Keychain 凭据副本、应用层加密、密钥文件或解锁口令。系统沙盒权限、账号 owner 授权、撤销、HTTPS 与日志脱敏仍生效。

## 事务、性能与生命周期

`SQLiteDatabase` 与 `packages/script-host/native/sqlite-storage.cc` 统一执行相同的 PRAGMA 基准：`busy_timeout=5000`、`trusted_schema=OFF`、`foreign_keys=ON`、`journal_mode=WAL`、`synchronous=FULL`、`wal_autocheckpoint=0`、`auto_vacuum=INCREMENTAL`、`temp_store=MEMORY`、`cache_size=-16384`，并在 `open` 阶段逐项校验生效。

- **WAL 读写分离**：`SQLiteDatabase` 维护一条串行写连接（`writeQueue`）和一条独立只读连接（`readQueue`，`SQLITE_OPEN_READONLY` + `PRAGMA query_only=ON`）。常规只读查询与 `readSnapshot` 运行在只读连接上，不与写事务互斥；写事务内重入只读调用自动复用当前写连接以保持读己所写。
- **Prepared Statement LRU 缓存**：`SQLiteDatabase` 的读写连接及 `sqlite-storage.cc` 均维护有界 Prepared Statement LRU 缓存（上限 256 条、SQL ≤ 4096 字节、跳过 `PRAGMA`），复用句柄并在连接关闭前统一 `sqlite3_finalize`。
- **零每请求文件 syscall 与锁争用**：仅在打开库、显式 `checkpoint`、在线 `backup` 以及达到提交阈值（64 次提交）触发周期维护时执行文件/inode 校验并获取 `maintenance.lock`；常规 `query`/`execute`/`transaction`/`snapshot` 零额外文件 stat 与锁文件争用。
- **写入准入与生命周期**：写入有 pending count/bytes 准入，短事务只读取和更改对应实体及关系；无变化更新不增加 WAL。关闭先拒绝新任务、排空已受理事务、释放全部缓存语句并关闭连接，不在关闭时执行 checkpoint／vacuum。在线备份由 SQLite backup API 生成一致快照。

图片、音频、歌词和脚本源码字节不塞入 SQLite。原始资源目录与媒体 cache 各自持有文件生命周期和容量策略，数据库只做索引；缓存失效不能触发删除或重建整库。WAL 是单机本地事务机制，不表示跨设备多主或服务器集群高可用。

JSON 仍用于 HTTP、运行器私密 IPC、脚本 API、源码／构建元数据及用户主动交换文件。只有应用自身运行期内部持久化禁止 JSON 文档落盘或完整 JSON 文档入库；不按 `.json` 扩展名封禁三方脚本自管文件、测试／构建材料或用户导出。

## 本机数据清除

显式清除先持久记录重置请求、立即发起本代业务和运行器停止并显示需重新打开应用，不等待后台释放才返回操作；实际删除仅在下一次冷启动、任何数据库或 Node owner 创建前执行，不在前台恢复时删除。删除覆盖应用自有数据根（含其内脚本私有卷），不会调用云端删除或触碰外部文件及其他应用数据。清除不是数据库格式不匹配时的恢复方式；损坏或旧格式不会被自动当作空库删除。

系统强制终止不保证发送或等待 `willTerminateNotification`。已确认的保存依赖运行期间的 COMMIT；终止通知仅停止音乐，不承担全量异步保存和资源排空。

端侧共同边界见[共享 SQLite 契约](../common/settings-storage.md)，运行器 RPC 及能力边界见[运行器入口](runtime-preflight.md)。
