# 跨端 Debug 模式与调试接口

> 状态：**已定稿共同契约（Windows、Node 与 iOS 端均已落地）**。本文是 Windows、iOS 与共用 Node 运行时 Debug 模式的唯一契约来源；各端实现事实、平台细节与落地状态在对应端侧文档与工作记录中维护，不复制本契约。

## 1. 目标与核心原则

1. **运行期参数开关，不做构建分叉**
   - 仅使用正常构建产物（`Release` / `Debug` 均可），通过命令行启动参数 `--debug` 开启 Debug 模式。
   - **未传 `--debug` 时保持零副作用**：不创建调试目录、不写 `active-session.json`、不监听 Debug HTTP 端口、不落盘任何调试日志或子进程输出、不在 Node 握手配置中发送 `debug` 字段、不在 Node 中注册 `/v1/debug/*` 路由。
2. **替代回归测试与 UI 盲操**
   - 以**分类结构化日志 + 不脱敏原始载荷外置 + 本地语义级 Control API + 底层内存/堆栈/转储诊断**替代传统回归测试、测试夹具和基于控件树猜点的外部自动化。
   - 禁止为验证业务新增回归测试、测试夹具、Mock 替身或仅服务于测试的生产钩子。
3. **原始数据不脱敏，超长文本去重外置**
   - 本地 Debug 会话中**不对凭据字段脱敏**：Cookie、Token、Authorization、手机号、完整请求/响应体、账号导入导出原文、脚本返回值均保留原始明文，便于直接核对鉴权与协议数据。
   - 通过 `$blob` 按 `sha256` 去重外置机制控制日志单行体积，防止超长文本把 JSONL 撑爆。
   - 调试输出目录始终位于本机用户数据/App 容器路径下，严禁提交到源码仓库或文档中。
4. **零新增第三方依赖，严守模块边界**
   - Windows（.NET 10）：全部使用标准库与系统自带 Win32 API（如 `dbghelp.dll`），不新增 NuGet 包，不破坏 [verify-projects.mjs](../../scripts/windows/verify-projects.mjs) 的项目依赖方向。
   - iOS（Swift / UIKit）：全部使用 Foundation / UIKit / `Network.framework`（`NWListener`）与 Darwin/Mach 系统 API，不新增第三方依赖。
   - Node（Node 24）：全部使用内置模块（`node:fs`、`node:path`、`node:crypto`、`node:v8`、`node:inspector`、`node:perf_hooks`）。`packages/source-runtime` 继续遵守无 Node builtin 约束（见 [verify-boundaries.mjs](../../scripts/verify-boundaries.mjs)），相关事件由 `packages/script-host` 宿主边界记录。

---

## 2. 启动参数与平台入口

### 2.1 统一启动参数

| 参数 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `--debug` | flag | 关闭 | 开启 Debug 模式总开关 |
| `--debug-port <port>` | int (`0..65535`) | `0` | 本地 Control HTTP 监听端口；`0` 表示由系统自动分配可用端口 |
| `--debug-bind <addr>` | string | `127.0.0.1` | Control HTTP 监听地址；iOS 真机局域网直连时可显式传 `0.0.0.0` |
| `--debug-dir <path>` | string | 见 §2.3 | 覆盖默认调试输出根目录 |
| `--debug-modules <csv>` | string | `all` | 启用的日志分类（逗号分隔），如 `music-platform,music-account,runtime-bridge`；`all` 表示全量 |
| `--debug-inline-limit <chars>` | int (`>= 0`) | `2048` | 单字符串超过该字符数时触发 `$blob` 外置折叠；`0` 表示禁用折叠、强制全内联 |
| `--debug-event-limit <bytes>` | int (`>= 0`) | `16384` | 单条事件 `data` 序列化 UTF-8 字节数超过该阈值时整块外置；`0` 表示禁用整块外置 |
| `--debug-keep <N>` | int (`>= 1`) | `10` | 启动时自动清理早于最近 `N` 次的历史会话目录（不会删除当前会话） |
| `--debug-first-chance [bool]` | flag / bool | 关闭 | 启动即记录首机会异常（First-Chance / 捕获前异常），支持无参开关或显式 `true\|false\|1\|0`；也可运行时通过 `POST /debug/v1/config` 动态开关 |
| `--data-root <path>` | string | 正常用户数据目录 | 指定本次启动使用的业务数据根目录；**未指定时默认直接复用真实数据目录**，开箱带真实账号/歌单/书源/猫源排错 |

规则：
- 未传 `--debug` 时，其他 `--debug-*` 参数被忽略且不开启任何调试行为。
- `--data-root <path>` 仅在同时传入 `--debug` 时生效，防止非调试场景被外部参数改写数据根路径。
- 参数解析失败时（如端口非法，或 `--debug-dir` 与 `--data-root` 路径相等／互为父子目录）在标准错误输出简明诊断并拒绝启动，不静默回退。

### 2.2 端侧参数传入方式

- **Windows**：`meowfilm.exe --debug [--debug-port 19777] [--data-root ...]`（在 `apps/windows/app/Program.cs` `Main(string[] args)` 解析）。
- **iOS 模拟器**：`xcrun simctl launch <UDID> <bundleId> --debug [--debug-port 19777]`（通过 `ProcessInfo.processInfo.arguments` 解析）。
- **iOS 真机**：`xcrun devicectl device process launch --device <id> <bundleId> -- --debug --debug-bind 0.0.0.0 --debug-port 19777` 或 Xcode Scheme Launch Arguments。

### 2.3 默认调试根目录与数据重置隔离

| 平台 | 默认 `<debugRoot>` | 说明 |
| --- | --- | --- |
| Windows | `%LOCALAPPDATA%\meowfilm\aggregate-debug` | 与 `%LOCALAPPDATA%\meowfilm\aggregate` 平级，与 `UserDataDirectory` 整目录清除天然隔离，不会在「清空本机数据」时被误删或占用锁冲突 |
| iOS | `<AppContainer>/Library/Application Support/meowfilm-debug` | 设置 `isExcludedFromBackup = true`（不进 iCloud 备份）；不被系统缓存策略误清；与 `Application Support/meowfilm` 平级，与 `MeowfilmUserDataReset` 天然隔离；真机离线通过 `xcrun devicectl device copy from --domain-type appDataContainer` 读取 |

### 2.4 原生宿主 → Node 运行时握手透传协议

