# 原生音乐状态 owner

集合分类、内容所有权及订阅副作用以[共同歌单契约](../common/music/playlist-ownership.md)为准；本页记录 Windows owner、命令和持久格式。

## 责任

`MeowFilm.Capabilities.Library.MusicLibraryOwner` 是音乐曲库、队列、历史及相关设置的唯一原生业务写入者；脚本安装与私有数据归公共音乐模块。

- App 持有物理分离的永久数据库（`storage-v1/meowfilm-data.sqlite3`）与可重建缓存数据库（`storage-v1/meowfilm-cache.sqlite3`）；`MeowFilm.Capabilities/Music/Library/Storage/` 下的 `MusicLibraryDatabase`（组合 `MusicSongStore`、`MusicPlayContextStore`、`MusicCollectionsStore`、`MusicQueueStore`、`MusicHistoryStore`、`MusicSettingsStore`）配合 `MusicLibrarySession` 直接读写永久库中的 `music_*`、`playback_volume` 与 `windows_music_*` 强类型 `STRICT` 表（全链路纯强类型 C# 领域模型，零 `JsonObject`/`JsonNode`/`LJ` 整区文档持久化读写，仅在外部命令/平台 wire 边界由 `MusicWireCodec` 投影），owner 不释放 App 的数据库。
- 默认 scope 为 `local`。
- 普通业务不经 Node 读写该数据库。
- 同一 App 内不得再装配第二个歌曲 writer；不探测、读取、迁移或删除旧音乐文档。

## 公开边界

```csharp
MusicLibraryOwner(MusicLibraryDatabase store, string scope = "local")

Task<JsonElement> ExecuteAsync(
    string method,
    object? parameters = null,
    CancellationToken cancellationToken = default);

Task<JsonElement> CreatePlaylistAsync(
    string name,
    IReadOnlyList<JsonElement> songs,
    CancellationToken cancellationToken = default);

Task<JsonElement> ReplaceQueueAsync(
    IReadOnlyList<JsonElement> songs,
    int index,
    JsonElement? context,
    long expectedVersion,
    CancellationToken cancellationToken = default);

Task<JsonElement> AppendQueueAsync(
    IReadOnlyList<JsonElement> songs,
    JsonElement? context,
    long expectedVersion,
    CancellationToken cancellationToken = default);

Task ReconcileAsync(CancellationToken cancellationToken = default);
Task PrepareShutdownAsync(CancellationToken cancellationToken = default);
Task AbortAsync(CancellationToken cancellationToken = default);
ValueTask DisposeAsync();
```

业务命令返回 `{ok:true,...}` 或 `{ok:false,code}`。可信的进程内事务端口直接抛出固定 `MusicLibraryException.Code`，不返回存储正文、凭据或内部异常文本。

`AppendQueueAsync` 是完整歌单的进程内 next 入口，返回 `{ok:true,currentId,version,mode}`，不返回全量 entryIds。它在唯一串行边界核对 `expectedVersion`，创建独立 occurrence 并将整段按输入顺序插在当前曲之后，一次 CAS、一次版本递增；当前曲、模式及旧优先队列保留，新段优先于旧 priority。空队列选中首曲且 priority 为空，不调用媒体播放。失败不分批落盘；CAS 准入前取消不写入，准入后报告真实结果，未知写入使用既有 reconcile 机制，不重放。

普通 `music.queue.append` 接受 `songs/context/next`，保持完整 `entryIds` 回执，不使用调用方传入的 `expectedVersion`。普通与完整入口、返回结果、控制 area 和歌曲记录均不设人为字节或歌曲数量上限，空输入不允许。队列当前 schema 为 1，entry 只存 `id/songId/context`，读取时由唯一歌曲仓库投影资料。

## 唯一当前数据库

音乐原生 owner 借用 App 的永久 `NativeDatabase`（`meowfilm-data.sqlite3`），由官方 `Microsoft.Data.Sqlite` 10.0.15／SQLitePCLRaw 2.1.13 的 NuGet 资产提供 SQLite；不单建音乐数据库。当前双库统一 schema 见[共享存储契约](../common/settings-storage.md)。

