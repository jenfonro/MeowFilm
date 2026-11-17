# Windows 原生播放反馈

共同语义见[真实播放反馈](../common/music/playback-feedback.md)。`MusicFeedbackController` 是当前 App 唯一的播放反馈协调器。它只消费真实播放器和当前歌曲选择状态，不创建播放、不改变队列，也不阻塞正常听音。

## 所有权

- `MusicFeedbackSettingsOwner` 独占反馈设置文档 `music.feedback.v2`；网易安全待发由 `NeteasePlaybackOutbox` 独占原生文档 `music.playback-outbox.v2`，复用同一 App 借用存储。
- `MusicFeedbackController` 拥有唯一的当前听音事实与计量、平台资格和账号绑定、取消和关闭 drain。平台不各自维护媒体时钟。
- `NativeMusicFeedbackProtocol` 拥有平台开始／结束协议会话；它显式借用当前账号 owner、同一 [Node 平台客户端](music-platform-client-context.md)。
- 页面只投影三个平台开关和当前错误，不保存第二份草稿或后台设置。

## 设置格式

当前文档唯一格式为：

```json
{"version":1,"revision":0,"enabled":[]}
```

`enabled` 消费不重复的 `wy`、`tx`、`kg` 子集，保存按固定顺序输出。读取在必要字段处转换整数，接受等价数值拼写；未编辑扩展及精确数字随 CAS 保存保留，不以额外字段拒绝整份设置。真实大小与写入回执边界保留；只有文档不存在表示默认关闭。损坏、另一版本或提交结果未知均明确失败并保留原始数据。

开关使用 revision CAS 立即保存。CAS 准入前先冻结旧策略；写入回执不确定时所有上报保持禁用，直到 owner 在下一次准入前完成当前权威文档协调。

## 真实播放准入

反馈只能由以下顺序建立：

1. 当前队列选择提供规范歌曲、entryId、来源上下文和可验证录音身份；
2. 唯一原生播放器发出当前 entry 的真实 `Started`；
3. 设置已确认启用目标平台；
4. 当前账号会话与该平台录音仍有效；网易先用原账号实时核对匹配的 profile，完成资格确认后才开始截取听音；
5. 将该平台的事实交给 `StartAsync`；它只表示平台适配准入，不是共用远端 ACK。网易完成本地持久准入后异步上传，QQ 仍有自己的注册与结束确认。

资格在实际开始、已保存开关和已确认有效账号具备时建立；文件 ACK 等待不重置听音计量。账号／开关具备之前的区间不倒补，账号获取／验证在两个媒体样本之间完成时也排除准入之前的区间。

URL 解析、选择来源、恢复位置、缓存命中、页面打开或 `Play()` 被接受都不能创建听音实例。可靠录音映射、独立开关和有效账号决定各平台分别参与，同一遍可以同时报给多个平台，不由最终媒体 URL 的交付方独占。

## 听音计时

- 位置、曲长、速率回执和播放状态来自当前播放器观察。速率回执是当前输入的原生读回值，不是对物理音频输出倍速的测量。
- 每个连续媒体区间只计量一次：分别保留单调时钟约束的 `Listened` 与 `MediaListened`；平台只截取准入后的部分。`MusicFeedbackEnd.PlaybackListened`／`PlaybackMediaListened` 保留共用全遍累计，`Listened`／`MediaListened` 是该账号／开关准入后的累计。
- pause、buffer/stall、显式 seek（包括小步跳转）、非法样本和切曲不补时。seek 和速率命令在整个异步过渡中保持非听音边界，真实后续样本才恢复；速率读回与请求不符时不宣称请求成功，听音事实仍使用该输入已知的读回值，未知速率不能冒充 1×。
- 未知曲长以播放器的 `0` 表示，不中断共用真实进展。`IMusicFeedbackProtocol.RequiresDuration(platform)` 表达适配层是否必须等待曲长；网易不要求曲长，QQ／酷狗保留其现有协议要求。网易事实不经旧 86400 秒、16× 或迟到 dispatch elapsed 的限制；有限非负位置／时长、正有限速率和真实结束身份仍须成立。
- 一遍由当前 queue occurrence 与可信录音归属建立，拥有 `PlaybackId`。原始 `StartedAt`、平台 `AdmittedAt` 和结束 `EndedAt` 由事实发生时采集，不由 ACK 到达时生成。
- 每个平台实例绑定当前 generation、账号 session、录音和一个独立 `MusicFeedbackAttempt`；同遍的多个平台共享 `PlaybackId`，不共享请求、确认或取消状态。网易每次平台准入另建 `OutboxId`，作为持久 start／end 配对身份；撤销旧准入不能删除同遍重新准入的新配对。
- 同一歌曲／平台每代只准入一次；网易安全未发送事件可在已授权触发点恢复上传，正在发送或上游回执未知的文件不重放。

