# 共用脚本运行时

## 职责与目录

```text
apps/runtime/bootstrap.mjs
  被动装配方法表、双向调用与模块关闭
apps/runtime/modules/music-script.mjs
  音乐脚本暂存、安装身份、版本与存储会话绑定
packages/script-host/
  实例管理、协议适配、QuickJS 执行、网络/加解密桥接与资源回收
packages/runtime-bridge/
  有界 JSON 帧、双向 RPC、取消与连接生命周期
apps/windows/runtime/
  Windows 启动参数、命名管道 bootstrap 与薄 entry
```

公共层不依赖 Windows 源码、命名管道或原生业务私有实现。Windows entry 导入公共 `createMusicScriptModule`，不保留平台目录中的业务模块副本。官方平台账号、目录、曲库、歌词、缓存和播放仍由原生业务 owner 承担，不因使用共用脚本核心而获得或改变权限。

当前音乐 descriptor 是 `music@0.5.0`，业务响应契约是 `music.script.v2`。Windows 的 `aggregate` 只是当前 wire 身份，不是第二个产品或脚本协议。

## 一套核心与协议适配

当前接入 LX：适配器处理 `lx` 事件、声明的 source/action/quality 和请求返回值；音乐模块处理安装、版本与定向调用。入口显式确定协议和业务归属，不通过作者、文件名、源码哈希或网络域名猜测。

核心可以管理多份隔离实例。当前音乐模块拥有自己创建的 ScriptHost 及其会话，关闭回收这些资源，不销毁其他实例复用的编译模块缓存。协议的业务动作不是额外宿主权限，也不是宿主隔离边界；扩展动作必须核对声明、处理器和实际返回语义，不能因为不是 `musicUrl`／`lyric` 就直接判定不兼容。

后续 Cat 接入使用独立业务 owner 和协议适配器，复用同一实例、隔离、桥接与生命周期机制，不另建 runner，也不把 spider、账号或 Web 管理业务塞进 LX。当前没有 Cat、移动端或 Web 服务的空实现。

这里共用的是运行器代码、协议适配机制与隔离后端，不是同一份 Node 可执行文件。各平台仍负责运行环境的引入／嵌入、进程或容器生命周期、通信和发布。本仓只实现 Windows 的接入，不据此声明 iOS 或 Android 已可运行。

## QuickJS 执行边界

用户脚本只在 QuickJS WebAssembly guest heap 中执行；宿主调用交换受限 JSON 数据，guest 回调通过 ID 引用，不把 Node 对象或任意宿主 callable 直接交给脚本。不提供任意文件、进程、`require` 或宿主模块加载接口，也不在失败时回退到 `node:vm`。

桥接控制参数使用预先捕获的序列化方法和无原型信封，脚本修改 `JSON.stringify` 或继承的 `toJSON` 不能篡改内部请求身份；业务数据仍按 JSON 语义传递，不通过冻结脚本全局原型限制正常执行。

受信 `quickjs-engine.js` 通过 `node:fs/promises` 与 `import.meta.resolve('@jitl/quickjs-wasmfile-release-sync/wasm')` 读取打包内的锁定引擎文件；这不向 guest 授予文件访问。`WebAssembly.compile` 编译模块只执行一次并缓存复用，但每个 installation／generation 独立实例化 WASM，不共享线性内存，避免某个堆或模块损坏影响其他实例。

默认 `maxMemoryBytes` 为 64 MiB，`maxStackBytes` 为 64 KiB，引擎拒绝超过 64 KiB 的栈限额。guest 栈必须受 WASM C 栈边界约束，不能通过放宽到 512 KiB 让混淆脚本越过引擎栈。实例还受同步执行片段、请求截止时间、回调与未结束任务数量限制。取消、超时或卸载 fence 当前结果并回收所属实例；不把取消信号当作不合作外部 I/O 已完成的证明，无法排空时明确报告清理失败。

隔离不依赖 AppContainer、完整虚拟机或 Node permission model；宿主网络／加解密适配是受信代码，不属于 guest 沙箱。JS 引擎/WASM 仍需维护与安全更新，不能承诺抵御引擎本身的漏洞。

