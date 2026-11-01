# 共用来源脚本运行时

## 结构

```text
apps/runtime/bootstrap.mjs
  被动装配方法表、双向调用与模块关闭
apps/runtime/modules/music-script.mjs
  LX 暂存、安装身份、版本和原生存储会话
apps/runtime/modules/source-runtime.mjs
  Reader／Cat 暂存、安装身份、动作调用和 profile 回收
packages/source-runtime/
  capability ABI、规则与结果语义、Legado／异次元／轻悦时光／Cat adapter
packages/script-host/
  QuickJS/WASM 承载、guest SDK、实例管理和 Windows Node capability adapter
packages/runtime-bridge/
  有界 JSON 帧、双向 RPC、取消与连接生命周期
apps/windows/runtime/
  Windows 启动参数、命名管道 bootstrap 与薄 entry
```

当前 runtime descriptor：

```text
music@0.5.0  -> music.script.v2
source@0.1.0 -> source.runtime.v1
```

Windows 的 `aggregate` 是唯一 App 的 wire 身份，不是另一套协议。官方账号、目录、曲库、歌词、缓存和播放仍由原生 owner 承担。

## 一套内核与独立协议 adapter

ScriptHost 统一管理 installation、generation、候选更新、取消、预算和回收。协议语义保持独立：

- LX adapter：声明 site、action、quality，并保留 LX 请求/返回模型。
- Reader adapter：Legado 规则核心、异次元旧格式 importer、轻悦时光 HTML/bridge。
- Cat adapter：CatVod JS Spider 动作与虚拟代理路由。

协议更新只修改对应公共 adapter。只有新增 capability ABI 无法表达的系统能力时，平台才需要各自适配。Cat、Reader 和 LX 不拆成三套 Node runner，也不把动作强行合并成一种业务格式。

公共 `@meowfilm/source-runtime` 禁止 `node:*`、`Buffer`、`process`、`require`、真实 JVM 和平台私有 API。Windows 当前由 Node 承载 QuickJS 和 capability adapter；Android/iOS 后续可更换承载方式而复用协议包。共用代码不表示同一个 Node 可执行文件跨平台运行。

## QuickJS 隔离边界

不可信脚本只在独立 QuickJS WebAssembly heap 中执行。每个 installation/generation 独立实例化 WASM 线性内存；编译后的受信模块可复用，但 guest 全局、堆、timer、回调和 profile 不共享。

宿主与 guest 只交换有界 JSON、规范 Base64 和回调 ID。不提供文件、进程、`require`、Node 模块、任意宿主 callable、真实 `Packages.*` 或任意 JVM 类；失败时不回退到 `node:vm`。

默认内存、栈、同步执行片段、请求截止时间、并发、来源字节、DOM 节点、外部脚本和虚拟路由均有上限。取消或超时封闭逻辑结果，但不把不合作 I/O 已收到 signal 当成物理完成；未排空任务继续计入清理债务。

隔离不依赖 AppContainer 或完整虚拟机。Node 子进程提供额外崩溃边界，但网络、编码和加解密 adapter 是受信宿主代码；仍需维护 QuickJS/WASM 和依赖安全更新。

## Capability ABI

来源协议通过 `hostApiVersion = 1` 请求：

```text
http
cookie
storage
encoding
crypto
web interaction
virtual resource
```

ABI 按系统语义命名，不出现 `lxRequest`、`legadoGet` 或 `catStartServer`。完整信封、作用域和平台责任见 [capability ABI](source-capability-abi.md)。

Windows 的 HTTP adapter 允许语法合法的 HTTP(S)，不维护域名白名单；它限制超时、重定向、响应大小和并发，并在跨 origin 重定向时移除敏感请求头。此行为不等于阻断本机或私网。

## LX 请求存储

LX 请求处理器第二参数提供显式特权存储：

```js
lx.on(lx.EVENT_NAMES.request, async (payload, context) => {
  return context.storage.get('key');
});
```

`context.storage` 绑定调用 ID、site、installation、generation 和原生 grant。全局 `meow.storage`、startup、timer 和 background 不继承请求权限；保存的旧 context 在请求结束或代次退休后失效。

Node 的异步上下文只用于记录宿主执行回合，不能推断 guest Promise 权限。存储物理任务占用预算直至 adapter 真正完成。详见 [LX 契约](music-script-runtime.md)。

## Reader 与 Cat

Reader 统一执行搜索、发现、详情、目录、正文、登录状态和图片处理；Legado 外观同步的能力调用通过受控 AST 转换传播 `await`，无法安全转换时明确失败。轻悦时光获得隔离的来源 DOM、常用浏览器 API、jQuery 兼容层和 CryptoJS AES/DES/WordArray 子集；需要真实浏览器的动作转为可选 `web.interact`。详见 [Reader 运行时](reader-source-runtime.md)。

CatVod JS 支持 Spider 的 init/home/category/search/detail/play/proxy/action/destroy 等实际声明动作。`js2Proxy` 使用 generation 绑定的 `meow-source://` 虚拟路由，不允许脚本绑定端口或启动 Node HTTP 服务。详见 [Cat 运行时](cat-source-runtime.md)。

JAR、DEX、TVBox Android JAR、任意动态 JVM 类加载不属于本运行时。

## 生命周期

- stage 只保存有界临时字节，按长度、顺序 Base64、严格 UTF-8 和 SHA-256 校验。
- load/update 使用 revision CAS；候选在发布前失败不改变当前实例。
- source 与 music 使用独立 profile 命名空间；发布后 profile 清理失败会回滚或退休无法证明安全的会话，不能把缺失清理接口视为成功。
- unload/close 回收 QuickJS、timer、DOM registry、Cookie、存储 profile 和虚拟路由。
- close 幂等；成功必须证明本 owner 的实例和物理任务已排空。

## 发布与验收

公共载荷包含 bootstrap、music/source 两个 module、三个 workspace、锁定 QuickJS 引擎，以及 DOM、XPath、解析、编码所需的锁定传递依赖。Windows 只追加命名管道 bootstrap 和薄 entry，完整目录自带 `runtime/node.exe`。

合成测试、离线初始化和真实联网动作是不同证据。外部验收按来源实际声明动作执行，并区分上游、账号/地区配置、宿主能力、引擎兼容、资源限制和清理失败；见[外部来源动态验收](source-acceptance.md)。