| 关系 | 内容与索引 |
| --- | --- |
| `owner_revisions` | 以 `music.library:<scope>` 维护纯整数 owner revision；真实变更同事务推进 |
| `music_songs` | 永久 UUID、类型化单曲字段、统一录音匹配键 `match_key`、固定 `baseline_duration`、封面 URL 与精确查询索引（本地文件授权 locator 位于 Windows 专属子表 `windows_music_song_local_files`） |
| `music_song_sources` 及来源资料子关系 | 来源稳定键、song_id 外键、平台／原生 ID／必要变体和顺序；已登记来源不可删除或重绑 |
| `music_play_contexts` | 内容寻址（SHA-256 `context_id`）播放上下文去重表（搭配 Windows 专属子表 `windows_music_play_context_extras`），供队列与历史各表外键引用并按需清理孤立行 |
| 队列、历史、集合及偏好专属关系 | 类型化控制字段、按身份组织的有序成员和引用；不嵌入歌曲实体 |

所有永久库连接使用 WAL／FULL，同库原生和 Node writer 以短事务及纯整数 owner revision CAS 协作。一次提交只更新本轮实际改变的歌曲、来源、控制关系与 revision；路径、维护锁、备份和故障语义见[存储](protected-storage.md)。

普通读先取得 owner revision，再按实际动作读取所选 area／关系／成员；显式诊断导出才展开所有 area。history 列表只读摘要与计数，详情只展开所选分页，恢复校验所选完整 archive 和当前队列，播放记录只合并当天真实字段。

完整覆盖设置只消费旧 schema/revision；准确成员删除按原表键执行。局部写入保留未提供的列与未读关系，不把部分投影当完整 previous 覆写无关表项。所选内容、全量覆盖和局部字段更新在提交中显式区分，仍与同一 owner revision 在同一 CAS 事务提交。

普通歌曲读按 ID／来源键取得相关歌曲；命令的逐记录工作集只保留本轮读写，发布的长期 reader 不缓存全库歌曲。

- 控制与单记录的内存／协议投影保留实际字节和深度预算；数据库使用手写强类型 `STRICT` 固定列及关系表，不为内部设置建泛化扩展槽或保存整份 JSON。owner 状态仍须满足 scope／revision。
- 仅在实际 getter／command 处校验当前动作涉及的 area；未知 area 不获得命令权限，也不阻断其他 area，未修改值原样保留。脚本实例私有存储不在此库。
- 缺少可选 area 表示该 area 尚未创建；已有 `null`、错误类型或错误 schema 不是空状态，在实际消费时明确失败，不写回修复或覆盖为空库。
- 不在加载／每次提交时遍历所有 area 或要求歌曲等于 Normalize 投影。所选歌曲身份、录音唯一 owner、队列引用／current、修订与账号绑定仍须成立；持久化的非法策略不当作用户写入参数返回。整库同步仍核对其实际遍历的集合／绑定／journal 关系。
- 未选坏记录不阻断无关操作；该记录的 UUID、来源键或本地文件身份被再次选中时明确失败，不另建 owner 或重绑来源。封面策略只应用本次集合成员；显式全库策略变更由其消费者读取全部歌曲。
- 本代次已验证且不会删除的歌曲关系无需在小型控制提交时重复查询，新增关系仍核对真实记录；队列加载／导航与引用校验不会因消费边界收窄而取消。

未知提交保留精确 Before／After 回执；只读对账为 After 时结算本次来源观察代次，为 Before 时恢复命令前随机导航及失败状态，不重放原操作。直接歌曲／来源读取与关闭、准备关闭在同一同步读门结算；owner 关闭不依靠释放借用数据库来撤销读取。

当前控制 area：

```text
library
library-sync
queue
history
native-source-exclusions
online-settings
native-catalog-source
native-official-sources
native-quality-policy
native-metadata-policy
native-appearance
native-device-settings
native-search-policy
native-search-history
native-local-lyrics
native-desktop-lyrics
playback-settings
```

共享图片与展示歌词由 `MusicPresentationCache` 维护（图片原字节存于 `resources-v1/music-images` 并在缓存库 `meowfilm-cache.sqlite3` 的 `cached_images` 索引，展示歌词由 `LyricsCacheStore` 直接以强类型记录存于缓存库表 `cached_music_lyrics` / `cached_music_lyric_lines` / `cached_music_lyric_words` 及 Windows 专属子表 `windows_cached_music_lyric_line_extras` / `windows_cached_music_lyric_word_extras`），不属于业务 area，不参与永久歌曲数据库提交；手工歌词仍属于永久库 `native-local-lyrics`。完整 URL 图片键、预算、清理隔离等共同语义见[展示资源](../common/music/presentation-resources.md)，本端接线见[元数据策略](music-metadata-policy.md)。

