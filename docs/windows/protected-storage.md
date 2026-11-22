# Windows 本地存储

Windows App 在 `%LOCALAPPDATA%/meowfilm/aggregate/storage-v1/meowfilm.sqlite3` 使用唯一内部数据库。原生 App owner 直接借用一个 `NativeDatabase`；随 App 启动的 Node Worker 通过受限私密启动管道，以相同数据根和平台 SQLite driver 打开同一路径。它们共享 schema identity、事务和持久事实，不再有业务 JSON 文档库、单独缓存数据库或凭据文件库。

Windows 平台表定义按域组织在 `packages/storage/schema/{core,runtime,music,windows}/`，由 `scripts/storage/generate-schema.py` 生成独立的 `packages/storage/generated/schema-windows.sql`（不含任何 `ios_*` 表），嵌入 `MeowFilm.Platform.csproj`（逻辑名 `MeowFilm.Storage.schema.sql`），同时由 Windows 聚合 Node runtime 以 `target: 'windows'` 校验同一份 schema identity。所有表均为手写强类型 `STRICT` 关系表，零 `shape_mask`/`null_mask`/泛化内部扩展槽；仅对第三方来源/规则定义等真正动态结构使用 `TypedValueNodes` 有界节点表。当前 schema identity／版本／hash 不匹配、损坏或孤立 journal 失败关闭，不执行升级或旧数据转换。

## 目录归属

- `MeowFilm.Platform/Storage/Engine/`：`IStorageDatabase`、`NativeDatabase`、`DatabaseSession`、`PreparedStatementCache`、`TypedValueNodes`、`SerialOperationQueue`、`StorageError.cs`（`DocumentStoreError`／`DocumentStoreException`）
- `MeowFilm.Platform/Storage/Schema/`：`StorageSchema`（嵌入资源加载与 schema identity/hash 校验）
- `MeowFilm.Platform/Storage/Files/`：`DatabaseDirectory`、`FileResourceCache`、`RawResourceDirectory`、`UserDataDirectory`、`WindowsDocumentFiles`
- `MeowFilm.Capabilities/<Domain>/Storage/`：各业务域持久化仓库直接执行强类型 SQL ↔ 领域模型（`Film/Storage/`、`Pan/Storage/`、`Novel/Storage/`、`Accounts/Storage/`、`Feedback/Storage/`、`Recommendations/Storage/`、`Catalog/Storage/`、`PlaybackCache/Storage/`、`Library/Storage/`），不再使用统一 JSON-AST 投影框架或生成的 `*StorageRecords.cs`。

## 数据 owner

| 数据 | 写入 owner |
| --- | --- |
| Windows 外观、窗口、关闭、媒体控制、字幕、语言等 App 设置 | 对应原生 AppHost owner，写共享库 |
| 影视规则、来源意图、TMDB／豆瓣设置、小说来源配置、网盘凭据 | 原生领域 owner 写 typed rows；Node 只获得 capability 指定的读取或 provider 行回写。TMDB／豆瓣 runtime cache 与影视播放资料由各自 Node owner 写同库专属关系表 |
| 音乐账号与凭据、曲库、匹配、歌单快照、结构化与手工歌词、feedback outbox、建议偏好 | Windows 原生音乐 owner 写共享库；Node 执行协议请求但不另存账号、曲库或第二事实源 |
| 小说书架、阅读位置与历史 | Windows 原生 Novel owner 写共享库；Node runtime 管理被授权的来源执行状态，不复制原生书架 |
| Cat/source/music 脚本安装、来源 Cookie、脚本 KV 与运行器数据 | Node owner 写共享库对应关系表；源码文件仍留在对应 runtime 源码目录 |
| 原始图片、音频和其他二进制资源 | 各资源 owner 使用 `resources-v1` 下的文件目录；SQLite 仅保存强类型身份、索引、时间和状态 |
| 用户显式导入／导出 JSON 与第三方脚本 private volume | 外部交换或脚本自有存储，不属于应用内部 JSON 持久化禁令 |

原生 UI 可以直接写库，不依赖运行器在线。Node 登录／刷新网盘凭据等受信写入使用 provider revision CAS；设置成功通知只传块标识，不带凭据或完整设置值。SQLite 串行化物理事务，owner revision/CAS 解决 stale-write；不以共享内存快照回写覆盖另一 writer。私密管道限制 owner 和操作，但不是 SQLite 表级安全 ACL。

凭据字段与所属业务行同库同事务、纯明文保存。不使用 DPAPI、Keychain、数据库加密、应用层字段加密、密钥文件或额外解锁口令。保留 App 私有目录 ACL、owner 授权、撤销、HTTPS、错误封闭与日志脱敏。

## 事务与可靠性

`NativeDatabase` 采用 WAL 读写分离连接模型：写事务在单一 writer lane 串行执行，只读查询路由至独立只读连接（`ReadOnly` + `PRAGMA query_only=ON`），并内置有界 `PreparedStatementCache`（LRU 256）避免热点 SQL 重复编译。连接打开时统一应用基准 PRAGMA：`foreign_keys=ON`、`trusted_schema=OFF`、`journal_mode=WAL`、`synchronous=FULL`、`wal_autocheckpoint=0`、`auto_vacuum=INCREMENTAL`、`busy_timeout=5000`、`temp_store=MEMORY`、`cache_size=-16384`。常规读写直接运行在已打开句柄之上，不执行每请求目录/inode 文件系统 syscall，也不获取跨进程 `maintenance.lock`；仅开库、周期维护（checkpoint / incremental_vacuum）与在线备份执行目录检查及维护锁协调。数据库关闭先拒绝新操作并排空已接纳事务，然后释放连接；不在关闭时执行 checkpoint／vacuum。SQLite backup API 生成一致全库快照；本地 SQLite 不等同于服务器集群或跨设备多主高可用。

缓存按所属表／记录策略失效，不因图片或 metadata cache 失败删除主数据库。图片、音频、源码等原始 bytes 不进入数据库（结构化歌词与手工歌词直接以强类型关系表保存在 SQLite）。`resources-v1` 是资源文件目录，不是第二份结构化数据库。

JSON 仍允许用于 HTTP/IPC、脚本 API、构建元数据、源码与锁文件，以及用户主动导入／导出的交换文件。第三方脚本自建 private volume 由其脚本和现有宿主文件 capability 管理，应用不按 `.json` 扩展名禁止或接管。仅禁止应用自身内部运行期业务／缓存 JSON 文档直接落盘或整文档 JSON 入数据库。

## 清除本机数据

显式重置在所有 owner、Node runtime 和受理操作真实关闭后，清除当前应用拥有的数据根，再建立新的当前格式数据库。该流程不是格式迁移或错误自愈；不探测、读取、转换、回退或自动删除旧存储路径，不触碰应用根之外的用户文件、第三方未纳管数据或其它产品数据。