Windows（Named Pipe，[apps/windows/runtime/bootstrap.mjs](../../apps/windows/runtime/bootstrap.mjs)）与 iOS（Unix socketpair `--meow-bootstrap-fd`，[apps/ios/runtime/bootstrap.mjs](../../apps/ios/runtime/bootstrap.mjs)）在首行配置 JSON 中使用完全一致的可选 `debug` 对象：

```json
{
  "sessionId": "<runtime-session-uuid>",
  "dataRoot": "<abs-data-root>",
  "nativeSettings": true,
  "debug": {
    "enabled": true,
    "sessionId": "20261120T173000Z-12345",
    "sessionDir": "<abs-session-dir>",
    "inlineLimit": 2048,
    "eventBytesLimit": 16384,
    "modules": ["all"]
  }
}
```

- **非 Debug 模式**：严禁包含 `debug` 字段，保持现有握手格式零变更。
- **Debug 模式校验**：`debug.enabled === true` 时，`sessionId` 为非空字符串，`sessionDir` 为绝对路径，`inlineLimit` / `eventBytesLimit` 为非负整数，`modules` 为非空字符串数组。

---

## 3. 目录布局与会话发现

每次 `--debug` 启动创建唯一会话目录：

```text
<debugRoot>/
  active-session.json
  sessions/
    <YYYYMMDDTHHMMSSZ>-<pid>/
      session.json
      logs/
        lifecycle.jsonl
        ui-nav.jsonl
        ui-action.jsonl
        runtime-bridge.jsonl
        runtime-node.jsonl
        music-platform.jsonl
        music-account.jsonl
        music-catalog.jsonl
        music-library.jsonl
        music-playback.jsonl
        film-catalog.jsonl
        film-playback.jsonl
        novel.jsonl
        search.jsonl
        storage.jsonl
        diagnostics.jsonl
        runtime-node.stdout.log
        runtime-node.stderr.log
      blobs/
        <sha256>.txt
        <sha256>.json
        <sha256>.<ext>
      dumps/
        <timestamp>-<kind>.<ext>
      screenshots/
        <timestamp>-<seq>.png
```

说明：
- 日志分类文件按需惰性创建（某分类在本次会话无事件时不强制生成空文件）；`runtime-node.stdout.log` 仅 Windows 独立子进程产生，iOS 内嵌 Node 不要求生成。
- **历史会话清理**：启动创建新会话目录后，按目录名降序保留最近 `--debug-keep`（默认 `10`）个会话目录，删除更早会话目录（跳过当前活跃会话）。

### 3.1 `active-session.json` 格式（原子写入）

Control HTTP 监听就绪后以「写临时文件 + 原子替换」写入 `<debugRoot>/active-session.json`；Node 运行时就绪后更新 `runtimeBaseUrl` 和 `runtimePid`；应用正常退出时更新 `status: "exited"` 与 `endedAt`（保留最后一次会话路径便于离线分析）。

```json
{
  "schemaVersion": 1,
  "status": "running",
  "platform": "windows",
  "appVersion": "0.1.0",
  "pid": 12345,
  "sessionId": "20261120T173000Z-12345",
  "sessionDir": "<abs-session-dir>",
  "controlUrl": "http://127.0.0.1:19777",
  "bind": "127.0.0.1",
  "port": 19777,
  "runtimeBaseUrl": "http://127.0.0.1:54321",
  "runtimePid": 12388,
  "dataRoot": "<abs-data-root>",
  "startedAt": "2026-11-20T17:30:00.123Z",
  "endedAt": null,
  "options": {
    "modules": ["all"],
    "inlineLimit": 2048,
    "eventBytesLimit": 16384,
    "keepSessions": 10,
    "firstChanceExceptions": false
  }
}
```

- `platform` 枚举：`"windows"` | `"ios"`。
- iOS 内嵌 Node 与主进程同 PID，`runtimePid` 等于主进程 `pid` 或 `null`。

### 3.2 `session.json` 格式

与 `active-session.json` 同结构，额外补充环境信息 `environment`（操作系统版本、CPU 架构、设备型号/模拟器标识、.NET 或 Swift/iOS 运行时版本、Node 版本、DPI/屏幕分辨率）以及退出时的 `exitSummary`（退出码、是否干净排空、最后错误）。

---

## 4. 分类结构化日志与不脱敏外置规范

### 4.1 固定 16 个日志分类

每个分类对应独立文件 `logs/<cat>.jsonl`；双端与 Node 严格使用同一组分类名，避免全部信息混堆：

| `cat` | 写入方 | 记录内容边界 |
| --- | --- | --- |
| `lifecycle` | 原生 | 启动参数、窗口/Scene、前后台切换、安装/重置门禁、关闭排空、Runtime 重建与恢复 |
| `ui-nav` | 原生 | 页面路由跳转、前进/后退、Tab/频道/设置分组切换、弹窗/Sheet 开关、状态栏与错误通知 |
| `ui-action` | 原生 | 用户动作或 Control API 触发的点击/索引调用/搜索提交/保存/取消动作与完成耗时 |
| `runtime-bridge` | 原生 + Node | 管道/socketpair 握手、HTTP RPC 请求与响应体/状态码/错误码/耗时、原生设置帧、SSE 事件 |
| `runtime-node` | Node + 原生捕获 | Node 启动各 stage、未捕获异常/UnhandledRejection、QuickJS/Worker 池创建销毁与资源排空 |
| `music-platform` | Node + 原生 | `music.platform.v1` / `netease.token.v1` 官方平台 HTTP 请求/响应、客户端上下文、风控与签名结果 |
| `music-account` | 原生 + Node | 音乐登录（扫码/短信/Cookie）、凭据读写、VIP 页、账号导入/导出原始载荷与冲突决策 |
| `music-catalog` | 原生 | 音乐首页推荐、频道目录与切换、每日推荐、私人 FM、发现页、首页快照读写与刷新 |
| `music-library` | 原生 | 曲库、收藏、自建/网络歌单、播放历史、已播队列、同步、元数据与歌词匹配解析 |
| `music-playback` | 原生 + Node | 选源、音质策略、`music.script.v3` 脚本解析、播放队列、AudioKernel 解码输出、真实播放反馈 |
| `film-catalog` | 原生 + Node | 影视首页、分类/数据页、TMDB/豆瓣元数据、`cat.script.v2` / `source.runtime.v2` 探测与详情 |
| `film-playback` | 原生 + Node | 智能匹配、选集/换源、网盘接管与解析（`pan.runtime.v1`）、本地代理流（`film.playback.v1`）、FilmKernel/VideoKernel/AetherKernel 播放状态、回退与音轨字幕 |
| `novel` | 原生 + Node | 小说书源、发现/分类、书籍详情、目录、正文抓取与净化、书架/进度、翻页/换源、Web 交互 |
| `search` | 原生 + Node | 跨模块聚合搜索（音乐/影视/小说）、搜索发现、分页加载与结果合并 |
| `storage` | 原生 + Node | SQLite 读写与事务、设置块变更通知、图片/音频/元数据缓存命中与淘汰、文件授权 |
| `diagnostics` | 原生 + Node | 首机会/未处理异常、主线程卡顿告警、周期性或主动触发的内存/租约计数、堆栈与转储索引 |

