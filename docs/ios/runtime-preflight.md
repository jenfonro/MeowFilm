# 内嵌 Node 运行底座预检

本工具验证 iOS 的正式 NodeMobile／原生引擎载体，不维护另一套 LX、Cat 或 Reader 实现。独立诊断 App 复用正式构建输入，不能代替 `MeowfilmRuntimeTests` 的真实 App 装配、发布签名或真机验收。

## 构建与运行

需要 macOS、Xcode、构建机 Node 24 及根锁依赖。App 内嵌版本仍由 `scripts/ios-node-mobile.json` 锁定为官方 NodeMobile 18.20.4；构建机 Node 不成为用户设备的运行依赖。

```sh
npm ci --ignore-scripts
python3 -I -B scripts/ios-node-preflight.py --simulator <UDID>
```

预检工具先分别调用准备工具，为本机构建架构的 simulator 和 device arm64 准备输入，再构建诊断 App；准备失败即停止，不用旧载荷继续构建。准备工具复用已校验的 NodeMobile archive／解包缓存，按明确的 iOS SDK 和架构准备原生引擎及载荷。device arm64 与 simulator arm64／x86_64 是不同目标，不能把 macOS arm64 二进制或另一种 iOS variant 改清单后使用；缓存按实际目标复用，不按测试轮次新建。

`common-native` 公共闭包含业务模块、规则 SDK 和锁定依赖。通用默认入口仍为 music／cat／source；iOS 实际 entry 显式增加 pan、tmdb、douban，并由 `connectFilmPlayback` 复用这些 provider 装配 film 和变更撤权 hook，最后追加 netease。完整 manifest 为 `platform: ios`、`transport: runtime.http.v1`、精确 `modules: ["music","cat","source","pan","tmdb","douban","film","netease"]`，入口为 `app/ios/entries/aggregate.mjs`。App 在唯一 launcher 发布连接前按序严格校验 describe 的 music@0.8.0／music.script.v3、cat@0.2.0／cat.script.v2、source@0.3.0／source.runtime.v2、pan@0.1.0／pan.runtime.v1、tmdb@0.1.0／tmdb.data.v1、douban@0.1.0／douban.data.v1、film@0.1.0／film.playback.v1、netease@0.1.0／netease.token.v1 八模块身份；仅对 TMDB 与 netease 的完整方法清单额外严格核验（token 仅有 `netease.token.get`），其余模块沿各自既有方法契约，不作全方法握手校验。旧帧协议、分片、asm.js／WASM 后端及“iOS 不装 source”的例外不保留。

精确 describe 不获取网易 token，也不下载或执行 Watchman。原生仅在关系写时借用 token 模块；Worker 的 45s 总预算由该调用至少 60s 的 HTTP 预算承接，取消仍取消实际 URLSession task。启动／脚本管理／普通账号业务不因该能力而依赖 SDK 网络。合成 SDK 与真实 jsdom Worker 在正式 NodeMobile 中运行才能证明嵌入承载；真实无账号 token 探测仅按显式选择执行，不作为默认联网门禁，结果只记录结构。Linux Node 24 结果不能作为 NodeMobile 18.20.4 的证明。

仅验证网易 token 的离线嵌入承载时，显式选择既有预检用例：

```sh
python3 -I -B scripts/ios-node-preflight.py --simulator <UDID> --simulator-only --case netease-token-sdk-worker
```

该用例在正式 NodeMobile 18.20.4 中，经 loopback HTTP 下载合成 SDK，并执行真实 jsdom Worker；不访问外部 SDK 网络，也不使用账号或发送订阅写。它验证下载路径与 Worker 的实际承载，不证明真实账号订阅成功。真实无账号取 token 如需验证，另行显式执行一次性临时 probe，不加入常态联网门禁，不记录 token 内容。

App 嵌入两个 framework：

- 官方 `NodeMobile.framework` 提供 Node 主控和 Cat Worker。
- `MeowScriptHost.framework` 提供同一套原生 Node-API 8 执行核心；QuickJS-NG 0.18.0、锁定派生补丁、OpenSSL libcrypto 和 zlib 静态进入该 framework，并限制导出。LX／source／影视纯 JSON 程序各自使用独立 Runtime／Context／线程，不是每脚本复制 framework 或新建 Node 进程。

影视规则、搜索及匹配统一由 Node 加载共享程序并通过该原生载体执行；客户端只调用 `film.rules.*`／`film.search.*`／播放会话接口，不再静态链接另一套引擎。规则和脚本状态隔离，取消／截止保持真实资源排空与恢复；共享核心和 Unicode 表许可随 App 分发。

NodeMobile 18 的内置 `Request` 等 getter 会加载依赖 WASM 的 Undici 网络实现。iOS 因此只在构建期从锁定的 Undici 6.29.1 打包 `Request`／`Response`／`Headers`／`FormData` 纯值闭包为 `cat-web-values.cjs`，不发布完整 npm 包、fetch／dispatcher／llhttp 或 WASM；网络仍经现有 managed `node:http`，Buffer／Blob／WebStream 仍用 Node 原生值。该产物、实际输入和许可纳入 receipt 与编译身份。其他平台仍用原生 Web 值构造器。缺失的 `CustomEvent` 仅在 Cat realm 内以原生 `Event` 子类提供，不改宿主全局或启动开关。

