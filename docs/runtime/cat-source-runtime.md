# source CatVod Spider 运行时

本文件描述 `@meowfilm/source-runtime` 中原 portable CatVod adapter 的独立 Spider 输入，由 `source@0.3.0`／`source.runtime.v2` 的 `protocol: cat` 接纳。它与 Reader 共用[来源 capability ABI](source-capability-abi.md)，通用 script-host 为每个 source 创建独立原生线程、一个 QuickJS Runtime 和可信 core/用户 realm 两个 Context；LX 共用原生绑定机制，CatNode 则直接使用 Node Worker。

完整 CatNode 输入属于 [`cat@0.2.0`／`cat.script.v2`](cat-script-runtime.md)：包含有限 Node 兼容、HTTP 服务、direct Spider、私有卷和管理同步。本 source adapter 只执行 Spider；即使两个入口都能调用 Spider 动作，也不能将这里的支持范围冒充为 CatNode，或将 Node 兼容层写成无 Node 可运行。

## 脚本入口与动作

支持常见 `export default`（具名 class/function 保留声明绑定）、导出声明、`export { Spider as default }`、`__JS_SPIDER__`、`__jsEvalReturn`、`spider`、`Spider` 和 `rule` 入口，并按脚本实际方法声明：

| 公共动作 | Spider 方法 |
| --- | --- |
| `init` | `init` |
| `home` | `homeContent` |
| `homeVideo` | `homeVideoContent` |
| `category` | `categoryContent` |
| `search` | `searchContent` |
| `detail` | `detailContent` |
| `play` | `playerContent` |
| `report` | `report(payload)` |
| `proxy` | `proxy` |
| `action` | `action` |
| `destroy` | `destroy` |

返回字符串若是 JSON 对象或数组会解析为结构化值；其他返回值按 JSON 边界透传。BigInt、循环结构或抛错 getter 明确报 `SCRIPT_EXCEPTION`，不等待动作超时。

`report` 只有脚本实际声明时才可调用，payload 原样传递准确的 `id`、`flag`、`sessionId`、`event`、`positionSeconds` 和 `durationSeconds`。影视业务仅在 `play` 明确返回 `watchReport:true` 且真实播放事件发生后调用；`report` 不是任意回调 URL，脚本返回的 `ok:false` 不视作业务成功。

首次普通动作会自动初始化 Spider；并发首动作等待同一次成功初始化，然后以各自请求身份执行。等待者取消不会取消另一请求拥有的初始化；初始化拥有者失败或取消后，仍有效的后继动作可用自己的身份重试，旧请求的迟到完成不能提交初始化状态。

显式 `init` 仍每次真实调用 Spider，成功 `destroy` 后下一普通动作重新自动初始化。adapter 只按调用顺序协调这些生命周期操作，不串行化普通动作：一个动作等待 I/O 时，同实例的其他普通动作及其他安装仍可独立推进。

## Guest SDK

QuickJS realm 提供受限 `Buffer`、URL、定时器、HTTP helper、作用域存储、DOM helper 和 CryptoJS 常用摘要/HMAC/AES/DES 子集。Cat 源先以 Acorn AST 识别并改写真实模块语法；注释或字符串中的 `require(`、`process.`、`import(` 不会误报。真实静态/动态 import、外部 re-export、`import.meta`、`require(...)` 和 Node/Deno/Bun 成员调用明确拒绝。

脚本在初始化期保存的 `req`、`local`、DOM helper 或 `js2Proxy` 不保留 startup 权限；后续只在当前动作的 task scope 内执行。动作结束后再次使用旧异步回调会因 scope 失效而失败。

`setInterval` 持续执行至显式清除或所属动作结束；动作完成、取消、超时和 session close 均释放真实 timer 句柄及 guest 回调。`fetch` 的 ArrayBuffer／typed-array 请求体按原始视图字节发送。

外部依赖不能通过 npm 或系统路径加载。需要新增兼容 helper 时，应在 source CatVod adapter／guest SDK 中实现并加入平台无关测试；不能让脚本绕过 ABI 调用 Node。该限制属于 source 输入契约，不限制 CatNode 契约中已实现的受控 `require`。

