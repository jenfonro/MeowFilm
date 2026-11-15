# 原生音乐状态 owner

集合分类、内容所有权及订阅副作用以[共同歌单契约](../common/music/playlist-ownership.md)为准；本页记录 Windows owner、命令和持久格式。

## 责任

`MeowFilm.Capabilities.Library.MusicLibraryOwner` 是音乐曲库、队列、历史及相关设置的唯一原生文档写入者；脚本安装与私有数据归公共音乐模块。

- 文档命名空间固定为 `music.library.v2`。
- 默认 scope 为 `local`。
- 普通业务不经 Node 读写该文档。
- 同一 App 内不得再装配第二个文档 writer。

## 公开边界

```csharp
MusicLibraryOwner(IDocumentStore store)
MusicLibraryOwner(IDocumentStore store, string scope)

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
Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default);
Task AbortAsync(CancellationToken cancellationToken = default);
ValueTask DisposeAsync();
```

业务命令返回 `{ok:true,...}` 或 `{ok:false,code}`。可信的进程内事务端口直接抛出固定 `MusicLibraryException.Code`，不返回存储正文、凭据或内部异常文本。

`AppendQueueAsync` 是完整歌单的进程内 next 入口，返回 `{ok:true,currentId,version,mode}`，不返回全量 entryIds。它在唯一串行边界核对 `expectedVersion`，创建独立 occurrence 并将整段按输入顺序插在当前曲之后，一次 CAS、一次版本递增；当前曲、模式及旧优先队列保留，新段优先于旧 priority。空队列选中首曲且 priority 为空，不调用媒体播放。失败不分批落盘；CAS 准入前取消不写入，准入后报告真实结果，未知写入使用既有 reconcile 机制，不重放。

普通 `music.queue.append` 接受 `songs/context/next`，保持 `entryIds` 回执与 512KiB 输入字节边界，不接受 `expectedVersion`。完整入口沿现有 16MiB 完整输入、8MiB 文档资源边界；两者不以歌曲条数截断，空输入不允许。队列当前 schema 为 1，entry 只存 `id/songId/context`，读取时由唯一歌曲仓库投影资料。

## 唯一当前文档

根文档严格为：

```json
{
  "version": 1,
  "entries": [
    { "key": "[\"meowfilm\",\"music\",1,\"local\",\"library\"]", "value": {} }
  ]
}
```

要求：

- UTF-8 字节必须等于当前 canonical JSON 编码；重复属性、非规范持久整数、额外根字段和未知 entry 均拒绝。
- entry key 必须属于当前 owner 的明确 area；脚本实例私有存储不在此文档。
- 缺少可选 area 表示该 area 尚未创建；entry 已存在但值为 `null`、错误类型或错误 schema 时不是空状态。
- 按各 area 当前字段、schema 及引用约束校验；不符合契约的文档保持原字节不变并明确失败，读取不执行格式转换或回退。

当前普通 area：

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

共享图片和歌词展示字节存于 `MusicPresentationCache` 的 `music.presentation-resources.v1`，不属于业务 area，不参与整库提交；手工歌词仍属于 `native-local-lyrics`。完整 URL 图片键、预算、清理隔离等共同语义见[展示资源](../common/music/presentation-resources.md)，本端接线见[元数据策略](music-metadata-policy.md)。

脚本安装与私有 KV 由[公共音乐模块](../runtime/music-script-runtime.md)唯一持久化。
本 owner 只从空数据建立并读取当前 namespace，不检查或承接历史脚本条目。
`native-source-exclusions` 保存 schema 1、revision、items，每项包含 songId/provider/platform/providerName/title/artist/at。
同身份幂等添加，只显式 remove 解除，不随缓存清理删除。沿用根文档的字节资源边界，不另设条数上限。

## 在线设置当前格式

`online-settings` 的当前格式为 schema 3，字段仅有：

```json
{
  "schema": 3,
  "revision": 0,
  "lyrics": true
}
```

