# 原生音乐曲库同步

## 所有权

`MusicLibrarySyncService` 是网易云、QQ 音乐和酷狗曲库同步的唯一协调器。

- 它借用同一个 `MusicLibraryOwner`、账号会话 owner、[客户端 owner](music-platform-client-context.md)和固定平台 HTTP。
- 它不拥有第二份曲库、不启动 Node、不保存账号凭据。
- 同步状态位于 `music.library.v1` 的 `library-sync` area，并与曲库在同一 envelope CAS 中提交。
- binding map 的唯一方向是 `nativeId -> songId`。
- 按明确同步需求委托目录补读时，目录验证本次请求所用客户端快照和响应；成功后适配器显式取得已确认的当前快照再继续，不把合法委托更新误判为旧请求失效，也不通过重取代次绕过原请求校验。

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

`status.phase` 只允许：

```text
idle pushing synced partial blocked superseded
```

`pending` 是一次远端写入 fence，操作只允许：

```text
create delete add remove order
```

pending、binding、账号和集合必须彼此引用一致。格式错误或关系不一致会以 `CORRUPT_SYNC` 失败，原文档不写回。

## 同步流程

1. 初始化只建立本地生命周期，不访问平台；首次计划轮最早在五分钟后触发。
2. 同一时间只允许一个 round；账号变化、设置变化和关闭会提升 generation 并取消旧 round。
3. 只读取已启用平台和类型；未启用平台不发请求。
4. 每轮先完整读取必要远端目录和成员，再在内存中合并。
5. 本地曲库和同步状态通过 `CommitLibrarySyncAsync` 在同一 envelope CAS 中提交。
6. 每次远端写入前先耐久写入 pending fence。
7. HTTP 返回或异常都不直接视为远端写入结果；必须重新完整读取并确认实际状态。
8. 未确认结果保持受阻，不自动重放 create/delete/add/remove/order。
9. 设置页明确启用、确认或用户明确同步会立即进入同一协调器；计划轮只接受 `idle`／`synced` 状态，`blocked`、`partial`、确认及 pending 不会形成后台重试环。

## 冲突和确认

- 本地集合 revision、同步 state revision、账号代次和 owner snapshot 都参与准入。
- 远端相对 baseline 已变化时返回 `REMOTE_CHANGED`，不覆盖远端新状态。
- 删除远端歌单、大批删除和空远端引发的破坏性变化必须产生确认 token。
- 确认只批准当前 token 对应的当前状态；revision 或证据变化后旧确认失效。
- `resetAccount` 只撤销该平台同步授权、账号绑定和 binding；不删除本地歌曲／歌单，也不访问远端。

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

- 每轮最长四分钟，并有明确取消 token。
- 重复 `run` 不并行扩散，返回 `SYNC_BUSY`。
- 同一轮所有原生 HTTP、账号目录、映射搜索和酷狗云调用共享一个物理预算：每平台最多 96 次，整轮最多 192 次。
- 每个平台一轮最多同步 40 个集合；目录最多 20 页，集合成员最多 40 页；跨平台录音映射最多尝试 24 首，其余保留为后续明确轮次。
- 拉取阶段已经确认且没有本地变化的集合直接复用同轮快照；不会为了证明“无需写入”再次读取整张远端歌单。
- 唯一 pending fence 每轮只恢复一次；恢复未确认即停止，不存在循环读回。
- 页面隐藏只停用 UI 观察者，不撤销已保存授权，不另建 scheduler。
- 正常关闭取消 scheduler、等待当前 round 和控制命令物理结束，再进入 library 关闭。
- 取消关闭只重新启动原协调器；不恢复旧请求、不重放 pending 远端写入。