## 虚拟代理

`js2Proxy` 生成 `meow-source://<profile>/<route>`，路由元数据通过 `virtual.register` 绑定当前 generation。应用可将该地址映射到内部资源处理；source 脚本不能调用 `http.createServer`、监听端口或获得命名管道。CatNode 的回环服务、真实端口映射和私有卷继续遵循其自身契约。

`proxy` 动作收到虚拟 URL 时，运行时解析为已登记的原 URL、headers 和 site key。更新、卸载或关闭会注销全部路由；注销失败使清理失败，不保留可误用的旧 generation。

## 排除项

本输入只支持 CatVod JS Spider。TVBox Android JAR／DEX、任意 JVM 插件和 Android API 不在当前范围，也不提供 CatNode 的文件、服务和管理 API。Node-independent 仅指 source-runtime 纯规则包，当前通用执行由 Node 主控和原生 QuickJS 线程承载；iOS 与 Windows 均通过当前 HTTP 接入，平台载体及验证边界分别见[iOS 运行底座](../ios/runtime-preflight.md)与[Windows 平台接入](../windows/app-runtime.md)。

一次 raw HTTP 源码上传、普通 JSON、完整候选和 CAS 与[Reader 来源入口](reader-source-runtime.md#生命周期与-rpc)相同。源码 16 MiB、栈、60 秒初始化/请求和 3 秒清理保持；取消、真实线程退出见[共用运行时](script-runtime.md#quickjs-执行边界)。虚拟路由、变量、Cookie、存储和响应不再有运行器累计内存配额，不进行 OOM 自动重启；`maximumParseBytes` 仅限制解析，不限制普通 body 或状态。资源仍按 owner 释放，无固定实例/并发/timer/网络/profile 数量。通用原生构建与定向回归已通过，适用环境及未覆盖项见[通用验证范围](script-runtime.md#构建发布与验证边界)。

## CatNode 媒体交接边界

以下接缝属于上文链接的 CatNode owner，不给 source CatVod Spider 增加监听端口或 Node 能力。无头 Cat 媒体直接使用返回的正确 proxy 地址；需要请求头时沿影视 Node 媒体入口，不要求额外 controlled 标记或 URL 授权 API。取址准备不发起媒体 GET／HEAD 或额外管理请求；业务 scope 继续负责当前 installation／generation 失效与媒体生命周期，不以网络地址许可图代替资源归属。

CatNode aggregate 代理的连接与等待响应头仍受 `requestTimeoutMs` 限制；二进制响应头成功后不再套用脚本绝对60秒截止，按真实背压传输，由客户端断连、来源撤权或 owner close 取消。Range／If-Range、HEAD 与上游状态保留，不整片缓冲。每条上游请求独占连接；停止来源及关闭 owner 等待 ClientRequest、上游响应和 socket 的真实 close，超过有限清理期限明确失败，重复 close 复用原结果。

HLS／DASH 清单是有限控制文档：读取原始输入最多8MiB，读取期间仍受控制请求期限约束，已知超额长度或读取中超额均在发送下游成功响应头前报 `RESOURCE_LIMIT`（HTTP 413），不静默截断。HLS 仅改写真实资源引用，`EXTINF` 自由标题保持原文。DASH 与影视媒体层共用无网络副作用的结构化解析，保留 namespace、继承的 `BaseURL`／`xml:base`、模板及相对引用；按块、受背压输出。

DASH 的模板和准确子资源映射附属于已有 Cat proxy，只用于寻址，不是 URL 访问许可图；普通 suffix 与 query 按原基址解析，正常 HTTP(S) 绝对资源和重定向可用。HLS 先替换 DEFINE／IMPORT／QUERYPARAM 变量再解析 URL，清单重定向保留真实基址和变量上下文。调用输入及当前媒体上下文提供所需头，不借用其它账号或资源凭据；内部 request scope 不发送到外部资源。路由映射随原 endpoint／session 回收，没有第二媒体缓存或授权预算。这些能力不表示支持任意 DASH／DRM 结构。
