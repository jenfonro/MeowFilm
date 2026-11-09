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

## 媒体 seam

```csharp
public interface IMusicRecommendationSource
{
    event Action<JsonElement>? Changed;
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
}
```

规则：

- 同时只允许一个媒体 consumer。
- selection 已指向完成提交的 occurrence；媒体 consumer 不再次调用 `queue.select`。
- callback 在实际解析和开播前后都核对 session、entry、queue version、application generation 和 binding generation。
- 取到推荐歌曲不等于播放成功；只有真实播放器事件更新播放观察和反馈。

## 唯一 queue lane

`MusicRecommendationQueuePort` 是一个 admission lane，不是第二个存储 owner。

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
music.catalog.playlist.commit action=play
```

`music.catalog.playlist.commit action=save` 不替换当前队列，但仍与同一个 library owner 串行。

`music.queue.recommendation.*` 只供协调器内部使用；普通 application 调用返回 `CAPABILITY_UNAVAILABLE`。

## 启动和读取

- `music.recommendations.start` 在需要时先加入账号 owner 的唯一首次权威观察，再注册协调器 session；一次成功后，相同配置重复 start 不再次读取整份账号文档。
- 每个实际平台批次只捕获该批请求取得的账号 lease。
- 账号失效只撤销失败批次的 lease。
- 未绑定播放 consumer 时 start 返回 `PLAYBACK_UNAVAILABLE`。
- 同一完整配置的重复 start、同一 session 的重复读取、继续加载和自然结束推进由协调器去重；在途 start 复用同一物理读取及最终快照，不通过 UI 绑定或集合变化递归触发。

## 原生命令与投影

`MusicNativeApplication.ExecuteAsync` 将全部 `music.recommendations.*` 命令交由同一 native coordinator，包含 `choose {id,candidateId}`；UI 不直接调用私有 `music.queue.recommendation.*`。

- `kind=queue` 的 start 从完整队列提取网易云 native IDs 与实际 current ID，只读返回 `candidates:[{id,song}]` 和 `exhausted`。start／retry／自动续播均要求整队每条歌曲有有效网易云 ID；混合或缺少 ID 时返回 `NATIVE_ID_REQUIRED`，不向平台发送过滤后的子集。候选 token 只属于当前候选批，retry、队列编辑、会话替换和账号失效都会使旧 token 失效。
- choose 在同一 lane 核对队列版本／current 后，将完整新曲批次 append，并以私有 `selectIndex` 选中候选；一次 envelope CAS 后才派发一个媒体 selection。自动尾部 append 不带 `selectIndex`，不抢当前播放。
- 自动尾部追加优先消费已有候选批，沿用该批队列版本／epoch 和同一 CAS，不另发请求；未消费的最后一批候选不会因 `hasMore=false` 被提前标记 exhausted。消费后按该批 hasMore 决定是否结束；后续请求仍按原队列 occurrence 顺序保留重复 native ID。
- continue 关闭后不再准入旧请求结果；已准入的 CAS 仍等实际结算。快速关闭／重开和初始预览转入尾部追加时，next／Ended 的等待跟随实际后继 flight，不先停止或漏过新批次。
- 心动 native 参数为 `entry=favorites|current` 和 current 的 `songId`／可选 `seedEntryId`。只有显式播放器 `seedEntryId` 要求严格匹配实际当前 occurrence 和歌曲 ID；浏览歌曲无此参数时允许空队列或不同当前曲，实际匹配的当前 occurrence 可自动复用。平台响应的真实喜欢列表 context 用于队列 context 和反馈来源；完整首批入队，默认关闭续播。
- 心动各返回入口共用 `music.recommendations.return`，在 owner 的一次 restore CAS 中恢复原 checkpoint 并仅带回当前 occurrence，插入原选择之后；已存在不重复，空原队列保留当前，同录音不同 occurrence 不合并。返回保持 currentId，不发送 selection，现有 UI 队列刷新保留媒体实例、进度及播放／暂停状态，同时撤销已退休会话的反馈 proof。相似推荐仍恢复原队列与原选择。
- FM snapshot 的 `mode/submode/ruleId/tabTagId` 是已提交配置；尚未提交、切换失败和旧响应不污染播放器显示。

逐曲反馈通过 `MusicRecommendationFeedback` 的 `Provider/Algorithm/FeedbackSource` 与 session／batch／entry proof 传递。候选读取及原队列条目不凭推荐标签获得 proof；只有当前活会话实际入队的 occurrence 可读取。sourceMap 缺失为 `moreRcmd`，心动首批为 `aiMode`；反馈 consent 和账号校验仍由既有反馈职责拥有。请求配方、结束条件和资源边界见[原生持续推荐](music-recommendations.md)。

## 未知提交

推荐队列提交若返回 `COMMIT_UNKNOWN`：

- 唯一 library owner 保留未知 nonce 和 CAS fence。
- 取消关闭可以恢复普通界面控制，但新的推荐读取和提交仍保持封闭。
- 只允许读取状态、取消和显式 `music.recommendations.reconcile`。
- reconcile 只读取真实持久结果；不重放未知队列写入。
- 全部未知项结算后才恢复推荐 admission。

## 关闭

1. `MusicCapability` 先同步封闭播放器侧入口。
2. `PrepareRecommendationsAsync` 取消并排空启动读取、推荐 flight、通知和媒体 callback。
3. application 再排空其他业务，最后准备 library owner。
4. 取消关闭创建新 recommendation generation，不恢复旧 session 或旧 callback。
5. 最终释放时先 dispose 协调器，再释放它借用的 catalog 和 library。
