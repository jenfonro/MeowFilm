# 网易云漫游、心动与队列推荐

本端沿用[音乐能力](music.md)的唯一队列、账号、播放和持久化所有者；推荐不经过 Node/LX，LX 仍只承担已声明的取链等音源能力。平台身份遵循[平台客户端上下文](../common/music/platform-client-context.md)，不为推荐另造 Android 身份或实验接口。

## 所有权与入口

- `MeowMusicFeature` 只持有显示快照和操作意图；UIKit 播放器、队列和「我的」页不拼平台歌曲 ID、喜欢歌单 ID 或 HTTP 参数。
- `NativeMusicService` 持有心动／SIMILAR 会话、独立普通队列候选、队列 CAS、播放事实和返回 checkpoint。
- `NativeMusicAccountService` 按原始 `MusicAccountQueueLease` 固定账号代次与凭据。平台适配器负责完整喜欢读取、请求参数及结果归一；迟到结果不能换账号继续。
- `MusicSimilarModeServing` 负责心动／SIMILAR；`MusicQueueRecommendationServing` 负责普通队列的只读候选、选择、续播与重试。二者复用已有变化通知、播放事实和暂停围栏，不各建播放器或第二个队列。

首页心动与「云喜欢心动」传 `.heartbeat`，由平台读取本人喜欢；播放器当前曲传 `.currentHeartbeat(occurrenceID:expectedRevision:)`，由队列 owner 核对真实 occurrence 和网易云 native ID。公开浏览歌单、标题含「喜欢」的歌单和本地收藏不能冒充这个身份。

## 漫游

首页私人漫游使用 `DEFAULT`。显式目录配置保留 `mode/subMode` 或 `ruleId/tabTagId`；播放器只在活网易云 FM 中提供模式／场景设置，普通循环菜单保持原有含义。

主模式为 `DEFAULT`、`FAMILIAR`、`EXPLORE`、`PUZZLE_MODE_RCMD`；场景目录有 43 项，包含 `HOT_SONG`，保留官方 `FRANCH`、`PUNK` 键。显示选中值来自已提交的 `activeHomeFM`，不是正在请求的草稿；失败、取消、账号失效和退出不能重新发布旧配置，已失效的设置入口关闭。

FM 普通批次 3、规则批次 5 和明确歌曲 SIMILAR 批次 3 是各自协议参数，不是普通队列推荐的数量限制。

## 心动首批与返回

同一账号读取本人 `specialType=5` 且 owner 相符的喜欢歌单，复核详情身份。完整成员唯一且数量符合 `trackCount` 或 `trackCount + cloudTrackCount`，缺少真实显示元数据时按详情协议分批补齐；不把显示占位当成有效种子。

心动使用 `playmode/intelligence/list`：

- `playlistId` 来自已核对的本人喜欢；
- `startMusicId` 是完整喜欢中首个有效歌曲；
- 喜欢入口的 `songId` 从完整有效喜欢中随机选择，`type=fromPlayAll`；
- 当前曲入口的 `songId` 是核对后的真实当前歌曲，`type=fromPlayOne`；
- `count` 是实际完整喜欢成员数，不固定为 100。

首批完整消费并按网易云 native ID 去重；当前曲入口保留原 occurrence，推荐响应里的重复曲不制造第二个种子。新会话续播默认关闭。开启后的心动续播走普通更多推荐接口，不重新调用心动首批或私人 FM。

心动／SIMILAR 的「返回原队列」恢复原 entries、选择、来源及 checkpoint；不把关闭续播当成退出。恢复到不同歌曲只恢复状态，不强制开始播放。既有 schema 2 冷恢复继续保留原队列和持久开关值，但不恢复 live 推荐反馈资格。

## 独立普通队列候选

`MusicQueueRecommendationSnapshot` 是当前队列／账号代次绑定的不透明快照，候选没有队列 ref。首页、搜索等普通播放提交队列后可读取候选，默认续播关闭。

请求使用 `homepage/more/rcmd/song`、`source=curlist_more_rcmd`，传完整队列按 occurrence 顺序排列的网易云 native ID（包括重复 ID）及真实当前歌曲；混合平台或缺 ID 队列不能筛选子集后请求。没有伪造的 offset 或推荐分页游标。

- 预览和非自动尾部条件下的重试不入队、不选曲、不改变原列表来源。
- 选择候选用快照 ID 和候选 token，一次 CAS 将整批新曲尾部追加并选中所点曲，再由既有播放 owner 起播。
- 开启续播且原生播放事实到达真实队尾时才单飞追加；已有有效候选优先消费。自动追加不改变当前选择。
- 下一首／自然结束可以等待在途追加；暂停和新导航使旧播放意图失效，不因晚返回而起播。
- 关闭续播取消未提交工作，已入队歌曲保留。队列变化、旧快照、账号变化或不确定提交不能被旧结果覆盖。
- 空批、全部 native ID 重复和 `hasMore=false` 终止后续自动请求；最后一个非空批仍可正常消费。明确重试不构造远端页码。

普通推荐运行状态仅在当前进程中存在；已追加的歌曲仍由原有队列持久化，不增加活推荐冷恢复格式。

## 来源反馈与生命周期

原列表来源、推荐 provider、音频 provider 与逐曲反馈来源互不替代。平台 `_recommendation` 是无凭据元数据，不是授权：

- 心动首批 `feedbackSource=aiMode`，source ID 为本人喜欢歌单；
- 普通推荐及心动续播按 `sourceMap` 原值发送，缺项使用 `moreRcmd`；`algMap` 保存对应歌曲的真实算法字段；
- 只有实际入队 occurrence 和准确网易云录音匹配后，原生 owner 才生成非持久 `MusicPlaybackRecommendationProof`。

Proof 绑定 occurrence、native ID、反馈字段和原始 queue authorization。反馈 wire 校验字段绑定；反馈 worker 在挂起边界校验，并登记账号／会话撤销取消。复制持久推荐标签、冷恢复、未入队候选、退出及跨账号都不能恢复资格。consent 仍默认关闭，只消费已有真实原生播放观察，不增加播放历史采集。

推荐调用按 lease 与 operation 登记取消，物理 provider worker 完成后才移除；取消 ACK 不充当 drain。保留单响应、逐曲和累计字节资源边界，以及真实详情分页协议。

## 验证边界

合成协议测试验证请求、完整性、来源和取消；Swift 队列测试验证 CAS／生命周期；UIKit 宿主测试验证入口与交互。它们不证明上游推荐算法已生效。已有 Windows 真实证据中 `EXPLORE→default`、`HOT_SONG→random` 的语义仍未确认；不能用 HTTP 200、非空或歌曲不同宣布 iOS 对应算法已验证。
