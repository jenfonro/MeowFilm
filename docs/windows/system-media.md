# Windows 系统媒体会话

`MeowFilm.SystemMedia` 将当前 App 唯一播放器投影到 Windows 系统媒体传输控件。它不创建播放器、不选择来源、不访问账号，也不持久化系统会话状态。

## 装配

- App 窗口只附加一个 `SystemMediaSession` 和一个 `WindowsSystemMediaBackend`。
- `Program` 从 `ShellWindow.MediaControls` 获取启动时已恢复的设置，并显式调用音乐能力的 `AttachDesktopLyricsOwner(window)`；媒体动作统一交给 `ExecuteMediaShortcutAsync`，不创建第二播放器或隐式主窗口依赖。
- `AppMediaWindowAttachment` 统一持有系统媒体附件、快捷键附件和设置订阅；初始及后续 SMTC 开关为 `IsAvailable && Preferences.SystemMediaEnabled`，不受全局快捷键开关或录制状态影响。未完成装配时的附件仍由入口持有并清理。
- `ISystemPlayback` 投影当前播放器状态及 play、pause、stop、seek；外层附件持有 App 的 `AppSystemMediaAttachment`，以唯一 `NativePlayback` 的准确 owner／identity 选择投影。Film 使用同一 `FilmCapability.PlaybackMedia`，包含外播的真实时钟和暂停状态；其他 owner 保持原生播放器投影，不创建第二个媒体会话。
- `SystemPlaybackOwner` 按 `OwnerId` 注册 next／previous 和 audio／video kind，可提供 Play 回调执行业务恢复准入；提供后不再回退到原生 Play。音乐用此入口阻止恢复已排除的错源，未提供回调的 owner 保持原生恢复行为。未注册 owner 的媒体不会向系统控制面声明可操作。
- 系统只接收标题、播放状态、位置、时长和能力标志；不接收平台凭据、媒体 URL、文件路径或脚本状态。

## 系统媒体快捷键开关

- `SystemMediaSession` 两个构造函数在 `dispatcher` 后接收 `bool enabled = true`；`SystemMediaWindowAttachment` 与 App 的 `AppSystemMediaAttachment` 在 `owners` 后接收同一可选参数，默认保持现有系统媒体行为。
- `SetEnabled(bool enabled)` 在窗口所属 dispatcher 调用；窗口附件直接转发给会话。此开关只控制 Windows SMTC 通道，与自定义全局快捷键开关相互独立。
- 初始关闭和运行中关闭都调用 backend `Disable`，立即撤销系统会话可操作状态，不停止当前播放。关闭期间拒绝系统请求，播放器状态更新不重新启用系统会话。
- 关闭推进命令 generation，使关闭前尚未执行的 dispatcher 请求和串行队列请求失效；即使同一媒体立即重开，旧请求也不能恢复执行。已开始执行的命令沿用既有命令、取消与排空语义，切换开关不取消它们。
- 重开重新读取并发布当前播放器状态、owner 能力和元数据；同一媒体的标题在禁用后也会重新发布。
- `Suspend`／`DisposeAsync` 永久关闭会话，后续 `SetEnabled(true)` 不恢复发布、事件订阅或命令执行。通用设置通过独立 `app.media-controls.v1` 文档保存两个开关与键位，保存后的权威设置即时驱动窗口附件。

## 应用内与全局组合键

- `MediaShortcutWindowAttachment` 负责 WPF 应用内组合键与真实 `RegisterHotKey`／`WM_HOTKEY` 通道；默认 8 个应用内动作、7 个全局动作，译文显隐只有应用内键。
- 全局开关关闭、录制或设置不可用时撤销注册并清除占用状态，不探测抢键；实际键位／全局开关变化和显式重新检测会真实重注册。仅 SMTC 开关、busy、Status、注册错误及值未改变的属性通知不打断注册或已排队请求。
- 只有 `RegisterHotKey` 返回 1409 才显示“热键被占用”；拒绝访问、无效窗口／组合键、资源不足和其他失败分别映射固定错误，不暴露原始系统文本。释放失败保留真实所有权，宿主可重试清理。
- 本地输入保护文本框、密码框、可编辑 ComboBox、菜单和 Popup。全局输入先用 `GetForegroundWindow` 与自有 HWND 判断归属，仅保护本 App 当前前台输入；后台遗留焦点以及其他程序的编辑状态不会阻断用户启用的全局控制。
- 成功注册且同一动作的局部／全局组合键重叠时，由 `WM_HOTKEY` 唯一执行，WPF 只消费对应按键，消息先后顺序不产生双执行。本地按下／释放及 `IsRepeat`、全局 `MOD_NOREPEAT` 避免长按重复。
- 注册 ID 按 DLL 的 Win32 全局 atom 范围分配；撤销后经窗口消息队列屏障才释放 ID。改绑、暂停或关闭后，旧消息不能命中新注册。

