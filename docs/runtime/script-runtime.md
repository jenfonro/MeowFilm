# 共用脚本运行时

## 职责与目录

```text
apps/runtime/entry.mjs
  受信 session config 的 createRuntime/startRuntime 独立入口
apps/runtime/bootstrap.mjs、client.mjs、contracts.mjs
  标准本地 HTTP 装配、Node 调用端、三模块版本和安全错误
apps/runtime/modules/{music-script,cat-script,source-runtime}.mjs
  LX、CatNode、Reader/CatVod 的明确业务 owner
apps/runtime/modules/source-candidates.mjs
  完整源码候选归属、同 installation 管理顺序
packages/script-host/
  脚本管理、LX/source 原生 QuickJS 线程、Cat Node Worker 与资源回收
packages/source-runtime/
  Node-independent Reader 规则、CatVod Spider adapter 与 capability ABI 2
packages/runtime-bridge/
  标准 Node HTTP、严格 JSON、反向调用和交换生命周期
packages/pan-runtime/、apps/runtime/pan-entry.mjs
  显式装配的内置网盘能力，不加载脚本引擎或额外 Worker
```

通用运行器不依赖平台目录。官方账号、目录、曲库、歌词、播放等原生业务由调用方管理；脚本安装、源码、来源地址和私有数据由运行器持久管理。客户端可保存来源列表、检测结果等供原生界面直接读取的快照，不形成第二套安装管理或脚本存储。独立入口描述 `aggregate`，模块为：

| 模块 | 响应契约 | 输入职责 |
| --- | --- | --- |
| `music@0.8.0` | `music.script.v3` | 原始 LX 音源 |
| `cat@0.2.0` | `cat.script.v2` | CatNode、HTTP 服务、direct Spider、私有卷和管理同步 |
| `source@0.2.0` | `source.runtime.v2` | Legado、异次元、轻悦时光、独立 CatVod Spider |

受信调用方可显式启用 `pan@0.1.0/pan.runtime.v1` 的[网盘账号、分享与取址能力](pan-runtime.md)；默认三模块不变，平台 App 尚未接入。网盘属于 Node 内置业务，不是第四类脚本执行所有者。

iOS 与 Windows 均已接入同一 HTTP 契约和三个模块。iOS 受信启动及内置原生 framework 见[iOS 运行底座](../ios/runtime-preflight.md)，Windows launcher、HTTP 消费者和原生引擎包装见[平台接入](../windows/app-runtime.md)与[打包](../windows/runtime-packaging.md)。通用运行器不保留旧 wire 或旧引擎。独立入口、平台 App 和设备运行须分别验证，不据此声明 Android 已接入。

## 三类执行所有者

Node 主控负责客户端 HTTP、安装与代次、调度、共享异步 I/O 和能力 owner。LX、source 与 CatNode 保留各自业务协议，不按文件名、作者、内容 hash 或网络域名猜业务类型。

LX 每个启用实例有一个原生线程、一个原生 QuickJS Runtime 和一个 Context。source 每个启用实例有一个原生线程、一个 Runtime、两个 Context：可信规则/SDK core 与用户脚本 realm；两个 Context 共用同实例 Runtime，不给每个书源再套 V8 Worker。CatNode 每个实例使用独立 Node Worker/V8 直接执行，移除内部 QuickJS。独立实例保留状态，不通过闲置卸载、重建或固定活跃数配额降低成本。

脚本正常运行中的变量由引擎自然持有，不做内存快照持久化。Cat 同一私有卷只允许一个 Worker：更新前先退出旧 Worker，失败恢复的是旧源码而不是旧内存状态；其他实例不因此重建。明确管理重启与闲置回收是不同操作。尚未接入的网盘宿主缓存不在当前实现内。

慢实例的同步 JavaScript、显式算法或网络等待不通过全局执行队列阻塞其他实例或控制 HTTP；同安装管理修改有序，各协议原有动作调度语义保留。实际 CPU/内存竞争仍存在，原生进程崩溃不承诺仅影响单实例。

Node-independent 只描述 `@meowfilm/source-runtime` 的纯规则与 ABI 包；它不导入 `node:*` 或平台全局。CatNode 的 Node 能力不能据此宣称无 Node 可运行。source 的 `protocol: cat` 是 CatVod Spider，不能代替 CatNode 的服务、私有卷和管理能力。

## QuickJS 执行边界

引擎采用 QuickJS-NG 原生 Node-API 绑定，来源、ABI、架构及精确输入以 `scripts/script-host-native.json` 为准；不加载旧 WASM/asmjs，不在失败时切换后端。原生 Runtime 的建立、Context、求值、pending jobs、值引用和释放均由所属线程拥有，主线程不调用跨线程 JSValue。宿主回调传递明确身份、结构和 typed bytes，必要的所有权边界复制保留，不声称零拷贝。