### 4.2 JSONL 事件 Envelope

每行一条完整 UTF-8 JSON（无 BOM，以 `\n` 结尾），并发写入须保证单行原子追加（不交叉撕裂）：

```json
{
  "seq": 1042,
  "ts": "2026-11-20T17:30:12.345Z",
  "monoMs": 12845.6,
  "proc": "app",
  "level": "info",
  "cat": "music-platform",
  "sub": "qq.home",
  "event": "http.response",
  "opId": "op_000042",
  "traceId": "tr_9f1a2b3c",
  "durationMs": 183.4,
  "data": {},
  "error": null
}
```

字段契约：
- `seq`（必填，int64）：各自进程内单调递增序号（`app` 与 `node` 各自维护）。
- `ts`（必填，ISO-8601 UTC 字符串，精确到毫秒）。
- `monoMs`（必填，number）：进程启动以来的单调时钟毫秒数。
- `proc`（必填，string）：`"app"`（原生宿主）或 `"node"`（Node 运行时）。
- `level`（必填，string）：`"trace"` | `"debug"` | `"info"` | `"warn"` | `"error"` | `"fatal"`。
- `cat`（必填，string）：§4.1 中的 16 个分类名之一。
- `sub`（必填，string）：子领域/模块细粒度标识（如 `qq.home`、`netease.login`、`cat.probe`、`pan.quark`、`shell.nav`、`audiokernel`）。
- `event`（必填，string）：动词/阶段事件名（如 `request.start`、`request.end`、`http.request`、`http.response`、`state.change`、`error.caught`）。
- `opId`（可选，string）：单次动作/RPC 关联 ID；原生发往 Node 的 HTTP RPC 在 debug 模式下附加请求头 `X-Meow-Debug-Op: <opId>` 与 `X-Meow-Debug-Trace: <traceId>`，实现原生 ↔ Node 跨进程全链路串联。
- `traceId`（可选，string）：同一用户动作/Control API 请求的全链路追踪 ID。
- `durationMs`（可选，number）：耗时毫秒数。
- `data`（可选，object）：事件结构化业务数据（经过 §4.3 折叠处理）。
- `error`（可选，object）：`{ "code": "...", "type": "...", "message": "...", "stack": "...", "inner": ... }`。

### 4.3 超长文本与大载荷去重外置规则（不脱敏）

原生侧与 Node 侧严格实现同一算法，确保双端日志结构完全一致：

1. **单字符串折叠**（当 `inlineLimit > 0` 且字符串 `length > inlineLimit`，默认 `2048` 字符）：
   - 对字符串 UTF-8 原文计算小写十六进制 `sha256`。
   - 若 `<sessionDir>/blobs/<sha256>.txt` 尚不存在，则以「写临时文件 + 原子重命名」写入完整原文；若已存在则直接复用（同一份 Cookie、脚本或响应体不会重复占盘）。
   - 将原字符串字段替换为对象：
     ```json
     {
       "$blob": "blobs/<sha256>.txt",
       "sha256": "<sha256>",
       "length": 18420,
       "bytes": 24109,
       "head": "<前 512 字符>",
       "tail": "<后 128 字符>"
     }
     ```
2. **单条事件整块 `data` 外置**（当 `eventBytesLimit > 0` 且折叠完单字符串后的 `data` 序列化 UTF-8 字节数仍 `> eventBytesLimit`，默认 `16384` 字节）：
   - 计算完整 `data` JSON 的 `sha256`，原子去重写入 `<sessionDir>/blobs/<sha256>.json`。
   - 行内 `data` 替换为：
     ```json
     {
       "$blob": "blobs/<sha256>.json",
       "sha256": "<sha256>",
       "bytes": 48210,
       "summary": {
         "keys": ["status", "headers", "body"],
         "counts": { "items": 120 }
       }
     }
     ```
3. **二进制数据**：若记录原始二进制载荷（如图片、响应二进制），写入 `blobs/<sha256>.<ext>`，行内引用 `{ "$blob": "blobs/<sha256>.<ext>", "sha256": "...", "bytes": ..., "contentType": "..." }`。
4. **强制全内联**：当 `--debug-inline-limit 0` 时禁用步骤 1；当 `--debug-event-limit 0` 时禁用步骤 2。

---

## 5. Node 运行时内部 Debug 契约

该部分位于共用运行时（[apps/runtime/bootstrap.mjs](../../apps/runtime/bootstrap.mjs) 及相关 `apps/runtime/modules/*`、`packages/*`），同时服务 Windows 与 iOS：

1. **启用条件**：仅当启动握手收到 `debug.enabled === true` 时启用；否则零注册、零磁盘写入、零钩子开销。
2. **启动与异常诊断不吞错**：
   - Debug 模式下，启动各 stage 的进入/完成/失败、`uncaughtException`、`unhandledRejection`、Worker/QuickJS 异常完整写入 `logs/runtime-node.jsonl`（含完整 message、code、stack），不再只剩一个错误码。
   - Windows 侧 `NodeRuntime` 在 Debug 模式下不再丢弃子进程 `stdout`/`stderr`，改为实时流式落盘到 `logs/runtime-node.stdout.log` 与 `logs/runtime-node.stderr.log`。
