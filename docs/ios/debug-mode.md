# iOS Debug 模式与调试接口

共同契约、启动参数全集、16 类 JSONL 结构、`$blob` 不脱敏外置规则及 `/debug/v1/*` 全部接口规范以 [跨端 Debug 模式与调试接口](../common/debug-mode.md) 为唯一来源；本文记录 iOS 端显式 Debug 模式的实现边界、启动方式、目录隔离、Control API 端侧差异与底层诊断事实。

## 1. 模块与工程边界

- **共享基础设施**：`native/MeowKit/Sources/MeowDebug`（零第三方依赖，仅依赖系统框架 `Foundation`、`CryptoKit`、`Network`、`Darwin`；UIKit 辅助代码以 `#if os(iOS)` 隔离以保持 `.macOS("15.0")` 兼容），登记于 `scripts/check-source-scope.py` 的 `SHARED_TARGETS`；不反向依赖任何业务 target，不向音频/音乐依赖闭包引入视频内核依赖。
- **App 宿主装配**：`apps/ios/Debug/` 按职责分层为 `Host/`（`MeowfilmDebugHost.swift`、`MeowfilmDebugDiagnosticsBridge.swift`）、`Navigation/`（`MeowfilmDebugNavigator.swift`）、`Inspection/`（`MeowfilmDebugInspection.swift`）与 `Actions/`（`MeowfilmDebugActions.swift`），通过 `scripts/generate-ios-project.py` 的 `APPLICATION_DIRECTORIES` 纳入 `Meowfilm.xcodeproj`。
- **生产协议零放宽**：
  - `RuntimeHTTPClient` 生产 `request(...)` / `value(...)` 路径不变；Debug 专用透传位于 `RuntimeDebugBridge.swift`。
  - `SQLiteDatabase` 正常写入与事务契约不变；`storage/query` 位于 `SQLiteDebugQuery.swift`，支持 `db: "data" | "cache"`（默认 `"data"`）双库路由与 `maxRows`（默认 `200`，夹取 `1..5000`），返回统一信封 `{ db, columns, rows: [{ col: val }], rowCount, truncated, durationMs }`（BLOB 统一 raw base64），并在独立只读连接（`SQLITE_OPEN_READONLY` + `PRAGMA query_only = ON`）上结合领先动词检查、只读 `PRAGMA` 白名单、单语句校验与 `sqlite3_stmt_readonly(stmt) != 0` 强制只读。
  - `apps/ios/Info.plist` 声明 `NSLocalNetworkUsageDescription` 以支持真机 `--debug-bind 0.0.0.0`；非 Debug 启动不触发本地网络监听与系统弹窗。

## 2. 启动参数与零副作用约束

参数解析在 `MeowfilmAppDelegate.init()` 最前端执行（`DebugLaunchConfiguration.parse(arguments:)`）：

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `--debug` | 关闭 | 唯一总开关；未传入时忽略所有 `--debug-*` 与 `--data-root`，不创建调试目录、不写会话文件、不监听端口、不安装异常钩子 |
| `--debug-port <0..65535>` | `0`（自动分配） | Control Server HTTP/1.1 监听端口 |
| `--debug-bind <addr>` | `127.0.0.1` | 模拟器使用 `127.0.0.1`；真机局域网直连使用 `0.0.0.0` |
| `--debug-dir <abs-path>` | `<AppContainer>/Library/Application Support/meowfilm-debug` | 调试会话根目录（设置 `isExcludedFromBackup = true`） |
| `--data-root <abs-path>` | 默认应用数据目录 | 仅在同时传入 `--debug` 时生效，隔离 SQLite、资源缓存与复位标记，不影响真实用户数据 |
| `--debug-modules <csv>` | `all` | 16 个分类名子集或 `all` |
| `--debug-inline-limit <chars>` | `2048` | 单字符串超过阈值外置为 `blobs/<sha256>.txt`（`0` 禁用） |
| `--debug-event-limit <bytes>` | `16384` | 事件 `data` 超过阈值外置为 `blobs/<sha256>.json`（`0` 禁用） |
| `--debug-keep <N>` | `10` | 启动时保留最近 `N` 个历史会话目录 |
| `--debug-first-chance` | 关闭 | 开启关键/首机会错误堆栈记录到异常环形缓冲 |

