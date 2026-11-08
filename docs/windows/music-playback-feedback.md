# Windows 原生播放反馈

`MusicFeedbackController` 是当前 App 唯一的播放反馈协调器。它只消费真实播放器和当前歌曲选择状态，不创建播放、不改变队列，也不阻塞正常听音。

## 所有权

- `MusicFeedbackSettingsOwner` 独占反馈设置文档 `music.feedback.v1`。
- `MusicFeedbackController` 拥有当前听音实例、资格计算、账号绑定、取消和关闭 drain。
- `NativeMusicFeedbackProtocol` 拥有平台开始／结束协议会话；它显式借用当前账号 owner、固定 HTTP 和[客户端 owner](music-platform-client-context.md)。
- 页面只投影三个平台开关和当前错误，不保存第二份草稿或后台设置。

## 设置格式

当前文档唯一格式为：

```json
{"version":1,"revision":0,"enabled":[]}
```

`enabled` 只允许按序且不重复的 `wy`、`tx`、`kg`。读取会完整校验字段、规范整数、fingerprint 和大小；只有文档不存在表示默认关闭。损坏、另一版本或提交结果未知均明确失败并保留原始数据。

开关使用 revision CAS 立即保存。CAS 准入前先冻结旧策略；写入回执不确定时所有上报保持禁用，直到 owner 在下一次准入前完成当前权威文档协调。

## 真实播放准入

反馈只能由以下顺序建立：

1. 当前队列选择提供规范歌曲、entryId、来源上下文和可验证录音身份；
2. 唯一原生播放器发出当前 entry 的真实 `Started`；
3. 设置已确认启用目标平台；
4. 当前账号会话与该平台录音仍有效；
5. 协议 `StartAsync` 明确接受精确账号、精确录音和当前实例。

URL 解析、选择来源、恢复位置、缓存命中、页面打开或 `Play()` 被接受都不能创建听音实例。反馈平台取自歌曲的原生录音身份，不取自最终媒体 URL 的提供平台，也不自动换到另一平台。

## 听音计时

- 位置、时长、倍速和播放状态来自当前播放器观察。
- 只累计连续、有限、单调且能由墙钟和倍速解释的位置变化。
- pause、seek 跳跃、未知时长、错误样本、切曲和旧 entry 会中断连续区间，不把间隔补算为听音。
- 每个平台实例绑定当前歌曲 generation、账号 session、录音和一个 `MusicFeedbackAttempt` 对象。
- 同一歌曲／平台每代只尝试一次；上游回执未知时不自动重发，避免重复上报。

## 平台边界

- 网易云使用当前账号及当前推荐／来源上下文，开始和结束都需固定 ACK。
- 心动首批使用 `aiMode` 与已解析的云喜欢歌单 ID；更多推荐逐曲使用上游 `sourceMap`，缺失时为桌面 `moreRcmd`。推荐的请求来源、provider 与反馈来源分开，`aiMode`／`moreRcmd` 不再在 weblog 序列化时丢失。
- `sourceMap` 只由当前 native 推荐会话的 occurrence proof 注入，按上游原值发送，不套用普通歌单来源别名；原队列来源和持久化标签不能制造此资格。来源文本沿用受控元数据边界（512 字符、无控制字符），不携带新增历史数据，也不改变反馈默认关闭、真实播放和账号代次门禁。
- 网易云的实时账号身份、录音身份／时长和报告 ACK 分别先核对，再交接对应客户端响应；错误身份、录音或协议结果不会更新客户端，也不会继续发送报告。
- QQ 使用当前账号和可撤销设备 lease；QIMEI／匿名 session 由独立客户端 owner 保存，账号 owner 每次实时验证账号后才签发组合 lease。客户端变化只撤销依赖它的反馈，设备过期、身份变化或不确定回执明确失败。
- 酷狗当前仍拒绝发送：现有精确反馈配方未满足受验证 TLS 协议边界，`NativeMusicFeedbackProtocol` 返回 `UNVERIFIED_TLS_PROTOCOL`。不能降级到 HTTP、猜测网关或用匿名设备冒充账号。

平台独立失败不会停止播放，也不能转换为另一平台成功。错误只保留固定代码，不记录 Cookie、设备标识、请求体、响应正文或 URL。

## 结束与生命周期

- 自然结束、用户切曲、播放错误、停止和关闭分别形成当前 `MusicFeedbackEnd` reason。
- 结束前再次核对设置、账号与录音；未被开始协议接受的实例不发送结束。
- 账号变化或关闭平台开关立即取消对应实例并等待实际协议 session 释放。
- 正常关闭先停止新准入、结束当前实例并 drain 全部工作；设置 `COMMIT_UNKNOWN` 或释放失败会阻止关闭准备被报告为成功。
- 协议没有持久待发队列、重试 scheduler 或旧实例恢复。
- 防重复状态只属于单次播放、单个平台的 Attempt；开始和结束各至多尝试一次，旧 Attempt 的终态不能重新创建上报。协议只持有活动 session 与尚未结算的任务，结束后释放 QQ lease、取消源及 owner 引用，不累计永久实例台账，也不设每分钟、活动 session 或 pending 次数额度。

曲库收藏／歌单同步由独立 `MusicLibrarySyncService` 负责，见 `music-library-sync.md`；反馈设置和同步设置不能互相代替。