3. **全量 RPC 与模块埋点**：
   - HTTP server 入口自动读取 `X-Meow-Debug-Op` / `X-Meow-Debug-Trace`，记录每次 `/v1/call/*`、`/v1/upload/*`、`/v1/image/*`、`/v1/events/*`、`/v1/settings/changed` 的入参、响应、耗时与错误码到 `runtime-bridge.jsonl`（超长走 `$blob`）。
   - 业务模块分别记录关键上游请求与计算产出到对应分类：
     - `music-platform` + `netease` → `music-platform.jsonl`
     - `music`（LX 脚本执行与解析）→ `music-playback.jsonl`
     - `cat` + `tmdb` + `douban` → `film-catalog.jsonl`
     - `pan` + `film`（智能匹配、网盘解析、本地代理流）→ `film-playback.jsonl`
     - `source`（Reader 书源执行，经由 `packages/script-host` 宿主边界记录，不违反 `packages/source-runtime` 无 builtin 约束）→ `novel.jsonl` / `film-catalog.jsonl`
     - `packages/storage` → `storage.jsonl`
4. **仅 Debug 注册的 Node 内部诊断端点**（复用同一 session 鉴权，由原生 Control Server 转发调用）：
   - `GET /v1/debug/diagnostics`：返回 `process.pid`、`uptimeSec`、`nodeVersion`、`process.memoryUsage()`、`v8.getHeapStatistics()`、`v8.getHeapSpaceStatistics()`、活跃 HTTP 请求数、活跃 QuickJS/Worker/代理流/事件流计数、当前 debug 配置。
   - `POST /v1/debug/config`：动态更新 Node 侧 `modules`、`inlineLimit`、`eventBytesLimit`。
   - `POST /v1/debug/gc`：若可用触发 `globalThis.gc()`，返回 GC 前后内存与 V8 heap 对比。
   - `POST /v1/debug/heapsnapshot`：调用 `v8.writeHeapSnapshot()` 写入 `<sessionDir>/dumps/<timestamp>-node.heapsnapshot`，返回文件路径与字节数。
   - `POST /v1/debug/cpu-profile`：入参 `{ "durationMs": 5000 }`（范围 `500..60000`），通过 `node:inspector` 采集 CPU Profile 写入 `<sessionDir>/dumps/<timestamp>-node.cpuprofile`，返回文件路径与字节数。

---

## 6. 本地 Debug Control HTTP API（`/debug/v1/*`）

### 6.1 通用协议

- **监听**：默认 `http://127.0.0.1:<port>`，免 Token 鉴权（显式传入 `--debug-bind 0.0.0.0` 时才对外网卡开放，供 iOS 真机局域网直连）。
- **编码**：请求与响应（除二进制文件/截图外）均为 `application/json; charset=utf-8`。
- **统一响应信封**：
  - 成功：
    ```json
    {
      "ok": true,
      "ts": "2026-11-20T17:30:12.345Z",
      "traceId": "tr_...",
      "data": {}
    }
    ```
  - 失败：
    ```json
    {
      "ok": false,
      "ts": "2026-11-20T17:30:12.345Z",
      "traceId": "tr_...",
      "error": {
        "code": "INVALID_ARGUMENT",
        "message": "...",
        "details": {}
      }
    }
    ```
- **标准错误码**：
  - `INVALID_ARGUMENT`（400）：参数缺失、类型错误、索引越界或非法 SQL
  - `NOT_FOUND`（404）：未知端点、未知 target/section/itemId、日志或文件不存在
  - `UNSUPPORTED_ON_PLATFORM`（501）：合法跨端接口，但当前平台不存在该页面/能力（如 iOS 调用 `settings.media-controls`）
  - `INVALID_STATE`（409）：当前页面状态不允许该动作（如未处于阅读器时调用翻页、或数据重置未传确认标识）
  - `TIMEOUT`（504）：`ui/wait`、页面切换、RPC 或诊断采样超时
  - `RUNTIME_UNAVAILABLE`（503）：Node 运行时尚未就绪或已断开
  - `INTERNAL_ERROR`（500）：未预期内部异常（附带堆栈）

### 6.2 会话与能力发现

| 方法与路径 | 说明 |
| --- | --- |
| `GET /debug/v1/health` | 返回存活态、`platform`、`pid`、`appVersion`、`sessionId`、`sessionDir`、`uptimeMs`、`runtimeConnected`、`runtimeBaseUrl`、`windowReady`、`dataRoot` |
| `GET /debug/v1/capabilities` | 返回本端支持的 `targets[]`、`actions[]`、`diagnostics[]`、`logCategories[]`、`platformFeatures`（供 CLI/调用方自动识别端侧能力） |
| `POST /debug/v1/config` | 运行时动态更新配置：`{ "modules"?: string[], "inlineLimit"?: number, "eventBytesLimit"?: number, "firstChanceExceptions"?: boolean }`，同步下发至 Node 运行时 |

### 6.3 直达导航（Navigation）

| 方法与路径 | 请求体 | 说明 |
| --- | --- | --- |
| `POST /debug/v1/nav/open` | `{ "target": "<target>", "params"?: { ... }, "waitIdle"?: boolean, "timeoutMs"?: number }` | 直接打开任意规范目标页面或设置分组，返回导航后的 `ui/state` 摘要 |
| `POST /debug/v1/nav/back` | `{}` | 返回上一页 / 关闭当前设置或二级页 |
| `POST /debug/v1/nav/forward` | `{}` | 前进（Windows 支持；iOS 无前进栈时返回 `UNSUPPORTED_ON_PLATFORM`） |
| `POST /debug/v1/nav/refresh` | `{}` | 触发当前页面显式刷新 |

#### 全量规范 `target` 与双端映射表

