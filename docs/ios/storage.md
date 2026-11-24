# 本地存储

iOS App 的内部业务数据和结构化缓存物理分离为 Application Support 下 `meowfilm/storage-v1/` 内的两个 SQLite 数据库（格式版本均固定为 `user_version = 1`）：

- **永久数据与运行态库 `meowfilm/storage-v1/meowfilm-data.sqlite3`**（`application_id = 0x4D464442`、`synchronous = FULL`）
- **可重建缓存库 `meowfilm/storage-v1/meowfilm-cache.sqlite3`**（`application_id = 0x4D464348`、`synchronous = NORMAL`）

`MeowfilmResources` 打开双库（`SQLiteDatabase.openData` 与 `SQLiteDatabase.openCache`）后，将对应角色的 `SQLiteDatabase` 显式交给各原生 owner；受信 Node runtime（`apps/ios/runtime/bootstrap.mjs` 显式传入 `target: 'ios'`）通过 App 私密启动描述符和原生异步 SQLite binding（`packages/script-host/native/sqlite-storage.cc`）打开同一对路径。两端使用各自的平台 SQLite driver，但共享同一对 iOS schema SQL、identity 和数据根。schema 不匹配、损坏或不支持的当前格式会阻止启动，不会探测、迁移、兼容或删除旧数据。

## 数据模型与 owner

iOS 专属双库关系模式按数据库角色与职责分层位于 `packages/storage/schema/{data,cache}/`，由 `python3 -I -B scripts/storage/generate-schema.py` 合并生成并镜像至 `native/MeowKit/Sources/MeowStorage/Resources/`：

- `packages/storage/generated/schema-data-ios.sql` → `storage-data-schema.sql`（129 张 `STRICT` 表、44 个索引）
- `packages/storage/generated/schema-cache-ios.sql` → `storage-cache-schema.sql`（26 张 `STRICT` 表、13 个索引）

两份产物均不包含任何 `windows_*` 表，无 `shape_mask` / `null_mask`，所有时间点列统一为 `INTEGER` UTC 毫秒（`*_at_ms`），所有外键子表均配有非部分覆盖索引，且无跨库外键。所有内部业务持久化均由各仓库手写强类型 SQL ↔ 强类型 Swift 模型直接映射，通过位置式 `SQLiteCursor` 零字典分配解码，全链路零 `MJ` / 零 `JSONEncoder`/`JSONDecoder` / 零 `RuntimeValue` 数据库往返、禁止 `SELECT *` 与 `SQLiteRow`、禁止写入前整表回读再 Diff 或非清空路径整表重插；仅外部第三方脚本／上游动态扩展字段使用有界 typed value tree（`SQLiteValueTree`）。

目录职责划分：
- `native/MeowKit/Sources/MeowStorage/`：`Engine/`（`SQLiteDatabase.swift`，支持 `.data` / `.cache` 双角色、零分配 `SQLiteCursor`、纯整数 `owner_revisions` CAS 与 `epochMillis` / `dateFromMillis` 辅助）、`Schema/`（`SQLiteSchema.swift`）、`Files/`（原始资源与文件读取）、`Values/`（`SQLiteValueTree.swift`）、`Debug/`（`SQLiteDebugQuery.swift`）。
- `native/MeowKit/Sources/MeowMusicNativeService/`：`Model/`（`MusicNativeModel`、`MusicNativeDataCleaning` 与外部 wire 边界编解码 `MusicWireCodec`）、`Service/`（`NativeMusicService`，全链路纯强类型 `MusicDomainState`，零 `MJ` / 零整区文档翻译壳）、`Persistence/`（定点强类型 SQL 仓储：`Core/` 含 `MusicDomainModels`、`MusicNativeLibraryDatabase`、`MusicPlayContextStore`、`MusicCatalogSourceRepository`、`MusicPlaybackSettingsRepository`（含 iOS 专属表 `ios_music_feedback_settings`、`ios_music_playback_state`、`ios_music_recording_exclusions`、`ios_music_fm_selections`）；`Songs/` `MusicSongRepository`；`Library/` `MusicLibraryRepository` & `MusicSyncRepository`；`Queue/` `MusicQueueRepository`（含 `ios_music_queue_fm_state`）；`History/` `MusicHistoryRepository`；`Discovery/` 接入 `cache` 库；`Home/` 接入 `cache` 库）。
- 各业务模块数据仓库独立收敛至强类型 `*Repository.swift` / `*Store.swift`（`FilmSettingsRepository`、`FilmFavoriteRepository`、`FilmSourceRepository`、`FilmPlayerPreferencesRepository`、`MusicAppearanceRepository`（外观设置入 `data` 库、歌曲调色板入 `cache` 库）、`MusicChannelSelectionRepository`、`MusicSearchPreferencesRepository`、`NovelLibraryRepository`、`PlaybackVolumeRepository`、`SearchHistoryRepository`），ViewModel / Store 不内联原始 SQL。

