# 排查与实跑手册

这份文件记录**把 MeowFilm 跑起来排查缺陷与做真实验收的通用实操方法**，避免后续开发者或 Agent 靠猜构建产物、猜命令行、猜端口、猜控件或写残缺测试交差。单次任务的排查日志、截图、转储与临时环境只能保存在被 Git 忽略的 `tmp/` 或平台 `temp/` 目录，绝不入库。协作规则与目录边界在 [`AGENTS.md`](AGENTS.md)，完整协议 Schema 在 [`docs/common/debug-mode.md`](docs/common/debug-mode.md)，双端实现细节见 [`docs/windows/debug-mode.md`](docs/windows/debug-mode.md) 与 [`docs/ios/debug-mode.md`](docs/ios/debug-mode.md)。

每一条都来自 Windows / iOS / 共用 Node 真实联调踩过的坑并写明原因，不靠推理。

## 一、红线：什么是禁止的「残缺版测试」

**回归测试本身就是最典型的残缺版测试**。以下 6 类形式均属于**禁止使用的残缺版测试 / 替身验证**，不得编写、引入或用来充当验收结果：

1. **回归测试与单元测试套件**：包括各类单元测试、组件测试与 UI/业务回归测试套件。它们脱离真实窗口、真实内嵌 Node 进程与真实本地存储，每改一次业务就要成片改测试与夹具，且极易用全绿掩盖真实渲染、IPC 或时序问题。
2. **Mock / 替身 / 合成假服务 / 硬编码夹具**：用本地假 HTTP 服务器、内存假数据库、假 `Transport`/`Stub`/`Fake*`、离屏伪造快照或硬编码假 JSON 响应来证明业务「通过」。
3. **生产代码里的纯测试钩子**：如 `InternalsVisibleTo("...Tests")`、`#if DEBUG` `XCTestCase` 旁路、仅供测试读取的环境变量开关或镜像断言。
4. **外部盲扫控件树、模拟键鼠与盲目 `sleep`**：用 Win32 / UIA 扫整页控件树猜按钮坐标、发模拟鼠标/键盘输入、用固定延时猜页面加载完成，或用自制 HTML / 离屏假数据图冒充原生客户端画面。
5. **半截探针脚本充当验收或残留入库**：只调单个内部函数或半条链路就宣称端到端完成；临时辅助脚本测完必须立即删除，不得提交进 Git。
6. **低层级检查冒充高层级实跑验收**：编译成功、`npm run verify`、`verify-projects.mjs`、`verify-native.sh`、`ios-node-preflight` 或单条 `runtime/invoke` 成功，只能证明各自覆盖范围，**不能**冒充真实界面、音视频播放、在线账号或真机验收通过。

### 明确允许且保留的入口（不属于回归测试）

- **静态与构建完整性门禁**（只检查依赖锁、模块边界、源码范围、编译、链接与签名，不跑业务回归或夹具）：
  - `npm run verify`（依赖锁、包边界、原生 JS 来源、Windows 应用结构）
  - `node scripts/windows/verify-projects.mjs`（Windows 项目单向依赖边界）
  - `python3 -I -B scripts/check-source-scope.py`（iOS 工程与 SwiftPM 源码边界）
  - `bash scripts/verify-native.sh`（macOS：源码边界、原生编译、Aether 链接与 Simulator ad-hoc 签名校验）
  - `scripts/prepare-ios-*-kernel.py --verify`、`scripts/check-simulator-signing.py`
- **外部第三方脚本联网动态验收**：`scripts/accept-sources.mjs`、`scripts/accept-reader-sources.mjs`（针对用户提供的真实外部脚本，不打包进产品）。
- **唯一业务、界面与底层故障验证入口**：正常构建产物 + `--debug` 模式 + `scripts/debug.mjs` / `/debug/v1/*`。

## 二、构建与产物同步防坑（改什么就得重编/重打什么）

### Windows：改完 JS / 运行器或做实跑，走 `.\build-windows.ps1`

