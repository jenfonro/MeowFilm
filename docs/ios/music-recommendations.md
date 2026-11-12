# 网易云来源、推荐与续播的 iOS 实现

业务语义以[播放来源、队列与自动续播](../common/music/playback-sessions.md)为准；本文记录本端类型、存储、协议和 UIKit 消费关系。音乐所有权见[音乐能力](music.md)，平台身份见[平台客户端上下文](../common/music/platform-client-context.md)。推荐不经过 Node/LX；LX 仅承担已声明的音源能力。

## 所有权与入口

- `NativeMusicService` 统一持有队列 CAS、真实播放事件、FM、搜索候选、主动推荐／相似／心动会话和返回 checkpoint，没有第二个播放器或队列。
- `MusicSimilarModeServing` 提供 `.recommendations(occurrenceID:expectedRevision:)`、`.currentHeartbeat`、`.heartbeatSong(songID:)`、`.heartbeat`、`.songRoaming` 与 `.artistRoaming`。`MusicQueueRecommendationServing` 负责搜索单曲候选；`MusicFMPlaybackServing` 负责 FM 记忆、会话投影和续播开关。
- `MusicSearchBatchPlaying.replaceSearchBatch` 只在直接播放一个搜索目标时写入 `sourceType=search-song` 和固定 `seedEntryId`；搜索批量播放、首页已展示列表及普通单曲不借队列长度取得搜索推荐身份。搜索“下一首播放”解析真实录音后走既有插队事务。
- 入口资格读取已共享的网易 ID，不补搜。普通网络歌单和普通本地歌单没有心动资格；本地收藏当前 occurrence 具有有效网易 ID 时可发起，由平台核实真实喜欢歌单及种子成员。其他当前曲心动按 occurrence 的官方喜欢来源校验，不把整队列资格授予插队歌曲。官方账号喜欢来源使用实际账号歌单。仅缺登录的单曲推荐及有效首页相似／心动入口保留，由执行时统一提示登录。
- `NativeMusicAccountService` 与平台适配器使用原始账号 lease，挂起前后校验账号、访问代次和授权，不刷新后换账号接着提交。FM 使用独立的当前首页 binding。

## UIKit

`MusicPlaybackControlsView` 将来源模式与普通三方式合并到播放模式按钮，收藏左侧不再放独立 ∞ 或保留其占位。图标、菜单顺序、退出资格和续播互斥遵循共同契约；循环图形保留原造型并增加纵向弧度。顶部私人 FM 入口显示当前具体频道／场景，菜单不重复频道项。

`MusicPlaybackControlsView`、`MusicPlayerContentModel`、队列页和更多菜单共用同一 `MusicViewModel`。模式回调绑定 occurrence、attempt、队列／设置 revision、账号、来源快照、命令代次及当前菜单状态；隐藏、布局切换或身份失效的旧菜单不能执行。队列页保留真实候选、分组、错误重试、未知提交核对和适用的返回按钮，不提供独立续播控件。`commitUnconfirmed` 只重读既有状态，不重放未确认提交；真实失败使用统一错误提醒并以 `errorID` 去重。

私人漫游选择页使用共享 `VerticalPageTransition(edge: .top)`；全屏播放器仍从底部进入。页头为当前频道名、15pt medium，背景使用 `MusicPlayerBackgroundView(.full)` 与播放器有效调色，贯穿全高，页头不叠加通用短渐变。主模式与场景卡片保留排列，显示边框和选中标记；旋转、交互取消、账号失效与关闭继续走既有页面生命周期。

## FM 记忆与取数

`MusicHomeAction.fmConfiguration` 去除展示用 entry／sourceName，保留真实 mode、subMode 或规则选择。首页默认 FM 动作先调用 `rememberedFM()`；显式频道选择直接使用所选配置。主模式与 43 项场景沿用现有 `MusicFMModeChoice`，包括 `HOT_SONG` 和官方 `FRANCH`、`PUNK` 键。

成功提交 FM 队列时，将规范化动作写入设备音乐文档 `playback.fmSelections[scope]`。登录 scope 为 `wy-fm:` 加真实网易 UID 的 SHA-256，游客使用独立 guest scope；不存凭据，不借短期账号 revision 作为跨重启身份。首次未保存使用 DEFAULT，存储或解码失败不冒充首次默认值。没有云同步。

