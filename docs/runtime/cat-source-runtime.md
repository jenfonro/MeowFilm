# source CatVod Spider 运行时

本文件描述 `@meowfilm/source-runtime` 中原 portable CatVod adapter 的独立 Spider 输入，由 `source@0.1.0`／`source.runtime.v1` 的 `protocol: cat` 接纳。它与 Reader 共用[来源 capability ABI](source-capability-abi.md)，并与 LX、CatNode 复用 script-host 的同一 WorkerSession、worker 协议和 QuickJS 后端，不另建 runner。

完整 CatNode 输入属于 [`cat@0.1.0`／`cat.script.v1`](cat-script-runtime.md)：包含有限 Node 兼容、HTTP 服务、direct Spider、私有卷和管理同步。本 source adapter 只执行 Spider；即使两个入口都能调用 Spider 动作，也不能将这里的支持范围冒充为 CatNode，或将 Node 兼容层写成无 Node 可运行。

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
| `proxy` | `proxy` |
| `action` | `action` |
| `destroy` | `destroy` |

返回字符串若是 JSON 对象或数组会解析为结构化值；其他返回值按 JSON 边界透传。BigInt、循环结构或抛错 getter 明确报 `SCRIPT_EXCEPTION`，不等待动作超时。

## Guest SDK

QuickJS realm 提供受限 `Buffer`、URL、定时器、HTTP helper、作用域存储、DOM helper 和 CryptoJS 常用摘要/HMAC/AES/DES 子集。Cat 源先以 Acorn AST 识别并改写真实模块语法；注释或字符串中的 `require(`、`process.`、`import(` 不会误报。真实静态/动态 import、外部 re-export、`import.meta`、`require(...)` 和 Node/Deno/Bun 成员调用明确拒绝。

脚本在初始化期保存的 `req`、`local`、DOM helper 或 `js2Proxy` 不保留 startup 权限；后续只在当前动作的 task scope 内执行。动作结束后再次使用旧异步回调会因 scope 失效而失败。

`setInterval` 持续执行至显式清除或所属动作结束；动作完成、取消、超时和 session close 均释放真实 timer 句柄及 guest 回调。`fetch` 的 ArrayBuffer／typed-array 请求体按原始视图字节发送。

外部依赖不能通过 npm 或系统路径加载。需要新增兼容 helper 时，应在 source CatVod adapter／guest SDK 中实现并加入平台无关测试；不能让脚本绕过 ABI 调用 Node。该限制属于 source 输入契约，不限制 CatNode 契约中已实现的受控 `require`。

## 虚拟代理

`js2Proxy` 生成 `meow-source://<profile>/<route>`，路由元数据通过 `virtual.register` 绑定当前 generation。应用可将该地址映射到内部资源处理；source 脚本不能调用 `http.createServer`、监听端口或获得命名管道。CatNode 的回环服务、真实端口映射和私有卷继续遵循其自身契约。

`proxy` 动作收到虚拟 URL 时，运行时解析为已登记的原 URL、headers 和 site key。更新、卸载或关闭会注销全部路由；注销失败使清理失败，不保留可误用的旧 generation。

## 排除项

本输入只支持 CatVod JS Spider。TVBox Android JAR／DEX、任意 JVM 插件和 Android API 不在当前范围，也不提供 CatNode 的文件、服务和管理 API。Node-independent 仅指 source-runtime 纯规则包，当前 Windows 执行仍由 Node 中的 worker 承载；本次不新增其他平台接入。

上传、控制帧和 CAS 规则与[Reader 来源入口](reader-source-runtime.md#生命周期与-rpc)相同；源码/heap/栈、60 秒初始化与请求、3 秒清理、worker 终止及 OOM 重启遵循[共用运行时](script-runtime.md#quickjs-执行边界)。虚拟路由、状态与响应保留字节预算，不设实例、并发、timer、网络或 profile 固定计数门槛。