| 数据 | 目标库 | 唯一 owner 与存储 |
| --- | --- | --- |
| UI 外观、音量、搜索、播放器与影视偏好 | `data` | 对应原生设置 owner 写永久库共享表（`music_appearance` / `ios_music_appearance_extras`、`playback_volume`、`music_search_*`、`ios_film_player_preferences`）；不使用 UserDefaults JSON 持久化 |
| 影视规则、猫源设置、TMDB／豆瓣设置、网盘账号 | `data` | `FilmSettingsRepository` 与 `PanSettingsStore` 直接提交 typed rows 与 revision；受信 Node 只经 `MeowfilmNativeSettings.access` 投影获得所需设置能力，在线网盘登录／刷新以 provider revision CAS 回写同一账号行 |
| 小说书架、进度、变量、阅读设置、净化规则与影视收藏／影视源 | `data` | iOS 原生业务 owner 写永久库共享关系表（`novel_*`、`film_favorites*`、`film_source_*`、`film_video_*`） |
| 音乐账号、歌曲、来源、上下文、队列、历史、播放反馈设置与出站箱 | `data` | 音乐原生 owner 写永久库音乐关系表（`music_songs`、`music_play_contexts`、`ios_music_queue_fm_state`、`ios_music_feedback_settings`、`ios_music_*`）；Node 执行外部配方，不持久化第二份账号或歌曲事实 |
| 歌曲调色板、图片索引、在线歌词、高潮缓存、音频文件/URL 缓存、来源发现与首页快照 | `cache` | 对应缓存 owner 写缓存库（`cached_images`、`cached_music_lyrics*`（共享 `cached_music_lyric_lines` 含 `romanization` 列）、`cached_music_chorus`、`ios_music_song_palettes`、`ios_music_audio_*`、`ios_music_source_discovery*`、`ios_music_home_*`） |
| Cat/source/music 脚本安装事实、私有 KV、来源 Cookie 与影视播放历史/匹配/屏蔽 | `data` | 对应 Node owner 写永久库专属关系表；App 注入原生设置时，Node 不另存该配置 |
| TMDB／豆瓣元数据缓存 | `cache` | Node metadata cache owner 写缓存库 `metadata_cache_*` |
| 真实脚本源码、原始图片／音频等大文件 | 文件目录 | 保留各自 owner 的原始文件或流式资源目录；SQLite 只保存索引、身份和状态 |
| 用户主动导入／导出的交换文件 | 外部文件 | 由用户选择的文件流程处理；JSON 是允许的外部交换格式，不是内部数据库 payload |
| 第三方脚本私有卷 | 外部文件 | 由脚本及宿主现有文件 capability 管理；格式由第三方决定，应用不扫描或禁止其自管 JSON |

原生设置提交和运行器应用是两个独立结果。设置页写入成功不等待网络、脚本装载或运行器通知；正在运行的 owner 收到不携带值的变更通知后，重新读取永久库中的最新行。SQLite WAL 事务串行化物理写入并天然保证原子性；业务 stale-write 通过纯整数 `owner_revisions` CAS 与具体行 revision 判定，不以整库内存快照覆盖其他 writer。受信 capability 限制可写 owner 和字段，但 SQLite 本身不提供表级应用 ACL。

凭据与所属业务同库、同事务，以明文 typed fields 保存于 `meowfilm-data.sqlite3`。当前设计不使用 Keychain 凭据副本、应用层加密、密钥文件或解锁口令。系统沙盒权限、账号 owner 授权、撤销、HTTPS 与日志脱敏仍生效。

