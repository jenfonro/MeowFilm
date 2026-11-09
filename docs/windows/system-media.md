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

`Window.Closing` 是可取消的关闭意图，不终止系统媒体 attachment。关闭询问打开、取消关闭、隐藏到托盘及恢复窗口均保持同一个会话，继续发布元数据和接收命令。

关闭期间的业务准入归现有 owner 管理：音乐的 `PrepareShutdownAsync` 同步封闭业务命令准入并排空已接纳工作，`CancelShutdownPreparationAsync` 完成恢复后重新准入；系统 Play／Next／Previous 仍走这些 owner 入口。Pause／Stop／Seek 操作 App 持有的唯一 `NativePlayback`，不访问模块已释放的业务资源。播放器自身的命令门与 disposing 围栏继续管理原生操作。

真正释放由 App 生命周期调用 attachment 的 `DisposeAsync`：正常关闭先完成模块 preparation 和 disposal，强制清理则等待 owner 的 Abort 实际完成。两条路径都在释放 attachment 后，才通过 App 的资源回调释放 `NativePlayback`。准备失败后取消退出不会释放 attachment；不在系统媒体层重复建设 preparation 屏障。

attachment 的重复 `DisposeAsync` 共享 session 的完成结果。会话释放首先调用不可恢复的 `Suspend`：

1. 停止发布状态；
2. 解绑播放器和 WinRT 事件；
3. 取消会话命令；
4. 禁用系统控制面。

`DisposeAsync` 随后释放 backend 一次，并等待已接纳命令最多 5 秒。已经投递到 dispatcher 的旧事件与排队命令在会话终止后失效，不再调用播放 owner 或播放器。超时明确为 `SYSTEM_MEDIA_DRAIN_FAILED`，不能把非合作命令当成已经终止，App 也不得据此继续释放播放器。更新、命令和禁用错误只发布固定错误代码，不暴露操作系统或媒体来源原文。
