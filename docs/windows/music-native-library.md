# 原生音乐状态 owner

## 责任

`MeowFilm.Capabilities.Library.MusicLibraryOwner` 是音乐曲库、队列、历史、相关设置和脚本源元数据的唯一受保护文档写入者。

- 文档命名空间固定为 `music.library.v1`。
- 默认 scope 为 `local`。
- 普通业务不经 Node 读写该文档。
- 同一 App 内不得再装配第二个文档 writer。

## 公开边界

```csharp
MusicLibraryOwner(IProtectedDocumentStore store)
MusicLibraryOwner(IProtectedDocumentStore store, string scope)

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

Task ReconcileAsync(CancellationToken cancellationToken = default);
Task PrepareShutdownAsync(CancellationToken cancellationToken = default);
Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default);
Task AbortAsync(CancellationToken cancellationToken = default);
ValueTask DisposeAsync();
```

业务命令返回 `{ok:true,...}` 或 `{ok:false,code}`。可信的进程内事务端口直接抛出固定 `MusicLibraryException.Code`，不返回存储正文、凭据或内部异常文本。

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
- 不兼容文档保持原字节不变并明确失败，不自动修复、删除、覆盖或转换。

当前普通 area：

```text
library
library-sync
queue
history
lx-installations
online-settings
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

脚本私有 area 使用当前安装实例和站点构成的精确 key；每个实例最多 32 个有界站点标识，不存在实例、非法标识或重复 key 均拒绝。

`lx-installations` 唯一当前格式为 schema 3：

```json
{
  "schema": 3,
  "revision": 0,
  "installations": []
}
```

- 最多 32 个安装实例。
- 每个 `current`／`previous` 只包含 `source`、`version`、`permissions`。
- `permissions` 仅含 `storage`，值为不重复的 `read`／`write` 数组。
- 单脚本最多 512 KiB，全部源元数据最多 3 MiB。
- 禁用或删除当前播放目标时，在同一文档 CAS 中把 `online-settings.target` 置空并推进其 revision。
- 读取不写入文档；不符合当前格式的源记录明确失败，不迁移、转换、删除或回退。

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

设置和歌词：

```text
music.settings.get / save
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

## CAS、取消与关闭

- 所有持久写入在同一 owner gate 内对完整 envelope 执行 CAS。
- CAS 准入后不把取消解释为回滚；丢失写入回执时保留内存候选并阻止后续写。
- `appearance`、`metadata`、`quality`、`queue` 和搜索策略在各自写入回执不确定时，只允许对应的权威 current read 在 owner gate 内读取实际文档并核对该次 CAS 的 before/after 指纹；不重放业务命令。
- 其他职责以及跨职责命令继续由显式 `ReconcileAsync` 协调；recommendation 使用其 nonce reconcile。一个职责的 current read 不得替另一个职责结算未知写入。
- 第三状态保持 `STORAGE_CONFLICT`，不提供“丢弃旧数据后继续”的回退入口。
- `PrepareShutdownAsync` 封闭新业务并要求未知写已结算；取消关闭只恢复当前 owner，不重放写入。
- `AbortAsync` 终止内存生命周期，不宣称未知持久写已经回滚。
