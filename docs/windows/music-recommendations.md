# 原生持续推荐

## 所有权

持续推荐由以下当前组件共同拥有：

```text
MusicNativeApplication
MusicRecommendationCoordinator
MusicRecommendationQueuePort
MusicLibraryOwner
MusicCapability
```

- `MusicRecommendationCoordinator` 拥有推荐会话、请求代次、继续条件、反馈来源和未知提交状态。
- `MusicRecommendationQueuePort` 是覆盖唯一 `MusicLibraryOwner` 的队列准入 lane，不是第二个存储 owner。
- 固定平台目录负责读取推荐批次；Node、LX 脚本和页面绑定不参与推荐编排。
- 推荐会话只存在于当前进程；持久化的仍是唯一曲库 owner 中的队列、历史和设置。

## 当前命令

```text
music.recommendations.start
music.recommendations.get
music.recommendations.continue
music.recommendations.retry
music.recommendations.return
music.recommendations.cancel
music.recommendations.reconcile
```

`start` 当前接受：

- `kind=fm`：网易云 `default|familiar|explore|puzzle|scene`。
- `kind=similar`：1 至 20 个明确的网易云歌曲 ID。
- `kind=heartbeat`：明确的 playlist、start music 和 song ID。

当前实现只允许 `platform=wy` 和本地播放；其他平台返回 `UNSUPPORTED`，`local=false` 返回 `REMOTE_NOT_SUPPORTED`。同一活跃推荐使用完全相同的完整配置重复开始时复用当前会话、在途任务及最终快照，不重复请求。

状态包含：

```text
id revision kind platform active status loading waiting continue play error
batch canRetry canReturn requiresReconcile retryAt reconcileIds
queueVersion currentId anchorId count failures
```

页面只投影该状态，不另建推荐请求、影子 loading 或第二套重试状态。

## 唯一队列 lane

所有会改变队列或当前选择的入口必须经过同一个 `MusicRecommendationQueuePort`：

```text
music.queue.select / next / previous / step
music.queue.append / remove / reorder / replace / playCollection / clear / mode
music.history.restorePlaylist
music.catalog.playlist.commit action=play
```

catalog 的 `action=save` 不替换队列，但仍与同一个 library owner 串行，防止交叉 CAS。

`music.queue.recommendation.*` 是协调器与 library owner 的私有事务协议；普通 application 调用必须返回 `CAPABILITY_UNAVAILABLE`。私有协议只处理当前推荐批次的 capture、enter、append、select、restore、discard 和 reconcile，不暴露给页面。

## 请求与会话生命周期

- 一个协调器同一时刻只有一个当前会话；被替换会话的物理工作仍须排空。
- 同一 session 的读取由 `Flight` 和 generation 串行；UI 绑定、状态通知和集合变化不得再次发起同一批请求。
- start、retry、继续加载、自然结束推进和返回均有明确 generation 与取消边界。
- 模式切换只有在新批次读取并提交成功后才成为当前配置；失败或取消恢复最后一次已成功配置，不采用未提交状态。
- 注册首个推荐 session 前的账号权威观察复用 `MusicAccountOwner` 的首次观察 flight；相同配置重复 start 不重复读取整份账号文档。每个真实推荐批次仍单独取得并核对当前账号 lease，旧请求的鉴权失败不能撤销并发登录产生的新 lease。
- 页面隐藏只解除观察和媒体绑定，不将可见性变化解释为全量刷新。

## 媒体绑定

`IMusicRecommendationSource` 向唯一播放协调提供：

```csharp
IDisposable BindSelection(
    Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync);
Task OnPlaybackAsync(MusicRecommendationPlaybackState state, CancellationToken ct = default);
Task<bool> TryAdvanceAsync(string entryId, bool naturalEnd, bool play, CancellationToken ct = default);
MusicRecommendationFeedback? GetFeedback(string entryId);
Task PrepareRecommendationsAsync(CancellationToken ct = default);
```

规则：

- 同时只允许一个媒体 consumer。
- selection 已指向由唯一 library owner 完成提交的 occurrence；consumer 不再次执行 `queue.select`。
- 异步解析前后都调用 `selection.IsCurrent()`，并核对 entry、queue version、application generation 和 binding generation。
- 只有实际播放器 Started／Ended／暂停／恢复事件更新推荐播放观察；队列选择或取得 URL 不等于播放成功。
- 反馈只属于当前活会话中实际入队的 occurrence，不从持久 queue context 恢复算法会话。

## 未知提交

队列 CAS 返回 `COMMIT_UNKNOWN` 或无法确认的冲突时：

- 唯一 library owner 只保留当前未确认操作的 nonce 和 before／after 指纹，不累计历史回执。
- 协调器将对应会话加入 `reconcileIds`，封闭新的推荐读取和写入。
- 仅允许 get、cancel 和显式 reconcile。
- reconcile 只读取当前持久文档判断该 nonce 是否生效，不重放 enter、append、select 或 restore。
- 全部未知项结算后才恢复推荐准入。

未确认的 pending 只通过对应 owner 的 reconcile 结算。已提交但队列读回失败时保留协调状态，且不把成功提交后的回读失败改记为 `COMMIT_UNKNOWN`。owner 只保留最近一次推荐结果回执，由下一次推荐提交覆盖；replay 和 reconcile 仍按 nonce 读取该回执。

返回原队列所需 checkpoint 按实际保留字节管理（8 MiB），由恢复、discard 或关闭释放，不另设四个 checkpoint 的数量门槛。

## 关闭

1. `MusicCapability` 同步封闭页面和播放器推荐入口。
2. 取消当前 generation，排空读取、队列事务、通知和媒体 callback。
3. application 排空其他音乐业务。
4. 最后准备唯一 `MusicLibraryOwner`。

取消关闭创建新的推荐生命周期，不恢复旧 session、旧 callback 或旧播放意图。最终释放时先 dispose 协调器，再释放它借用的 catalog 和 library。
