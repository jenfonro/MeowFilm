# 共享 SQLite 存储契约

每个 App 数据根在 `storage-v1/` 下物理分离为两个职责清晰的在线 SQLite 数据库（格式版本均固定为 `user_version = 1`）：

| 数据库角色 | 文件路径 | `application_id` | `synchronous` | 初始化锁 / 维护锁 | 在线备份 | 承载内容 |
| --- | --- | --- | --- | --- | --- | --- |
| **Data（永久数据与运行态）** | `storage-v1/meowfilm-data.sqlite3` | `0x4D464442`（`MFDB`） | `FULL`（`2`） | `data-initialize.lock` / `data-maintenance.lock` | **支持**（唯一备份对象） | 用户设置、账号凭据、收藏、历史、书架／进度、净化规则、音乐永久歌曲库／歌单／队列／历史／排除／同步／本地歌词／播放反馈设置、脚本安装与私有状态 |
| **Cache（可重建缓存）** | `storage-v1/meowfilm-cache.sqlite3` | `0x4D464348`（`MFCH`） | `NORMAL`（`1`） | `cache-initialize.lock` / `cache-maintenance.lock` | **明确拒绝**（`INVALID_REQUEST`） | TMDB／豆瓣元数据缓存、图片缓存索引、在线歌词缓存、高潮副歌缓存、音频文件与取址缓存、源匹配／源发现缓存、调色板缓存、账号详情缓存、首页展示快照 |

iOS 原生客户端、Windows 原生客户端及同一 App 内的 Node 运行器按各自 owner 职责连接这两个库；iOS 和 Windows 各自使用本机数据根，不通过 SQLite 文件跨设备共享。

## 分角色分平台 Schema 组织与产物隔离

- Schema 源码按数据库角色与真实归属拆分：
  - `packages/storage/schema/data/{core,runtime,shared,music,windows,ios}/`
  - `packages/storage/schema/cache/{core,runtime,shared,windows,ios}/`
  严禁将双端表混装到同一份产物中，也严禁在 `cache` 库建立指向 `data` 库的跨库外键。
- 唯一生成与静态校验入口为 `scripts/storage/generate-schema.py`（支持 `--check` 门禁，在内存 SQLite 中验证可执行性、外键合法性、全表 `STRICT`、100% `snake_case` 命名、全部外键子列被非部分索引前缀覆盖、零冗余前导重复显式索引、零跨平台表污染、零 `shape_mask`/`null_mask`/位掩码列、零非 `INTEGER` 时间点列、零冗余存在位与幽灵/伪共享列及表、零死表与零非规范化 `ctx_*` 复制列）。
- 固定输出六份独立产物（并同步两份 iOS 镜像）：
  - `packages/storage/generated/schema-data-standalone.sql`（48 表 / 9 索引）、`schema-cache-standalone.sql`（5 表 / 2 索引）（`core + runtime`）：独立 Node 运行器与验收工具使用。
  - `packages/storage/generated/schema-data-windows.sql`（125 表 / 43 索引）、`schema-cache-windows.sql`（32 表 / 22 索引）（`core + runtime + shared + music + windows`）：Windows 原生客户端嵌入资源与 Windows Node 运行器校验使用，不得包含任何 `ios_*` 表。
  - `packages/storage/generated/schema-data-ios.sql`（128 表 / 44 索引）、`schema-cache-ios.sql`（26 表 / 12 索引）（`core + runtime + shared + music + ios`，同步镜像至 `native/MeowKit/Sources/MeowStorage/Resources/storage-data-schema.sql` 与 `storage-cache-schema.sql`）：iOS 原生客户端与 iOS Node 运行器校验使用，不得包含任何 `windows_*` 表。
- `@meowfilm/storage`（目录按职责分为 `packages/storage/src/{engine,schema,query,values}/`）通过 `configureStorage({ driver, initialize, target })` 显式选定 `standalone | windows | ios` 目标 schema，并按 `openDatabase(dataRoot, { role: 'data' | 'cache' })` 分别打开与校验双库；任一库的根 schema identity（`application_id`、`user_version = 1`、`storage_identity.schema_hash` 与 `sqlite_schema` 结构）不匹配即启动失败，不执行数据库升级。