LX/source 用户脚本不能直接获得 Node 对象、任意宿主 callable、文件、进程或模块加载器。原生绑定线程与 Cat Worker 是执行及生命周期隔离，不是对恶意代码或引擎漏洞的安全保证；既有私有卷、grant、scope 和网络访问身份仍必须保留。

Promise reaction、原生 await、异步迭代和清理回调保留登记任务身份。旧 A 延续由 B 唤醒时仍属于 A，不能借 B 的能力。私有任务入口在 SDK 初始化捕获后、用户代码执行前移除；不包装公开 Promise.then、不增添微任务或改变标准排队顺序。LX 特权存储始终使用显式请求 context，不由 ambient task identity 授权。

异步生成器排队请求独立保存执行 owner，切换请求时同步切换取消与截止控制，不以权限身份替代执行控制；既有取消收尾时限不变。跨环境数组复制定义自身数据元素，不触发继承 setter；业务 JSON 和内部 typed bytes 契约不变。

源码上限 16 MiB、QuickJS 栈边界以及 Reader 解析预算是独立输入/引擎/解析契约。运行器已删除 `maxMemoryBytes`、引擎内存额度、派生宿主 body/算法/state 配额、耗尽标记及内存超限自动重启；没有 Infinity、极大值或 nullable 开关伪装保留。普通网络体、ABI bytes、JSON 和变量不套 Reader 解析额度。可报告分配失败、取消、超时和资源释放仍需处理，不保证系统 OOM 仅影响单实例。

保留真实内存观测：原生 QuickJS 使用实际引擎统计，Cat 使用 Node/V8 的含义，RSS 是进程级值；重叠统计不可相加。不将旧配额减 heap 伪装为 `os.freemem()`，也不把取消额度当作性能优化证据。

初始化和调用等待的现有截止保留：LX 初始化 5 秒、请求 60 秒，source 初始化/请求 60 秒、清理 3 秒，LX 清理预算保持 100 ms。主控能取消卡死执行；QuickJS interrupt 由所属原生执行线程观察，Cat 等待真实 Worker exit。没有新增单段同步计算时限、后台心跳或配额重启。停用/卸载/更新/关闭先触达实际初始化所有者，再等有序修改和真实退出。`stats()` 返回事实快照，不阻塞执行线程。

请求取消或到期立即撤销其能力权限与结果交付。原生执行中仍可运行的收尾沿用该协议的清理预算：窗口内结束则保留实例和状态，持续执行超窗才终止该实例；正常未取消计算不受此窗口限制。没有可执行工作时不等满预算，尚未 resolve 的 Promise 本身不构成卡死。旧 reaction 后来由 B 唤醒仍保留旧身份与权限失效事实，执行受 B 的有效调用控制，不能借 B 的权限恢复已撤销能力。

## HTTP 与源码管理

公共 wire 为 `runtime.http.v1`，细节见 [HTTP 契约](runtime-bridge.md)。源码用一次 raw PUT 登记完整候选，普通业务参数用一次 JSON POST，响应直接 value；合法大 body 采用同一路径，不增加自动摘要、压缩、正文加解密或预处理。

完整候选按 installation/sourceRevision 拥有，无续传、TTL、token 或全版本缓存。字段/身份/CAS 前置拒绝不消费候选，试运行失败释放，精确版本不存在明确失败；不偷偷执行最新候选。安装内容 hash 只计算一次复用，source 原始上传与规范化执行内容身份分别保留。Cat 的实际文件来源和 settings 仍按其安装事务落盘。

HTTP 取消以 abort 或未完成响应 close 传播；正常 finish 不误取消。取消 fence、协议截止、实例退休和物理排空分别核对。忽略 signal 的 I/O 保持归属，无法确认清理时返回安全清理错误。明确媒体代理保持流式、Range、必要头、背压、取消与已有地址映射；普通脚本上游仍按 SDK 自身语义处理，不被自动改为媒体流。

## v2 特权存储

LX SDK 请求处理器第二参数的显式能力保留；这里的 SDK v2 与外部 HTTP 响应 `music.script.v3` 是不同版本维度：

```js
lx.on(lx.EVENT_NAMES.request, async (payload, context) => {
  return await context.storage.get('key');
});
```

`context.storage.get/set/delete` 绑定调用 ID、site、installation、generation 及 read/write 权限，跨 await 保持同一身份。请求结束、取消或退休后旧引用失效。全局 `meow` 只提供 config；startup/background 不获得隐式存储授权，即使另一请求活跃也不能借用。Node AsyncLocalStorage 不能推断 guest Promise 的特权。

