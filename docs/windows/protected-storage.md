# Windows 本地存储

Windows App 在 `%LOCALAPPDATA%/meowfilm/aggregate/storage-v1/` 下物理分离为两个内部 SQLite 数据库（格式版本均固定为 `user_version = 1`）：

- **永久数据与运行态库 `storage-v1/meowfilm-data.sqlite3`**（`application_id = 0x4D464442`、`synchronous = FULL`、`data-initialize.lock` / `data-maintenance.lock`）
- **可重建缓存库 `storage-v1/meowfilm-cache.sqlite3`**（`application_id = 0x4D464348`、`synchronous = NORMAL`、`cache-initialize.lock` / `cache-maintenance.lock`）

原生 App owner 按数据生命周期分别借用对应的 `NativeDatabase`（`DatabaseRole.Data` 或 `DatabaseRole.Cache`）；随 App 启动的 Node Worker 通过受限私密启动管道，以相同数据根和平台 SQLite driver 打开同一对路径。两端共享 schema identity、事务和持久事实，不再有业务 JSON 文档库或单独凭据文件库。

Windows 平台表定义按数据库角色与域组织在 `packages/storage/schema/{data,cache}/`，由 `scripts/storage/generate-schema.py` 生成两份独立产物并嵌入 `MeowFilm.Platform.csproj`：

- `packages/storage/generated/schema-data-windows.sql`（125 张 `STRICT` 表、43 个索引，嵌入逻辑名 `MeowFilm.Storage.data-schema.sql`）
- `packages/storage/generated/schema-cache-windows.sql`（30 张 `STRICT` 表、22 个索引，嵌入逻辑名 `MeowFilm.Storage.cache-schema.sql`）

两份产物均不含任何 `ios_*` 表，同时由 Windows 聚合 Node runtime 以 `target: 'windows'` 校验同一对 schema identity。所有表均为手写强类型 `STRICT` 关系表，时间点列统一为 `INTEGER` UTC 毫秒（`*_at_ms`），零 `shape_mask`/`null_mask`/泛化内部扩展槽；仅对第三方来源/规则定义等真正动态结构使用纯强类型 `StorageValue` + `TypedValueNodes` 有界节点表。当前 schema identity／版本／hash 不匹配、损坏或孤立 journal 失败关闭，不执行升级或旧数据转换。

## 目录归属

- `MeowFilm.Platform/Storage/Engine/`：`DatabaseRole`、`IStorageDatabase`、`NativeDatabase`、`DatabaseSession`、`PreparedStatementCache`、`StorageValue`、`TypedValueNodes`、`SerialOperationQueue`、`StorageError.cs`（`StorageError`／`StorageException`；`SQLITE_CONSTRAINT` 统一映射 `Constraint`，准入上限统一 `128 / 32 MiB`）
- `MeowFilm.Platform/Storage/Schema/`：`StorageSchema`（双角色嵌入资源加载与 schema identity/hash 校验）
- `MeowFilm.Platform/Storage/Files/`：`DatabaseDirectory`、`FileResourceCache`、`RawResourceDirectory`、`UserDataDirectory`、`WindowsStorageFiles`
- `MeowFilm.Capabilities/<Domain>/Storage/`：各业务域持久化仓库直接执行强类型 SQL ↔ 强类型领域/存储记录（`Film/Storage/`、`Pan/Storage/`、`Novel/Storage/`、`Accounts/Storage/`、`Feedback/Storage/`、`Recommendations/Storage/`、`Catalog/Storage/`、`PlaybackCache/Storage/`、`Library/Storage/`），全链路采用纯强类型 C# 领域模型（`MusicLibrarySession` / `MusicWireCodec` / `MusicLibraryRules` 等），存储层内零 `System.Text.Json` / `JsonObject` / `JsonNode` / `LJ` / 写入前整表回读 Diff 或非清空路径整表重插。

## 数据 owner

