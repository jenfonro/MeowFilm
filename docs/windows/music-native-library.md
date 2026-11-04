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
- 不兼容文档保持原字节不变并明确失败；唯一支持的旧格式转换是下述 `online-settings` schema 2 → 3，转换前仍须验证完整文档。

当前普通 area：

```text
library
library-sync
queue
history
lx-installations
online-settings
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

脚本私有 area 使用当前安装实例和站点构成的精确 key；每个实例最多 32 个有界站点标识，不存在实例、非法标识或重复 key 均拒绝。

`lx-installations` 唯一当前格式为 schema 4：

```json
{
  "schema": 4,
  "revision": 0,
  "installations": []
}
```

- 不设固定安装实例数量或根文档 entries 数量上限；仍受 metadata 的 3 MiB 和根文档的 8 MiB 字节边界约束。
- 每个 `current`／`previous` 只包含 `sourceHash`、`sourceBytes`、`version`、`permissions`。
- `permissions` 仅含 `storage`，值为不重复的 `read`／`write` 数组。
- metadata 总量最多 3 MiB；单个脚本源码最多 16 MiB，与公共 ScriptHost 上限一致。
- 源码位于受保护 collection `music.script-sources.v1`，按 1 MiB、内容 hash 和 chunk index 保存为不可变字节。发布 metadata 前必须已完整写入；读取核对每片 fingerprint、长度和整份 hash。
- 中断或并发的相同不可变分片写入可通过读回相同字节结算；冲突或损坏明确失败。未被 metadata 引用的内容不会被静默删除或当作安装实例。
- 安装启停或删除不修改在线设置及其 revision；删除安装实例仍在同一 CAS 中移除该实例的私有 storage。
- 安装元数据本身不因读取而写入；安装 schema 3、内联源码和其他不符合当前安装格式的记录明确失败，不迁移、转换、删除或回退。文档内合法的旧在线设置按下一节转换。

## 在线设置当前格式与定点转换

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
- 旧 schema 2 必须精确包含 `schema revision lyrics target`。`revision` 必须是合法持久整数，`lyrics` 必须是布尔值；旧 `target` 只能为 `null` 或精确包含 `installationId platform` 的对象，安装 ID 须合法并引用已存在实例，平台只允许 `wy/tx/kw/kg/mg/local`。引用禁用实例仍属于合法旧数据，不改变该实例的启停状态。
- 读取旧文档时，先校验 canonical envelope、全部 area、安装 schema 4、私有 storage 及旧设置 shape/引用，再仅将在线设置的 schema 改为 3 并移除旧 `target`。转换保留原有 `lyrics` 和 `revision`，也保留曲库、队列、历史、安装版本/权限/启停、私有 storage、其他设置以及独立存储的源码内容。
- 转换通过现有 owner gate 和完整文档 CAS 持久化。当前 schema 3 的普通读取和重启不再写入；未知 schema、缺字段、额外字段、损坏或悬空引用均拒绝，原文档不覆写。
- 转换未知提交与在线设置保存采用相同 pending 规则：后续命令封闭，使用显式 `ReconcileAsync` 读回 before/after 指纹。强制加载和结算不转换、不重放写入，也不替换 pending；第三状态保持冲突。若结算确认转换尚未提交，下一次新业务调用才可重试转换，继续沿用原 revision。
- 相关公开行为用例位于 `MusicNativeLibraryTests.cs` 和 `MusicOnlineSettingsTests.cs`；执行结果由集中验证记录。

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

设置和歌词：

```text
music.settings.get / save
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

## CAS、取消与关闭

- 所有持久写入在同一 owner gate 内对完整 envelope 执行 CAS。
- CAS 准入后不把取消解释为回滚；丢失写入回执时保留内存候选并阻止后续写。
- `appearance`、`metadata`、`quality`、官方音源设置（`music.officialSources.settings.get`）、`queue` 和搜索策略在各自写入回执不确定时，只允许对应的权威 current read 在 owner gate 内读取实际文档并核对该次 CAS 的 before/after 指纹；该读取会结算同职责的未知 CAS 结果，不重放业务命令。官方音源设置的权威读取对应 `music.officialSources.settings.save`。
- 其他职责以及跨职责命令继续由显式 `ReconcileAsync` 协调；recommendation 使用其 nonce reconcile。一个职责的 current read 不得替另一个职责结算未知写入。
- 第三状态保持 `STORAGE_CONFLICT`，不提供“丢弃旧数据后继续”的回退入口。
- `PrepareShutdownAsync` 封闭新业务并要求未知写已结算；取消关闭只恢复当前 owner，不重放写入。
- `AbortAsync` 终止内存生命周期，不宣称未知持久写已经回滚。