| 领域 | `target` | 可选 `params` | Windows 映射 | iOS 映射 |
| --- | --- | --- | --- | --- |
| **音乐** | `music.home` | — | `music` / `catalog-home` | Primary `.recommend` |
| | `music.channels` | `{ channelKey?, channelIndex? }` | `music` / `catalog-channels` 或 `catalog-channel/<key>` | Primary `.channels` + 可选 `selectChannel` |
| | `music.discovery` | — | `music` / `catalog-discovery` | 音乐搜索发现页 |
| | `music.personal` | `{ platform? }` | `music` / `catalog-personal` | 平台个人歌单页 |
| | `music.daily` | `{ platform? }` | `music` / `catalog-daily` | 每日推荐页（`showPublicBrowse` daily） |
| | `music.fm` | `{ platform? }` | `music` / `catalog-fm` | 私人 FM / 漫游页 |
| | `music.library` | — | `music` / `favorites`（我的音乐入口） | Primary `.library` |
| | `music.favorites` | — | `music` / `favorites` | 收藏歌单页 |
| | `music.history` | `{ date? }` | `music` / `history` | 播放历史页 |
| | `music.archives` | — | `music` / `archives` | 已播队列页 |
| | `music.playlist` | `{ id? }` 或 `{ platform, remoteId }` | `playlist/<id>` 或 `catalog-playlist/<platform>/<remoteId>` | `showLocalCollection` / `showPublicCollection` |
| | `music.player` | `{ expanded?: boolean }` | 全屏/详情音乐播放器浮层 | `MusicFullPlayerViewController` |
| | `music.queue` | `{ open?: boolean }` | 音乐播放队列面板 | `MusicQueueViewController` |
| **影视** | `film.home` | `{ category?, tab? }` | `film` / `film-home` | Primary `.film`（`FilmHomeViewController`） |
| | `film.data` | `{ category? }` | `film` / `film-data` | `FilmCategoryViewController` / `FilmDirectoryViewController` |
| | `film.detail` | `{ id? }` | 当前/指定影片详情（与播放页统一或切至详情区） | `FilmDetailViewController` |
| | `film.playback` | `{}` | `film` / `film-playback` | `FilmPlayerViewController` |
| **小说** | `novel.home` | `{ sourceId? }` | `novel` / `novel-home` | Primary `.novel`（`NovelHomeViewController`） |
| | `novel.data` | `{ sourceId?, category?, sectionId?, sectionIndex? }` | `novel` / `novel-data` | `NovelCategoryViewController` |
| | `novel.shelf` | — | `novel` / `novel-shelf`（独立书架视图） | 小说书架视图 |
| | `novel.history` | — | `novel` / `novel-history`（独立阅读历史视图） | 平台可选（当前 Windows 已支持） |
| | `novel.book` | `{ bookId? }` | `novel` / `novel-book` | `NovelDetailViewController` |
| | `novel.reader` | `{}` | `novel` / `novel-reader` | `NovelReaderViewController` |
| **搜索** | `search` | `{ query?, target?: "music"\|"film"\|"novel", tab? }` | `SearchResultsPage`（若提供 `query` 同时触发提交） | `MeowfilmCoordinator.showSearch()` + 可选提交与切 Tab |
| **通用设置** | `settings.general` | `{ section?: "appearance"\|"sources"\|"cache"\|"window"\|"shortcuts" }` | `ShellWindow` 通用设置 + 对应区域 Tab | `MeowfilmSettingsViewController`（`appearance`→外观设置，`cache`→缓存管理；`window`/`shortcuts` 返 `UNSUPPORTED_ON_PLATFORM`） |
| **音乐设置** | `settings.music` | `{ section?: "accounts"\|"sources"\|"exclusions"\|"search"\|"quality"\|"playback"\|"metadata"\|"home-source"\|"cache" }` | `MusicSettingsView` 对应锚点/子区（`home-source`/`cache` 切到通用设置对应区或子面板） | `MusicSettingsViewController` + 对应子页面（`.aggregation`/`.sources`/`.exclusions`/`.search`/`.playback`/`.metadata`） |
| **影视设置** | `settings.film` | `{ section?: "rules"\|"search-rules"\|"cats"\|"videos"\|"metadata"\|"pan"\|"pan-routing"\|"preferences"\|"player"\|"cache" }` | `FilmSettingsView` 对应分段锚点（`cache`→缓存子页） | `FilmSettingsViewController` + 对应子页面（`.matching`/`.search`/`.cats`/`.videos`/`.dataSources`/`.pan`/`.panInterception`/`.preferences`/`.player`/缓存页） |
| **小说设置** | `settings.novel` | `{ section?: "sources"\|"rules"\|"appearance" }` | `NovelSettingsView`（`sources`/`rules` Tab；`appearance`→阅读排版面板） | `NovelSettingsViewController` + 对应子页（书源/净化/阅读排版） |
| **独立设置快捷项** | `settings.cache` | — | 通用设置 `cache` 区 | `FilmMetadataCacheSettingsViewController` |
| | `settings.media-controls` | — | 通用设置 `shortcuts` 区（媒体控制与快捷键） | `UNSUPPORTED_ON_PLATFORM` |

### 6.4 界面语义状态读取与截图

| 方法与路径 | 查询参数 | 返回数据结构 |
| --- | --- | --- |
| `GET /debug/v1/ui/state` | — | `{ target, moduleId, route, title, settingsOpen, settingsDomain, settingsSection, navigationStack: [{ target, route, title }], canGoBack, canGoForward, isBusy, statusText, isError, errorText, activeDialog: { title, message, buttons: [{ index, label, kind }] } \| null, notices: [{ id, level, message }], window: { visible, active, width, height, scale } }` |
| `GET /debug/v1/ui/content` | `?maxItems=200&includeRaw=false` | **当前页面结构化业务投影**（见下方统一 Schema），严禁只返回空壳或截断成无意义摘要 |
| `GET /debug/v1/ui/tree` | `?maxDepth=8&visibleOnly=true` | 低层视图树（WinUI VisualTree / UIKit ViewController+View Hierarchy）：`{ type, name, automationId, visible, enabled, bounds: { x, y, w, h }, text, children }`（仅用于布局/遮挡/可见性排错） |
| `GET /debug/v1/ui/images` | — | 平台可选（当前 Windows 已支持）：扫描当前可见视觉树中的图像控件，返回 `{ total, settled, byState, loadingCount, loadedCount, failedCount, zeroSizeAnomalyCount, zeroSizeAnomalies, failedItems, items }` |
| `GET /debug/v1/ui/screenshot` | `?save=true&format=png[&window=<name>]` | 直接返回 `image/png` 字节流；响应头附带 `X-Meow-Screenshot-Path`（当 `save=true` 时同步落盘至 `screenshots/`）；可选 `window` 参数由平台按需支持辅助窗口截屏（当前 Windows 支持如 `desktop-lyrics`） |

#### `GET /debug/v1/ui/content` 统一页面投影结构

无论当前处于哪个页面，均返回同一顶层信封结构（按 `kind` 填充对应字段）：