私有 KV 由音乐模块在 `<dataRoot>/music` 中持久保存，不再通过客户端 grant 或反向存储 RPC；运行器在处理器有效请求内提供读写权限，旧 context 在请求结束、取消或退休后失效。已有 KV 文档大小和键数边界是持久化数据契约，不是脚本内存配额。详见 [音乐脚本契约](music-script-runtime.md)。

## 网络与 Cat 持久资源

LX 网络适配器形状保持 `network({url,options,signal,scope,validateURL})`。validateURL 检查 HTTP(S)、URL userinfo 和重定向语法，不是域名/私网白名单；跨 origin 不转发 Authorization/Cookie。`followRedirect:false` 禁止自动跳转，true 仍服从既有次数边界；`retry` 兼容字段不触发宿主自动重试或重放 POST。脚本显式 crypto/zlib、既有字符集/JSON/HTTP 解压语义保留，没有附加正文加工层。

管理入口的网络源码通过专用 `createSourceDownload` 下载，沿用各模块的 URL、状态、重定向和期限规则；传输与解压后的源码均受 16 MiB 输入边界约束，取消等待连接与解压流真实关闭。普通 guest 网络适配器不恢复响应字节配额，也不套用源码大小限制。

Cat 的 HTTP(S)、DNS、net/TLS 不按公网、私网或链路本地地址过滤；已登记 listener 别名映射本实例实际回环端点并携带访问身份，未登记回环目标直接访问且不附加凭据。socketPath/fd 与私有卷规则继续属于文件/IPC 边界。完整服务、管理 Cookie、provider 拦截和目录规则见 [CatNode 契约](cat-script-runtime.md)。

Cat 真实 Buffer/流/密钥对象按 Node 语义工作，资源按请求或实例生命周期跟踪。停用、更新、端口变化及进程重启保留私有卷；卸载或设置移除在真实停止后删除该卷与无引用源码，删除失败明确记录，不扫描未知目录或残卷。

## 构建、发布与验证边界

公共构建输入见 `scripts/script-host-native.json`，桌面构建入口为 `scripts/quickjs/build-native.mjs`；公共打包 `scripts/package-runtime.mjs` 仅支持 `common-native`，manifest 的 entry 为 `app/entry.mjs`、status 为 complete 时表示打包完成。闭包含公共装配、三 workspace 包、规则 SDK、Cat 依赖和精确受信原生载体。Windows wrapper 在公共产物上增加私密启动包装；iOS wrapper 增加受信启动及签名 framework 装配，目标明确区分 device／simulator。旧 profile 不提供兼容后端。平台构建和完整性边界分别见[Windows 打包说明](../windows/runtime-packaging.md)及[iOS 发布说明](../ios/release.md)。

构建成功、原生可加载、合成行为测试、清理/取消验证、真实第三方联网动作和同机性能测量是不同证据。main 整合前的定档 `b2ec723` 已在 Linux arm64／Node 24.21.0 上完成原生／SDK 增量构建；受影响的 33 个测试文件、740 个不同用例结合定向复验均通过，包含真实独立 payload 的三协议启动。首轮仅三个新增 Cat 夹具遗漏实例标准流基线，修正为完整对象身份及实际关闭检查后，三例复验通过；产品输入未变，其余通过结果复用。这些是定档证据，不代替本次管理功能与 HTTP 接线整合后的定向验证。

本次整合 main `17b97e2` 后，在同一环境完成最终 native／SDK 增量构建；20 个相关测试文件的 495 个不同用例结合定向复验通过，无 skip。覆盖音乐/Cat 管理、完整 HTTP 与 64/65 层、源码下载的真实取消/关闭、独立推进、私有 KV、验收 CLI、打包边界和实际三协议 payload。首轮六项失败分别修复了 LX 存储控制包装重复计深及四处测试夹具/预期不匹配；复验另有一项 HTTP 取消用例偶发失败，同输入精确重跑通过，未修改该断言，原失败日志保留。详细测试迁移、输入适用范围及不确定点见任务交付记录。

返修前另有 30 条完整请求链路及 LX/Cat/source 同步 JS、显式算法的六组独立推进时序证据；当时没有重跑整套性能测量，不将历史测量冒充新产物实测。以上是通用重构阶段的历史验证，当时未迁移 Windows/iOS 接入；后续两端接入与验证边界分别见[Windows 平台接入](../windows/app-runtime.md)和[iOS 运行底座](../ios/runtime-preflight.md)。真实第三方脚本和外部来源验收未覆盖，这些历史结果不代表全平台、平台应用验证或可发布。外部验收和脱敏报告规则见 [来源动态验收](source-acceptance.md)；逐次结果保存在任务证据中。
