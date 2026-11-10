# Windows live HLS

live HLS 由 `AdaptiveLiveSession` 和 `NativePlayback` 当前会话共同拥有。它复用同一个自适应资源图和原生播放器，不创建刷新播放器、备用播放器、后台录制或下载任务。

## 当前 profile

- 接受带明确媒体序号的标准 HLS media playlist，以及由已验证 master 选择出的受支持 variant／audio rendition。
- 支持滑动窗口和 append-only `EVENT`；正常出现 `ENDLIST` 后按有限媒体完成。
- 媒体使用当前受支持的 MPEG-TS 或固定初始化 fragmented MP4 profile。
- 重叠序号的媒体、时长、discontinuity、初始化段和实际 payload 身份必须保持一致。
- 刷新后的媒体 endpoint 不能变成新的 master；实际 HTTP(S) 引用与重定向不按 Origin 或 URL 许可图限制。
- live window 最多 8 个 playlist、每窗最多 1024 个 segment；资源图和实际缓冲字节继续服从自适应媒体资源边界，不附加 HTTP 请求数或总并发额度。

不支持动态 DASH、DVR／录制、任意 live seek、LL-HLS part、delta update、blocking reload、运行时 master 演化、任意初始化段切换、DRM 或 `SAMPLE-AES`。开放中的 live window 不能被发布为完整离线归档。

## 刷新和表示一致性

- 原生读取驱动清单刷新；没有常驻刷新 timer，也不会因页面可见性反复创建请求。
- 每个 playlist 只有一个刷新 gate。未到 target 时按当前窗口等待；同一窗口不会并发刷新。
- 每次刷新从最后解析的清单地址开始，保留输入请求头，校验实际重定向、URI 和清单结构。
- 表示序号、discontinuity、playlist 类型或重叠 payload 回退／改变时明确失败。
- 最后一个已验证窗口只可在受限的瞬时网络／服务错误下保留最多两次调度重试；格式、权限和表示错误不重试。
- 重复未前进窗口有界计数，超过预算后报告过期，不形成无限轮询。

## 原生 EOF 恢复

窗口滑动导致原生请求已经退役的 segment 时：

1. 刷新当前 playlist 并确认出现真实窗口缺口；
2. 保留同一个 `MediaPlayer`、会话身份和本地 playlist URL；
3. 只有已开始播放且存在新的 recovery ticket 时才允许恢复；
4. 连续最多两次原生 `Play` 恢复尝试；
5. 只有新 payload 已完成读取且原生观察到新的音频解码或画面输出后才清零尝试预算。

`Play()` 被接受或收到普通 `Playing` 事件本身不足以证明恢复成功。恢复用 12 秒有界观察；失败报告当前媒体错误，不伪装成自然结束。

暂停在进入共享命令门前锁存意图并取消正在观察的恢复。后来的 resume 只能延续当前输入或按当前 recovery ticket 恢复；旧 resume、旧 EOF 和旧回调不能覆盖新的暂停、停止或媒体替换。

## 生命周期

- stop、替换、资源撤权、调用方取消和关闭会取消刷新与恢复，等待 playlist gate、上游读取、本地表示服务和原生会话退出。
- live 状态只属于当前 generation，不持久化窗口、序号、能力或 recovery ticket。
- 失败后不脱离原 owner／revoked 生命周期另开输入或伪装恢复成功。