```json
{
  "target": "music.home",
  "kind": "sections | list | search | detail | player | reader | settings | empty",
  "loading": false,
  "error": null,
  "tabs": [
    { "group": "channel", "index": 0, "id": "...", "title": "推荐", "selected": true, "enabled": true }
  ],
  "filters": [
    { "group": "tag", "index": 0, "id": "...", "title": "全部", "selected": true }
  ],
  "sections": [
    {
      "index": 0,
      "id": "section_0",
      "title": "推荐歌单",
      "kind": "playlist | song | album | artist | chart | film | novel | mixed",
      "layout": "rail | grid | list | banner",
      "hasMore": true,
      "itemCount": 12,
      "items": [
        {
          "index": 0,
          "id": "...",
          "kind": "playlist | song | album | film | novel | action",
          "title": "...",
          "subtitle": "...",
          "badge": "...",
          "coverUrl": "...",
          "actions": ["open", "play", "favorite"],
          "meta": {}
        }
      ]
    }
  ],
  "rows": [],
  "hasMore": false,
  "detail": null,
  "player": null,
  "reader": null,
  "settings": null
}
```

各类页面填充要求：
- **首页 / 频道页 / 分类发现页（`kind: "sections"`）**：完整列出当前展示的 `tabs[]`、`filters[]`、`sections[]` 及每栏 `items[]`（带精确 `sectionIndex`、`itemIndex` 和可执行 `actions[]`），直接支撑「点击第 1 个栏目的第 1 项」。
- **歌单 / 收藏 / 历史 / 已播队列 / 书架（`kind: "list"`）**：填充 `rows[]`（`index`、`id`、`title`、`subtitle`、`selected`、`state`、`actions[]`、`meta`）与 `hasMore`。
- **搜索页（`kind: "search"`）**：填充 `query`、`target`、`tabs[]`、`sections[]`/`rows[]`、`hasMore`、`metadataStatus`。
- **详情页（`kind: "detail"`）**：填充 `detail`（影片/书籍/歌单基础信息、分集/章节/线路/来源列表、收藏/在架状态）。
- **播放页（`kind: "player"`）**：填充 `player`（当前曲目/影片、选源、剧集/线路/音轨/字幕、歌词摘要、播放态、进度、缓冲、错误信息）。
- **阅读器（`kind: "reader"`）**：填充 `reader`（书名、书源、当前章索引与标题、章节总数、当前页/总页数、当前页首尾文本片段、净化规则数、排版设置）。
- **设置页（`kind: "settings"`）**：填充 `settings`（当前领域与分组列表、各设置项键值、源列表/账号列表/规则列表与真实状态/错误，不脱敏）。

### 6.5 语义级界面操控与条件等待

#### `POST /debug/v1/ui/action`

请求体：`{ "action": "<actionName>", "params": { ... }, "waitIdle"?: boolean, "timeoutMs"?: number }`

| `action` | `params` | 行为说明 |
| --- | --- | --- |
| `section.item.invoke` | `{ sectionIndex?, sectionId?, itemIndex?, itemId?, verb?: "open"\|"play"\|"favorite"\|"more" }` | 调用当前页指定栏目的指定项（索引从 `0` 开始，支持按索引或稳定 ID 定位） |
| `section.more` | `{ sectionIndex?, sectionId? }` | 触发指定栏目的「更多 / 查看全部」 |
| `tab.select` | `{ group: "channel"\|"category"\|"search-tab"\|"settings-section"\|"filter", index?, id? }` | 切换频道 Tab、分类 Tab、搜索子 Tab、设置分组或筛选标签 |
| `list.item.invoke` | `{ index?, id?, verb?: "open"\|"play"\|"select"\|"favorite"\|"delete"\|"toggle"\|"edit" }` | 调用当前列表页的第 `index` 项或指定 `id` 项 |
| `list.load_more` | `{}` | 触发当前列表/搜索/发现页加载更多 |
| `search.submit` | `{ query, target?: "music"\|"film"\|"novel", tab? }` | 打开或复用搜索页并提交查询 |
| `dialog.action` | `{ action: "confirm"\|"cancel"\|"dismiss"\|"button", index?, label? }` | 操作当前活动弹窗/Dialog/Sheet |
| `setting.set` | `{ domain: "general"\|"music"\|"film"\|"novel", section?, key, value }` | 修改指定设置项并走真实保存/回执链路 |
| `setting.action` | `{ domain, section?, command, params? }` | 触发设置页业务动作（如刷新来源、切换猫源/视频源/书源启停、检测/探测、清缓存、数据重置） |

各端可在 `capabilities.actions` 中声明平台扩展语义动作（例如 Windows 端支持的 `window.close_request`、`search.scope.*`、`dropdown.*`、`nonclient.refresh` 等，具体见对应端侧文档）。

#### `POST /debug/v1/ui/wait`

避免外部盲目 `sleep`，在应用内按真实状态变化或事件等待：

请求体：
```json
{
  "condition": "idle | route | section_count | row_count | playback_state | dialog | images_settled | log_event",
  "params": { ... },
  "timeoutMs": 15000
}
```

- `idle`：等待当前页面 `isBusy === false` 且无进行中导航加载。
- `route`：`{ target }` 或 `{ route }`，等待当前页面达到目标。
- `section_count`：`{ minSections?, sectionIndex?, minItems? }`，等待栏目/条目加载到位。
- `row_count`：`{ minRows }`，等待列表/搜索结果加载到位。
- `playback_state`：`{ domain: "music"|"film", state: "playing"|"paused"|"stopped"|"buffering"|"ended"|"error" }`。
- `dialog`：`{ open: true|false }`。
- `images_settled`：平台可选（当前 Windows 已支持），等待当前可见视觉树中全部图像控件脱离 `loading` 状态并返回图像巡检摘要。
- `log_event`：`{ cat, sub?, event?, sinceSeq? }`，等待某条日志事件实际产生并返回该事件。

### 6.6 播放与阅读直控