`MusicRemoteQueueSource.homeAction` 携带真实入口动作，包括 FM 配置、相似种子和日推分支。已在 FM 内切频道先取得新批，再追加旧队列并选择新批首项，保留当前播放方式；新进入 FM 由 owner 一致设置列表顺序和续播开启。预览仍按既有缓存转移首批，不重复请求已转移批次。

私人 FM 走 `/api/v1/radio/get`：`mode/subMode/limit="3"/unplaySongIds="[]"/openAidj="false"/aidjReqTimes="0"`；频道预览请求 `limit="1"`。规则频道使用其既有 recommends／cover 路径，批次参数 5。相似歌曲／艺人走同一 radio 路径，但为 `mode=SIMILAR`、真实 `sourceType/sourceIds`、`limit="3"` 和少一个 g 的 `unplaySonIds`。这些是请求参数，不是客户端队列长度上限。日推／风格日推仍为歌单来源。

播放反馈的来源值与取曲路由分开：仅普通 FM 在缺少逐曲上游来源时默认 `userfm`，规则频道（`ruleFM`）无上游来源时省略；心动使用真实 `play_scene=heartbeat`，逐曲上游 `source/sourceId/alg` 独立保留；心动收藏歌单 ID 仅描述本地入口，不借作上游反馈来源。推荐 `sourceMap` 缺失、空或 null 时不填 `moreRcmd`，相似模式也不生成猜测标签。反馈协议与有限待发见[音乐能力](music.md#网易协议与有限待发)。

网易反馈 outbox 使用 Application Support 下 `documents/music.netease-feedback-outbox.v1` 的独立业务 `DocumentStore`，由账号服务通过惰性工厂注入，不与 URL／副歌缓存共用存储，也不读取或导入其它存储的数据。普通清缓存不删除待发事实；存储不可用只限制反馈，不阻断账号准备、启动或播放。发送前仍须成功落盘 inflight，ACK、账号身份和未知发送结果不重放规则不变。

## 固定单曲推荐

搜索自动候选和主动单曲推荐复用同一平台请求：

```text
POST EAPI /api/homepage/scene/more/rcmd/song
currentPlaySongId="<固定 seed>"
songIds="[<固定 seed 数值>]"
songCount="1"
offset="<响应 offset，首次 0>"
source="curlist_more_rcmd"
extJson="{}"
```

完整消费 `data.songList/algMap/sourceMap/offset/hasMore`；有后页时响应 offset 必须前进，后续不按条数推算。种子不随当前推荐曲、插队或当前队列变化。不制造 `searchTransmissionInfo`／搜索意图，不保留旧推荐路由 fallback。实网证据只支持接受这些参数与分页，不保证算法仅依赖 seed 或永不耗尽。

搜索首批在种子真实开播后异步准备，不阻塞首曲，也不打开续播；尚未开播或无推荐平台能力时不自动请求。普通方式只准备候选，不自动并入或继续请求后批。首批保存在候选区，选择时通过快照与每批候选 token 一次追加整批并选中目标。显式下一首优先；未激活时普通尾部追加撤销候选和请求，插队不撤销。激活后插队／追加均保留固定种子。搜索候选不保存原队列；主动推荐在 `queue.similar` 保存 checkpoint，当前 seed 的 occurrence、音频、进度及暂停状态保留。

主动推荐失败保留本次入口快照供重试，重试继续原种子与原游标；不能因再次执行入口动作而先作废待重试快照。首页相似入口失败后再次点击同入口会重新请求，不复用失败预览。

## 心动与恢复

平台先完整读取当前账号本人、`specialType=5` 且 owner 相符的喜欢歌单并复核详情；缺元数据按详情协议补齐。心动使用 `/api/playmode/intelligence/list`，`playlistId` 为真实歌单，首批 `songId/startMusicId` 同为实际进入种子，当前曲使用 `fromPlayOne`，首页使用 `fromPlayAll`。展示 H 的卡片传 `.heartbeatSong(H)`，不因 H 不在喜欢成员中就替换它，由上游决定是否接受；无展示种子的账号入口才从有效喜欢成员选择。当前曲入口仍校验真实成员，不开放普通网络歌单。`count` 使用实际完整成员数，不当返回硬上限；`songInfo=null` 时按顶层 id 补详情。种子缺于响应时优先复用已读取详情，确实缺失再按种子 ID 补齐首项，不伪造反馈归属。

心动续批沿用原账号喜欢歌单、进入种子 `startMusicId` 和 `fromPlayAll/fromPlayOne`，仅将 `songId` 推进到实际尾曲。当前曲入口仍核实原种子的喜欢成员身份，不要求推荐尾曲属于喜欢列表。推荐／心动的返回由原生 owner 一次 CAS 恢复 checkpoint，并把当前实际 occurrence 插回原锚点之后且选中它：`ABC/B → E → ABEC`。同 occurrence 不重复插入，同录音不同 occurrence 不合并；保留原行上下文与 E 的内容、音频实例、进度和暂停状态，不重新 resolve、play、pause 或 seek，不恢复旧循环值。来源标签读取唯一 `queue.source` 的 `queueSourceTitle` 派生投影，不把保留的 E 条目上下文误当成队列来源。相似来源没有返回 checkpoint。

`queue.similar` 的 schema 2 包含 provider、sourceIds、providerContext、continue、offset、exhausted、batches、attempted、scope 和可选 returnQueue；缺少必需状态拒绝读取，不保留旧格式 fallback。冷恢复重新获取同 scope lease，不恢复 live 推荐反馈资格。搜索推荐 runtime 不冷恢复，已追加歌曲仍按普通队列持久化。

## 播放事件、取消与反馈

`recordSimilarPlaybackFact` 只消费真实准入的播放事实。特殊方式在开始播放实际尾 occurrence 时检查续取，普通三方式不自动请求或消费缓冲；`nextID` 只负责队列内随机选歌。同 occurrence 换音源、暂停／恢复、seek、元数据及 UI 读取不创造新播放事件。

同来源同游标只保留一个在途请求。成功续批只追加，不抢当前；请求在途、失败或耗尽不阻塞既有队列循环。关闭续播保留候选、种子及迟到结果，但不自动并入；重新开启在真实播放边界消费缓冲批。页面关闭只退休显示读取，不撤销播放拥有的搜索候选。替换、清队、账号或访问撤销拒绝旧响应；物理 worker 仍追踪到 drain，取消 ACK 不冒充收尾。

原列表来源、推荐 provider 和音频 provider 分离。`_recommendation` 是无凭据标签；只有活 lease 下实际入队且网易录音一致的 occurrence 才获得 `MusicPlaybackRecommendationProof`。`NativeMusicService` 的统一 occurrence map 在队列 CAS 后保留逐曲来源，队列移除后修剪，访问撤销／关闭时清除；新插入的同曲 occurrence 不继承旧 proof。

FM 首批、续取及关闭续播时缓冲的批次同时保留歌曲和逐曲 alg；补展示元数据不覆盖响应外层 alg。相似保留真实歌曲／艺人 seed IDs；心动保留真实喜欢歌单与本次实际 seed；单曲推荐保留原始 `algMap/sourceMap`。FM／相似／风格日推通过中性 `MusicPlaybackFeedbackOrigin` 传递真实动作与来源，不据接口名称猜造网易 wire source。搜索保留关键词，外平台歌单／专辑 ID 不作为网易集合 ID。新暴露的算法文本同样拒绝反射凭据。

反馈的来源 proof 在准入时核对；已准入的听音捕获来源，由独立原账号反馈 lease 继续围栏，正常退休读取 lease 或恢复原队列不拆遍。候选、持久标签及冷恢复不能恢复新的 live proof；consent 默认关闭。共用计量、冻结结束与关闭流程见[原生播放反馈事实与生命周期](music.md#原生播放反馈事实与生命周期)；本层不声明云端统计或推荐效果。

保留既有单响应／逐曲／累计字节预算、游标循环检查和安全整数容量，不新增歌曲条数、随机轮次、冷却或概率配置。合成协议、原生行为、Feature 和 UIKit 测试分别覆盖各自边界；不替代实网算法验证、真实多账号或真机听音。
