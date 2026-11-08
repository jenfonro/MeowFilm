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
music.recommendations.choose
music.recommendations.continue
music.recommendations.retry
music.recommendations.return
music.recommendations.cancel
music.recommendations.reconcile
```

`start` 当前接受：

- `kind=fm`：网易云 `default|familiar|explore|puzzle|scene`。
- `kind=similar`：1 至 20 个明确的网易云歌曲 ID。
- `kind=heartbeat`：`entry=favorites|current`；current 携带种子曲的 `songId`。播放器入口同时携带当前 occurrence 的 `seedEntryId`，严格核对当前 occurrence 和 native ID；浏览歌单歌曲入口只携带 `songId`，允许空队列或不同当前曲。喜欢列表 ID、首曲、随机喜欢种子及完整喜欢数量由同一账号 lease 的平台读取解析。
- `kind=queue`：读取当前完整网易云队列及实际当前 native ID，只生成候选，不改队列。整队每条歌曲均须具有有效网易云 native ID；混合平台或缺少该 ID 的队列返回 `NATIVE_ID_REQUIRED`，不筛选子集请求。请求按队列顺序保留每个 occurrence 的 native ID，包括重复 ID；仅新增推荐候选按 native ID 去重。

当前实现只允许 `platform=wy` 和本地播放；其他平台返回 `UNSUPPORTED`，`local=false` 返回 `REMOTE_NOT_SUPPORTED`。同一活跃推荐使用完全相同的完整配置重复开始时复用当前会话、在途任务及最终快照，不重复请求。

状态包含：

```text
id revision kind platform active status loading waiting continue play error
batch canRetry canReturn requiresReconcile retryAt reconcileIds
queueVersion currentId anchorId count failures
candidates:[{id,song}] exhausted
mode submode ruleId tabTagId
```

FM 的 `mode/submode/ruleId/tabTagId` 来自最后提交成功的配置；首批尚未提交时为空，切换在途、失败或取消不显示草稿。页面只投影该状态，不另建推荐请求、影子 loading 或第二套重试状态。

## 心动与普通队列推荐

心动首批完整消费平台返回的有效条目，按 native recording 去重；播放器当前曲入口保留实际当前 occurrence，浏览曲种子恰与实际当前曲匹配时也复用该 occurrence，否则与喜欢入口一样以返回结果建立队列。首批不裁成三首，也不固定请求一百首。心动保留原队列 checkpoint，返回时恢复原 occurrence、原来源和选择；默认续播关闭，显式开启后使用桌面更多推荐读取后续批次。

普通 `queue` 候选与已入队 occurrence 分开：

- `start` 和未处于自动尾部追加条件的 `retry` 只生成候选。原列表歌曲、来源、当前 occurrence 和持久版本不变；候选尚无反馈 proof。
- `choose {id,candidateId}` 使用当前批专有的不透明候选 token，把该批所有新曲一次追加并选择所点候选，由唯一 queue writer 完成一次 CAS 后再请求播放。旧批 token、旧队列版本和旧账号结果不能提交。
- 默认续播关闭；开启后，本地播放器实际播放到真实队尾时优先消费已有候选批，复用候选的队列版本／epoch 校验和同一 append CAS，不重复请求上游；没有候选且尚未结束时才读取新批。最后一批候选的 `hasMore=false` 在消费后终止后续请求。追加保留当前选择，不触发媒体开播；下一首或自然结束复用在途等待，真实 Ended 可续接先到达的停止状态。等待期间暂停会阻止迟到开播。
- 关闭续播取消尚未提交的工作，保留已入队内容。CAS 已准入后等待实际结算，同步已提交的队列事实，继续封闭该请求的迟到播放意图。
- 空批、原生 ID 全重复或上游 `hasMore=false` 结束当前自动链；仍有有效新曲的最后一批完整追加。失败可显式 `retry`，不伪造 offset 或游标。

普通与心动续播使用已证实的 Windows `homepage/more/rcmd/song` 配方，request source 为 `curlist_more_rcmd`；普通推荐保持 `kind=queue`，不冒用 SIMILAR 或 heartbeat。该端点的逐曲 `algMap/sourceMap` 经平台读取转为 live proof；provider 与 `FeedbackSource` 分离，真实逐曲 source 优先，缺失时为 `moreRcmd`，心动首批为 `aiMode`。原队列 context 不被这些推荐来源替换。

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
- start、choose、retry、继续加载、自然结束推进和返回均有明确 generation 与取消边界。
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
- `MusicRecommendationFeedback.FeedbackSource` 只随 live proof 传递，不启用反馈 consent、不增加历史采集；准确 native recording、live occurrence 和账号代次仍由反馈消费者验证。
- 账号失效撤销整份活会话 proof；停止播放仅针对该会话实际入队且当前正在选择的 occurrence。只读预览或自动追加保留的原列表当前曲不因此被停止。

## 未知提交

队列 CAS 返回 `COMMIT_UNKNOWN` 或无法确认的冲突时：

- 唯一 library owner 只保留当前未确认操作的 nonce 和 before／after 指纹，不累计历史回执。
- 协调器将对应会话加入 `reconcileIds`，封闭新的推荐读取和写入。
- 仅允许 get、cancel 和显式 reconcile。
- reconcile 只读取当前持久文档判断该 nonce 是否生效，不重放 enter、append、select 或 restore。
- 全部未知项结算后才恢复推荐准入。

未确认的 pending 只通过对应 owner 的 reconcile 结算。已提交但队列读回失败时保留协调状态，且不把成功提交后的回读失败改记为 `COMMIT_UNKNOWN`。owner 只保留最近一次推荐结果回执，由下一次推荐提交覆盖；replay 和 reconcile 仍按 nonce 读取该回执。

返回原队列所需 checkpoint 按实际保留字节管理（8 MiB），由恢复、discard 或关闭释放，不另设四个 checkpoint 的数量门槛。

推荐平台响应保留 512 KiB 字节资源边界，曲库仍执行请求／响应字节和持久 envelope 的资源校验。心动及桌面更多推荐不另设条数裁剪；FM 三首、规则五首和 SIMILAR 三首仍是各自的协议批次。

## 关闭

1. `MusicCapability` 同步封闭页面和播放器推荐入口。
2. 取消当前 generation 的提交／媒体资格，排空已准入读取、队列事务、通知和媒体 callback。窄推荐准备不取消已准入的 Catalog 读取或共享账号初始观察，必须等待它们真实结算；退出的启动订阅者不再继续派发。
3. application 全局关闭先围住推荐 callback，再失效 Catalog／账号读取并排空其他音乐业务，不能以调用者退订当作底层排空。
4. 最后准备唯一 `MusicLibraryOwner`。

取消关闭创建新的推荐生命周期，不恢复旧 session、旧 callback 或旧播放意图。最终释放时先 dispose 协调器，再释放它借用的 catalog 和 library。
