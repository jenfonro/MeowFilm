# Windows 原生媒体实现

`MeowFilm.Media` 是当前 Windows App 唯一的媒体播放基础设施。原生音乐能力与 Node film 分别负责各自业务选择、授权资源和恢复状态；Media 只负责受控输入、共享原生播放 owner、媒体命令、轨道以及实际释放。

## 依赖和所有权

- App 只创建一个 `NativePlayback` 作为共享播放 owner，不按页面、平台或内容类型创建独立 owner；两阶段交接的候选播放器也由它管理，不拥有第二套当前输出。
- `NativePlayback` 在所属 WPF STA dispatcher 上创建；全部公开状态和事件回到该 dispatcher。
- LibVLC engine 延迟创建并由播放器实例拥有。播放器替换、停止、自然结束、失败和关闭都经过同一串行命令门。
- `OwnerId`、`Identity` 和内部 generation 共同标识当前会话。旧命令、旧回调、旧取消和旧资源不得作用于新会话。
- Media 不选择平台来源、不读取账号、不运行 Node、不保存业务队列，也不提供下载管理。

影视广格式路径保留锁定 libVLC 3.0.24 的解码／渲染，不替换播放器框架。`apps/windows/native/VideoKernel` 的私有 access-demux 插件使用锁定 FFmpeg 8.1.2（来源／配置见 `video-kernel.lock.json`）解复用及处理合法 ClearKey，再将 packet／codec 参数交给 VLC `es_out`；不转码、不伪造长度 remux、不整片落盘，也不创建新媒体 HTTP 服务。FFmpeg 禁用自有网络／协议，根与子 AVIO 经现有原生受控 HTTP 或准确 resource lease；key 只经私有内存传递，不进入 URL、MRL／公开 options、日志或普通事件。

## 输入边界

| 入口 | 输入 | 约束 |
| --- | --- | --- |
| `OpenAsync(..., Uri, ...)` | 本地普通文件或 HTTP(S) | 本地文件按实际输入格式解析；网络输入按 HTTP／媒体格式处理 |
| `OpenAsync(..., Uri, headers, ...)` | 需要受控请求头的 HTTP(S) | 请求头、重定向、地址、Range、超时和表示一致性由 `HttpMediaInput` 验证 |
| `OpenReadAsync` | `INativeMediaReadLease` | 调用即转移独占所有权，只读取已经授权的不可变、只读、可 seek 字节流 |
| `OpenResourceAsync` | `INativeMediaResourceLease` | 接管可撤销 resource receipt；不授予或限制 URL 访问 |
| `PrepareRuntimeMediaAsync` | 同一 `RuntimeMediaPlaybackIntent`、`NativeVideoInput` 及 HTTP(S) 地址或准确 resource lease | typed format／ClearKey 经私有桥；地址不作 Origin／回环或 URL 图准入，候选 prepare 与 commit／discard 绑定同一 intent 实例 |

身份和标题从不解释为路径或 URL。普通 `Open*` 成功返回只表示内核接受播放请求，不代表已经产生声音、画面或硬件解码；影视 `PrepareRuntimeMediaAsync` 还须等待真实帧准备，但准备成功不等于已提交当前输出、已出声或已验证硬件解码。

## HTTP 与自适应媒体

- 普通带头 progressive 输入使用受控 `HttpClient` 和 libVLC callback，不经过 Node、Base64、临时文件或产品内转发服务。
- 没有地址分类、域名名单、Origin／回环／DNS 或 URL 图访问阻断；HTTP(S) 重定向与子请求按实际地址访问。URI／HTTP／媒体解析仍真实执行。
- 标准 HTTP(S) URL 的 userinfo 按百分号解码后用于 Basic Authorization，无密码时使用空密码；显式 Authorization 请求头优先。每次重定向／子请求按其实际 URL 处理，不建立额外凭据或 Origin 许可表。URL 不设任意长度准入；HTTP framing、header 与媒体解析资源预算仍保留。
- 显式调用方头按输入快照传递，不按 Origin 剥离；响应 Cookie 不形成 Cookie jar。
- 只有通过强校验的 Range 表示才允许随机访问；表示、长度或 validator 变化会明确失败。
- 既有 `AdaptiveMediaSession` 输入解析 HLS／DASH 并重建播放器可读的实际资源路由；其本地服务不做 Origin／Host／随机请求头访问校验，未知路由仍为 404。影视 typed 输入经上述 FFmpeg 私有 AVIO 路径。
- 当前 HLS VOD、受限 live HLS、受限 DASH、AES-128 和离线 `MEOWVOD1` 归档边界分别见 `adaptive-media.md`、`live-hls.md`、`adaptive-encryption.md` 与 `media-offline-input.md`。

## 播放状态与命令

- `OpenAsync`／`OpenReadAsync`／`OpenResourceAsync` 先验证新请求，再替换旧会话；无效新请求不能中断当前播放。
- `Started` 只由当前原生 `Playing` 事件触发一次；选择来源或 `Play()` 被接受不等于开始播放。
- `PauseAsync`、`ResumeAsync`、`SeekAsync`、轨道、倍速和字幕命令都绑定提交时的当前会话。
- 暂停和 seek 意图具有独立时序围栏，排队的旧操作不能落到后来切换的媒体。
- `PositionSeconds`、`DurationSeconds`、`IsPlaying` 和轨道快照来自当前原生会话；不以 UI 动画伪造播放状态。
- 受控 continuation 只保存当前逻辑位置和暂停意图，不授予新的 URL、文件或账号权限。
- 影视 runtime 输入先 prepare 实际视频帧，再以同一 intent commit；准确 donor scope 用于保留旧流，失败不把新 attempt 身份伪装成旧 owner。时间 seek 使用内核时间轴与 flush，不以字节偏移冒充；实现存在不等于这些行为已完成真实媒体验收。

## 生命周期和错误

- 调用方取消、资源撤权、替换、停止和释放先使当前 generation 失效，再等待原生回调、输入、监听器和租约实际退出。
- read lease 和 resource lease 均恰好释放一次；释放失败使用稳定错误并阻止实例继续打开媒体。
- `DisposeAsync` 必须在 WPF dispatcher 关闭前等待完成。
- `PlaybackException` 只公开 `PlaybackError` 中的固定代码；不暴露 URI、路径、请求头、凭据、响应正文或原生日志。
- 非当前离线归档和损坏输入明确失败且不被改写。

当前私有 native DLL 已有独立构建证据，但八模块／VideoKernel 合并后的 Media C#、整 App 与真实媒体帧、ClearKey、seek、撤权排空仍无本轮有效平台通过结论；单独 DLL、旧音频测试或 UI 状态不能替代这些证据。影视业务及端侧接缝分别见[共同运行器契约](../runtime/film-playback.md)和[影视播放页](film-playback.md)。

## 相关契约

- `media-offline-input.md`
- `scoped-media-resources.md`
- `adaptive-media.md`
- `live-hls.md`
- `adaptive-encryption.md`
- `media-tracks.md`
- `system-media.md`