脚本安装与私有 KV 由[公共音乐模块](../runtime/music-script-runtime.md)唯一持久化。
本 owner 只从空数据建立并读取当前数据库，不检查或承接历史脚本条目。
`native-source-exclusions` 保存 schema 1、revision、items，每项包含 songId/provider/platform/providerName/title/artist/at。
同身份幂等添加，只显式 remove 解除，不随缓存清理删除；不设业务字节或条数上限。

## 在线设置当前格式

`online-settings` 的当前格式为 schema 1，消费以下必需字段：

```json
{
  "schema": 1,
  "revision": 0,
  "lyrics": true
}
```

- 缺少该 area 时，`music.settings.get` 返回上述缺省 `settings`，不制造 area 或写入数据库。
- `music.settings.save` 必须携带 `ifRevision` 和 `lyrics`，未消费请求字段不影响当前动作；成功保存推进一次 revision，旧 revision 返回 `REVISION_CONFLICT`。当前动作不消费全局音源选择，已有同名扩展不赋予该设置音源控制权。
- 已存在该 area 时在实际读取／保存处校验必需字段：schema 必须是数值 1（`1.0`／`1e0` 同值可读），revision 为合法安全整数、lyrics 为布尔值；未消费的额外请求字段不作为拒绝条件，也不持久化扩展槽。未知 schema、重复属性及实际使用的坏字段明确失败，原记录不覆写。
- 读取和重启不写入；保存通过 owner gate 和 generation／receipt CAS 写入实际控制变更。未知提交由显式 reconcile 核对实际结果，不重放写入。
- 在线设置保存未知提交后，后续命令封闭，使用显式 `ReconcileAsync` 读回 Before／After generation／receipt。结算不重放写入，也不替换 pending；第三状态保持冲突。
- 相关公开行为用例位于 `MusicNativeLibraryTests.cs` 和 `MusicOnlineSettingsTests.cs`；执行结果由集中验证记录。

## 浏览数据源设置当前格式

`native-catalog-source` 的当前格式为 schema 1：

```json
{
  "schema": 1,
  "revision": 0,
  "platform": "wy"
}
```

- `music.catalogSource.settings.get` 返回 `settings`；缺少该 area 时返回上述缺省值，读取不写盘。
- `music.catalogSource.settings.save` 消费 `ifRevision` 和 `platform`，未消费请求字段不持久化；平台仅允许 `wy/kg`；revision 不符返回 `REVISION_CONFLICT`，同值保存不写盘，改变平台只推进本设置的 revision。
- 该偏好只控制首页与频道，不改详情资源平台、账号、搜索偏好、音源启停或在线设置；唯一 writer 仍是本 owner。
- 未知 CAS 由本职责的权威 get 核对 Before／After generation／receipt 结算，不重放保存，也不替其他职责结算。测试归属为 `MusicCatalogSourceSettingsTests.cs`。

## 官方音源设置当前格式

`native-official-sources` 的当前格式为 schema 1：

```json
{
  "schema": 1,
  "revision": 0,
  "enabled": ["wy", "tx", "kg"]
}
```

- `enabled` 是 `wy`、`tx`、`kg` 的无重复子集；空数组表示三平台全部关闭。
- 缺少该 area 时，`music.officialSources.settings.get` 返回上述 revision 0、三平台全开的缺省 `settings`，不为读取创建或写入文档。
- `music.officialSources.settings.save` 必须携带 `ifRevision` 和 `enabled`；`ifRevision` 与当前 revision 不符时返回 `REVISION_CONFLICT`，成功保存只推进本设置的 revision。
- 该设置独立于 `online-settings`、`native-quality-policy` 和 `native-metadata-policy`，不复用它们的启停字段或改写音质、歌词／封面设置。
- 持久化覆盖契约与测试归属见 `MusicOfficialSourcesPolicyTests.cs`，包括缺省读取、独立启停与全关、重启恢复及同职责未知 CAS 结算；测试执行结果另行记录。

## 曲库当前格式

Library owner contract 为 `0.7.0`。

wrapper 消费以下必需字段：

```json
{
  "schema": 1,
  "scope": "local",
  "revision": 0,
  "library": {}
}
```

`wrapper.revision` 必须等于 `library.revision`。`library` 消费以下字段：

```text
version ownerId revision nextId collections accounts networkPlaylists
```

约束：

