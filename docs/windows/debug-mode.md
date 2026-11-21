# Windows 端 Debug 模式实现说明

共同契约、启动参数全集、16 类 JSONL 结构、`$blob` 不脱敏外置规则及 `/debug/v1/*` 全部接口规范以 [跨端 Debug 模式与调试接口](../common/debug-mode.md) 为唯一来源；本文仅记录 Windows 端的具体分层、现行实现事实与本端差异。

## 1. 启动入口与零副作用边界

- **入口**：`apps/windows/app/Program.cs` 通过 `MeowFilm.Core.Debug.DebugLaunchOptions.Parse(args)` 解析命令行参数。
  - 未传 `--debug` 时直接返回 `DebugLaunchOptions.Disabled`，忽略所有 `--debug-*` 与 `--data-root` 选项，不创建调试目录、不绑定本地监听端口、不重定向 Node 子进程输出、不写入日志、不在握手 JSON 中发送 `debug` 字段，也不放开 `--data-root` 覆盖。
  - 参数非法（端口越界、非法分类名、缺值等）时直接向 `stderr` 输出错误并以退出码 `2` 终止，不进入应用主循环。
- **默认调试目录与重置隔离**：`AppDataPaths.DefaultDebugRoot(AppIdentity.StorageId)` 默认为 `%LOCALAPPDATA%\meowfilm\aggregate-debug`，与 `%LOCALAPPDATA%\meowfilm\aggregate` 平级；`UserDataDirectory` 整目录清除仅作用于数据目录，不会误删正在运行的调试会话。

## 2. 项目分层与依赖约束

遵循 `scripts/windows/verify-projects.mjs` 规定的单向依赖边界（不改动任何 `.csproj` 项目引用）：

| 项目 | Debug 职责与核心文件 |
| --- | --- |
| `MeowFilm.Core` | 中性契约与接口：`Debug/DebugCategories.cs`、`DebugLaunchOptions.cs`、`DebugLog.cs`（`DebugLog` 静态路由、`AsyncLocal` 关联上下文、`DebugLeaseRegistry`）、`DebugControlContracts.cs`（`IDebugShellHost`、`IDebugModuleAdapter`、`IDebugRuntimeHost`、`IDebugStorageQueryHost`、`DebugApiException`）；`Runtime/RuntimeHttpClient.cs` 与 `RuntimeHttpEvents.cs` 的 RPC/SSE 埋点、按模块镜像至业务分类、`X-Meow-Debug-Op`/`X-Meow-Debug-Trace` 透传及 `/v1/debug/*` 转发。 |
| `MeowFilm.Platform` | `Debug/DebugBlobStore.cs`（SHA-256 去重外置与原子写盘）、`DebugSession.cs`（会话目录、`active-session.json` / `session.json` 原子更新、16 类惰性 JSONL 写入、tail/过滤、`log_event` 等待器、`--debug-keep` 清理）、`DebugDiagnostics.cs`（进程内存/GC/句柄/OS 线程/ThreadPool/UI 线程探活与堆栈、异常环形缓冲、独立后台线程调用 `dbghelp.dll` `MiniDumpWriteDump`）、`DebugControlHttpServer.cs`（零外部依赖 `TcpListener` HTTP/1.1 Server，承载全部 `/debug/v1/*` 路由）；`Runtime/NodeRuntime.cs`（握手 `debug` 字段透传、stdout/stderr 落盘与 `runtime-node` 日志）；`Storage/NativeDatabase.cs`（事务耗时记录与严格只读 SQL 查询）。 |
| `MeowFilm.AppHost` | `AppLauncher.cs` 负责 `DebugSession` / `DebugDiagnostics` / `DebugControlHttpServer` 的完整生命周期（启动监听、运行期关联 `ShellWindow` / `NativeDatabase` / `AppRuntimeConnectionHost`、退出写回 `status: "exited"` 与 `exitSummary`）；`AppRuntimeConnectionHost.cs` 实现 `IDebugRuntimeHost` 并在运行时地址变更时刷新 `active-session.json`。 |
| `MeowFilm.Desktop` | `ShellWindow.Debug.cs` 实现 `IDebugShellHost`：UI 线程安全调度、`nav/open`/`back`/`forward`/`refresh`、通用设置 5 区与模块/缓存/媒体控制直达、`ui/state`、`ui/content`、`ui/tree`（WinUI `VisualTreeHelper`）、`ui/screenshot`（优先 `RenderTargetBitmap`，回退 `PrintWindow(PW_RENDERFULLCONTENT)`）、`ui/action`、`ui/wait`、`playback/*`、`novel/*`；`GeneralSettingsView.xaml.cs` 暴露 `SelectArea` / `DescribeDebugTabs` / `BuildDebugSnapshot` / `TryDebugSetAsync`。 |
| `MeowFilm.Capabilities` | 各模块通过 `IDebugModuleAdapter` 局部实现语义导航、结构化页面投影与动作执行：`Composition/MusicCapability.Debug.cs` + `Settings/MusicSettingsView.xaml.cs`、`Film/FilmCapability.Debug.cs` + `Film/Presentation/FilmSettingsView.xaml.cs`、`Novel/NovelCapability.Debug.cs` + `Novel/Presentation/NovelSettingsView.cs` + `NovelView.cs`、`Search/SearchResultsPage.Debug.cs`。 |
| `MeowFilm.Audio` / `MeowFilm.Media` | `NativeAudioPlayback.cs` 与 `FilmPlayback.cs` 向 `DebugLeaseRegistry` 注册活跃会话与句柄租约，并在 `--debug` 下记录原生核心加载/状态/错误至 `music-playback` / `film-playback`。 |