iOS 端解析规则：
- 宽容忽略 Xcode / `simctl` / `devicectl` / UIKit 注入的系统参数（如 `-AppleLanguages`）。
- 传入 `--debug` 且任意 `--debug-*` / `--data-root` 非法时，向 `stderr` 输出诊断并立即退出（`exit(2)`），不静默回退为非 Debug 模式。

### 启动与访问示例

- **iOS 模拟器（Mac 宿主机直连 `127.0.0.1`）**：
  ```bash
  xcrun simctl launch <UDID> dev.meowfilm.aggregate --debug --debug-port 19777
  curl -s http://127.0.0.1:19777/debug/v1/health | jq
  ```
- **iOS 真机（局域网直连 + 离线容器拉取）**：
  ```bash
  xcrun devicectl device process launch --device <DEVICE_ID> dev.meowfilm.aggregate -- --debug --debug-bind 0.0.0.0 --debug-port 19777
  curl -s http://<IPHONE_IP>:19777/debug/v1/files/session.json | jq
  xcrun devicectl device copy from --device <DEVICE_ID> --domain-type appDataContainer --domain-identifier dev.meowfilm.aggregate --source "Library/Application Support/meowfilm-debug" --destination ./tmp/ios-debug-pull
  ```

## 3. 目录隔离与日志写入

- 调试根目录默认位于 `<AppContainer>/Library/Application Support/meowfilm-debug`，与业务目录 `Application Support/meowfilm` 平级；`MeowfilmUserDataReset` 清空本机数据时不会删除正在运行的调试会话。
- Swift 侧与同进程 `NodeMobile` 共享写入 `<sessionDir>/logs/<cat>.jsonl`（16 个分类：`lifecycle`、`ui-nav`、`ui-action`、`runtime-bridge`、`runtime-node`、`music-platform`、`music-account`、`music-catalog`、`music-library`、`music-playback`、`film-catalog`、`film-playback`、`novel`、`search`、`storage`、`diagnostics`），采用串行写队列 + 单次 `POSIX O_APPEND` 原子追加与 `CryptoKit.SHA256` 去重外置 `$blob`。
- 调试模式下不做脱敏，原始请求、响应、账号状态与规则报错完整记录（仅按长度阈值去重外置至 `blobs/`）。

## 4. Control Server 与 iOS 端侧路由差异

基于 `Network.framework`（`NWListener` / `NWConnection`）提供全部 `/debug/v1/*` 端点，支持统一 JSON 信封（`ok`/`ts`/`traceId`/`data` 或 `error`）与二进制响应（PNG 截图、Blob、会话文件下载，严格防路径穿越）。

### iOS 特有 `nav/open` 映射与 `501 UNSUPPORTED_ON_PLATFORM`

| 目标 / 端点 | iOS 行为 | 说明 |
| --- | --- | --- |
| `POST /debug/v1/nav/forward` | `501 UNSUPPORTED_ON_PLATFORM` | iOS 导航栈无前进历史 |
| `novel.shelf` | `501 UNSUPPORTED_ON_PLATFORM` | iOS 无独立小说书架页（见 [novel.md](novel.md)）；可通过 `storage/query` 查询 `novel_books` 或用 `novel/command` (`toggle_shelf`) 操控 |
| `settings.media-controls` | `501 UNSUPPORTED_ON_PLATFORM` | Windows 桌面专属快捷键设置页 |
| `settings.general` (`window` / `shortcuts`) | `501 UNSUPPORTED_ON_PLATFORM` | Windows 桌面专属窗口/快捷键分组 |
| `music.history` | 打开 `MusicQueueViewController` 并切至 `songs` | iOS 历史单曲统一承载于队列浮层 |
| `music.archives` | 打开 `MusicQueueViewController` 并切至历史歌单卡片（空历史时落回 `songs`） | 对应已播队列记录 |
| `music.personal` / `music.favorites` | 映射至 `MusicLibraryViewController` 或收藏歌单 `MusicCollectionViewController` | 归并展示于曲库 |
| `settings.film` 各子页 + `cache` | 逐一推入对应子控制器并上报精确 `settingsSection` | 覆盖 `search-rules`、`rules`、`cats`、`videos`、`metadata`、`pan`、`pan-routing`、`preferences`、`preferences-detail`、`site-clean`、`auto-prepare`、`player`、`cache` |

