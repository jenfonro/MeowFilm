# 原生音乐曲库同步

内容分类、订阅副作用与跨端规则以[共同歌单契约](../common/music/playlist-ownership.md)为准；本页仅说明 Windows 协调器、适配器、journal 和生命周期事实。

## 所有权

`MusicLibrarySyncService` 是网易云、QQ 音乐和酷狗曲库同步的唯一协调器。

- 它借用同一个 `MusicLibraryOwner`、账号会话 owner、[客户端 owner](music-platform-client-context.md)和固定平台 HTTP。
- 它不拥有第二份曲库或 Node 会话，不保存账号凭据；网易订阅准备显式借用 App 的 `IRuntimeClient`。
- 同步状态位于 `music.library.v1` 的 `library-sync` area，并与曲库在同一 envelope CAS 中提交。
- binding map 的唯一方向是 `nativeId -> songId`。
- 按明确同步需求委托目录补读时，目录验证本次请求所用客户端快照和响应；成功后适配器显式取得已确认的当前快照再继续，不把合法委托更新误判为旧请求失效，也不通过重取代次绕过原请求校验。
- 平台映射与写入仅枚举歌曲实际 `sources`，不把整首歌曲重复当作主来源。本地歌曲的空来源是合法状态：有足够元数据时尝试匹配，否则报告该歌曲未映射，继续同步其它可用歌曲，不使整轮变为 `INVALID_SONG`。

## 平台与设置

当前同步平台固定为：

```text
wy  网易云音乐
tx  QQ 音乐
kg  酷狗音乐
```

每个平台只有：

```text
favorites
playlists
accountId
```

设置页直接投影该唯一 owner：

- 开关变更立即带 revision CAS 保存。
- 保存成功后立即触发同一协调器的一轮同步。
- 没有草稿、保存按钮、重新读取按钮、第二个 timer 或另一套状态。
- UI 失败后的读取只用于确认该次 CAS 的实际当前结果；它不重放远端写入。

## 当前状态格式

`library-sync` 严格包含：

```text
schema
revision
platforms
pending
confirmation
approved
status
```

当前 schema 为 `2`，同步 contract 为 `1.1.0`。旧 schema 拒绝；本端不建立迁移分支。

`status.phase` 只允许：

```text
idle pushing synced partial blocked superseded
```

`pending` 是一次远端写入 fence，操作只允许：

```text
create delete add remove order subscribe unsubscribe
```

自管写入仍使用 `before/desired/ids/name/directory`。订阅写入使用独立字段：

```text
platform accountId collectionId remoteId operation beforePresent desiredPresent locator name
```

`collectionId` 指向 `networkPlaylists`，不能指向自管集合；两个 presence 必须相反且与操作对应。KG subscribe locator 是 `{creatorId,creatorListId}`，unsubscribe locator 是当前账号的 `{listId}`。其它两平台 locator 为空对象。pending、账号、自管 binding 或网络引用须引用一致；错误以 `CORRUPT_SYNC` 失败且原文档不写回。

## 同步流程

1. 初始化只建立本地生命周期，不访问平台；首次计划轮最早在五分钟后触发。
2. 同一时间只允许一个 round；账号变化、设置变化和关闭会提升 generation 并取消旧 round。
3. 只读取已启用平台和类型；未启用平台不发请求。
4. `SyncDirectory.Collections` 只含当前账号本人可写的已选集合及其完整成员；`Subscriptions` 只含订阅元信息。适配器不为订阅调用成员或补搜接口。
5. 本地曲库和同步状态通过 `CommitLibrarySyncAsync` 在同一 envelope CAS 中提交。
6. 每次远端写入前先耐久写入 pending fence。
7. HTTP 返回或异常都不直接视为远端写入结果；必须重新完整读取并确认实际状态。
8. 未确认结果保留 pending，不自动重放。订阅读回以完整稳定目录核对 presence；只有观测到 `desiredPresent` 才清 fence 和对应意图。仍为 `beforePresent` 不能排除请求迟到，继续保留围栏及意图并返回 `WRITE_UNCONFIRMED`；重复 run、重开及计划轮均不重发，随后读回目标效果才解除围栏。
9. 设置页明确启用、确认或用户明确同步会立即进入同一协调器；计划轮只接受 `idle`／`synced` 状态，`blocked`、`partial`、确认及 pending 不会形成后台重试环。

网络保存／取消在本地 owner CAS 退出 gate 后调用原协调器的定向轮次。该轮只获取所属平台租约、读取其订阅目录和推送其 presence 意图，不进入自管成员融合、补搜或其他平台写入。启动控制锁在 round 开始后释放，关闭／禁用仍能取消它；已保存本地结果和官方结果分别返回。未结算的自管确认不会被该定向轮次吞掉。

## 冲突和确认

- 本地集合 revision、同步 state revision、账号代次和 owner snapshot 都参与准入。
- 本地新建及订阅目录导入的网络引用，以当前 library revision 加一作为初始 revision；无账号关系和围栏的取消项仍物理删除，同 identity 重建不会复用旧移除句柄的 revision。
- 远端相对 baseline 已变化时返回 `REMOTE_CHANGED`，不覆盖远端新状态。
- 删除远端歌单、大批删除和空远端引发的破坏性变化必须产生确认 token。
- 确认只批准当前 token 对应的当前状态；revision 或证据变化后旧确认失效。
- `resetAccount` 撤销该平台同步授权、账号绑定、自管 binding 及网络 baseline/intent；保留本地歌曲、自管集合和可见网络引用，不访问远端。该平台仍有 pending 时拒绝 reset。

