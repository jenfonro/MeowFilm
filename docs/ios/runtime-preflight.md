# 内嵌 Node 运行底座预检

本工具验证 iOS 的正式 NodeMobile／原生引擎载体，不维护另一套 LX、Cat 或 Reader 实现。独立诊断 App 复用正式构建输入，不能代替 `MeowfilmRuntimeTests` 的真实 App 装配、发布签名或真机验收。

## 构建与运行

需要 macOS、Xcode、构建机 Node 24 及根锁依赖。App 内嵌版本仍由 `scripts/ios-node-mobile.json` 锁定为官方 NodeMobile 18.20.4；构建机 Node 不成为用户设备的运行依赖。

```sh
npm ci --ignore-scripts
python3 -I -B scripts/ios-node-preflight.py --simulator <UDID>
```

预检工具先分别调用准备工具，为本机构建架构的 simulator 和 device arm64 准备输入，再构建诊断 App；准备失败即停止，不用旧载荷继续构建。准备工具复用已校验的 NodeMobile archive／解包缓存，按明确的 iOS SDK 和架构准备原生引擎及载荷。device arm64 与 simulator arm64／x86_64 是不同目标，不能把 macOS arm64 二进制或另一种 iOS variant 改清单后使用；缓存按实际目标复用，不按测试轮次新建。

`common-native` 公共闭包含 music、Cat、source 三模块、规则 SDK 和锁定依赖。iOS 包装只增加受信启动及平台产物接线，完整 manifest 为 `platform: ios`、`transport: runtime.http.v1`，入口为 `app/ios/entries/aggregate.mjs`。旧帧协议、分片、asm.js／WASM 后端及“iOS 不装 source”的例外不保留。

App 嵌入两个 framework：

- 官方 `NodeMobile.framework` 提供 Node 主控和 Cat Worker。
- `MeowScriptHost.framework` 提供同一套原生 Node-API 8 执行核心；QuickJS-NG 0.14、四份固定补丁、OpenSSL libcrypto 和 zlib 静态进入该 framework，并限制导出。LX／source 每实例使用独立 Runtime／线程，不是每脚本复制 framework 或新建 Node 进程。

客户端影视规则的 QuickJS-NG 0.18 继续由 `MeowJavaScript` 独立静态链接。两套引擎有不同职责及补丁，不能依赖链接顺序混用符号。许可和来源分别保留。

NodeMobile 18 的内置 `Request` 等 getter 会加载依赖 WASM 的 Undici 网络实现。iOS 因此只在构建期从锁定的 Undici 6.29.1 打包 `Request`／`Response`／`Headers`／`FormData` 纯值闭包为 `cat-web-values.cjs`，不发布完整 npm 包、fetch／dispatcher／llhttp 或 WASM；网络仍经现有 managed `node:http`，Buffer／Blob／WebStream 仍用 Node 原生值。该产物、实际输入和许可纳入 receipt 与编译身份。其他平台仍用原生 Web 值构造器。缺失的 `CustomEvent` 仅在 Cat realm 内以原生 `Event` 子类提供，不改宿主全局或启动开关。

## 完整性与启动边界

构建期校验固定来源、补丁、工具、目标、源码／SDK／依赖清单、许可与签名前产物。安装后的原生 framework 由 App 签名链保护，并核对编译内置身份、ABI 和目标；JS／SDK 的文件身份继续校验。IPA 移除签名和侧载重签会改变 Mach-O，不能把签名前全文件 hash 当作安装后恒定值，也不能借此关闭桌面 `.node` 校验。

原生宿主使用最小私有启动通道交接临时 session、requestKey、数据目录，并接收实际回环监听地址；通道只负责启动与生命期，不承载业务请求。key 不进入命令行、URL、日志或持久文件。所有业务经标准 HTTP；App 最终关闭运行器并等待 `node_start` 返回，不能用 HTTP close 回执或固定延时假定已退出。

每个 App 进程只启动一次 Node；不可恢复失败后脚本不可用，不自动重启，也不通过 `process.exit` 杀掉整个 App。它不是恶意原生代码安全沙箱，取消和独立执行不等于整个进程崩溃／OOM 隔离。

## 验证内容与成功判据

独立预检覆盖实际 Node／N-API／目标身份、正式 framework 加载、异步原生回调、typed bytes、明确脚本算法、线程取消及真实退出；同时保留有用途的文件、Node crypto、HTTP 和 Worker 能力检查。正式 App 宿主覆盖：

- 当前三模块 describe；LX 导入、启用、取链、请求私有 KV、歌词、停用、删除。
- Cat 实际 HTTP 五阶段请求、私有卷、客户端选项、删除标记及结果返回后管理换代。
- source 的实际原生装载、调用、卸载，以及 Reader 的统一结果，不新增阅读 UI。
- 同进程客户端规则引擎与运行器引擎共存；Unicode 原样 JSON 键及业务 `blob`／`token` 字段。
- 音乐模块关闭后 Cat 仍可用，App 最终关 listener 和全部执行资源并等 Node 线程返回。

独立预检的 Cat 安装／调用链还验证 Web 值、原生事件分发及脚本内 loopback fetch；不能只验证普通 `require('http')` 而漏掉 Web 值的隐式依赖。

测试报告须逐项区分编译／链接、模拟器运行、真实 App 装配和设备运行。独立预检只有所有选定检查及 `node_start` 返回状态符合预期才成功；不能加载时明确失败，不回退 `eval`、Node `vm` 或旧引擎。

终止宿主的关闭场景和持久冷启动场景必须独立进程运行，流程见[影视设置验证边界](film-settings.md#生命周期与验证边界)。默认跳过这些显式场景不等于通过；测试替身和桌面 Node 结果也不能算 iOS 实跑。

Node 18 已结束上游维护，采用官方嵌入包不意味着获得上游安全维护。当前无需 ICU 或 WebAssembly 承载原生引擎。真机、长时间前后台恢复、外部真实源、媒体播放和其他架构须有各自证据，不能从一次模拟器通过外推。
