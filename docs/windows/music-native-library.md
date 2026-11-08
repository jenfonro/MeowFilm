# 原生音乐状态 owner

## 责任

`MeowFilm.Capabilities.Library.MusicLibraryOwner` 是音乐曲库、队列、历史、相关设置和脚本源元数据的唯一原生文档写入者。

- 文档命名空间固定为 `music.library.v1`。
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

普通 `music.queue.append` 仍仅接受 `songs/context/next`，保持 `entryIds` 回执、最多 1000 首与 512KiB 输入边界，不接受 `expectedVersion`。完整入口取消歌曲数量上限，继续使用现有 16MiB 完整输入、8MiB 文档资源边界；空输入不允许。存储 schema 和其他平台契约不变。

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
- entry key 必须属于当前 owner 的明确 area，或属于当前已安装脚本实例的精确私有 storage key。
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
native-lyric-display
native-metadata-policy
native-appearance
native-device-settings
native-search-policy
native-search-history
native-local-lyrics
playback-settings
```

脚本安装与私有 KV 由[公共音乐模块](../runtime/music-script-runtime.md)唯一持久化。
旧 `lx-installations` 与 `lx-data` 条目不读取、验证、迁移或改写；原文档随无关设置保存原样保留。
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

wrapper 只有以下字段：

```json
{
  "schema": 1,
  "scope": "local",
  "revision": 0,
  "library": {}
}
```

`wrapper.revision` 必须等于 `library.revision`。`library` 只有：

```text
version ownerId revision nextId collections songs accounts
```

约束：

- `version` 为 `1`，`ownerId` 精确等于当前 scope。
- `nextId` 大于所有现存 `playlist:<正整数>` 的编号。
- 只有一个收藏集合：`id=favorites`、`kind=favorites`；它没有 `deleted` 或 `locallyCreated` 字段。
- 普通歌单 ID 必须为 canonical `playlist:<正整数>`。
- `deleted`、`locallyCreated` 若存在只能为 `true`。
- `order` 无重复，并且每项都引用当前 `songs`。
- `accounts` 只允许 `wy`、`tx`、`kg`，账号 ID 最长 64 字符。
- binding 只允许相同三个平台；`map` 方向固定为 `nativeId -> songId`。
- intent 的 `acknowledged` 只包含该集合中现存 binding 的 canonical binding key。
- 歌曲必须等于 `NativeMusicSong.Normalize` 的当前规范结果；临时媒体 URL、凭据和签名资源字段不得进入持久歌曲。

## 统一歌曲与录音分组

- `NativeMusicSong` 是 Windows 精简歌曲模型的唯一 owner。显示字段为 `songId / title / artist / album / duration / cover_url`；`sources` 中每项保存 `sourceKey / source / musicInfo`，录音自身的精确秒数保存在 `musicInfo.duration`。
- `RecordingIdentity` 解析规范标题、歌手、版本、来源键和时长。精确来源键匹配允许补全未知信息；跨来源匹配要求已知且相同的标题/歌手/版本指纹，以及双方有效时长。容差为 `max(3 秒, 较短时长 × 2%)`，不能通过中间录音传递兼容。
- `Recordings` 管理一个操作内的代表歌曲与录音集合。代表及全部来源必须两两兼容；来源键唯一，输出时主录音第一，其余按 `sourceKey` 排序。规范化、搜索、曲库、播放历史、官方候选与同步复用此实现。
- `NativeMusicSongGroups` 是搜索、曲库 upsert 和播放历史共用的操作内索引：依次检查兼容的精确来源组、既有歌曲 ID、同指纹子组；无匹配才建组。原 ID 冲突时，根据新候选的完整规范录音证据生成确定性后缀，不因代表相同就覆盖不同录音集合。
- 导入已显示或已持久化的分组不重新合并、不改变 ID。曲库更新优先采用新显示信息、主来源及同键录音，保留既有逻辑 ID；播放历史则由最新 occurrence 保留时间、context、代表和同键录音。本地歌曲只按本地 `songId` 参与分组，不能凭同名元信息隐式并入另一文件或在线结果；同步显式绑定仍需通过完整录音兼容检查。
- 同步复用同一曲库快照内的分组索引，不在逐条 upsert 时重复解析整库；检查点提交重读后丢弃旧索引，再按新快照建立，不能保留跨检查点的旧 JsonNode。
- `membership` 与 upsert 使用相同的完整录音匹配，能够找到带冲突后缀的子组。曲库专属的“每个原生来源只属于一首歌曲”仍由 Library owner 校验，冲突返回 `NATIVE_ID_CONFLICT`，不能隐式重绑集合。

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

原生持续推荐在同一 owner 内使用私有 `music.queue.recommendation.*` 事务，application 普通调用不可用。`enter` 捕获原队列 checkpoint 并可保留实际当前 seed occurrence；心动首批完整入队。`append` 核对 `expectedVersion` 并复用现有队列尾部追加算法，私有回执包含全部新增 `entryIds`，不改变完整目录 next 入口的公开响应。它可携带新批次内的 `selectIndex`，在同一次 envelope CAS 中追加整批并选择指定 occurrence；不携带时保留当前选择、原队列来源及已有 next priority。私有完整批次不受普通外部 append 的条数门槛裁剪，仍服从既有请求／响应字节和持久 envelope 资源校验。

私有 `restore` 接受 `nonce/expectedVersion/checkpoint`，心动额外传 `retainCurrent=true`。owner 从实际队列取当前 occurrence，在 checkpoint 原 currentId 之后插入并选中；同 occurrence 已存在时不重复，空 checkpoint 保留当前曲，同录音不同 occurrence 不合并。原条目 context、队列 source 及导航状态来自 checkpoint；选中的 occurrence 从 next priority 移除。恢复与合并共用一次 envelope CAS，不另行 append 或分次落盘。省略该参数或传 false 仍完全恢复 checkpoint，供相似推荐使用。

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
music.lyrics.parse
music.online.song
```

