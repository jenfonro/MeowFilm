# 原生持续推荐

## 所有权

持续推荐由以下当前组件共同拥有：

```text
MusicNativeApplication
MusicRecommendationCoordinator
MusicLibraryOwner
MusicCapability
```

- `MusicRecommendationCoordinator` 拥有推荐会话、请求代次、继续条件和反馈来源。
- `MusicLibraryOwner` 是唯一队列存储 owner，推荐队列切换与追加直接在其串行写边界按 `expectedVersion` CAS 原子执行。
- 固定平台目录通过共同 Node 读取推荐批次；推荐会话、唯一队列 owner 和媒体编排仍由原生拥有，LX 脚本和页面绑定不参与。
- 推荐会话与返回点（checkpoint）已由 `MusicRecommendationStore` 持久化至永久库 5 张表 `windows_music_recommendation_{checkpoints,checkpoint_entries,checkpoint_lists,sessions,session_lists}`，支持冷启动恢复与离线保护 `returnQueue`；逐曲反馈 live proof 与在途请求状态仍仅属于当前进程。

## 当前命令与类型

产品行为以[播放来源、队列与自动续播](../common/music/playback-sessions.md)为准；本文件记录 Windows 协调器的类型、事务与生命周期。

```text
music.recommendations.start / get / choose / continue / retry / return / cancel
```

`start` 的本端参数：

| kind | 参数与提交 |
| --- | --- |
| `fm` | `mode=default|familiar|explore|puzzle|scene`、`submode`，或 `ruleId/tabTagId`；同一 FM lineage 的频道切换使用 append + selectIndex |
| `similar` | `sourceType=artist|song`、非空 `sourceIds`；实际所需定位交共同 Node 配方，上游拒绝按真实分类返回；始终独立 enter，不捕获 checkpoint |
| `heartbeat` | `entry=favorites|current`，首页保留真实网易种子，播放器另带 `seedEntryId`；本地融合收藏不传本地歌单 ID。实际喜欢歌单定位和请求由 Node 执行，不以旧完整成员证明准入。捕获 checkpoint；续批沿共同返回 context，以实际尾曲推进 |
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

`Session.Config` 是来源配置，队列 `playback-settings.mode` 是唯一全局循环值（1 列表／2 随机／3 单曲），checkpoint 不包含该设置。普通 mode 命令同时关闭续播；进入或选回特殊方式由协调器经同一队列 lane 一致设为列表顺序和续播开启。待进入来源暂存 `PreviousSession`；取得可用批次并经唯一队列 owner 提交后才转移来源所有权，失败保留原来源与媒体。

