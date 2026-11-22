# 内嵌 Node 运行底座预检

本工具验证 iOS 的正式 NodeMobile／原生引擎载体，不维护另一套 LX、Cat 或 Reader 实现。独立诊断 App 复用正式构建输入，不能代替完整 App 的 `--debug` 真实运行验证、发布签名或真机验收。

## 构建与运行

需要 macOS、Xcode、构建机 Node 24 及根锁依赖。App 内嵌版本仍由 `scripts/ios-node-mobile.json` 锁定为官方 NodeMobile 18.20.4；构建机 Node 不成为用户设备的运行依赖。

```sh
npm ci --ignore-scripts
python3 -I -B scripts/ios-node-preflight.py --simulator <UDID>
```

预检工具先分别调用准备工具，为本机构建架构的 simulator 和 device arm64 准备输入，再构建诊断 App；准备失败即停止，不用旧载荷继续构建。准备工具复用已校验的 NodeMobile archive／解包缓存，按明确的 iOS SDK 和架构准备原生引擎及载荷。device arm64 与 simulator arm64／x86_64 是不同目标，不能把 macOS arm64 二进制或另一种 iOS variant 改清单后使用；缓存按实际目标复用，不按测试轮次新建。

`common-native` 公共闭包含业务模块、规则 SDK 和锁定依赖。通用默认入口仍为 music／cat／source；iOS 实际 entry 增加 pan、tmdb、douban，由 `connectFilmPlayback` 复用这些 provider 装配 film 和变更撤权 hook，再追加 netease 和借用其 token 的 music-platform。manifest 声明 `platform: ios`、`transport: runtime.http.v1`，入口为 `app/ios/entries/aggregate.mjs`；准备、诊断和原生载荷读取不以完整模块数组作为准入条件。App 发布连接前核对实际依赖的模块 ID、版本和契约，见[运行器契约](../runtime/runtime-bridge.md)；同一依赖身份歧义仍失败。未调用方法的描述和额外模块、方法或排列差异不阻断连接，实际调用仍按路由和响应契约处理失败。旧帧协议、分片、asm.js／WASM 后端及“iOS 不装 source”的例外不保留。

describe 不获取网易 token，也不下载或执行 Watchman。共同音乐模块仅在实际网易关系写准备时借用 token owner，原生等待 `sync.prepare` 的预算承接 Worker 的 45s 总预算，token 不交给原生。启动／脚本管理／普通账号业务不因该能力而依赖 SDK 网络。合成 SDK 与真实 jsdom Worker 在正式 NodeMobile 中运行才能证明嵌入承载；真实无账号 token 探测仅按显式选择执行，不作为默认联网门禁，结果只记录结构。Linux Node 24 结果不能作为 NodeMobile 18.20.4 的证明。

仅验证网易 token 的离线嵌入承载时，显式选择既有预检用例：

```sh
python3 -I -B scripts/ios-node-preflight.py --simulator <UDID> --simulator-only --case netease-token-sdk-worker
```

该用例在正式 NodeMobile 18.20.4 中，经 loopback HTTP 下载合成 SDK，并执行真实 jsdom Worker；不访问外部 SDK 网络，也不使用账号或发送订阅写。它验证下载路径与 Worker 的实际承载，不证明真实账号订阅成功。真实无账号取 token 如需验证，另行显式执行一次性临时 probe，不加入常态联网门禁，不记录 token 内容。

App 嵌入两个 framework：

- 官方 `NodeMobile.framework` 提供 Node 主控和 Cat Worker。
- `MeowScriptHost.framework` 提供同一套原生 Node-API 8 执行核心；QuickJS-NG 0.18.0、锁定派生补丁、OpenSSL libcrypto 和 zlib 静态进入该 framework，并限制导出。LX／source／影视纯 JSON 程序各自使用独立 Runtime／Context／线程，不是每脚本复制 framework 或新建 Node 进程。