## v2 特权存储

LX 请求处理器的第二参数提供显式请求能力：

```js
lx.on(lx.EVENT_NAMES.request, async (payload, context) => {
  const value = await context.storage.get('key');
  return value;
});
```

- `context.storage.get/set/delete` 绑定创建它的调用 ID、site、installation 和 generation，并核对显式 `permissions.storage` 的 read/write 权限。
- 绑定在 guest `await` 后仍指向同一个调用；请求结束、取消或代次退休后，保存的引用不能借用后来调用的身份。
- 全局 `meow.storage` 没有权限；startup、background 不会自动获得或继承请求存储权限，即使另一个请求正在执行。
- QuickJS 没有 Node `AsyncLocalStorage` 的异步上下文钩子，因此不能用宿主的 ambient async scope 推断 guest Promise 的特权。显式 `context.storage` 是 v2 公开行为变化，不提供全局存储的兼容回退。

存储实例与业务作用域仍必须隔离。音乐宿主的原生存储回调与安装会话绑定见 [音乐脚本契约](music-script-runtime.md)；这里的存储 token 不是域名授权或网络隔离机制。

存储物理任务归属实例及请求直至 adapter 真正完成；取消只撤销结果和 guest 回调引用，不提前遗弃不合作 I/O。已完成任务即时释放，不累计历史回调，也不设置全局／实例／site 请求数额度。

Node 的异步上下文只记录宿主执行回合，不能当作 guest Promise 的权限传播。存储必须使用显式请求 context；网络／定时器的执行回合归属不授予特权。取消结果、网络截止时间、实例退休与物理排空分别核对，不把会话后台请求误报为已在初始化期间完成的请求。

## 网络边界

Node 网络适配器接受：

```js
network({ url, options, signal, scope, validateURL })
```

`validateURL` 检查 HTTP(S) URL 语法并拒绝 URL 中的用户信息，初始地址和每次重定向都重新检查；它不是域名或私网授权判断。接口不再叫 `authorizeURL`，安装记录也不维护 origin 白名单或按源码 SHA-256 补域名的策略。源码哈希仍用于脚本字节完整性与版本身份，不能误删其他业务的数据完整性校验。

脚本网络可以访问已开放的 HTTP(S) 服务，包括本机和私网地址。受控网络桥接保留截止时间、取消、响应大小、重定向上限及跨 origin 不转发 Authorization/Cookie 的行为，但这些不构成网络隔离。文件／进程隔离、Windows 命名管道身份检查和 QuickJS guest heap 都不能冒称网络沙箱；其他官方业务的网络或播放授权规则不受影响。

网络物理任务通过 `adapterTasks` 跟踪至 adapter 真正结束，不设置 `maxNetworkRequests` 数量额度；取消只回收结果和回调句柄，不伪造已排空。`followRedirect` 接受布尔值，false 禁止自动跳转，true 仍受 `follow_max`／宿主上限约束。`retry` 扩展字段兼容接受但不触发宿主自动重放，重试由脚本负责，不叠加非幂等请求的重放。

## 后续按需 Web 能力

当前不创建固定 HTTP 入口。以后 Cat 确实需要 Web 管理时，将服务作为按需宿主能力接入：地址和服务资源绑定 installation 与运行 generation，经现有通信通道交给应用，停用、卸载、更新或关闭时回收。不引入另一套脚本 runner，不把用户配置或服务句柄暴露为任意宿主对象。

## 验证与发布

打包闭包为公共 bootstrap、共享音乐模块、runtime-bridge、script-host 和锁定的 release-sync 引擎依赖；Windows 只补齐启动／管道 bootstrap 和薄 entry。WASM 的精确允许路径、来源、manifest／许可／文件哈希见 [Windows 打包](../windows/runtime-packaging.md)。

引擎初始化、合成 LX 回归、Windows 管道集成与外部真实脚本的联网业务验收分别记录。语法或离线初始化通过不能代替声明动作、异步网络、加解密、取消和真实业务数据验收。外部脚本报告和退出条件见 [动态验收](source-acceptance.md)；任务进度及某批样本的数值写入临时工作记录，不作为协议或源码版本冻结条件。