## 事务、性能与生命周期

`SQLiteDatabase` 与 `packages/script-host/native/sqlite-storage.cc` 执行相同的 PRAGMA 基准：`busy_timeout=5000`、`trusted_schema=OFF`、`foreign_keys=ON`、`journal_mode=WAL`、`synchronous=FULL`（`data` 库）/`NORMAL`（`cache` 库）、`wal_autocheckpoint=0`、`auto_vacuum=INCREMENTAL`、`temp_store=MEMORY`、`cache_size=-16384`，并通过 `application_id`、`user_version = 1` 与 `storage_identity.schema_hash` 核对 schema 身份。错误分类将 `SQLITE_CONSTRAINT (19)` 统一映射为 `.constraint` / `STORAGE_CONSTRAINT`。

- **WAL 读写分离**：`SQLiteDatabase` 与 `packages/script-host/native/sqlite-storage.cc` 均采用真正双连接 WAL 读写分离：串行写线程持有读写连接处理写事务与维护；独立只读线程持有只读连接（`SQLITE_OPEN_READONLY` + `PRAGMA query_only=ON`）处理并发 `query`/`snapshot`，不与写事务互斥；写事务内重入只读调用自动复用当前写连接以保持读己所写。
- **Prepared Statement LRU 缓存**：`SQLiteDatabase` 的读写连接及 `sqlite-storage.cc` 均维护有界 Prepared Statement LRU 缓存（上限 256 条、SQL ≤ 4096 字节、跳过 `PRAGMA`），复用句柄并在连接关闭前统一 `sqlite3_finalize`。
- **单进程沙箱简化边界**：iOS 原生 `SQLiteDatabase` 与 `RawResourceDirectory` 依托 iOS App 沙箱单进程隔离与 SQLite WAL / `busy_timeout=5000`，不再执行宿主级的 `O_NOFOLLOW` / `chmod 0o600` / inode / `st_nlink` / `flock` 锁文件与手工写队列字节配额（`ownedBytes`）；每 64 次提交执行一次 `wal_checkpoint(PASSIVE)` 与增量 vacuum。
- **生命周期**：短事务只读取和更改对应实体及关系；无变化更新不增加 WAL。关闭先拒绝新任务、排空已受理事务、释放全部缓存语句并关闭双库连接，不在关闭时执行 checkpoint／vacuum。

图片、音频和脚本源码字节不塞入 SQLite（在线歌词行/词保存在缓存库 `cached_music_lyrics*`）。原始资源目录（`resources-v1/`，含 `music-images`、`music-audio`）与应用统一临时目录（`<dataRoot>/tmp`，承载导入导出临时文件并在每次冷启动自动清扫）均标记 `isExcludedFromBackup = true`（不进入 iCloud 备份）。缓存库故障不能触发删除或重建永久数据库。WAL 是单机本地事务机制，不表示跨设备多主或服务器集群高可用。

JSON 仍用于 HTTP、运行器私密 IPC、脚本 API、源码／构建元数据及用户主动交换文件。只有应用自身运行期内部持久化禁止 JSON 文档落盘或完整 JSON 文档入库；不按 `.json` 扩展名封禁三方脚本自管文件、测试／构建材料或用户导出。

## 本机数据清除

显式清除先持久记录重置请求、立即发起本代业务和运行器停止并显示需重新打开应用，不等待后台释放才返回操作；实际删除仅在下一次冷启动、任何数据库或 Node owner 创建前执行，不在前台恢复时删除。删除覆盖应用自有数据根（含其内脚本私有卷），不会调用云端删除或触碰外部文件及其他应用数据。清除不是数据库格式不匹配时的恢复方式；损坏或旧格式不会被自动当作空库删除。

系统强制终止不保证发送或等待 `willTerminateNotification`。已确认的保存依赖运行期间的 COMMIT；终止通知仅停止音乐，不承担全量异步保存和资源排空。

端侧共同边界见[共享 SQLite 契约](../common/settings-storage.md)，运行器 RPC 及能力边界见[运行器入口](runtime-preflight.md)。