`MusicQueueRecommendationsRequest(Platform, SeedSongId, Offset)` 的 seed 在会话中固定，分页仅前移服务端 offset；[共同平台请求](../common/music/platform-client-context.md)不携带当前整队或搜索上下文，推荐候选规则见[推荐候选与插队](../common/music/playback-sessions.md#推荐候选与插队)。

## 播放器呈现

- 底部与全屏播放器复用 `MusicPlayerView`，只保留统一播放模式按钮，不再有独立 ∞。图标和选中项表达实际方式，推荐采用与 iOS 一致的 sparkles 元素；条目顺序与返回资格遵循共同契约。
- 模式菜单不显示来源标题、独立续播或“重试取歌”；候选区仍有实际错误重试和未知提交核对。菜单每次打开有独立代次，动作核对推荐命令／快照、当前播放及队列、Catalog action 和网易账号代次，旧 `MenuItem` 不能提交。
- 只有漫游提供“切换频道”。频道由桌面外壳在应用内容内承载，外侧完整点击只关闭并消费，不触发底层按钮或进度；点击其他 App 不关闭，不使用全局鼠标监听。标题栏系统操作保持正常，Escape 只关闭最上层，Space 不误控播放器；关闭、账号失效和卸载释放旧按钮及焦点关系。
- 普通底部按钮菜单采用同按钮关闭、异按钮切换，队列遵循相同规则；频道浮层打开时点击任何下层区域都只关闭频道。音质异步加载失效后不能重开菜单；普通菜单不扩大为跨 App 保留。
- FM 面板复用 `MeowFilm.OverlayScrollViewer` 和现有场景矢量；颜色跟随播放器调色，背景覆盖完整面板，卡片边框、hover／pressed／selected 和键盘焦点清晰，保留系统高对比。

## 候选与续取实现

- 候选 token 属于当前 epoch 和候选批；推荐候选提交时重读实际队列、剔除已入队录音，再一次 append CAS。有效 PlayNext、模式变化和导航不废弃原 seed／候选；队列替换与账号失效有独立 fence。
- `OnPlaybackAsync` 消费真实 `MusicRecommendationPlaybackState`。`PlaybackEvent` 来自首次有效 Started；暂停／恢复、seek 和重复 Started 不增加事件号。只有特殊方式自动续取，队列随机选歌不再参与网络请求决策。
- `MaybeBeginLocked` 按共同契约决定何时取歌；同一 `Flight`／generation 防止并发读取，`Attempted` 避免同尾项／游标失败后忙重试。没有计时冷却、队列长度阈值或随机轮次限制。
- `TryAdvanceAsync` 只在已有候选需要并入时等待本地 CAS，不等待 provider HTTP；随后由唯一队列 owner 执行实际循环与插队优先级。普通续批不带 selectIndex，不抢当前媒体。
- 搜索候选仅追加时 `started` 仍为 false，队列 source 保持搜索；普通方式不会自动追加候选。唯一队列 writer 真正选择活会话推荐 occurrence 时，在该次选择 CAS 中切换 source，成功回执同步激活推荐，再允许后续普通追加保留会话。显式 choose／主动 enter 同样以实际提交的选择为准，不从持久条目 context 重建活会话。
- FM 显式换频道的首批相对旧队列不去重，创建新 occurrence 并选择新批真实首项；全部与旧批重复也能进入。正常后续取歌仍按已有录音去重。
- 关闭 continue 保留候选及 seed；在途读取可完成为候选，但不自动并入。已准入 CAS 等待实际结算，不回滚已提交内容。最后候选消费后才按 hasMore 结束；空批／全部重复同样停止自动链。
- 心动与主动推荐共用 retainCurrent restore；相似、FM 和搜索候选没有返回 checkpoint。完整心动结果及缺失详情由平台 owner 解析；续批调用同一心动接口并保持原进入上下文。

## 本机 FM 记忆

`MusicFmPreferences`（位于 `MeowFilm.Capabilities/Music/Recommendations/Storage/`）直接通过强类型 SQL 按 `account_id` 读写 `windows_music_fm_preferences`（`account_id`、`mode`、`submode`、`rule_id`、`tab_tag_id`）。不存凭据、队列或活会话，不进入曲库同步。

首页不指定 mode 的 FM start 在账号首次观察后读取该账号选择；无记录使用 default。只有账号 lease 和已提交 FM 配置仍属当前来源时才保存；切换失败、取消、旧响应不能覆盖。

## 唯一队列写边界

所有会改变队列或当前选择的入口均经由唯一 `MusicLibraryOwner` 的串行写边界按 `expectedVersion` CAS 执行：

```text
music.queue.select / next / previous / step
music.queue.append / remove / reorder / replace / playCollection / clear / mode
music.history.restorePlaylist
music.catalog.playlist.commit action=play|next
```

网络歌单收藏使用 `music.catalog.playlist.save`，由同一个 library owner 保存远端引用，不先 prepare／读取成员，也不替换队列或退休当前推荐。

`music.queue.recommendation.*` 是协调器与 library owner 的私有事务协议；私有协议只处理当前推荐批次的 capture、enter、append、select、restore 和 discard，不暴露给页面。

## 请求与会话生命周期

- 一个协调器同一时刻只有一个当前会话；被替换会话的物理工作仍须排空。
- 同一 session 的读取由 `Flight` 和 generation 串行；UI 绑定、状态通知和集合变化不得再次发起同一批请求。
- start、choose、retry、继续加载、自然结束推进和返回均有明确 generation 与取消边界。
- 模式切换只有在新批次读取并提交成功后才成为当前配置；失败或取消恢复最后一次已成功配置，不采用未提交状态。
- 注册首个推荐 session 前的账号权威观察复用 `MusicAccountOwner` 的首次观察 flight；相同配置重复 start 不重复读取整份账号文档。每个真实推荐批次仍单独取得并核对当前账号 lease，旧请求的鉴权失败不能撤销并发登录产生的新 lease。
- 浏览页面或隐藏视图不退出已提交来源；媒体绑定由模块关闭／释放生命周期管理，不将可见性变化解释为全量刷新。

## 媒体绑定

`MusicNativeApplication` 向唯一播放协调提供：

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

返回原队列所需 checkpoint 由恢复、discard 或关闭释放，不设字节或数量门槛。

推荐平台响应、曲库请求／响应及业务持久数据不设人为字节上限，格式、身份和事务回执校验继续执行。心动及 scene 单曲推荐不另设条数裁剪；FM 三首、规则五首和 SIMILAR 三首仍是各自的协议批次。

## 关闭

1. `MusicCapability` 同步封闭页面和播放器推荐入口。
2. 取消当前 generation 的提交／媒体资格，排空已准入读取、队列事务、通知和媒体 callback。窄推荐准备不取消已准入的 Catalog 读取或共享账号初始观察，必须等待它们真实结算；退出的启动订阅者不再继续派发。
3. application 全局关闭先围住推荐 callback，再失效 Catalog／账号读取并排空其他音乐业务，不能以调用者退订当作底层排空。
4. 最后准备唯一 `MusicLibraryOwner`。

进程内退役单向完成，不恢复旧 session、callback 或播放意图；最终释放先 dispose 协调器，再释放它借用的 catalog 和 library。正常整进程退出不以全部推荐任务排空为门禁，见[App生命周期](app-runtime.md)。