影视规则执行、搜索及匹配统一由 Node 加载共享程序并通过该原生载体执行；客户端通过 `film.rules.*` 做预览，搜索／播放继续调用原接口，不再静态链接另一套引擎。规则配置的原生读写见[影视设置](film-settings.md)。规则和脚本状态隔离，取消／截止保持真实资源排空与恢复；共享核心和 Unicode 表许可随 App 分发。

NodeMobile 18 的内置 `Request` 等 getter 会加载依赖 WASM 的 Undici 网络实现。iOS 因此只在构建期从锁定的 Undici 6.29.1 打包 `Request`／`Response`／`Headers`／`FormData` 纯值闭包为 `cat-web-values.cjs`，不发布完整 npm 包、fetch／dispatcher／llhttp 或 WASM；网络仍经现有 managed `node:http`，Buffer／Blob／WebStream 仍用 Node 原生值。该产物、实际输入和许可纳入 receipt 与编译身份。其他平台仍用原生 Web 值构造器。缺失的 `CustomEvent` 仅在 Cat realm 内以原生 `Event` 子类提供，不改宿主全局或启动开关。

## 完整性与启动边界

构建期校验固定来源、补丁、工具、目标、源码／SDK／依赖清单、许可与签名前产物。安装后的原生 framework 由 App 签名链保护，并核对编译内置身份、ABI 和目标；JS／SDK 的文件身份继续校验。IPA 移除签名和侧载重签会改变 Mach-O，不能把签名前全文件 hash 当作安装后恒定值，也不能借此关闭桌面 `.node` 校验。

原生宿主使用私有 socketpair 交接生命周期 session、数据目录，并接收本机访问 URL；同一通道可显式提供原生设置能力，见下节，不承载脚本调用或其他业务 RPC。公共 HTTP 监听全部可用接口，外部和浏览器请求无需密钥或 session，Host／Origin 不作为访问门禁。业务请求仍经标准 HTTP；App 最终关闭运行器并等待 `node_start` 返回，不能用 HTTP close 回执或固定延时假定已退出。

每个 App 进程只启动一次 Node；不可恢复失败后脚本不可用，不自动重启，也不通过 `process.exit` 杀掉整个 App。它不是恶意原生代码安全沙箱，取消和独立执行不等于整个进程崩溃／OOM 隔离。

启动失败仅向 stderr 输出 `IOS_RUNTIME_START_FAILED stage=<静态阶段> category=<白名单错误类别>`，并设置退出码 1。阶段区分 `arguments`、`native-binding`、`bootstrap-channel`、`configuration`、`assembly`、九模块各自初始化、`listen` 和 `endpoint`；模块阶段使用固定模块 ID。类别仅保留明确列入白名单的协议、存储、原生载体、通道和系统 I/O 错误码，其余固定为 `UNKNOWN`，非法阶段固定为 `unknown`。不输出异常 message、stack、路径、配置或凭据；该记录只定位失败阶段和类别，不能还原未记录的原始异常。成功握手仍仅交接 `baseUrl`，运行期逃逸异常仍使用 `IOS_RUNTIME_FAILED`。

### 原生设置私密通道

`RuntimeEmbeddedLauncher(..., settings: RuntimeSettingsAccess?)` 在注入时向一次启动配置加入 `nativeSettings:true`；未注入仍为 `{sessionId,dataRoot}`，不创建设置能力。`MeowfilmNativeSettings.access` 允许读取共同契约中的六块，唯一可写块为 `pan.accounts`。Node 在 `configuration` 阶段读齐快照后才装配业务模块；端点帧仍只有 `{baseUrl}`，没有凭据。

通道使用 LF 分隔 JSON，不设置单帧正文的业务字节配额：

| 方向 | 帧 |
| --- | --- |
| Node → Swift 读 | `{kind:"settings.read",id,block}` |
| Node → Swift 写 | `{kind:"settings.pan.update",id,block,command}`，仅 `pan.accounts` |
| Swift → Node 成功 | `{kind:"settings.result",id,ok:true,document,revision}` |
| Swift → Node 失败 | `{kind:"settings.result",id,ok:false,code}` |