| 方法与路径 | 说明 |
| --- | --- |
| `GET /debug/v1/playback/state` | 返回聚合播放态：`{ activeOwner: "music"\|"film"\|null, music: { state, track, source, quality, positionSec, durationSec, bufferedSec, mode, queueIndex, queueCount, lyricLine, lastError }, film: { state, stage, engine, title, source, episode, line, positionSec, durationSec, bufferedSec, canSeek, paused, buffering, rate, volume (0.0..1.0), pictureInPicture, pictureInPictureState, videoSize, videoTracks, audioTracks, subtitleTracks, selectedVideoTrack, selectedAudioTrack, selectedSubtitleTrack, chapters, diagnostics, lastError } }`（绝不返回原始媒体 URL、Headers 或凭据） |
| `POST /debug/v1/playback/command` | `{ domain: "music"\|"film", command, params? }`；统一响应 `{ domain, command, state }`；支持 `play`、`pause`、`toggle`、`stop`、`next`、`previous`、`seek`（`seconds`）、`rate`、`volume`（`0.0..1.0`）、`mute`、`aspect`、`pip_start`、`pip_stop`、`retry`、`switch`、`block`、`pan`、`quality`、`mode`（循环模式）、`select_episode`（`index`/`id`）、`select_source`（`index`/`id`）、`select_line`、`select_candidate`、`select_video_track`、`select_audio_track`、`select_subtitle_track`（`disable: true` / `id: -1` / `null` 关闭字幕）、`play_queue_index` |
| `POST /debug/v1/novel/command` | `{ command, params? }`；支持 `open_book`（`bookId`）、`start_reading`、`select_chapter`（`index`）、`next_chapter`、`prev_chapter`、`next_page`、`prev_page`、`switch_source`（`sourceIndex`/`sourceId`）、`toggle_shelf` |

### 6.7 日志、Blob、会话文件、Runtime 与 SQLite 透视

| 方法与路径 | 说明 |
| --- | --- |
| `GET /debug/v1/sessions` | 列出 `<debugRoot>/sessions/` 中所有会话（含当前活跃标识、启动时间、目录大小、日志总行数、错误数与可下载文件相对路径数组 `files`） |
| `GET /debug/v1/files/<relpath>` | 只读下载当前会话（或 `?sessionId=<id>` 指定历史会话）内的任意文件；iOS 真机在线拉取主力入口 |
| `GET /debug/v1/logs/categories` | 返回当前会话（或 `?sessionId=<id>` 指定历史会话）各日志分类文件大小、行数、按级别计数及最新 `seq` |
| `GET /debug/v1/logs/tail` | 查询参数：`cat`（必填或 `all`）、`limit`（默认 `100`，上限 `2000`）、`sinceSeq`、`level`、`sub`、`event`、`opId`、`traceId`、`q`（子串过滤）、`sessionId`（可选历史会话）；返回 `{ sessionId?, cat, count, events }` |
| `GET /debug/v1/blobs/<sha256>` | 按 `sha256` 直接返回完整未折叠原文（`text/plain`、`application/json` 或原始二进制）；支持 `?sessionId=<id>` |
| `POST /debug/v1/runtime/invoke` | `{ "method": "music.platform.home", "input": {}, "timeoutMs"?: 30000 }`（支持标准前缀、短前缀、`module/operation` 或显式 `{ "module", "operation" }`）；直接经当前活跃 Node Runtime 通道调用任意已注册模块方法，返回 `{ module, operation, path, httpStatus, durationMs, response }` 并自动写入 `runtime-bridge` 与对应模块日志 |
| `POST /debug/v1/storage/query` | `{ "db"?: "data" \| "cache", "sql": "SELECT ...", "params"?: [], "maxRows"?: 200 }`（`db` 默认 `"data"`，`maxRows` 夹取范围 `1..5000`）；在独立只读连接（`ReadOnly` + `PRAGMA query_only = ON`）上执行只读查询，统一返回 `{ db, columns, rows: [{ col: val }], rowCount, truncated, durationMs }`（二进制字段统一 raw base64，不加前缀），方便直查双库 SQLite 状态 |

### 6.8 底层内存、堆栈与异常诊断（Diagnostics）

| 方法与路径 | 说明 |
| --- | --- |
| `GET /debug/v1/diag/summary` | 聚合原生 + Node 运行时核心指标：<br>• 原生进程：内存（Win `WorkingSetBytes`/`PrivateMemoryBytes`/`ManagedHeapBytes` + GC 代次计数；iOS `physFootprintBytes`/`residentBytes`）、线程数、句柄/FD 数、活跃业务租约与全量缓存指标（AudioKernel / FilmKernel / VideoKernel / AetherKernel / 图片与特效缓存 / 音乐音频、展示、URL、高潮、匹配发现、调色板、首页快照与内存缓存 / RuntimeSessions）<br>• Node 进程：透传 `GET /v1/debug/diagnostics` 全部数据（含 `film.cache`、`film.data.metadataCache`、`film.media.cachePool` 与 `music-platform` LRU 统计） |
| `GET /debug/v1/diag/threads` | 抓取当前原生进程全线程堆栈快照（Windows：托管与原生线程堆栈 + 线程池/UI 线程卡顿状态；iOS：Mach 线程列表 + `backtrace_symbols` / 符号地址），并附带 Node 活跃异步请求/Worker 清单 |
| `POST /debug/v1/diag/gc` | 释放三端可回收内存缓存（Windows 图片内存缓存 + `GC.Collect(full)`；iOS 图片/特效/歌词/搜索内存缓存 + `URLCache` + `autoreleasepool`；Node `film`/`music-platform` 内存缓存 + `globalThis.gc()`），返回清理项与前后内存对比 |
| `POST /debug/v1/diag/dump` | 生成原生转储落盘至 `dumps/`：<br>• Windows：`{ "mode"?: "mini" \| "heap" }`，通过 `dbghelp.dll` `MiniDumpWriteDump` 生成 `.dmp`<br>• iOS：生成线程堆栈 + Mach VM 内存区域摘要 + 活跃对象/租约诊断报告 `.json` |
| `POST /debug/v1/diag/node/heapsnapshot` | 调用 Node `POST /v1/debug/heapsnapshot` 生成 `.heapsnapshot`（Chrome DevTools 可直接加载） |
| `POST /debug/v1/diag/node/cpu-profile` | `{ "durationMs"?: 5000 }`，调用 Node `POST /v1/debug/cpu-profile` 生成 `.cpuprofile` |
| `GET /debug/v1/diag/exceptions` | 查询参数：`?limit=100&kind=all\|first_chance\|unhandled\|unobserved`；返回近期捕获的异常环形缓冲（含时间戳、线程、完整堆栈、关联 `opId`/`traceId`） |

---

## 7. 跨端统一 CLI 规范（`scripts/debug.mjs`）

提供单一零外部依赖 Node CLI（`scripts/debug.mjs`），以同一套子命令操作 Windows、iOS 模拟器与 iOS 真机：