## 来源事实

- 来源在入队 occurrence 的选择时绑定。映射补全、后台队列读取和成功换交付方只更新录音映射，不能把当前浏览页来源覆盖到已开始的一遍。
- 融合搜索保留真实关键词；集合上下文保留原 `sourcePlatform`、类型和 ID，外平台集合不会成为网易集合 ID，可靠映射到目标平台的歌曲也不因此丢失原来源。
- FM 模式／场景／规则、相似种子、心动及队列推荐由活跃 native 协调器提供逐曲 proof。响应 `context` 与逐曲 `metadata` 随该 occurrence 保留，包括真实 alg、完整种子和上游来源值；不同批次或重复录音项不交换来源。
- `MusicFeedbackStart.Context` 与 `Recommendation` 是中性来源事实，不是最终 wire 字段。推荐 proof 必须仍在当前 native 队列中且对应原生录音；持久化标签、队列外旧来源或另一个 occurrence 不能制造 proof。准入时已成立的来源与结束事实一起冻结。
- 上游没有提供 `sourceMap` 时不制造 `feedbackSource`，不再用 `moreRcmd`／`aiMode` 作为响应元数据缺失的默认值。实际 provider、集合与逐曲 alg 仍保留；协议自身的来源映射与成功码不属于共用事实模型。

## 平台边界

`NativeMusicFeedbackProtocol` 与 `NeteasePlaybackTransport` 通过同一 `MusicPlatformClient` 消费共同 Node 反馈操作，不保留平台 HTTP、QIMEI 或 NCBL 编码副本。共同事实与协议归属见[真实播放反馈](../common/music/playback-feedback.md)。

- 网易以 `feedback.qualify` 实际核对原账号，`feedback.upload` 提交中性 events；原生 outbox 仍拥有耐久准入、inflight 和恢复。
- QQ 以 `feedback.prepare` 取得 Node transient session，结束走 `feedback.send`，释放走 `feedback.release`；原生 session 绑定真实账号及 Attempt，不持久设备材料。
- 酷狗当前 Windows `RunStart` 仍返回 `UNVERIFIED_TLS_PROTOCOL`，没有发送；共同 Node 已有其固定反馈配方不等于 Windows 已接通，也不等于真实上游已验证。该差异是当前集成缺口，不是共同业务规则。

平台独立失败不会停止播放，也不能转换为另一平台成功。错误只保留固定代码，不记录 Cookie、设备标识、请求体、响应正文或 URL。

## 结束与生命周期