## Windows 外观值

`native-appearance` 保持 schema 1、revision CAS 及原字段：`playlistView` 为 grid/list，`playerTheme` 为 starlight，`colorMode` 为 song/fixed；`colorPalette` 接受既有预设键或严格不透明 `#RRGGBB`（UI提交统一大写）。读取旧预设不会重写数据，自定义颜色沿原外观保存/权威重读路径消费。本次不保证旧客户端能读取新的自定义值，不增加迁移或双写。程序背景主题不属于音乐库，见[桌面接线](desktop-shell.md)。

## CAS、取消与关闭

- 所有持久写入在同一 owner gate 内对完整 envelope 执行 CAS。
- CAS 准入后不把取消解释为回滚；丢失写入回执时保留内存候选并阻止后续写。
- `appearance`、`metadata`、`quality`、浏览数据源、官方音源设置、`queue` 和搜索策略在各自写入回执不确定时，只允许对应的权威 current read 在 owner gate 内读取实际文档并核对该次 CAS 的 before/after 指纹；该读取会结算同职责的未知 CAS 结果，不重放业务命令。浏览数据源与官方音源分别通过 `music.catalogSource.settings.get`、`music.officialSources.settings.get` 结算对应 save。
- 其他职责以及跨职责命令继续由显式 `ReconcileAsync` 协调；recommendation 使用其 nonce reconcile。一个职责的 current read 不得替另一个职责结算未知写入。
- 第三状态保持 `STORAGE_CONFLICT`，不提供“丢弃旧数据后继续”的回退入口。
- `PrepareShutdownAsync` 封闭新业务并要求未知写已结算；取消关闭只恢复当前 owner，不重放写入。
- `AbortAsync` 终止内存生命周期，不宣称未知持久写已经回滚。
