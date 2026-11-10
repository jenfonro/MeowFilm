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

## 当前命令与类型

产品行为以[播放来源、队列与自动续播](../common/music/playback-sessions.md)为准；本文件记录 Windows 协调器的类型、事务与生命周期。

```text
music.recommendations.start / get / choose / continue / retry / return / cancel / reconcile
```

`start` 的本端参数：

| kind | 参数与提交 |
| --- | --- |
| `fm` | `mode=default|familiar|explore|puzzle|scene`、`submode`，或 `ruleId/tabTagId`；同一 FM lineage 的频道切换使用 append + selectIndex |
| `similar` | `sourceType=artist|song`、`sourceIds`（协议最多 20 个 ID）；始终独立 enter，不捕获 checkpoint |
| `heartbeat` | `entry=favorites|current`，首页保留 `songId`；current 必须带真实 `songId/playlistId`，播放器另带 `seedEntryId`；捕获 checkpoint，不提供 continue |
| `queue` | 必须带当前 occurrence 的 `seedEntryId`；`restore=false` 为只读候选，`restore=true` 为主动 enter 并捕获 checkpoint；只要求种子可解析成网易 ID，不要求整队网易身份 |

当前只接 `platform=wy`、本地播放；其他平台返回 `UNSUPPORTED`，`local=false` 返回 `REMOTE_NOT_SUPPORTED`。已有共享资料中的网易 ID 由 `MusicFeedbackIdentity.Resolve` 解析，不为菜单补搜。

状态投影：

```text
id revision kind platform active status loading continue play error
batch canRetry canReturn requiresReconcile reconcileIds
queueVersion currentId anchorId count failures
candidates:[{id,song}] exhausted seedSongId started offset
sourceType heartbeatPlaylistId mode submode ruleId tabTagId
```

`Session.Config` 是来源配置，队列 `playback-settings.mode` 是唯一全局循环值（1 列表／2 随机／3 单曲），checkpoint 不包含该设置。待进入来源暂存 `PreviousSession`；取得可用批次并经唯一队列 owner 提交后才转移来源所有权，失败保留原来源与媒体。