| 数据 | 目标库 | 写入 owner |
| --- | --- | --- |
| Windows 外观、窗口、关闭、媒体控制、字幕、语言等 App 设置 | `data` | 对应原生 AppHost owner，写永久库 |
| 影视规则、来源意图、TMDB／豆瓣设置、小说来源配置、网盘凭据 | `data` | 原生领域 owner 写 typed rows；Node 只获得 capability 指定的读取或 provider 行回写 |
| TMDB／豆瓣 runtime metadata cache | `cache` | Node metadata cache owner 写缓存库 `metadata_cache_*` |
| 音乐账号与凭据、曲库、队列、历史、收藏、排除、同步、手工本地歌词、播放反馈设置与出站箱、FM 偏好 | `data` | Windows 原生音乐 owner 写永久库；Node 执行协议请求但不另存账号、曲库或第二事实源 |
| 音乐展示图片索引、在线歌词缓存、高潮缓存、音频文件/取址缓存、源匹配缓存、账号详情缓存与首页展示快照 | `cache` | Windows 原生音乐缓存 owner 写缓存库（`cached_images`、`cached_music_lyrics*`、`cached_music_chorus`、`windows_music_*`） |
| 小说书架、阅读位置、变量、阅读设置与净化规则 | `data` | Windows 原生 Novel owner 写永久库共享 `novel_*` 表；Node runtime 管理被授权的来源执行状态，不复制原生书架 |
| Cat/source/music 脚本安装、来源 Cookie、脚本 KV 与影视播放历史/匹配/屏蔽 | `data` | Node owner 写永久库对应关系表；源码文件仍留在对应 runtime 源码目录 |
| 原始图片、音频和其他二进制资源 | 文件目录 | 各资源 owner 使用 `resources-v1` 下的文件目录；SQLite 仅保存强类型身份、索引、时间和状态 |
| 用户显式导入／导出 JSON 与第三方脚本 private volume | 外部文件 | 外部交换或脚本自有存储，不属于应用内部 JSON 持久化禁令 |

原生 UI 可以直接写库，不依赖运行器在线。Node 登录／刷新网盘凭据等受信写入使用 provider revision CAS；设置成功通知只传块标识，不带凭据或完整设置值。SQLite 串行化物理事务，纯整数 `owner_revisions` CAS 与具体行 revision 解决 stale-write；不以共享内存快照回写覆盖另一 writer。私密管道限制 owner 和操作，但不是 SQLite 表级安全 ACL。

凭据字段与所属业务行同库同事务、纯明文保存。不使用 DPAPI、Keychain、数据库加密、应用层字段加密、密钥文件或额外解锁口令。保留用户私有目录隔离、owner 授权、撤销、HTTPS、错误封闭与日志脱敏。

## 事务与可靠性

`NativeDatabase` 采用 WAL 读写分离连接模型：写事务在单一 writer lane 串行执行，只读查询路由至独立只读连接（`ReadOnly` + `PRAGMA query_only=ON`），并内置有界 `PreparedStatementCache`（LRU 256）避免热点 SQL 重复编译。写连接打开时统一应用并核对基准 PRAGMA：`foreign_keys=ON`、`trusted_schema=OFF`、`journal_mode=WAL`、`synchronous=FULL`（`data` 库）/`NORMAL`（`cache` 库）、`wal_autocheckpoint=0`、`auto_vacuum=INCREMENTAL`、`busy_timeout=5000`、`temp_store=MEMORY`、`cache_size=-16384`，并通过 `application_id`、`user_version = 1` 与 `storage_identity.schema_hash` 核对 schema 身份；只读连接配置 `busy_timeout=5000`、`foreign_keys=ON`、`trusted_schema=OFF`、`temp_store=MEMORY`、`cache_size=-16384`、`query_only=ON`，核对 `journal_mode=wal`、`auto_vacuum=2`、`query_only=1`。常规读写直接运行在已打开句柄之上，不执行每请求目录/文件 syscall，也不获取跨进程维护锁；仅开库与周期维护（checkpoint / incremental_vacuum）通过角色独立维护锁（`data-maintenance.lock` / `cache-maintenance.lock`）协调。数据库关闭先拒绝新操作并排空已接纳事务，然后释放连接；不在关闭时执行 checkpoint／vacuum。本地 SQLite 不等同于服务器集群或跨设备多主高可用。

缓存按所属表／记录策略失效，`meowfilm-cache.sqlite3` 故障不破坏或删除永久库 `meowfilm-data.sqlite3`。图片、音频、源码等原始 bytes 不进入数据库（在线展示歌词保存在缓存库 `cached_music_lyrics*`，手工本地歌词保存在永久库 `windows_music_local_lyrics*`）。`resources-v1` 是资源文件目录，不是第二份结构化数据库；所有运行期临时文件（含小说 WebView2 临时 profile）统一落在 `<dataRoot>/tmp` 并在启动时自动清扫。正常退出统一走 `AppShutdown` 并受 5 秒排空超时保护（超时强杀残留 Node 子进程并记录告警）。

JSON 仍允许用于 HTTP/IPC、脚本 API、构建元数据、源码与锁文件，以及用户主动导入／导出的交换文件。第三方脚本自建 private volume 由其脚本和现有宿主文件 capability 管理，应用不按 `.json` 扩展名禁止或接管。仅禁止应用自身内部运行期业务／缓存 JSON 文档直接落盘或整文档 JSON 入数据库。

## 清除本机数据

显式重置在所有 owner、Node runtime 和受理操作真实关闭后，清除当前应用拥有的数据根，再建立新的当前格式双库。该流程不是格式迁移或错误自愈；不探测、读取、转换、回退或自动删除旧存储路径，不触碰应用根之外的用户文件、第三方未纳管数据或其它产品数据。