- 自然结束、用户切曲、播放错误、停止和关闭分别形成当前 `MusicFeedbackEnd` reason。
- 相同 occurrence、相同可信录音成功换 URL、脚本、quality 或交付方不结束／重建一遍；普通 handoff 准备期间旧的实际音频继续采样，准备失败不影响旧遍。reopen 候选进展在提交前不成为已接受事实，提交后恢复同一遍。
- 真正下一曲／重播新建 `PlaybackId`。用户错源报告或实际曲长矛盾先同步失效旧计量与归属、停止错误音频；替代源只能从自身真实开始／恢复建立新事实，不能继承错音频。暂停状态的替代源不制造 Started。
- 结束冻结累计、位置、原始时间与原因，即使 `StartAsync` 尚在等待 ACK 也不修改冻结数据；它迟到成功时只可能结束原账号、原录音、原遍，不作用于新遍。取消／撤销的迟到 session 只释放，不补发。
- 结束发送仍核对设置与账号；平台协议未接受的实例不发送结束。
- 账号变化或关闭平台开关立即取消对应实例并等待实际协议 session 释放。
- 正常关闭由 Shell 的退出路径进入 `AppShutdown`，模块同步冻结当前 `interrupt` 结束事实并封闭新准入，之后才进入 native feedback 的 prepare/drain；重复关闭不重复结束。最小化到托盘只隐藏窗口，不进入该链路，仍继续观察播放。设置 `COMMIT_UNKNOWN` 或释放失败会阻止关闭准备被报告为成功。
- 正常关闭冻结本地 terminal 后暂停并取消网络，排空已准入的本地持久化和防重标记即可完成准备，不等待网络 ACK。已在途的整对事件按未知清理，安全未发与原账号保留；取消关闭可以恢复安全未发。传输继续拥有物理清理任务，最终释放仍收束取消后的请求资源。正常关闭不等同禁用／登出。
- 防重复状态只属于单次播放、单个平台的 Attempt；开始和结束各至多尝试一次，旧 Attempt 的终态不能重新创建上报。协议只持有活动 session 与尚未结算的任务，结束后释放 QQ lease、取消源及 owner 引用，不累计永久实例台账，也不设每分钟、活动 session 或 pending 次数额度。

曲库收藏／歌单同步由独立 `MusicLibrarySyncService` 负责，见 `music-library-sync.md`；反馈设置和同步设置不能互相代替。

## 网易安全待发

- 只保存必要播放事件、原发生时间、已证来源／alg、平台准入配对 ID、原账号 scope／generation／userId 与同意 revision；不保存 Cookie、token、请求头、设备或账号快照。原账号是恢复条件，不把待发转给新账号。
- 每遍 start／end 按顺序上传；确认 start 后才允许对应 end。只剩 end 的持久项必须带 start 已确认事实；start 结果未知则连同配对 end 丢弃，不猜上游幂等。
- 首字节可发送前先持久 CAS 为 `inflight`；该写不确定则不上传。精确文件 ACK 后删除相应事件；已标记 inflight 的未知结果、取消或崩溃记录不恢复成可重试。写盘失败保持固定错误并阻止静默关闭成功，音频继续，不盲写/轮询协调。
- 独立账号只读核对或本地准备失败时，播放文件尚未进入发送函数，才有明确未发送依据；保留为安全未发。后续仅启动、账号就绪和真实反馈事件触发有界 drain，没有业务重试定时器。本实现不把 .NET ConnectionError／TIMEOUT／IOException、HTTP 非 200 或任何笼统网络错误推断为未发送；一旦进入持久 `inflight` 发送边界，失败均按未知处理。未知 start 丢弃整遍而不补 end；start 已 ACK 后未知 end 也不重放。
- 7 天、最多 256 个事件、4 MiB 文档、32 KiB 单遍事件资料是资源/隐私边界，不是播放计次或听音阈值；淘汰完整配对，不能产生孤立 end。
- 禁用、登出、换账号立即撤销网络并串行清理。启动仅在已启用且原账号匹配时恢复；账号未就绪暂缓。持久同意 revision 防止禁用清理写盘失败后再次启用恢复旧数据；本进程已确认保持网易启用的其它平台设置修改可携带现有事实，若在设置提交与待发 revision 结算之间崩溃，保守丢弃而非跨同意恢复。

协议向量归共同 Node 测试；原生待发／消费者使用合成账号与规范运行器 peer，当前均未执行。官方实包的精确文件 ACK 证明官方容器／事件通道被接收，不替代本产品仅含 `MUSIC_U` 的 metadata 与最小事件 schema 的线上接收验证；后者当前未覆盖。组件测试与构建不证明个人排行次数、刷新时点或推荐效果。