### 弹窗与 Sheet 操控（无私有 API）

- 全仓 `UIAlertController` 收口于 `PageAlertPresenter`，构造与呈现时自动向 `MeowDebug` 的弱引用公共表 `DebugAlertActionRegistry` 登记全部业务按钮回调（不触碰任何 `UIAlertAction` 私有 KVC 键），`dialog.action` 直接触发真实按钮动作或安全关闭。
- 全部居中卡片弹窗继承 `FloatingCardDialogController` 并实现 `DebugDialogControllable`（覆盖 `ManagementDialog`、音乐账号登录／详情、平台排序与网盘登录等），`dialog.action` 通过公开调试入口读写字段、提交或关闭。
- `apps/ios/Debug/` 按职责拆分为 `Host/`、`Navigation/`、`Inspection/`、`Actions/` 四个子目录，分别承载生命周期与诊断桥接、路由导航、内容与视图树检查、以及语义动作执行。

## 5. 底层诊断与 Node 透传降级

- **`diag/summary`**：通过 Mach `task_info(TASK_VM_INFO)` 报告真实 `physFootprintBytes`（iOS jetsam 计费内存）、`residentBytes`、`virtualBytes`、`compressedBytes`、`task_threads` 线程数、`/dev/fd` 打开句柄数，以及 `native.leases` 下的 `playback`（`PlaybackController`）、三域图片缓存（`filmImagesCache` / `musicImagesCache` / `novelImagesCache`）、`uiMetrics`（`DebugUIMetrics`：歌词动画重建次数、详情页渲染统计等）、`activeFilmModelsCount`、`musicAudioFileCache`（`NativeMusicAudioFileCache`）与 `runtimeSession`（`RuntimeSession`）活跃租约计数。
- **`diag/threads`**：枚举全部 Mach 线程状态与 CPU 时间，并采集主线程与当前线程符号化调用栈。
- **`diag/gc`**：清理 `URLCache` 与三域图片内存缓存、执行 `autoreleasepool` 排空，并转发 Node `/v1/debug/gc`，返回前后内存对比。
- **`diag/dump`**：在 `dumps/<timestamp>-ios-diag.json` 原子输出包含 summary、线程堆栈、Mach `vm_region_recurse_64` 内存区域分布、异常缓冲、UI 状态与播放态的完整诊断报告，并可通过 `GET /debug/v1/files/<relpath>` 下载。
- **`diag/exceptions`**：维护容量 500 的线程安全异常环形缓冲，串联 `NSSetUncaughtExceptionHandler` 与关键 Swift 错误。
- **Node 透传与降级**：`RuntimeEmbeddedLauncher` 在 Debug 启动时向 socketpair 握手首行附加 `debug` 配置块；`RuntimeHTTPClient` 透传 `X-Meow-Debug-Op` / `X-Meow-Debug-Trace`，与 Node 内置 `/v1/debug/*` 端点及 `scripts/debug.mjs` 统一 CLI 全链路联通。当内嵌 Node 尚未启动或已断开时，`diag/summary` / `diag/gc` 标记 `node.available = false`，`diag/node/heapsnapshot` / `diag/node/cpu-profile` 返回 `503 RUNTIME_UNAVAILABLE`，不影响原生诊断与离线会话拉取。