## 存储格式与边界

- 开发期数据库 `user_version`、内部业务记录的 `schema` 及同义格式 `version` 统一固定为 `1`，不因功能开发递增，不维护历史格式或迁移链。表定义只保留当前模型；固定版本号不代替产品身份、实际字段与关系约束的校验。并发控制的 revision／generation／队列序号，以及第三方协议或工具产物版本，不属于存储格式版本。
- 应用内部业务记录和结构化缓存必须使用手写、规范化的 SQLite `STRICT` 关系表、typed columns、外键、唯一约束、索引及有序子关系；所有持久化时间点列统一使用 `INTEGER` UTC 毫秒（`*_at_ms`）；音乐队列／历史／相似推荐共用内容寻址表 `music_play_contexts(context_id TEXT PRIMARY KEY)` 消除重复列；跨端同域表（影视源与收藏、小说书架／阅读设置、音量、音乐歌曲／队列／历史／上下文、搜索策略、播放设置与音源排除、图片／歌词／高潮复合主键缓存）仅保留双端真实共同读写的列，单端特有字段及语义差异表严格落在对应平台专属表／子表（如 `{windows,ios}_novel_reading_background`、`windows_novel_book_extras`、`windows_film_favorite_display`、`ios_film_favorite_identity`、`windows_music_song_local_files`、`{windows,ios}_music_play_context_extras`、`windows_music_{queue,history_playlist}_source_kind`、`ios_music_{queue,history_playlist}_extras`、`windows_music_search_history`、`ios_search_history`、`ios_music_search_policy_extras`、`windows_music_feedback_platforms`、`ios_music_feedback_settings`、`ios_music_playback_state`、`ios_music_recording_exclusions`、`ios_music_fm_selections`、`windows_cached_music_lyric_{line,word}_extras`）。
- 严禁整份业务对象序列化为 TEXT/BLOB 保存，也严禁使用伪关系 JSON-AST 投影框架与整区文档翻译壳（禁止 `shape_mask`／`null_mask`、禁止由 JSON 往返生成的 `*StorageRecords`／`MusicPersistenceAdapter`／`MusicLibraryProjection` 胶水、禁止原生业务与存储层保留 `MJ`/`LJ`/`JsonNode`/`JsonObject`/`JsonElement`/`JSONEncoder`/`RuntimeValue` 整区文档中转、禁止写入前整表回读再 Diff 或非清空路径整表 `DELETE FROM` 重插、禁止给内部业务表加泛化 `*_extensions` 槽位）。
- 有界 typed value-tree relations 仅允许用于极少数真正动态的第三方脚本或外部规则／元数据扩展（如 LX 脚本 KV／声明扩展、Reader 书源规则／变量、Cat 配置、TMDB／豆瓣未知扩展字段、第三方音乐源未知元数据字段、导入小说规则扩展）。
- JSON 仅作为网络、Node/原生私密 IPC、脚本 API、构建元数据与用户明确导入／导出的交换格式；编码或解码 JSON 以形成 API 快照时不表示数据库储存 JSON 文档。
- 第三方脚本自有 private volume 由脚本及宿主文件 capability 按第三方约定管理，不由应用把 JSON 文件禁令扩展到第三方格式，也不以扩展名过滤其内容。用户选择导出的 JSON 文件为正常外部交换文件。
- 真实源代码、媒体、图片及其他不可变/流式二进制资源按所属资源 owner 使用文件；数据库保存强类型索引和身份，不将大块媒体嵌入 JSON。
- 应用持有的 Cookie、Token、密码及其他可恢复凭据以普通明文 typed fields 存放在 `meowfilm-data.sqlite3` 的所属业务表，与记录 revision 同一事务提交。无 Keychain/DPAPI 凭据副本、应用加密层、本地密钥文件或额外解锁口令。系统沙箱、文件权限、账号授权隔离、撤销及日志脱敏仍然生效。