## 3. 本端实现细节与差异

- **静默底层无焦点启动**：Windows 端在 `--debug` 模式下默认静默启动——启动过渡窗不弹出，主窗口通过 `DesktopWindow.ShowBackgroundInactive()`（`WM_WINDOWPOSCHANGING` 压至 `HWND_BOTTOM` + `AppWindow.Show(false)`）以正常可见尺寸落在桌面 Z-order 最底层，不抢占当前前台焦点、不遮挡用户当前窗口；窗口非隐藏/非最小化，WinUI 渲染与截图完整生效，用户可随时点击任务栏切回前台。`meowfilm.exe` 为 `WinExe` 且 `node.exe` 子进程以 `CreateNoWindow = true` 启动，本身不弹出任何控制台窗口；经 SSH／计划任务在交互桌面拉起时应直接执行 `meowfilm.exe --debug ...`，避免再通过交互 `cmd.exe` / `powershell.exe` 包装拉起而弹出控制台黑框抢焦点。未传 `--debug` 时保持原样前台 `Activate()`。
- **本地 Control HTTP Server**：默认监听 `127.0.0.1:0`（支持 `--debug-bind` 与 `--debug-port`），使用纯托管 `TcpListener` 解析 HTTP/1.1 请求（单请求体上限 16 MiB），避免引入额外运行时依赖或需要管理员 `netsh http add urlacl` 权限。
- **截图策略（`GET /debug/v1/ui/screenshot`）**：在 UI 线程优先使用 WinUI `RenderTargetBitmap.RenderAsync(Root)` 编码 PNG；若渲染缓冲不可用则回退至 Win32 `PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT)`。`save=true` 时同步落盘至 `<sessionDir>/screenshots/` 并返回 `X-Meow-Screenshot-Path` 响应头。
- **只读 SQLite 查询（`POST /debug/v1/storage/query`）**：仅放行单条 `SELECT` / `WITH` / 只读 `PRAGMA`（禁止 `=` 写型赋值及 `query_only` 修改）/ `EXPLAIN` 语句，在 `NativeDatabase` 现有单一通道内以 `PRAGMA query_only = ON` 执行并在 `finally` 恢复，确保绝不写入并兼容 WAL 读一致性。
- **原生转储（`POST /debug/v1/diag/dump`）**：在独立后台线程调用 `dbghelp.dll` `MiniDumpWriteDump` 写入 `dumps/*.dmp`，支持 `mini` 与 `heap`（`MiniDumpWithFullMemory`）两种模式。
- **与 Node 侧 `/v1/debug/*` 的协同与降级**：Windows 原生宿主在握手 JSON 中透传完整 `debug` 配置并在 RPC/SSE 请求附带 `X-Meow-Debug-Op` / `X-Meow-Debug-Trace`。当连接的 Node 运行时尚未注册 `/v1/debug/*` 路由时，`/debug/v1/diag/summary`、`/debug/v1/diag/threads`、`/debug/v1/diag/gc`、`/debug/v1/config` 自动保留完整原生数据并将 `node` 段标为 `available: false`；`/debug/v1/diag/node/heapsnapshot` 与 `/debug/v1/diag/node/cpu-profile` 返回 `503 RUNTIME_UNAVAILABLE`。