## 命令围栏

- 每次发布都记录当前 owner、identity 和 generation。
- WinRT 命令回到 WPF dispatcher 后再次核对同一身份；迟到命令不能操作新媒体。
- 同时排队的系统请求最多 16 个。
- play、next、previous 和 seek 经会话命令门串行。
- pause 和 stop 先推进 interruption fence，使它们可以中断仍在等待 I/O 的旧 play／next 意图。
- seek 必须是有限非负值且不超过当前已知时长。
- 未提供 next／previous handler 时相应系统能力保持关闭。

## 关闭

`Window.Closing` 是可取消的关闭意图，不终止系统媒体 attachment。关闭询问打开、取消询问、隐藏到托盘及恢复窗口均保持同一个会话，继续发布元数据和接收命令；这些操作不等于进入退出准备。

真正退出准备通过 App 已有 sharedParticipants 顺序执行，不在系统媒体层另建 preparation 所有者：

- 共享媒体设置先将 `IsAvailable` 置为 false；组合附件禁用 SMTC，快捷键附件释放注册并拒绝新输入，尚未执行的旧请求失效。此后不再接收任何系统媒体命令，包括直接操作唯一 `NativePlayback` 的 Pause／Stop／Seek，不停止已在播放的媒体。
- 音乐 owner 随后封闭业务命令并排空已接纳工作；之前已开始的系统命令继续遵守原有取消与实际排空语义。
- 准备失败后取消退出，业务 owner 恢复后共享设置恢复 `IsAvailable`；两个通道依当前权威开关重新启用，保留的订阅继续生效。

系统 Play／Next／Previous 走各业务 owner 的恢复与切集入口。App typed attachment 将 Film 的 Pause／Stop／Seek 交回准确 Film owner，外播不误控暂停的 native donor；Film 的会话／attempt 围栏拒绝退休或替换后的操作。其他 owner 的直接控制仍操作 App 持有的唯一 `NativePlayback`，播放器自身的命令门与 disposing 围栏继续管理原生操作。

真正释放由 App 生命周期调用 attachment 的 `DisposeAsync`：正常关闭先完成模块 preparation 和 disposal，强制清理则等待 owner 的 Abort 实际完成。两条路径都在释放 attachment 后，才通过 App 的资源回调释放 `NativePlayback`。准备失败后取消退出不会释放 attachment。

实际 `Closed` 或附件 `DisposeAsync` 才永久封准入并解绑。系统媒体附件的重复 `DisposeAsync` 共享 session 的完成结果，实际关闭或会话释放调用不可恢复的 `Suspend`：

1. 停止发布状态；
2. 解绑播放器和 WinRT 事件；
3. 取消会话命令；
4. 禁用系统控制面。

组合附件清理时先封准入，等待已接纳的快捷键动作实际结束、完成原生注册释放，再释放系统媒体附件。子附件释放失败会向宿主报告；再次清理只重试未成功释放的资源，不永久缓存失败任务。

系统媒体会话 `DisposeAsync` 释放 backend 一次，每次观察已接纳命令排空最多等待 5 秒；实际排空任务在观察超时后继续持有命令生命周期，结束后才释放取消源。已经投递到 dispatcher 的旧事件与排队命令在会话终止后失效，不再调用播放 owner 或播放器。超时明确为 `SYSTEM_MEDIA_DRAIN_FAILED`，不能把非合作命令当成已经终止，App 也不得据此继续释放播放器。显式重试重新观察同一实际排空任务，不重复释放 backend；命令迟到结束后重试可成功，仍在执行则继续等待或报告超时。更新、命令和禁用错误只发布固定错误代码，不暴露操作系统或媒体来源原文。