## Owner 与并发写入

| 数据类别 | 目标库 | 唯一业务 owner | 可写参与方与协作 |
| --- | --- | --- | --- |
| 通用和平台设置 | `data` | 端侧原生设置 owner | UI 直接写 `data` 库；Node 只取得 capability 授予的块读取/有限字段更新。设置事务成功后可通知当前运行器重新读取，通知不承载设置值 |
| TMDB／豆瓣设置与元数据缓存 | 设置→`data`；缓存→`cache` | 设置归端侧或独立运行器 owner；缓存归 Node metadata owner | App 注入模式由客户端写 `data` 库设置表，运行器读取；运行时元数据缓存由运行器 cache owner 写 `cache` 库 `metadata_cache_*` |
| 网盘凭据 | `data` | 原生 Pan credential owner | UI 原生保存，Node 登录/刷新后经 provider 单项 revision CAS 回写。换号、撤销与自动刷新在对应业务事务中校验账号及行 revision |
| 音乐账号凭据与详情缓存 | 凭据→`data`；详情缓存→`cache` | 原生账号 owner | Node 执行外部认证/刷新配方但不另存账号；原生 owner 对同一 SQLite 行提交并发布授权 lease。外部 JSON 账号导出仍由用户显式触发 |
| 运行器脚本安装/私有数据 | `data` | 每个脚本/runtime owner | 运行器持有安装关系、每键 typed KV 与源码身份；宿主 private volume 文件 API 只代表第三方脚本自管文件，不等于其业务 JSON 成为数据库 payload |
| 音乐/影视/小说持久业务 | `data` | 对应平台业务 owner | 共享 SQLite 强类型事务入口，不保留 JSON-AST 投影或整表回读 Diff |
| 可重建展示/媒体/取址/首页缓存 | `cache` | 对应平台缓存 owner | 独立写 `meowfilm-cache.sqlite3`，按表大小/TTL/owner policy 回收，不与永久业务争抢写锁或检查点，不另设文件 JSON cache store |

SQLite 写事务序列化物理修改；跨 owner 的 stale-write 防护由 `owner_revisions(owner TEXT PRIMARY KEY NOT NULL, revision INTEGER NOT NULL CHECK (revision >= 1))` 纯整数 CAS 与具体行 revision 实现。不得用全库一个共享内存快照覆盖其他 writer；禁止跨文件双写和把两个 owner 合并成一个无边界 writer。仅已授权 owner 可获得对应 repository/capability；这是应用层权限，不声称 SQLite 表级 ACL。

## 事务、耐久与故障

- 统一连接 PRAGMA 基准：`foreign_keys=ON`、`trusted_schema=OFF`、`journal_mode=WAL`、`wal_autocheckpoint=0`、`auto_vacuum=INCREMENTAL`、`busy_timeout=5000`、`temp_store=MEMORY`、`cache_size=-16384`；其中 `data` 库固定 `synchronous=FULL`（`2`），`cache` 库固定 `synchronous=NORMAL`（`1`）。SQLite 是本机事务型数据库；WAL 不意味着多主高可用或跨设备集群。
- **错误分类与准入**：Windows、iOS、Node JS 与 Node C++ 引擎统一将 `SQLITE_CONSTRAINT (19)` 映射为 `Constraint` / `STORAGE_CONSTRAINT`（与并发版本冲突 `Conflict` / `STORAGE_CONFLICT` 分离）；Windows 与 Node 维持 `maxQueue = 128`、`maxBytes = 32 MiB` 写入准入上限，iOS 原生单进程沙箱不再叠加手工写队列字节配额。
- **热路径执行边界**：
  - 常规读写查询与事务直接运行在已打开连接、有界 Prepared Statement 缓存（LRU 256）和 SQLite WAL + `busy_timeout=5000` 之上；严禁在每次普通查询/写入前重复执行目录 `realpath`、文件 `stat`/`inode` 检查或获取跨进程维护锁。
  - Windows / Node 侧仅在开库、维护和在线备份时执行文件边界与维护锁检查；iOS 原生单进程沙箱直接依赖容器隔离与 SQLite WAL / `busy_timeout=5000`，不再维护额外的 `.lock` 文件或 inode 校验。
  - 端侧原生数据库引擎（Windows / iOS）与 Node 公共运行器均采用 WAL 读写分离：只读查询走独立只读连接（`ReadOnly` + `PRAGMA query_only=ON`，单语句读无事务开销，多表读使用 `ReadSnapshot` 显式只读快照），写事务仍在单一写 lane 串行提交，避免长写事务阻塞首页、搜索和播放读取。