- 缺少该 area 时，`music.settings.get` 返回上述缺省 `settings`，不制造 area 或文档。
- `music.settings.save` 必须且只能携带 `ifRevision` 和 `lyrics`；成功保存推进一次 revision，旧 revision 返回 `REVISION_CONFLICT`。当前读写格式均不包含全局音源选择。
- 已存在该 area 时按封闭协议校验：必须为 schema 3，完整且仅包含 `schema`、`revision`、`lyrics`，各字段类型和值须合法；不符合契约即失败，原文档不覆写。
- 读取和重启不写入；保存通过 owner gate 和完整文档 CAS 持久化。未知提交由显式 reconcile 核对实际结果，不重放写入。
- 在线设置保存未知提交后，后续命令封闭，使用显式 `ReconcileAsync` 读回 before/after 指纹。结算不重放写入，也不替换 pending；第三状态保持冲突。
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
- `music.catalogSource.settings.save` 只接收 `ifRevision` 和 `platform`，平台仅允许 `wy/kg`；revision 不符返回 `REVISION_CONFLICT`，同值保存不写盘，改变平台只推进本设置的 revision。
- 该偏好只控制首页与频道，不改详情资源平台、账号、搜索偏好、音源启停或在线设置；唯一 writer 仍是本 owner。
- 未知 CAS 由本职责的权威 get 核对 before/after 指纹结算，不重放保存，也不替其他职责结算。测试归属为 `MusicCatalogSourceSettingsTests.cs`。

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

wrapper 只有以下字段：

```json
{
  "schema": 2,
  "scope": "local",
  "revision": 0,
  "library": {}
}
```

`wrapper.revision` 必须等于 `library.revision`。`library` 只有：

```text
version ownerId revision nextId collections songs accounts networkPlaylists
```

约束：

- `version` 为 `2`，`ownerId` 精确等于当前 scope；只解码当前 wrapper/library 格式，不提供版本识别或转换。
- `nextId` 大于所有现存 `playlist:<正整数>` 的编号。
- 只有一个收藏集合：`id=favorites`、`kind=favorites`；它没有 `deleted` 或 `locallyCreated` 字段。
- 普通歌单 ID 必须为 canonical `playlist:<正整数>`。
- `deleted`、`locallyCreated` 若存在只能为 `true`。
- `order` 无重复，并且每项都引用当前 `songs`。
- `accounts` 只允许 `wy`、`tx`、`kg`，账号 ID 使用当前安全账号发布的非空字符串，不猜测长度或数字格式。
- binding 只允许相同三个平台且 `writable=true`；账号、远端集合和成员定位均保留共同结果的完整非空字符串，`map` 方向为实际成员 key 到永久 songId。账号精确关联、成员 occurrence、当前仓库引用及写事务 CAS/pending 仍由原生同步 owner 保证。
- intent 的 `acknowledged` 只包含该集合中现存 binding 的 canonical binding key。
- `songs` 以永久 UUID 为键，记录自身 songId 与键一致；来源不能跨记录重绑。临时媒体 URL、凭据和签名资源字段不得进入持久歌曲。
- 集合 order、队列 entry 和历史只持久 songId 引用；展示时读取仓库当前资料。队列 occurrence 与歌曲 ID 分离，资料补全不推进 queue version。

### 网络引用字段与回执

`networkPlaylists` 是独立数组，每项严格为：

```text
id platform remoteId name cover count revision present subscription intent
```

- `id` 固定为 `network:<platform>:<remoteId>`；`platform` 允许 `wy/tx/kg/kw/mg`。`remoteId` 使用共同平台结果中的非空定位字符串，原生不猜测数字格式、长度、字符集或前缀。
- `cover` 是清理后的公开 URL；`count` 是非负展示整数，不表示本地成员数量。该类型没有 `songs/order/bindings`。
- `present` 是本地引用可见性。`subscription` 为 `null` 或 `{accountId,present,locator}`；账号必须对应 `library.accounts[platform]`。`locator` 保存共同平台返回的当前不透明定位对象，不保存凭据；字段转换与实际写入配方归 Node，原生不按平台字段白名单再次解析。
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
- `NativeMusicSongGroups` 索引仓库已有记录及来源归属，新记录只在实际采用时分配 UUID；`MusicSongRepository` 在 library owner 的 CAS 边界采用平台资料。
- `localFileId` 是本地文件授权定位，不从永久 songId 反解文件。已登记本地 locator 复用其歌曲记录。
- 选定封面及来源字段保存在仓库；采用、明确设置重选及实际加载结果由同一 owner 更新。成功提交后发布 `SongMetadataChanged`，页面／搜索／当前播放只重投影资料，不重建队列或联网证明内容。
- `MusicSongMatchStore` 的 `music.source-discovery.v1` 只保存 source discovery 状态和排除，不复制完整 Song 或重新生成身份。同步检查点重新读取当前仓库后重建操作内索引。

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