- wrapper schema 与 library `version` 均为 `1`，`ownerId` 精确等于当前 scope；只解码当前控制格式，不提供旧版本转换。
- `nextId` 大于所有现存 `playlist:<正整数>` 的编号。
- 只有一个收藏集合：`id=favorites`、`kind=favorites`；它没有 `deleted` 或 `locallyCreated` 字段。
- 普通歌单 ID 必须为 canonical `playlist:<正整数>`。
- `deleted`、`locallyCreated` 若存在只能为 `true`。
- `order` 无重复，并且每项都引用数据库中当前歌曲。
- `accounts` 只允许 `wy`、`tx`、`kg`，账号 ID 使用当前安全账号发布的非空字符串，不猜测长度或数字格式。
- binding 只允许相同三个平台且 `writable=true`；账号、远端集合和成员定位均保留共同结果的完整非空字符串，`map` 方向为实际成员 key 到永久 songId。账号精确关联、成员 occurrence、当前仓库引用及写事务 CAS/pending 仍由原生同步 owner 保证。
- intent 的 `acknowledged` 只包含该集合中现存 binding 的 canonical binding key。
- `songs.song_id` 为永久 UUID，记录自身 songId 与键一致；来源不能跨记录重绑。临时媒体 URL、凭据和签名资源字段不得进入持久歌曲。
- 集合 order、队列 entry 和历史只持久 songId 引用；展示时读取仓库当前资料。队列 occurrence 与歌曲 ID 分离，资料补全不推进 queue version。

### 网络引用字段与回执

`networkPlaylists` 是独立数组，每项消费以下字段：

```text
id platform remoteId name cover count revision present subscription intent
```

- `id` 固定为 `network:<platform>:<remoteId>`；`platform` 允许 `wy/tx/kg/kw/mg`。`remoteId` 使用共同平台结果中的非空定位字符串，原生不猜测数字格式、长度、字符集或前缀。
- `cover` 是清理后的公开 URL；`count` 是非负展示整数，不表示本地成员数量。该类型不消费 `songs/order/bindings` 扩展，也不因此登记成员或获得写权限。
- `present` 是本地引用可见性。`subscription` 为 `null` 或 `{accountId,present,locator}`；账号必须对应 `library.accounts[platform]`。平台适配器将 `locator` 投影为订阅配方实际使用的 `listId/creatorId/creatorListId` 字段，按独立关系列保存，不持久化未消费展示字段或整份 JSON；定位字符串保持原值，额外输入字段不导致拒绝。实际平台写入配方归 Node。
- `intent` 为 `null` 或 `{accountId,present}`，记录已绑定账号的明确本地操作。即使当时开关关闭也保留意图，但不发请求；重新开启时交给原同步协调器先拉取。未绑定的纯本地引用没有账号意图。
- 取消后仍需基线、意图或耐久 fence 的记录以 `present=false` 保留；公开列表不显示它。无这些关系的纯本地记录可直接移除。

`music.library.list` 返回自管与网络两种视图。网络视图为 `{id,kind:"network",platform,remoteId,name,cover,count,revision,editable:false}`。`PlaylistRow` 将它投影至独立 `NetworkPlaylists` 和“网络歌单”导航分组；`Playlists` 仅供自管列表与单曲添加菜单使用。

网络命令：

| 命令 | 参数 |
| --- | --- |
| `music.library.network.list` | 可选 `offset/limit` |
| `music.library.network.get` | `platform/id`（远端 ID） |
| `music.library.network.save` | `platform/id/name`，可选 `cover/count` |
| `music.library.network.remove` | 本地 `id/ifRevision` |

`get` 返回 `{present,collection}`；不存在时 `collection=null`。保存／取消返回本地 `{ok:true,collection,present,localSaved:true}` 及独立 `sync`：关闭时为 `{ok:true,state:"local-only"}`；开启时为原协调器的状态回执或 `{ok:false,code}`。同步状态可能为 `partial`，不能仅凭其顶层 `ok=true` 宣称官方同步完成。

Catalog 的 `music.catalog.playlist.save` 只转交上述元信息命令，不生成 stage 或读取平台成员。完整 stage 的 `commit` 仅支持 `play/next`。网络详情通过 `catalog-playlist/<platform>/<remoteId>` 打开，每次重新进入请求 `refresh=true`，不恢复旧详情页缓存。收藏／取消按钮和卡片菜单使用同一引用命令；普通 Library detail／成员编辑／改名／删除原集合入口对网络 ID 返回 `READ_ONLY`。平台订阅及 WY 实时 token 能力见[原生同步](music-library-sync.md)。

## 统一歌曲与录音分组