- 普通打开只处理当前格式身份、真实结构、连接与文件边界，不执行全库存量外键数据审计；外键约束在写入时生效，所选关系在实际读取时解析。原生与附属运行器不重复审计全库业务内容，见[拒绝条件与数据消费边界](validation-boundaries.md)。显式整库备份的结果检查由备份 owner 承担。
- 短事务保持同步一致性；单个用户动作涉及的字段、子关系、缓存索引和 revision 一并提交。UI/Node 之外的长计算或网络等待不得持有写事务。请求按读取窗口和查询边界执行，不为单条记录读写强制物化整表。
- 已确认保存以事务 COMMIT 成功为准，不能只表示进入内存队列。进程被强制终止或 iOS 从后台被清除后，已提交事务由 SQLite 的正常 WAL 恢复保留，未提交事务不成为半份业务记录；正常退出、终止回调、checkpoint 或 vacuum 均不是已提交数据耐久的前提。播放／阅读进度等高频状态可以按业务定义合并保存，但尚未提交的最近变化不承诺在强制终止后保留。SQLite 事务恢复不属于旧 schema／旧数据迁移。
- 数据库写操作有界准入与内存预算；已受理事务返回真实提交结果，取消不谎报回滚；关闭先拒绝新任务、等待已受理操作完成并释放连接，不额外调度 checkpoint／vacuum 或等待维护锁；应用维护仅在运行中按阈值尝试。原生 driver 禁用 SQLite 关闭时 checkpoint；桌面 Node driver 的连接关闭由 `node:sqlite` 管理，不把其内部行为当作应用保存回执。共享写入口在 iOS SQLite queue 和 Windows/Node worker lane 上串行，跨进程维护使用角色独立文件互斥。
- 初次新建只允许当前固定 schema。空新路径可以初始化；已有空文件、身份/版本/hash/结构不符、损坏或孤立 WAL/journal 失败关闭，保留现场。不探测旧 JSON、兼容旧 DB、迁移、双写、回退或自动删除已有用户数据。
- 空间不足、只读、锁竞争、I/O、损坏、并发冲突与约束冲突以稳定错误分类返回；业务记录损坏不得伪装为空配置。缓存可按所属表的明确边界重新生成，且 `meowfilm-cache.sqlite3` 异常不得破坏或删除永久数据库 `meowfilm-data.sqlite3`。
- SQLite 在线 backup API 仅开放给永久数据库 `meowfilm-data.sqlite3`，用于取得单一一致快照；对 `meowfilm-cache.sqlite3` 调用备份明确返回 `INVALID_REQUEST`。恢复必须是相同当前 schema 与产品身份的完整 `data` 库，且在开放业务前按 owner 使可重建缓存失效。不在本契约加入自动备份频率/保留策略。
- 真实性能须用目标设备、正式平台 driver 和代表性数据验证；schema、编译或内存 driver 测试不证明设备 I/O 时延。

端侧数据库结构、driver、启动生命周期与已实测行为分别见 [iOS 存储](../ios/storage.md)、[Windows 本地存储](../windows/protected-storage.md)；运行器所需的配置投影和私密通道见 [Runtime bridge](../runtime/runtime-bridge.md)。

普通开库只核对当前数据库身份、结构与连接配置，不执行全库 `foreign_key_check` 或业务关系扫描；写事务启用外键约束，所选业务关系在实际消费处解析。显式备份与完整性诊断按各自操作核验。