`MusicQueueRecommendationsRequest(Platform, SeedSongId, Offset)` 的 seed 在会话中固定，分页仅前移服务端 offset；[平台配方](music-native-platforms.md#网易云漫游与推荐)不携带当前整队或搜索上下文。

## 候选与续取实现

- 候选 token 属于当前 epoch 和候选批；推荐候选提交时重读实际队列、剔除已入队录音，再一次 append CAS。有效 PlayNext、模式变化和导航不废弃原 seed／候选；队列替换与账号失效有独立 fence。
- `OnPlaybackAsync` 消费真实 `MusicRecommendationPlaybackState`。`PlaybackEvent` 来自首次有效 Started；暂停／恢复、seek 和重复 Started 不增加事件号。随机取歌判定使用独立 `Random.Shared.Next(2)`，与队列随机选歌不共享抽签。
- `MaybeBeginLocked` 按共同契约决定何时取歌；同一 `Flight`／generation 防止并发读取，`Attempted` 避免同尾项／游标失败后忙重试。没有计时冷却、队列长度阈值或随机轮次限制。
- `TryAdvanceAsync` 只在已有候选需要并入时等待本地 CAS，不等待 provider HTTP；随后由唯一队列 owner 执行实际循环与插队优先级。普通续批不带 selectIndex，不抢当前媒体。
- 搜索候选仅追加时 `started` 仍为 false，队列 source 保持搜索；全局随机可继续选旧 seed／插曲。唯一队列 writer 真正选择活会话推荐 occurrence 时，在该次选择 CAS 中切换 source，成功回执同步激活推荐，再允许后续普通追加保留会话。显式 choose／主动 enter 同样以实际提交的选择为准，不从持久条目 context 重建活会话。
- FM 显式换频道的首批相对旧队列不去重，创建新 occurrence 并选择新批真实首项；全部与旧批重复也能进入。正常后续取歌仍按已有录音去重。
- 关闭 continue 保留候选及 seed；在途读取可完成为候选，但不自动并入。已准入 CAS 等待实际结算，不回滚已提交内容。最后候选消费后才按 hasMore 结束；空批／全部重复同样停止自动链。
- 心动与主动推荐共用 retainCurrent restore；相似、FM 和搜索候选没有返回 checkpoint。完整心动结果及缺失详情由平台 owner 解析，不虚构更多心动接口。

## 本机 FM 记忆

`MusicFmPreferences` 使用 `music.fm-selection.v1`，已注册到 `AppIdentity.NativeNamespaces`。文档为 `schema=1/accounts:{<真实网易 userId>:{mode,submode,ruleId,tabTagId}}`。不存凭据、队列或活会话，不进入曲库同步。

首页不指定 mode 的 FM start 在账号首次观察后读取该账号选择；无记录使用 default。只有账号 lease 和已提交 FM 配置仍属当前来源时才保存；切换失败、取消、旧响应不能覆盖。未知提交须显式 reconcile，确认成功后才保存真实配置。

## 唯一队列 lane

所有会改变队列或当前选择的入口必须经过同一个 `MusicRecommendationQueuePort`：

```text
music.queue.select / next / previous / step
music.queue.append / remove / reorder / replace / playCollection / clear / mode
music.history.restorePlaylist
music.catalog.playlist.commit action=play|next
```

catalog 的 `action=save` 不替换队列，但仍与同一个 library owner 串行，防止交叉 CAS。

`music.queue.recommendation.*` 是协调器与 library owner 的私有事务协议；普通 application 调用必须返回 `CAPABILITY_UNAVAILABLE`。私有协议只处理当前推荐批次的 capture、enter、append、select、restore、discard 和 reconcile，不暴露给页面。

## 请求与会话生命周期

- 一个协调器同一时刻只有一个当前会话；被替换会话的物理工作仍须排空。
- 同一 session 的读取由 `Flight` 和 generation 串行；UI 绑定、状态通知和集合变化不得再次发起同一批请求。
- start、choose、retry、继续加载、自然结束推进和返回均有明确 generation 与取消边界。
- 模式切换只有在新批次读取并提交成功后才成为当前配置；失败或取消恢复最后一次已成功配置，不采用未提交状态。
- 注册首个推荐 session 前的账号权威观察复用 `MusicAccountOwner` 的首次观察 flight；相同配置重复 start 不重复读取整份账号文档。每个真实推荐批次仍单独取得并核对当前账号 lease，旧请求的鉴权失败不能撤销并发登录产生的新 lease。
- 浏览页面或隐藏视图不退出已提交来源；媒体绑定由模块关闭／释放生命周期管理，不将可见性变化解释为全量刷新。

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
- 心动／主动推荐返回保持同一当前 occurrence，不发送媒体 selection；既有队列刷新更新界面与反馈来源，保留音频实例、位置及播放／暂停状态。
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
- 心动 restore 的成功及丢 ACK 后显式核对均不派发媒体 selection，也不改写播放／暂停意图；确认未应用时保留 checkpoint 和当前推荐曲，允许再次返回。返回流程在确认已应用后消费 checkpoint、退休会话并清空推荐 proof。
- 全部未知项结算后才恢复推荐准入。

未确认的 pending 只通过对应 owner 的 reconcile 结算。已提交但队列读回失败时保留协调状态，且不把成功提交后的回读失败改记为 `COMMIT_UNKNOWN`。owner 只保留最近一次推荐结果回执，由下一次推荐提交覆盖；replay 和 reconcile 仍按 nonce 读取该回执。

返回原队列所需 checkpoint 按实际保留字节管理（8 MiB），由恢复、discard 或关闭释放，不另设四个 checkpoint 的数量门槛。

推荐平台响应保留 512 KiB 字节资源边界，曲库仍执行请求／响应字节和持久 envelope 的资源校验。心动及 scene 单曲推荐不另设条数裁剪；FM 三首、规则五首和 SIMILAR 三首仍是各自的协议批次。

## 关闭

1. `MusicCapability` 同步封闭页面和播放器推荐入口。
2. 取消当前 generation 的提交／媒体资格，排空已准入读取、队列事务、通知和媒体 callback。窄推荐准备不取消已准入的 Catalog 读取或共享账号初始观察，必须等待它们真实结算；退出的启动订阅者不再继续派发。
3. application 全局关闭先围住推荐 callback，再失效 Catalog／账号读取并排空其他音乐业务，不能以调用者退订当作底层排空。
4. 最后准备唯一 `MusicLibraryOwner`。

取消关闭创建新的推荐生命周期，不恢复旧 session、旧 callback 或旧播放意图。最终释放时先 dispose 协调器，再释放它借用的 catalog 和 library。