- **只跑 `dotnet build` 测不到 `packages/*`、`apps/runtime/*` 或 `apps/windows/runtime/*` 的改动**：完整发布目录里的 `build\windows\runtime\` 由 `scripts/windows/build.ps1` 调用 `package-runtime.mjs` 复制并写入 `runtime-manifest.json` 校验哈希；跳过打包会继续跑旧 runtime，或因清单校验失败拒绝启动。
- **构建进行中不要修改源码文件**：`scripts/windows/build.ps1` 首尾会比对 `input-fingerprint.mjs`，中途修改源码会直接中止并拒写产物。
- **`build\windows\build-result.json` 里的 `status: "built-not-accepted"` 是正常值**：它明确表示产物只完成了构建，尚未通过 `--debug` 实跑验收。

### iOS：增删源码或改 JS / 内核必须同步工程与构建输入

- 增删 `apps/ios/` 下的 Swift 文件或资源后，必须重跑 `python3 -I -B scripts/generate-ios-project.py`，否则新文件不会进入 `apps/ios/Meowfilm.xcodeproj`。
- 改动 `apps/runtime/`、`apps/ios/runtime/` 或 `packages/*` 后，必须重跑 `python3 -I -B scripts/prepare-ios-runtime.py`（模拟器传 `--sdk iphonesimulator --arch arm64`，真机传 `--sdk iphoneos --arch arm64`），再重新编译安装，否则 `.app` 内嵌的仍是旧运行器。
- 改动音视频或 Aether 内核源码/补丁后，必须先运行对应的 `scripts/prepare-ios-{audio,video,aether}-kernel.py`。

## 三、启动与连接（不盲猜端口、不弹黑框、不污染用户数据）

### 总开关与参数守卫

- `--debug` 是唯一总开关。**未传 `--debug` 时保持严格零副作用**：所有 `--debug-*` 和 `--data-root` 均被忽略，不建调试目录、不监听端口、不落盘日志。
- **传入 `--debug` 且任意参数非法**（如端口越界、分类名不在 16 类内、缺参数值）时，程序会向 `stderr` 报错并以退出码 `2` 立即终止，绝不静默回退为非 Debug 模式。
- 支持 `--flag value` 与 `--flag=value` 两种写法；`--debug-first-chance` 同时支持无值开关和 `true|false|1|0`。

### 先决定是否隔离数据目录

- 实跑改动、验证冷启动或破坏性操作时，**务必传入隔离的 `--data-root <临时绝对路径>` 与 `--debug-dir <临时绝对路径>`**，不污染真实账号、曲库与数据库。
- 只有明确要排查现有本机数据问题时，才省略 `--data-root`。即使不隔离数据目录，默认调试根目录也与业务数据目录平级（Windows `%LOCALAPPDATA%\meowfilm\aggregate-debug`，iOS `<AppContainer>/Library/Application Support/meowfilm-debug`），执行应用内「清空本机数据」不会误删当前调试会话。

### Windows 启动与防坑

本地或交互桌面直接启动（可按需加 `--debug-port 19777` 固定端口，默认 `0` 自动分配）：

```powershell
.\build\windows\meowfilm.exe --debug --debug-port 19777 `
  --data-root E:\Developer\temp\meow-debug-run\data `
  --debug-dir E:\Developer\temp\meow-debug-run\debug
```

**实跑踩过的三个关键点**：

1. **不要为了截图或操作去抢前台焦点**：Windows `--debug` 默认静默底层启动（`HWND_BOTTOM` + `AppWindow.Show(false)`），主窗口保持正常尺寸、任务栏可见、完整 WinUI 渲染且不抢当前窗口焦点；即使被其他窗口遮挡，`/debug/v1/ui/screenshot` 仍完整清晰，不黑屏。
2. **经 SSH 在远端 Windows 交互桌面（Session 1）启动时，禁止用交互 `cmd.exe` / `powershell.exe` 包装拉起**：通过计划任务直接将目标程序设为 `meowfilm.exe` 并传 `--debug ...` 参数；用 `cmd.exe` 或 `powershell.exe` 包装会在桌面弹出控制台黑框并抢焦点。
3. **启动前后确认没有残留实例抢端口或写乱 `active-session.json`**：先检查当前是否存在旧 `meowfilm.exe` 进程占用同一数据目录或端口，启动后再查 `status` 核对 `pid`、`windowReady` 与 `runtimeConnected`。

### iOS 模拟器启动（Mac 宿主机直连 `127.0.0.1`）

遵循 `AGENTS.md` 的模拟器限额（全机运行中最多 1 台，登记在 `~/Developer/temp/simulator-usage`）。

```bash
xcrun simctl launch <UDID> dev.meowfilm.aggregate --debug --debug-port 19777
node scripts/debug.mjs --endpoint http://127.0.0.1:19777 status
# 或通过容器自动发现端口（无需固定 --debug-port）：
node scripts/debug.mjs --target ios-sim --udid <UDID> status
```

### iOS 真机启动（局域网直连 + 离线容器拉取）

```bash
# 注意：devicectl 传递 App 参数前必须加 --；真机局域网直连必须显式 --debug-bind 0.0.0.0
xcrun devicectl device process launch --device <DEVICE_ID> dev.meowfilm.aggregate -- \
  --debug --debug-bind 0.0.0.0 --debug-port 19777

# 在线控制（Mac / 开发机直连手机局域网 IP）：
node scripts/debug.mjs --endpoint http://<IPHONE_IP>:19777 status

# App 崩溃或离线后，经 USB / Wi-Fi 容器直接拷回全部调试文件：
node scripts/debug.mjs pull --target ios-device --device <DEVICE_ID> --out ./tmp/ios-debug-pull
```

**为什么真机不能省 `--debug-bind 0.0.0.0`**：默认绑定 `127.0.0.1`，从 Mac 访问手机 IP 会直接拒绝连接；首次绑定 `0.0.0.0` 时 iOS 会弹本地网络权限确认，需在手机上允许。

### 启动后第一步：先查 `status` 和 `caps`，不要盲猜

```bash
node scripts/debug.mjs --endpoint http://127.0.0.1:19777 status
node scripts/debug.mjs --endpoint http://127.0.0.1:19777 caps
```

- `status` 核对 5 个事实：`pid`（确认为刚启动的新构建）、`windowReady: true`、`runtimeConnected: true`、`sessionId`、`dataRoot`。
- `caps` 查看本端真实支持的 `targets[]`、`actions[]`、`diagnostics[]` 与 `platformFeatures`。例如 iOS 无 `nav/forward`、`novel.shelf`、`settings.media-controls` 与桌面专属通用设置区，调用会明确返回 `501 UNSUPPORTED_ON_PLATFORM`，不是接口坏了。

## 四、标准排查手法（先拿现场事实，不靠猜）

### 1. 现场取证优先，不要急着重启

遇到卡死、报错、白屏、内存暴涨或状态异常时，**先取证再重启**——重启会立即清空当前 UI 导航栈、内存活跃租约、线程现场与异常环形缓冲：

```bash
node scripts/debug.mjs status
node scripts/debug.mjs content --max-items 50
node scripts/debug.mjs diag summary
node scripts/debug.mjs diag exceptions --kind all --limit 50
node scripts/debug.mjs screenshot --out tmp/incident.png
node scripts/debug.mjs pull --out tmp/incident-session
```

### 2. 直达页面 + 结构化读屏 + 语义操控

不要自己去翻控件树找「设置在哪」「第一个栏目在哪」：

```bash
# 直接打开目标页或设置分组，并等加载完成
node scripts/debug.mjs open music.home --wait-idle
node scripts/debug.mjs open settings.music --section sources --wait-idle
node scripts/debug.mjs open settings.film --section rules --wait-idle
node scripts/debug.mjs open music.channels --param channelIndex=0 --wait-idle

# 读取当前页面结构化业务内容（栏目、条目、列表行、Tab、设置字段）
node scripts/debug.mjs content --max-items 50

# 语义操控：点第 0 个栏目的第 0 项 / 点列表第 0 行 / 切 Tab / 提交搜索 / 改设置 / 操弹窗
node scripts/debug.mjs click-item 0 0 open --wait-idle
node scripts/debug.mjs click-row 0 play --wait-idle
node scripts/debug.mjs tab channel 1 --wait-idle
node scripts/debug.mjs search "周杰伦" --domain music --wait-idle
node scripts/debug.mjs action setting.set --json '{"domain":"music","key":"playbackQuality","value":"320k"}'
node scripts/debug.mjs action setting.set --json '{"domain":"novel","key":"fontSize","value":18}'
node scripts/debug.mjs action dialog.action --json '{"action":"confirm"}'
```

- **先看 `content`，布局排错才看 `tree` 和 `screenshot`**：`content` 给出的是业务层真实投影（标题、副标题、索引、稳定 ID、选中态、字段值）；只有排查裁切、遮挡、字体、对齐或不可见问题时，才结合 `tree --max-depth 8` 与 `screenshot --out tmp/shot.png` 并实际目视查看 PNG。
- **字段名、选项值与 `setting.set` 键名对着当前页 `content` 或对应 Debug 适配器核对，不靠记忆猜**（例如音乐设置支持 `playbackQuality`/`volume`，通用设置支持 `themeColor`/`closeBehavior`，小说外观支持 `fontSize`/`lineHeight`/`theme`/`mode`/`conversion`）。
- **视觉截图不能冒充协议/数据验收，结构化状态也不能冒充视觉排版验收**：两者按问题性质配合取证。

### 3. 异步时序用 `wait`，绝不用固定 `sleep`

页面路由切过去不等于异步数据已经回来。用内置条件等待代替拍脑袋 `sleep`：

```bash
node scripts/debug.mjs wait idle --timeout 15000
node scripts/debug.mjs wait route --param target=music.playlist --timeout 10000
node scripts/debug.mjs wait section_count --param minSections=1 --timeout 15000
node scripts/debug.mjs wait row_count --param minRows=1 --timeout 15000
node scripts/debug.mjs wait playback_state --param domain=music --param state=playing --timeout 15000
node scripts/debug.mjs wait dialog --param open=true --timeout 5000
node scripts/debug.mjs wait log_event --param cat=runtime-bridge --timeout 15000
# 指定具体 event 时先对照本端真实事件名（如 Windows: rpc.response，iOS: rpc.ok），不要凭印象猜
```

### 4. 按分类读日志 + 拉 `$blob` 原文 + `opId`/`traceId` 跨进程追链

日志按固定 16 个分类写入 `<sessionDir>/logs/<cat>.jsonl`，排查时只看相关分类，不要一次性翻全量：

| 排查问题 | 优先看哪几个分类 |
| --- | --- |
| 启动、窗口、关闭排空、Runtime 重建 | `lifecycle`、`runtime-node` |
| 页面跳转、Tab/设置切换、弹窗、错误通知 | `ui-nav`、`ui-action` |
| 原生 ↔ Node RPC/SSE、错误码、超时、设置帧 | `runtime-bridge`、`runtime-node` |
| 音乐登录、首页/频道、曲库、歌单、播放与反馈 | `music-platform`、`music-account`、`music-catalog`、`music-library`、`music-playback` |
| 影视首页/元数据、规则/搜索、选源/网盘/播放 | `film-catalog`、`film-playback`、`search` |
| 小说发现/书源/净化/阅读器/Web 交互 | `novel`、`search` |
| SQLite 读写/事务/缓存、异常/卡顿/内存 | `storage`、`diagnostics` |

常用查法：

```bash
# 看最近 50 条某分类日志，或实时跟踪
node scripts/debug.mjs logs runtime-bridge -n 50
node scripts/debug.mjs logs music-catalog --follow

# 用一次操作的 opId 或 traceId 串联原生（proc=app）与 Node（proc=node）全链路
node scripts/debug.mjs logs all --op <opId>
node scripts/debug.mjs logs all --trace <traceId>

# 事件字段被折叠为 {"$blob":"blobs/<sha256>.json","bytes":...} 时，直接拉回完整不脱敏原文
node scripts/debug.mjs blob <sha256> --out tmp/payload.json

# 查重启前或历史会话的日志与原文
node scripts/debug.mjs logs lifecycle --session <sessionId> -n 100
node scripts/debug.mjs blob <sha256> --session <sessionId> --out tmp/prev-payload.json
```

### 5. 分离定位「UI 问题」还是「Node / 存储问题」

- **直调 Node 运行器模块**（跳过 UI，直接看真实内嵌 Node 返回的 HTTP 状态码和载荷）：
  ```bash
  node scripts/debug.mjs invoke music.platform.describe --json '{}'
  node scripts/debug.mjs invoke film.rules.get --json '{}'
  node scripts/debug.mjs invoke film/rules.get --json '{}'
  node scripts/debug.mjs invoke music.script.sources.list --json '{}'
  ```
  CLI 支持标准前缀（如 `music.platform.*`、`music.script.*`、`cat.script.*`、`source.runtime.*`、`tmdb.data.*`、`douban.data.*`、`netease.token.*`、`film.*`、`pan.*`）与 `module/operation` 写法，`--json '<obj>'` 传入的是该操作的 `input` 对象；若直接调 HTTP `POST /debug/v1/runtime/invoke`，也支持显式传 `{"module":"...","operation":"...","input":{...}}`。业务错误会透传真实 `httpStatus` 与 `response`，不会包成 `500`；同时自动记入 `runtime-bridge` 与对应业务分类日志。
- **直查原生 SQLite（严格只读）**：
  ```bash
  node scripts/debug.mjs sql "SELECT name FROM sqlite_master WHERE type='table'"
  node scripts/debug.mjs sql "SELECT * FROM storage_identity"
  node scripts/debug.mjs sql "SELECT owner, revision FROM owner_revisions ORDER BY owner"
  ```
  两端都叠加了领先动词检查、只读 `PRAGMA` 白名单、`sqlite3_stmt_readonly` 引擎级只读检查（Windows 另加 `PRAGMA query_only = ON` 保护）；任何写语句、多语句或带副作用的 `PRAGMA`（如 `foreign_keys = OFF`、`wal_checkpoint`、`optimize`）都会被直接拒绝。

### 6. 底层诊断：内存泄漏、线程卡顿、异常与 V8 剖析

```bash
# 1. 先看聚合快照（原生内存/句柄/活跃媒体与图片租约 + Node 堆内存/Worker 数）
node scripts/debug.mjs diag summary

# 2. 卡顿、死锁或关闭未排空：抓全线程堆栈 + UI 线程探活 + Node 活跃请求/Worker
node scripts/debug.mjs diag threads

# 3. 怀疑内存只涨不降：强制原生 + Node GC，对比回收前后内存与活跃租约计数
node scripts/debug.mjs diag gc

# 4. 排查被吞掉或偶发异常：动态开启首机会异常并读取异常环形缓冲
node scripts/debug.mjs config --first-chance true
node scripts/debug.mjs diag exceptions --kind all --limit 100

# 5. 生成深度转储文件（落盘至 dumps/，可用 pull 拉回）
node scripts/debug.mjs diag dump --mode mini         # Windows: MiniDump .dmp (可改 heap); iOS: 线程+VM区域诊断 .json
node scripts/debug.mjs diag heapsnapshot             # Node V8 .heapsnapshot（Chrome DevTools 直接打开）
node scripts/debug.mjs diag cpu-profile --duration 5000  # Node V8 .cpuprofile

# 6. 一键拉回整轮会话全部产物（session.json + logs/* + blobs/* + dumps/* + screenshots/*）
node scripts/debug.mjs pull --out tmp/debug-pull
node scripts/debug.mjs pull --session <历史sessionId> --out tmp/debug-pull-prev
```

### 7. 修复验证：先留失败证据，再同路径复核

修复缺陷前，先在真实 `--debug` 会话里用对应日志、`$blob` 原文、SQL 结果或界面 `content`/`screenshot` 拿到明确的失败现场；修改并重新打包后，沿同一入口与同一输入复跑核对：

- 不仅确认「没报错」，还要核对产出数据、落库 revision/行内容、活跃租约计数与界面显示是否与预期一致；
- 无法在当前环境实跑验证的范围（例如未连真机、无外网登录账号）在交付中明确列为未覆盖项，绝不靠静态检查或脑补宣布通过。

## 五、常用 CLI 与接口速查

| 目的 | `node scripts/debug.mjs ...` | 对应 HTTP 接口 |
| --- | --- | --- |
| 存活与当前页摘要 | `status` | `GET /debug/v1/health` + `/ui/state` |
| 平台能力清单 | `caps` | `GET /debug/v1/capabilities` |
| 动态改日志/异常配置 | `config [--modules <csv>] [--first-chance true\|false]` | `POST /debug/v1/config` |
| 直达页面 / 设置分组 | `open <target> [--section <s>] [--param k=v] [--wait-idle]` | `POST /debug/v1/nav/open` |
| 后退 / 前进 / 刷新 | `back` / `forward` / `refresh` | `POST /debug/v1/nav/{back,forward,refresh}` |
| 当前页状态 / 内容 / 控件树 | `state` / `content [--include-raw]` / `tree [--max-depth 8]` | `GET /debug/v1/ui/{state,content,tree}` |
| 点击栏目项 / 列表行 / 切 Tab | `click-item <sec> <item> [verb]` / `click-row <row> [verb]` / `tab <grp> <id>` | `POST /debug/v1/ui/action` |
| 触发搜索 / 通用动作 | `search "<q>" --domain <d>` / `action <name> --json '{...}'` | `POST /debug/v1/ui/action` |
| 条件等待 | `wait <cond> [--param k=v] [--timeout <ms>]` | `POST /debug/v1/ui/wait` |
| 播放 / 小说直控 | `playback [state\|play\|pause\|seek <s>\|...]` / `novel <cmd>` | `GET /playback/state`, `POST /{playback,novel}/command` |
| 查分类日志 / 实时流 / 原文 | `logs [cat] [-n 50] [--follow] [--op <id>]` / `blob <sha256>` | `GET /debug/v1/logs/{categories,tail}`, `/blobs/<sha>` |
| 直调 Node 模块 | `invoke <method> --json '{...}'` | `POST /debug/v1/runtime/invoke` |
| 只读 SQLite 查询 | `sql "<SELECT ...>" [--params '[...]']` | `POST /debug/v1/storage/query` |
| 底层诊断与转储 | `diag <summary\|threads\|gc\|dump\|heapsnapshot\|cpu-profile\|exceptions>` | `/debug/v1/diag/*` |
| 截取窗口 PNG | `screenshot --out tmp/shot.png` | `GET /debug/v1/ui/screenshot` |
| 拉回整轮会话 | `pull [--session <id>] --out tmp/pull` | `GET /debug/v1/sessions` + `/files/<relpath>` |

### 容易踩的 5 个细节

1. **自定义 `--debug-dir` 必须同步传给 CLI**：如果启动应用时指定了自定义 `--debug-dir`，执行 `scripts/debug.mjs` 时必须传相同的 `--debug-dir` 或显式 `--endpoint http://127.0.0.1:<port>`，否则 CLI 会去读默认调试目录里的 `active-session.json`，连到旧会话或报找不到文件。
2. **`--json` 在不同位置含义不同**：放在 `<command>` 之前（如 `node scripts/debug.mjs --json status`）表示输出原始 JSON 信封；放在 `invoke`/`action`/`wait`/`novel`/`open`/`playback` 之后且紧跟 `{...}` 时表示请求 JSON 参数体（也可显式用 `--input '{...}'` 或 `--json='{...}'` 避免歧义）。
3. **Node 尚未连上时的合法降级**：当内嵌 Node 未启动或已断开时，`diag summary` / `diag threads` / `diag gc` / `config` 仍正常返回原生数据，仅把 `node.available` 标为 `false`；`invoke`、`diag heapsnapshot`、`diag cpu-profile` 会返回 `503 RUNTIME_UNAVAILABLE`。遇到时先看 `status` 里的 `runtimeConnected` 与 `logs runtime-node`（Windows 另可查 `logs/runtime-node.stderr.log`），不要盲目重跑 UI 操作。
4. **强杀进程不会写回 `status: "exited"`**：只有正常关闭应用才会把 `session.json` 更新为 `status: "exited"` 并附带 `exitSummary`；直接杀进程后旧会话仍显示 `"running"`，排查时要核对 `pid` 是否还在。
5. **历史会话保留上限**：`--debug-keep` 默认只保留最近 `10` 个会话目录，超出的最旧会话会在下次启动时自动清理；需要长期留存证据时及时用 `pull --out tmp/<dir>` 拷出。

## 六、保密红线与收尾清理

- **Debug 日志与 `blobs/` 有意不脱敏**（包含完整 Cookie、Token、请求响应原文与本地配置），严禁纳入 Git、贴入 PR 描述/文档或对外分享。
- 排查与验证产物只放在仓库 `tmp/` 或各平台开发目录的 `temp/`（均由 Git 忽略），**禁止把临时截图、转储、日志或一次性脚本提交进仓库**。
- 每轮实跑结束后必须完成清理：
  1. 正常退出本次拉起的调试进程，确认无残留 `meowfilm.exe` / `node.exe` 或孤立 Windows 计划任务；
  2. 删除本次创建的临时 `--data-root`、`--debug-dir` 与 `tmp/` 排查目录（不要误删真实用户数据）；
  3. 若占用了 Mac 模拟器，确认进程停止并按 `~/Developer/temp/simulator-usage` 释放占用；
  4. 执行 `git status` 确认工作区干净，没有排查垃圾文件混入改动。
