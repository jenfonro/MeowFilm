# Windows 原生音轨、字幕轨与倍速

`NativePlayback` 直接拥有当前媒体的轨道、倍速与字幕延迟操作。页面只投影当前快照并提交用户意图，不保存另一份轨道状态。

## 会话作用域

`NativePlaybackScope` 包含 `OwnerId`、`Identity` 和原生 session generation。以下操作都必须携带当前 scope：

- `SelectAudioTrackAsync`
- `SelectSubtitleTrackAsync`
- `SetRateAsync`
- `SetSubtitleDelayAsync`

scope 与当前任一字段不符时使用 `MEDIA_SUPERSEDED`，不能把旧页面、旧媒体或旧异步命令应用到新会话。

`ReadTracksAsync` 从当前 libVLC 实例读取 `NativeMediaTracks`。停止、替换、失败和结束会清空快照；列表不从文件名、平台元数据或页面缓存推导。

## 当前规则

- 音轨和字幕轨使用原生真实 ID，不使用 UI 下标。
- 每类最多 256 项，ID 必须唯一，名称去除控制字符并限制为 256 个字符。
- 音轨／字幕切换后立即再次读取原生快照；失败不伪造选中状态。
- 倍速只接受 0.25 至 4 的有限值。
- 字幕延迟只接受 -120 至 120 秒，且当前必须存在字幕轨。
- `NativeMediaTracks.Rate` 是 libVLC 读回的请求值；`EffectiveRateVerified` 固定为 `false`，不声称设备实际吞吐、音高或渲染速度。

所有操作经过唯一播放器命令门并在 await 后再次核对 scope。取消、替换或 generation 改变时，旧结果不得发布到当前绑定。

当前接口只管理媒体内嵌轨道、倍速与字幕延迟。外部歌词由音乐歌词 owner 管理。