## 当前公开命令

```text
music.librarySync.get
music.librarySync.run
music.librarySync.cancel
music.librarySync.set
music.librarySync.confirm
music.librarySync.resetAccount
```

`set`、`confirm`、`resetAccount` 都要求明确 `confirm:true` 和当前 `ifRevision`。

## 请求与生命周期

- 每轮具有明确取消 token；不以整轮四分钟截止截断大曲库。各平台操作仍有自身截止时间。
- 重复 `run` 不并行扩散，返回 `SYNC_BUSY`。
- 原生 HTTP、账号目录、映射搜索和酷狗云调用按真实分页、完整快照和取消运行，不共享每平台／整轮请求次数额度。
- 集合、目录页、成员页和录音映射不再按累计次数截断，也不设固定歌单歌曲数量。平台返回结束、游标停滞、快照变化及错误必须各自明确处理；持久曲库容量仍按现行数据契约校验。
- 拉取阶段已经确认且没有本地变化的集合直接复用同轮快照；不会为了证明“无需写入”再次读取整张远端歌单。
- 唯一 pending fence 每轮只恢复一次；恢复未确认即停止，不存在循环读回。
- 页面隐藏只停用 UI 观察者，不撤销已保存授权，不另建 scheduler。
- 正常关闭取消 scheduler、等待当前 round 和控制命令物理结束，再进入 library 关闭。
- 取消关闭只重新启动原协调器；不恢复旧请求、不重放 pending 远端写入。

## 平台订阅请求与实际能力边界

| 平台 | 本端实现 |
| --- | --- |
| WY | 订阅／退订准备通过 App runtime 的 `netease.token.get` 取新 token，再走 EAPI `/api/playlist/subscribe` 或 `/api/playlist/unsubscribe`；body 使用真实远端 `id`，token 注入加密 `header["X-antiCheatToken"]`，并沿既有 header 构造 HTTP Cookie。取消仅移除账号订阅关系。 |
| TX | `music.musicasset.PlaylistFavWrite.FavPlaylist/CancelFavPlaylist`，`uin=encrypt_uin`、`v_playlistId=[真实disstid]`；校验 `result/v_failedPlaylistId` 后仍须目录读回。 |
| KG | 元信息 `/v3/get_list_info` 的 `data[]` 核对 `global_collection_id/status/list_create_userid/list_create_listid`；type=1 的 `/cloudlist.service/v5/add_list` 只创建订阅关系。取消使用既有云加密 `/v2/delete_list`，携带当前账号订阅的 listId 和 type=1，不使用作者 listId。 |
| KW/MG | 无账号同步设置或远端订阅端口；仅使用本地引用命令。 |

只读出处：

- txy QQMusicApi `2698673d9b2e438bca7a0e06385ead7a4cdaeda9`，`qqmusic_api/modules/user.py:335–372`。
- txy KuGouMusicApi `e19a52a91b9ceb78d9acce768c357b61be9586d5`，`module/playlist_add.js`、`playlist_del.js`、`playlist_detail.js`。2026-11-09 对其 README.md:1908 文档公开歌单进行了单次无账号元信息请求，确认 `data[]` 响应结构；无 SSA、未进行账号写入。这与隔离替身的请求测试不是同一种证据。
- WY api-enhanced v4.41.1 的 `module/playlist_subscribe.js:5–13` 默认 EAPI `/api/playlist/subscribe|unsubscribe` 并强制 `checkToken=v2`；`util/request.js:219/402` 注入实时 token。上游字符串 `t='1'` 路径 body 仅含 `id`，header 仍取 v2 token；本端不复制旧静态 checkToken。

`MusicNeteaseTokenProvider` 仅在订阅状态确需改变时调用独立 `netease.token.v1` 模块，输入为空对象，不传 Cookie。它校验精确 envelope、固定错误码及非空 RFC6265 cookie-octet token；空白、控制字符、非 ASCII、分号、逗号、双引号和反斜杠均拒绝。token 不缓存、落盘或进入错误；准备失败不建立 pending，保留本地引用与同步意图。取回后重新核验 round、账号、同步授权和 CAS，再持久化围栏并派发；派发之后仍只有完整稳定目录读回目标 presence 才能解除围栏，HTTP 成功不能代替读回。

App 启动、账号操作、脚本列表及普通自管同步不调用 token provider。runtime 的 token HTTP 交换单独使用 60 秒预算，覆盖公共 Node 的 45 秒执行预算，仍响应主动取消；其余交换沿用原 30 秒预算。SDK 获取错误不使原生曲库、账号及播放整体不可用。

新增行为测试位于 `MusicNativeSyncTests.NeteaseToken.cs`、`MusicNativeSyncTests.NetworkPlaylists.cs`、`MusicNativeCatalogTests.NetworkPlaylists.cs` 与 `MusicCatalogTests.NetworkPlaylists.cs`，进入现有 LibrarySync／Catalog runner；`RuntimeHttpClientTests` 覆盖五模块、唯一路由、错误枚举和预算／取消。token 接入的测试及增量 App 构建由主会话集中执行；真实官方订阅写入未验证。