### 7.1 目标发现优先级
1. 显式 `--endpoint http://<host>:<port>`
2. `--target windows`（默认在 Windows 主机）：读取 `%LOCALAPPDATA%\meowfilm\aggregate-debug\active-session.json`（或 `--debug-dir` 指定路径）
3. `--target ios-sim [--udid <UDID>]`：通过 `xcrun simctl get_app_container <udid> <bundleId> data` 定位 `Library/Application Support/meowfilm-debug/active-session.json`（模拟器与 Mac 共享 `127.0.0.1`）
4. `--target ios-device --device <id>`：优先使用 `--endpoint`；离线拉取命令封装 `xcrun devicectl device copy from --domain-type appDataContainer`

### 7.2 标准子命令

| 命令 | 对应接口 / 作用 |
| --- | --- |
| `status` | 调用 `/debug/v1/health` + `/debug/v1/ui/state` 展示会话与当前页面 |
| `caps` | 调用 `/debug/v1/capabilities` |
| `open <target> [--section <s>] [--param k=v]` | 调用 `POST /debug/v1/nav/open` |
| `back` / `forward` / `refresh` | 调用对应 `/debug/v1/nav/*` |
| `state` / `content` / `tree` / `images` | 读取 `/debug/v1/ui/state`、`content`、`tree`、`images`（`images` 平台可选，当前 Windows 已支持） |
| `click-item <sectionIndex> <itemIndex> [verb]` | 调用 `ui/action` (`section.item.invoke`) |
| `click-row <index> [verb]` | 调用 `ui/action` (`list.item.invoke`) |
| `tab <group> <indexOrId>` | 调用 `ui/action` (`tab.select`) |
| `search <query> [--domain music\|film\|novel] [--tab <t>]` | 调用 `ui/action` (`search.submit`) |
| `wait <condition> [--timeout <ms>] [--param k=v]` | 调用 `POST /debug/v1/ui/wait` |
| `playback [state\|play\|pause\|toggle\|stop\|next\|prev\|seek <sec>\|rate <r>\|volume <0..1>\|mute\|aspect\|pip_start\|pip_stop\|select_episode\|select_source\|select_line\|select_candidate\|select_video_track\|select_audio_track\|select_subtitle_track]` | 调用 `/debug/v1/playback/*` |
| `novel <command> [--param k=v]` | 调用 `/debug/v1/novel/command` |
| `logs [cat] [-n 50] [--level ...] [--op ...] [--q ...] [--follow]` | 读取或轮询跟踪 `/debug/v1/logs/tail`（无参列分类） |
| `blob <sha256> [--out <file>]` | 拉取 `/debug/v1/blobs/<sha256>` |
| `invoke <method> [--json '<obj>']` | 调用 `POST /debug/v1/runtime/invoke` |
| `sql "<SELECT ...>" [--db data\|cache] [--params '[...]'] [--max-rows 200]` | 调用 `POST /debug/v1/storage/query` |
| `diag <summary\|threads\|gc\|dump\|heapsnapshot\|cpu-profile\|exceptions>` | 调用对应 `/debug/v1/diag/*` |
| `screenshot [--window <name>] [--out <file.png>]` | 调用 `GET /debug/v1/ui/screenshot` 并保存到本地（`--window` 平台可选，当前 Windows 支持辅助窗如 `desktop-lyrics`） |
| `pull [--session <id>] --out <dir>` | 从在线 HTTP `/debug/v1/files/*` 或离线 `devicectl` 拉取整套会话文件 |

所有子命令均支持 `--json` 输出原始 JSON，便于脚本/Agent 直接消费。

---

## 8. 端侧实现差异边界

| 维度 | Windows | iOS | 共用 Node Runtime |
| --- | --- | --- | --- |
| 启动开关 | `meowfilm.exe --debug ...`（默认静默底层无焦点显示，不抢前台焦点且正常渲染/截图） | `simctl` / `devicectl` / Scheme 传 `--debug ...` | 仅接受宿主握手中的 `debug` 对象 |
| HTTP Server | .NET 内置监听（`HttpListener` / `TcpListener`） | `Network.framework` `NWListener` | 现有 `packages/runtime-bridge` HTTP server 扩展仅 debug 路由 |
| Node 形态 | 独立 `node.exe` 子进程 + Named Pipe + stdout/stderr 落盘 | 进程内 `NodeMobile` 线程 + Unix socketpair FD | 同一份 `apps/runtime/bootstrap.mjs` 与 9 个业务模块 |
| 默认监听 | `127.0.0.1` | 模拟器 `127.0.0.1`；真机支持 `--debug-bind 0.0.0.0` | `127.0.0.1`（随既有随机端口） |
| 原生转储 | `dbghelp.dll` `MiniDumpWriteDump`（`.dmp`） | Mach 线程堆栈 + 内存区域 + 租约报告（`.json`） | `v8.writeHeapSnapshot()`（`.heapsnapshot`）+ `node:inspector`（`.cpuprofile`） |
| 截图实现 | WinUI / Win32 窗口帧捕获 | `UIWindow` `UIGraphicsImageRenderer` | 不适用 |
| 平台专属能力 | `settings.media-controls`、`nav/forward`、桌面歌词状态 | `music.library` 独立底部 Tab、真机 `devicectl` 离线拉取 | 跨端一致 |

---

## 9. 旧测试冻结与清理边界

1. **立即生效（禁止新增与适配）**：
   - 本契约生效后，禁止新增任何回归测试、测试夹具、Mock 替身或仅服务于旧测试的生产代码钩子。
   - 不再为既有旧测试做适配、维护或以旧测试结果作为验收依据。
2. **保留的非测试完整性检查**：
   - 构建与依赖边界校验继续保留：`scripts/windows/verify-app-structure.mjs`、`scripts/windows/verify-projects.mjs`、`scripts/verify-boundaries.mjs`、`scripts/verify-dependency-lock.mjs`、`scripts/check-source-scope.py`、`scripts/package-runtime.mjs`、`scripts/accept-sources.mjs`、`scripts/accept-reader-sources.mjs`。
3. **历史测试与夹具已全仓清理**：
   - 全部历史回归测试代码、夹具、旧测试脚本与纯测试生产代码钩子已从仓库移除，仅保留第 2 项所列非测试完整性与构建/打包边界检查。
