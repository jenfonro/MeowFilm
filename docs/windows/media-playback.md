# Windows 原生媒体实现

`MeowFilm.Media` 是当前 Windows App 唯一的媒体播放基础设施。能力层负责选择歌曲、授权资源和恢复业务状态；Media 只负责受控输入、一个原生播放器、媒体命令、轨道以及实际释放。

## 依赖和所有权

- App 只创建一个 `NativePlayback`，不按页面、平台或内容类型创建第二个播放器。
- `NativePlayback` 在所属 WPF STA dispatcher 上创建；全部公开状态和事件回到该 dispatcher。
- LibVLC engine 延迟创建并由播放器实例拥有。播放器替换、停止、自然结束、失败和关闭都经过同一串行命令门。
- `OwnerId`、`Identity` 和内部 generation 共同标识当前会话。旧命令、旧回调、旧取消和旧资源不得作用于新会话。
- Media 不选择平台来源、不读取账号、不运行 Node、不保存业务队列，也不提供下载管理。

## 输入边界

| 入口 | 输入 | 约束 |
| --- | --- | --- |
| `OpenAsync(..., Uri, ...)` | 本地普通文件或 HTTP(S) | 文件拒绝 UNC、设备路径、凭据、查询和 fragment；网络输入按当前 URI／HTTP 策略处理 |
| `OpenAsync(..., Uri, headers, ...)` | 需要受控请求头的 HTTP(S) | 请求头、重定向、地址、Range、超时和表示一致性由 `HttpMediaInput` 验证 |
| `OpenReadAsync` | `INativeMediaReadLease` | 调用即转移独占所有权，只读取已经授权的不可变、只读、可 seek 字节流 |
| `OpenResourceAsync` | `INativeMediaResourceLease` | 只接受原生 owner 签发的可撤销资源能力；不把普通 loopback URL 当授权 |

身份和标题从不解释为路径或 URL。成功返回只表示原生内核接受了播放请求，不代表已经产生声音、画面或硬件解码。

## HTTP 与自适应媒体

- 普通带头 progressive 输入使用受控 `HttpClient` 和 libVLC callback，不经过 Node、Base64、临时文件或产品内转发服务。
- 地址策略逐跳验证 DNS 和实际连接地址，拒绝用户信息、私网／回环／链路本地／保留地址以及 HTTPS 降级。
- 跨 origin 重定向永久移除调用方请求头；响应 Cookie 不形成 Cookie jar。
- 只有通过强校验的 Range 表示才允许随机访问；表示、长度或 validator 变化会明确失败。
- HLS／DASH 由 `AdaptiveMediaSession` 解析并重建受限资源图，原生内核只访问会话拥有的 `127.0.0.1` 临时 capability 服务。
- 当前 HLS VOD、受限 live HLS、受限 DASH、AES-128 和离线 `MEOWVOD1` 归档边界分别见 `adaptive-media.md`、`live-hls.md`、`adaptive-encryption.md` 与 `media-offline-input.md`。

## 播放状态与命令

- `OpenAsync`／`OpenReadAsync`／`OpenResourceAsync` 先验证新请求，再替换旧会话；无效新请求不能中断当前播放。
- `Started` 只由当前原生 `Playing` 事件触发一次；选择来源或 `Play()` 被接受不等于开始播放。
- `PauseAsync`、`ResumeAsync`、`SeekAsync`、轨道、倍速和字幕命令都绑定提交时的当前会话。
- 暂停和 seek 意图具有独立时序围栏，排队的旧操作不能落到后来切换的媒体。
- `PositionSeconds`、`DurationSeconds`、`IsPlaying` 和轨道快照来自当前原生会话；不以 UI 动画伪造播放状态。
- 受控 continuation 只保存当前逻辑位置和暂停意图，不授予新的 URL、文件或账号权限。

## 生命周期和错误

- 调用方取消、资源撤权、替换、停止和释放先使当前 generation 失效，再等待原生回调、输入、监听器和租约实际退出。
- read lease 和 resource lease 均恰好释放一次；释放失败使用稳定错误并阻止实例继续打开媒体。
- `DisposeAsync` 必须在 WPF dispatcher 关闭前等待完成。
- `PlaybackException` 只公开 `PlaybackError` 中的固定代码；不暴露 URI、路径、请求头、凭据、响应正文或原生日志。
- 非当前离线归档和损坏输入明确失败且不被改写。

## 相关契约

- `media-offline-input.md`
- `scoped-media-resources.md`
- `adaptive-media.md`
- `live-hls.md`
- `adaptive-encryption.md`
- `media-tracks.md`
- `system-media.md`
