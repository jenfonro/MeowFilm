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