## 完整性与启动边界

构建期校验固定来源、补丁、工具、目标、源码／SDK／依赖清单、许可与签名前产物。安装后的原生 framework 由 App 签名链保护，并核对编译内置身份、ABI 和目标；JS／SDK 的文件身份继续校验。IPA 移除签名和侧载重签会改变 Mach-O，不能把签名前全文件 hash 当作安装后恒定值，也不能借此关闭桌面 `.node` 校验。

原生宿主使用最小私有启动通道交接生命周期 session、数据目录，并接收本机访问 URL；通道只负责启动与生命期，不承载业务请求。公共 HTTP 监听全部可用接口，外部和浏览器请求无需密钥或 session，Host／Origin 不作为访问门禁。所有业务经标准 HTTP；App 最终关闭运行器并等待 `node_start` 返回，不能用 HTTP close 回执或固定延时假定已退出。

每个 App 进程只启动一次 Node；不可恢复失败后脚本不可用，不自动重启，也不通过 `process.exit` 杀掉整个 App。它不是恶意原生代码安全沙箱，取消和独立执行不等于整个进程崩溃／OOM 隔离。

启动失败仅向 stderr 输出 `IOS_RUNTIME_START_FAILED stage=<静态阶段> category=<白名单错误类别>`，并设置退出码 1。阶段区分 `arguments`、`native-binding`、`bootstrap-channel`、`configuration`、`assembly`、八模块各自初始化、`listen` 和 `endpoint`；模块阶段使用固定模块 ID。类别仅保留明确列入白名单的协议、存储、原生载体、通道和系统 I/O 错误码，其余固定为 `UNKNOWN`，非法阶段固定为 `unknown`。不输出异常 message、stack、路径、配置或凭据；该记录只定位失败阶段和类别，不能还原未记录的原始异常。成功握手仍仅交接 `baseUrl`，运行期逃逸异常仍使用 `IOS_RUNTIME_FAILED`。

## 验证内容与成功判据

独立预检的检查项包括实际 Node／N-API／目标身份、正式 framework 加载、异步原生回调、typed bytes、明确脚本算法、线程取消及真实退出；同时保留有用途的文件、Node crypto、HTTP 和 Worker 能力检查。正式 App 宿主的用例范围包括：

- 当前 App 八模块按序精确 describe（不获取 token）；LX 导入、启用、取链、请求私有 KV、歌词、停用、删除。
- Cat 实际 HTTP 五阶段请求、私有卷、客户端选项、删除标记及结果返回后管理换代。
- source 的实际原生装载、调用、卸载，以及 Reader 的统一结果；[小说](novel.md)另由正式 App 装配永久仓库、原生阅读／Web 消费者和两进程冷启动验证，不把独立预检当产品 UI 验收。
- TMDB 真实模块经可控 HTTP 上游验证设置／实际 Film adapter 搜索与详情／受信二进制图片／实际 Artwork 消费者，精确选择见[影视搜索](film-search.md#真实-tmdb-宿主)。合成上游不是公网账号验证。
- 同一真实 NodeMobile 的 source 与影视规则使用共享载体、独立 Runtime／Context；规则固定场景、Unicode／UTF16、取消／截止恢复，以及规则预览与搜索输入一致性。Unicode 原样 JSON 键及业务 `blob`／`token` 字段仍保留。
- 音乐模块关闭后 Cat 仍可用，App 最终关 listener 和全部执行资源并等 Node 线程返回。

独立预检的 Cat 安装／调用链还验证 Web 值、原生事件分发及脚本内 loopback fetch；不能只验证普通 `require('http')` 而漏掉 Web 值的隐式依赖。

测试报告须逐项区分编译／链接、模拟器运行、真实 App 装配和设备运行。独立预检只有所有选定检查及 `node_start` 返回状态符合预期才成功；不能加载时明确失败，不回退 `eval`、Node `vm` 或旧引擎。

此前八模块与 VideoKernel 合并后的原生接线已通过定向 macOS Swift、iOS Simulator x86_64 UIKit／真实 App-host 测试，以及模拟器 App 增量构建、独立验签和八模块装配检查；这些既有结果不覆盖本轮 source@0.3.0 与小说合并后的变更输入，待集中验证，亦未覆盖真机或完整视频矩阵。视频内核独立于脚本 framework，不能用这些运行器与转交验证证明真实视频帧、ClearKey、seek、PiP 或媒体资源排空。

终止宿主的关闭场景和持久冷启动场景必须独立进程运行，流程见[影视设置验证边界](film-settings.md#生命周期与验证边界)。默认跳过这些显式场景不等于通过；测试替身和桌面 Node 结果也不能算 iOS 实跑。

Node 18 已结束上游维护，采用官方嵌入包不意味着获得上游安全维护。当前无需 ICU 或 WebAssembly 承载原生引擎。真机、长时间前后台恢复、外部真实源、媒体播放和其他架构须有各自证据，不能从一次模拟器通过外推。