帧仅消费各动作需要的字段，extra 不增加写入权限。`id` 为本连接递增正安全整数；不存在的设置投影用 `null` 表示。读回的 `revision` 绑定设置块的 owner 版本；账号更新 `command` 包含 `provider`、`mode`、`accountRevision`、`rowRevision` 和 `credentials`，原生在同一事务核对所选 provider 的账号及行版本再更新。读取和 provider 更新经原生 repository 执行，Node 不取得任意 SQL 或整库写权限。凭据是共享 SQLite 中所属业务行的明文 typed fields。冲突返回 `STORAGE_CONFLICT`，存储错误返回 `STORAGE_ERROR`，不回传路径或异常正文。

Node 设置交换的等待为 5 秒；超时、非法帧及断连终止本次通道并清理所有 pending，运行器按既有关闭路径排空。Swift 保留唯一 reader 到 EOF，关闭后不接纳设置请求，不在阻塞读取期间另关／复用 descriptor。它是受信能力通道，不是公开反向脚本存储 RPC。

`FilmSettingsDocument` 本地提交通过 `RuntimeSession.settingsChanged(_:)` 异步通知。同步准入只认当前 Starting／活跃代次，不调用 `connection()` 启动运行器；已接纳启动中的通知仅等待同一次启动，关闭／失败后丢弃并排空通知任务。通知 HTTP 的具体接口见 [Runtime HTTP](../runtime/runtime-bridge.md#原生设置注入)，本地保存不等待其回执。

## 验证内容与成功判据

独立预检的检查项包括实际 Node／N-API／目标身份、正式 framework 加载、异步原生回调、typed bytes、明确脚本算法、线程取消及真实退出；同时保留有用途的文件、Node crypto、HTTP 和 Worker 能力检查。正式 App 宿主的用例范围包括：

- 当前 App 九模块依赖 describe（不获取 token）；LX 导入、启用、取链、请求私有 KV、歌词、停用、删除；music-platform 的结构与关闭不等于真实账号业务已通过。
- Cat 实际 HTTP 五阶段请求、私有卷、客户端选项、删除标记及结果返回后管理换代。
- source 的实际原生装载、调用、卸载，以及 Reader 的统一结果；[小说](novel.md)另由正式 App 装配永久仓库、原生阅读／Web 消费者和两进程冷启动验证，不把独立预检当产品 UI 验收。
- TMDB 真实模块经可控 HTTP 上游验证设置／实际 Film adapter 搜索与详情／受信二进制图片／实际 Artwork 消费者，精确选择见[影视搜索](film-search.md#真实-tmdb-接线)。合成上游不是公网账号验证。
- 同一真实 NodeMobile 的 source 与影视规则使用共享载体、独立 Runtime／Context；规则固定场景、Unicode／UTF16、取消／截止恢复，以及规则预览与搜索输入一致性。Unicode 原样 JSON 键及业务 `blob`／`token` 字段仍保留。
- 音乐模块关闭后 Cat 仍可用，App 最终关 listener 和全部执行资源并等 Node 线程返回。

独立预检的 Cat 安装／调用链还验证 Web 值、原生事件分发及脚本内 loopback fetch；不能只验证普通 `require('http')` 而漏掉 Web 值的隐式依赖。

测试报告须逐项区分编译／链接、模拟器运行、真实 App 装配和设备运行。独立预检只有所有选定检查及 `node_start` 返回状态符合预期才成功；不能加载时明确失败，不回退 `eval`、Node `vm` 或旧引擎。

独立预检不覆盖真机或完整视频矩阵。视频内核独立于脚本 framework，不能用运行器与转交验证证明真实视频帧、ClearKey、seek、PiP 或媒体资源排空。

终止宿主的关闭场景和持久冷启动场景必须独立进程运行，流程见[影视设置验证边界](film-settings.md#生命周期与验证边界)。默认跳过这些显式场景不等于通过；测试替身和桌面 Node 结果也不能算 iOS 实跑。

Node 18 已结束上游维护，采用官方嵌入包不意味着获得上游安全维护。当前无需 ICU 或 WebAssembly 承载原生引擎。真机、长时间前后台恢复、外部真实源、媒体播放和其他架构须有各自证据，不能从一次模拟器通过外推。
