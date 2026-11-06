# Windows 系统媒体会话

`MeowFilm.SystemMedia` 将当前 App 唯一播放器投影到 Windows 系统媒体传输控件。它不创建播放器、不选择来源、不访问账号，也不持久化系统会话状态。

## 装配

- App 窗口只附加一个 `SystemMediaSession` 和一个 `WindowsSystemMediaBackend`。
- `ISystemPlayback` 投影当前播放器状态及 play、pause、stop、seek；生产装配直接包装唯一 `NativePlayback`。
- `SystemPlaybackOwner` 按 `OwnerId` 注册 next／previous 和 audio／video kind，可提供 Play 回调执行业务恢复准入；提供后不再回退到原生 Play。音乐用此入口阻止恢复已排除的错源，未提供回调的 owner 保持原生恢复行为。未注册 owner 的媒体不会向系统控制面声明可操作。
- 系统只接收标题、播放状态、位置、时长和能力标志；不接收平台凭据、媒体 URL、文件路径或脚本状态。

## 命令围栏

- 每次发布都记录当前 owner、identity 和 generation。
- WinRT 命令回到 WPF dispatcher 后再次核对同一身份；迟到命令不能操作新媒体。
- 同时排队的系统请求最多 16 个。
- play、next、previous 和 seek 经会话命令门串行。
- pause 和 stop 先推进 interruption fence，使它们可以中断仍在等待 I/O 的旧 play／next 意图。
- seek 必须是有限非负值且不超过当前已知时长。
- 未提供 next／previous handler 时相应系统能力保持关闭。

## 关闭

窗口进入关闭流程时先调用 `Suspend`：

1. 停止发布状态；
2. 解绑播放器和 WinRT 事件；
3. 取消会话命令；
4. 禁用系统控制面。

`DisposeAsync` 随后释放 backend，并等待已接纳命令最多 5 秒。超时明确为 `SYSTEM_MEDIA_DRAIN_FAILED`，不能把非合作命令当成已经终止。更新、命令和禁用错误只发布固定错误代码，不暴露操作系统或媒体来源原文。
