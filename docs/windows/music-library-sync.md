# 原生音乐曲库同步

内容分类、订阅副作用与跨端规则以[共同歌单契约](../common/music/playlist-ownership.md)为准；本页仅说明 Windows 协调器、适配器、journal 和生命周期事实。

## 所有权

`MusicLibrarySyncService` 是网易云、QQ 音乐和酷狗曲库同步的唯一协调器。

- 它借用同一个 `MusicLibraryOwner`、账号会话 owner 和[平台客户端](music-platform-client-context.md)；`MusicSyncAdapter` 调共同 Node 配方，不执行原生平台 HTTP。
- 它不拥有第二份曲库或运行器，不保存账号凭据；网易订阅准备在 Node 内借用既有 token owner。
- 同步状态位于原生音乐数据库的 `library-sync` area，并与相关曲库控制在同一 SQL 事务提交。checkpoint 不含 songs；歌曲采用由同一 owner 的逐记录事务完成，不能从旧计划替换仓库。
- binding map 的唯一方向是实际成员 key → 永久 `songId`。
- 普通目录／集合读取只消费共同结果；远端写所需 baseline、preflight、journal 和 readback 由同步 engine 独立负责，不把事务证明施加到普通播放读取。
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

`library-sync` 消费以下状态：

```text
schema
revision
platforms
pending
confirmation
approved
status
```

未消费扩展随当前事务保留。同步读取才核对实际整库引用、账号绑定和 pending／confirmation 关系；无关队列或设置不参与该校验。

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
5. 已登记成员按来源键查询；新来源由 `AdoptLibrarySyncSongsAsync` 采用。`CommitLibrarySyncAsync` 仅提交控制状态，不扫描或复制歌曲。
6. 每次远端写入前先耐久写入 pending fence，结束本地 SQL 事务后才执行网络请求。
7. Node 回执区分 `confirmed/notSent/rejected/unknown`。明确未发送／拒绝按真实回执结算 fence，已发送未知不当作回滚；确认目标状态仍由事务 readback 完成。
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
- Node 平台调用、账号目录和映射搜索按真实分页及取消运行，不共享每平台／整轮请求次数额度；完整状态证据仅服务实际写事务。
- 集合、目录页、成员页和录音映射不再按累计次数截断，也不设固定歌单歌曲数量。平台返回结束、游标停滞、快照变化及错误必须各自明确处理；持久曲库容量仍按现行数据契约校验。
- 拉取阶段已经确认且没有本地变化的集合直接复用同轮快照；不会为了证明“无需写入”再次读取整张远端歌单。
- 唯一 pending fence 每轮只恢复一次；恢复未确认即停止，不存在循环读回。
- 页面隐藏只停用 UI 观察者，不撤销已保存授权，不另建 scheduler。
- 正常关闭取消 scheduler、等待当前 round 和控制命令物理结束，再进入 library 关闭。
- 取消关闭只重新启动原协调器；不恢复旧请求、不重放 pending 远端写入。

## 平台订阅请求与实际能力边界

| 平台 | 当前适配 |
| --- | --- |
| WY | `sync.prepare` 返回短期 preparationId，Node 内部按需取得 token；原生在真实 journal 准入后用 `sync.write` 消费，不持有 token。 |
| TX/KG | `sync.target/read/write` 保留真实远端定位与 locator，Node 负责平台回执；原生继续 journal、破坏性确认和目标效果读回。 |
| KW/MG | 无账号同步设置或远端订阅端口；仅使用本地引用命令。 |

`MusicSyncAdapter` 在调用前后保留 checkpoint／账号 guard；只把真实 `notSent/rejected` 分类为可明确结算的未应用，缺失／坏回执为 unknown。Node preparation 失败不产生远端写；写已派发后的页面取消不抹掉真实回执，不重放未知操作。

请求配方与 token 的共同来源见[平台请求](../common/music/platform-client-context.md)、[音乐平台运行器](../runtime/music-platform.md)和[网易 token](../runtime/netease-token.md)，上游 pin／许可归根 `LICENSES/NOTICE.md`。本轮同步、真实官方写入及增量构建均未验证。
