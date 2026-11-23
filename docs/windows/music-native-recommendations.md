# 原生持续推荐消费闭包

## 边界

持续推荐由以下当前组件共同拥有：

```text
MusicNativeApplication
MusicRecommendationCoordinator
MusicRecommendationQueuePort
MusicLibraryOwner
MusicCapability
```

推荐读取使用固定平台原生 catalog；最终队列提交使用唯一 `MusicLibraryOwner`。Node 不参与推荐、队列、账号或播放状态。

## 媒体边界

`MusicNativeApplication` 直接暴露推荐协调入口：

```csharp
event Action<JsonElement>? RecommendationChanged;
IDisposable BindSelection(
    Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync);
Task OnPlaybackAsync(
    MusicRecommendationPlaybackState state,
    CancellationToken ct = default);
Task<bool> TryAdvanceAsync(
    string entryId,
    bool naturalEnd,
    bool play,
    CancellationToken ct = default);
MusicRecommendationFeedback? GetFeedback(string entryId);
Task PrepareRecommendationsAsync(CancellationToken ct = default);
```

规则：

- 同时只允许一个媒体 consumer。
- selection 已指向完成提交的 occurrence；媒体 consumer 不再次调用 `queue.select`。
- callback 在实际解析和开播前后都核对 session、entry、queue version、application generation 和 binding generation。
- 取到推荐歌曲不等于播放成功；只有真实播放器事件更新播放观察和反馈。

## 唯一 queue lane

`MusicRecommendationQueuePort` 是一个 admission lane，不是第二个存储 owner。

lane 实际取得后，队列事务和快照计算在后台执行；热文档、可立即取得的 lane 或同步完成的存储任务不会把整份文档克隆及歌曲投影留在 UI DispatcherQueue。普通下一首没有有效推荐会话时直接交回普通队列操作，不先读取完整推荐快照；有效会话仍在读取后核对当前会话、队列代次与选择，取消和 lane 释放语义不变。

会改变队列或选择的当前入口：

```text
music.queue.select
music.queue.next
music.queue.previous
music.queue.step
music.queue.append
music.queue.remove
music.queue.reorder
music.queue.replace
music.queue.playCollection
music.queue.clear
music.queue.mode
music.history.restorePlaylist
music.catalog.playlist.commit action=play|next
```

网络歌单收藏调用 `music.catalog.playlist.save`，传入远端 `platform/id/name` 及必要展示信息，由唯一 library owner 保存引用，无须先 `prepare` 或读取完整成员；不退休推荐、不改变队列或当前 selection。播放时单独 `prepare`，再用 `commit action=play` 进入上述 queue lane，在最终 CAS 前围住旧 selection。共同规则见[歌单所有权与订阅关系](../common/music/playlist-ownership.md)。

`music.queue.recommendation.*` 只供协调器内部使用；普通 application 调用返回 `CAPABILITY_UNAVAILABLE`。

## 启动和读取

- `music.recommendations.start` 在需要时先加入账号 owner 的唯一首次权威观察，再注册协调器 session；一次成功后，相同配置重复 start 不再次读取整份账号文档。
- 每个实际平台批次只捕获该批请求取得的账号 lease。
- 账号失效只撤销失败批次的 lease。
- 未绑定播放 consumer 时 start 返回 `PLAYBACK_UNAVAILABLE`。
- 同一完整配置的重复 start、同一 session 的重复读取、继续加载和自然结束推进由协调器去重；在途 start 复用同一物理读取及最终快照，不通过 UI 绑定或集合变化递归触发。

## 消费者投影

业务行为只引用[播放来源、队列与自动续播](../common/music/playback-sessions.md)；协调器命令、参数、存储与资源边界见[原生持续推荐](music-recommendations.md)。

- `MusicLibraryController.PlaySelectionAsync` 捕获真实搜索直接播放动作，在打开媒体前登记 occurrence／播放 epoch；该动作真实首次 Started 后才异步调用 preview，不以 Open 返回替代 Started。暂停恢复、seek、同 occurrence 换源不重建；未开播便失败或被替换不会请求。普通首页／歌单播放、浏览搜索页及队列长度不产生推荐来源。
- `MusicRecommendationQueueChange.PlayNext` 与 `Append` 分开分类；catalog `action=next` 同样走前者。模式不改 epoch，替换／清空／历史恢复改 epoch 并退休来源。
- `RecommendationCandidates` 只投影真实候选 token 和歌曲；`ChooseRecommendationAsync` 校验行身份与当前 session，再交唯一协调器处理。
- `PlaybackQueueSourceText` 读取队列 source 和已提交 session；`PlaybackModeIcon/Text` 同时读取已提交来源、续播与循环值来表达实际方式；`HasPlaybackFm/PlaybackFmTitle` 控制统一模式菜单的频道入口。没有推荐成功／退出专用状态块、事件或视图。
- 真实失败通过既有 `Status`／`NoticeOutlet` 提醒；用户取消、被替换的旧请求与正常耗尽不生成通知。未知提交的核对操作仍在候选区可达。
- `MusicRecommendationPlaybackState.PlaybackEvent` 随真正开播增加；媒体观察投影去重，UI 重绘和 seek 不创造新的续取事件。`TryAdvanceAsync` 返回 false 后仍执行现有 queue.next，不等待网络。
- 主动推荐与心动 retainCurrent restore 后只刷新实际队列与反馈证明，不重开、Stop 或 Resume 相同音频。相似没有返回动作。

`MusicRecommendationFeedback` 的 Provider／Algorithm／FeedbackSource 与 session／batch／entry proof 只在活会话中有效，不从持久 queue context 恢复；反馈授权和账号校验仍属于既有反馈职责。

## 未知提交

推荐队列提交若返回 `COMMIT_UNKNOWN`：

- 唯一 library owner 保留未知 nonce 和 CAS fence。
- 未知回执保持推荐读取和提交封闭，直到显式核对完成。
- 只允许读取状态、取消和显式 `music.recommendations.reconcile`。
- reconcile 只读取真实持久结果；不重放未知队列写入。
- 全部未知项结算后才恢复推荐 admission。

## 进程内退役

`MusicCapability` 同步封闭播放器入口，推荐 owner 取消并排空启动读取、flight、通知与媒体 callback。App 同时停止共享执行端；application 等待已准入事务完成后释放协调器及其借用关系。退役单向完成，不恢复旧推荐 generation。

准备阶段仍报告 `COMMIT_UNKNOWN`；Dispose／Abort 只报告实际资源释放失败。已结束但回执未知的事务不阻止释放，也不重放写入或假定回滚。正常整进程退出不等待全部推荐任务，见[App生命周期](app-runtime.md)。