原生持续推荐在同一 owner 内使用私有 `music.queue.recommendation.*` 事务，application 普通调用不可用。`enter` 通过 `capture` 明确是否捕获原队列 checkpoint，并可保留实际当前 seed occurrence；心动首批完整入队。`append` 核对 `expectedVersion` 并复用现有队列尾部追加算法，私有回执包含全部新增 `entryIds`，不改变完整目录 next 入口的公开响应。它可携带新批次内的 `selectIndex`，在同一次 envelope CAS 中追加整批并选择指定 occurrence；不携带时保留当前选择、已有 next priority 和队列 source，不把候选入队冒充进入推荐。随后实际选择活会话推荐 occurrence 时，由协调器的只读 provenance 查询提供 source，唯一队列 writer 在选择 CAS 中提交；无活会话时不会从旧 context 复活来源。原列表来源仍保存在 checkpoint 或原 occurrence context。完整批次保留全部 occurrence，仍服从既有请求／响应字节和持久 envelope 资源校验。

私有 `restore` 接受 `nonce/expectedVersion/checkpoint`，心动与主动单曲推荐传 `retainCurrent=true`。owner 从实际队列取当前 occurrence，在 checkpoint 原 currentId 之后插入并选中；同 occurrence 已存在时不重复，空 checkpoint 保留当前曲，同录音不同 occurrence 不合并。原条目 context、队列 source 及导航状态来自 checkpoint；选中的 occurrence 从 next priority 移除。恢复与合并共用一次 envelope CAS，不另行 append 或分次落盘。相似、FM 和搜索自动推荐不捕获恢复 checkpoint。`playback-settings.mode` 只有 1／2／3，独立于 checkpoint，不在进入或恢复时改写。

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

## Windows 外观值

`native-appearance` 使用 schema 1、revision CAS：`playlistView` 为 grid/list，`playerTheme` 为 starlight，`colorMode` 为 song/fixed；`colorPalette` 接受当前预设键或严格不透明 `#RRGGBB`（UI 提交统一大写），沿同一外观保存／权威重读路径消费。程序背景主题不属于音乐库，见[桌面接线](desktop-shell.md)。

`native-desktop-lyrics` 是独立 schema 1 area，含 `schema`、`revision`、`layout`（single/double）、`normalColor`、`highlightColor`，以及可选窗口 `geometry`。几何对象含显示器设备名 `monitor`、相对该显示器工作区左上角的 DIP 偏移 `x`／`y` 和 DIP 尺寸 `width`／`height`；不带几何的既有设置仍可读取。颜色为不透明 `#RRGGBB`，保存规范为大写；缺失 area 只读返回单行、普通色 `#FFB5C5`、高亮色 `#FFF2A8`，不迁移或重写其他 area。`music.lyrics.desktop.settings.save` 接受 `ifRevision`、三个原设置值和可选 `geometry`，返回权威 `settings`；未知字段拒绝，无变化不写盘。歌词校正、锁定与源译文开关不跨重启保存。消费者在成功回执或对应权威读取后才安装值，失败通过现有通知通道报告；具体窗口交互见[原生歌词应用链](music-lyric-application.md)。

## CAS、取消与关闭

- 所有持久写入在同一 owner gate 内对完整 envelope 执行 CAS。
- CAS 准入后不把取消解释为回滚；丢失写入回执时保留内存候选并阻止后续写。
- `appearance`、`metadata`、`quality`、桌面歌词设置、浏览数据源、官方音源设置、`queue` 和搜索策略在各自写入回执不确定时，只允许对应的权威 current read 在 owner gate 内读取实际文档并核对该次 CAS 的 before/after 指纹；该读取会结算同职责的未知 CAS 结果，不重放业务命令。桌面歌词通过 `music.lyrics.desktop.settings.get`，浏览数据源与官方音源分别通过 `music.catalogSource.settings.get`、`music.officialSources.settings.get` 结算对应 save。
- 其他职责以及跨职责命令继续由显式 `ReconcileAsync` 协调；recommendation 使用其 nonce reconcile。一个职责的 current read 不得替另一个职责结算未知写入。
- 第三状态保持 `STORAGE_CONFLICT`，不提供“丢弃旧数据后继续”的回退入口。
- `PrepareShutdownAsync` 封闭新业务并要求未知写已结算；取消关闭只恢复当前 owner，不重放写入。
- `AbortAsync` 终止内存生命周期，不宣称未知持久写已经回滚。