共同匹配、固定基准、永久身份、资料不足与多候选规则以[歌曲仓库契约](../common/music/smart-playback.md#歌曲身份与唯一仓库)为唯一来源。

- `NativeMusicSong` 是原生投影，保留 title、artist／artists、album、duration、cover_url 与 `sources`；来源项保留 `sourceKey/source/musicInfo`。
- `MusicSongSession` 按 SQL 来源／本地定位／录音候选索引取得单曲，仅维护命令内真实变化的索引；新记录只在实际采用时分配 UUID。`MusicSongRepository` 在 library owner 的事务边界采用平台资料。
- `localFileId` 是本地文件授权定位，不从永久 songId 反解文件。已登记本地 locator 复用其歌曲记录。
- 选定封面及来源字段保存在仓库；采用、明确设置重选及实际图片失败由同一 owner 更新。成功提交后发布携带变更 ID 集合的 `SongMetadataChanged`，呈现只重投影受影响资料，不重建队列或联网证明内容。正常图片成功无需读取或复制歌曲库。
- UI 目录发布、缓存回访及歌曲变更通知使用 `MusicLibraryOwner.SongSnapshotsAsync`，只读取当前所需 ID。快照强制转入后台，在同一 repository 锁下取得已确认资料与 publication；每个歌曲行独立拒绝较旧 publication，未读取的 ID 不推进版本。读取计入 library 与 application 的 accepted 操作，关闭先封住新读取，再等待实际读取结束；取消不会提前报告排空。后台官方播放与脚本 owner 保留同步权威读取，未知提交期间仍读取对应记录的已确认版本。
- 资料设置的明确读取／保存完成边界会等待当前歌曲 ID 的异步投影，按设置代次与当前歌曲身份检查后更新封面；操作完成不依赖稍后投递的 metadata 事件。播放器显示、歌词／反馈投影及来源展开 getter 读取已发布歌曲行。来源 session 在异步选择／刷新边界取得所需歌曲快照，完成结果与继承状态同时携带 publication，旧显示行不能覆盖刚完成的扩展资料。
- `MusicSongMatchStore` 的 `music.source-discovery.v1` 只保存 source discovery 状态和排除，不复制完整 Song 或重新生成身份。同步检查点只保存类型化控制关系，引擎按来源键／永久 ID 读取当前歌曲，单次匹配只采用对应记录。

## 当前命令

曲库：

```text
music.library.list
music.library.detail
music.library.membership
music.library.search
music.library.create
music.library.edit
music.library.addSongs
music.library.removeSong
music.library.reorderSong
music.library.remove
```

队列与历史：

归档队列、播放日期分组、搜索历史及归档内的随机回退序列不按数量截断。历史列表沿现有每页 100 项协议完整读取；播放日期分组不等同于按当前时间计算的保留期。网易待发反馈仍保留独立的 7 天时间淘汰策略。

```text
music.queue.get
music.queue.peekNext
music.queue.mode
music.queue.playCollection
music.queue.replace
music.queue.append
music.queue.select
music.queue.remove
music.queue.reorder
music.queue.clear
music.queue.next
music.queue.previous
music.queue.step

music.history.get
music.history.list
music.history.detail
music.history.removePlaylist
music.history.restorePlaylist
music.history.recordPlayed
```

原生持续推荐在同一 owner 内使用私有 `music.queue.recommendation.*` 事务，application 普通调用不可用。`enter` 通过 `capture` 明确是否捕获原队列 checkpoint，并可保留实际当前 seed occurrence；心动首批完整入队。`append` 核对 `expectedVersion` 并复用现有队列尾部追加算法，私有回执包含全部新增 `entryIds`，不改变完整目录 next 入口的公开响应。它可携带新批次内的 `selectIndex`，在同一次 SQL 事务 中追加整批并选择指定 occurrence；不携带时保留当前选择、已有 next priority 和队列 source，不把候选入队冒充进入推荐。随后实际选择活会话推荐 occurrence 时，由协调器的只读 provenance 查询提供 source，唯一队列 writer 在选择 CAS 中提交；无活会话时不会从旧 context 复活来源。原列表来源仍保存在 checkpoint 或原 occurrence context。完整批次保留全部 occurrence，不按字节或数量截断。

私有 `restore` 接受 `nonce/expectedVersion/checkpoint`，心动与主动单曲推荐传 `retainCurrent=true`。owner 从实际队列取当前 occurrence，在 checkpoint 原 currentId 之后插入并选中；同 occurrence 已存在时不重复，空 checkpoint 保留当前曲，同录音不同 occurrence 不合并。原条目 context、队列 source 及导航状态来自 checkpoint；选中的 occurrence 从 next priority 移除。恢复与合并共用一次 generation／receipt CAS，不另行 append 或分次落盘。相似、FM 和搜索自动推荐不捕获恢复 checkpoint。`playback-settings.mode` 只有 1／2／3，独立于 checkpoint，不在进入或恢复时改写。

这些事务的 nonce、checkpoint 与算法／逐曲反馈 proof 不作为可恢复推荐会话持久化。丢失 ACK 只通过 nonce reconcile 读取真实结果，不重放 append／select／restore；成功恢复才消费 checkpoint，确认未应用保留 checkpoint 供新 nonce 重试。恢复原队列、取消或关闭按[推荐生命周期](music-recommendations.md)释放 checkpoint。

设置和歌词：

```text
music.settings.get / save
music.catalogSource.settings.get / save
music.officialSources.settings.get / save
music.quality.settings.get / save
music.metadata.settings.get / save
music.appearance.get / save
music.device.settings.get / save
music.search.settings.get / save
music.search.history.get / record / clear
music.lyrics.local.get / save / remove
music.lyrics.display.get / save
music.lyrics.desktop.settings.get / save
music.lyrics.parse
music.online.song
```

搜索设置读取可指定 `kind: songs|playlists`，只返回并消费对应分支；一种搜索不因未消费的另一分支损坏而失败。搜索、元数据和音质排序允许部分列表，设置页补全可选项只用于展示；未编辑的保存值与启用意图保持原样。

## Windows 外观值

`native-appearance` 使用 schema 1、revision CAS：`playlistView` 为 grid/list，`playerTheme` 为 starlight，`colorMode` 为 song/fixed；`colorPalette` 接受当前预设键或严格不透明 `#RRGGBB`（UI 提交统一大写），沿同一外观保存／权威重读路径消费。程序背景主题不属于音乐库，见[桌面接线](desktop-shell.md)。

`native-desktop-lyrics` 是独立 schema 1 area，含 `schema`、`revision`、`layout`（single/double）、`normalColor`、`highlightColor`，以及可选窗口 `geometry`。几何对象含显示器设备名 `monitor`、相对该显示器工作区左上角的 DIP 偏移 `x`／`y` 和 DIP 尺寸 `width`／`height`；不带几何的既有设置仍可读取。颜色为不透明 `#RRGGBB`，保存规范为大写；缺失 area 只读返回单行、普通色 `#FFB5C5`、高亮色 `#FFF2A8`，不迁移或重写其他 area。`music.lyrics.desktop.settings.save` 接受 `ifRevision`、三个原设置值和可选 `geometry`，返回权威 `settings`；未消费字段不作为拒绝条件也不持久化扩展槽；无变化不写盘。歌词校正、锁定与源译文开关不跨重启保存。消费者在成功回执或对应权威读取后才安装值，失败通过现有通知通道报告；具体窗口交互见[原生歌词应用链](music-lyric-application.md)。

## 事务、取消与关闭

- 所有持久写入在同一 owner gate 内，以 generation／receipt 在同一 SQL 事务核对并写实际变更集；零变化不提交。
- 事务准入后不把取消解释为回滚；丢失写入回执时保留本轮候选与受影响歌曲的 before 版本，不提前展示未确认资料，阻止后续写。
- `appearance`、`metadata`、`quality`、桌面歌词设置、浏览数据源、官方音源设置、`queue` 和搜索策略在各自写入回执不确定时，只允许对应的权威 current read 在 owner gate 内读取实际控制与 generation／receipt，核对该次 before／after revision；该读取会结算同职责未知结果，不重放业务命令。桌面歌词通过 `music.lyrics.desktop.settings.get`，浏览数据源与官方音源分别通过 `music.catalogSource.settings.get`、`music.officialSources.settings.get` 结算对应 save。
- 其他职责以及跨职责命令继续由显式 `ReconcileAsync` 协调；recommendation 使用其 nonce reconcile。一个职责的 current read 不得替另一个职责结算未知写入。
- 第三状态保持 `STORAGE_CONFLICT`，不提供“丢弃旧数据后继续”的回退入口。
- `PrepareShutdownAsync` 封闭新业务并要求未知写已结算；退役单向完成，新业务由新 owner 接纳，不重放写入。
- `AbortAsync` 终止内存生命周期，不宣称未知持久写已经回滚。
