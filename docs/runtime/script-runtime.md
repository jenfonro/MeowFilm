# 共用脚本运行时

## 职责与目录

```text
apps/runtime/bootstrap.mjs
  被动装配方法表、双向调用与模块关闭
apps/runtime/modules/music-script.mjs
  音乐源码／控制参数／结果分片、安装身份、版本与存储会话绑定
apps/runtime/modules/cat-script.mjs
  Cat 源码/结果分片、安装、设置刷新、调用与管理同步
packages/script-host/
  实例管理、LX/Cat 适配、有限 Node 风格能力、QuickJS 执行与资源回收
packages/runtime-bridge/
  有界 JSON 帧、双向 RPC、取消与连接生命周期
apps/windows/runtime/
  Windows 启动参数、命名管道 bootstrap 与薄 entry
```

公共层不依赖 Windows 源码、命名管道或原生业务私有实现。Windows entry 导入公共 `createMusicScriptModule` 与 `createCatScriptModule`，不保留平台目录中的业务模块副本。官方平台账号、目录、曲库、歌词、缓存和播放仍由原生业务 owner 承担，不因使用共用脚本核心而获得或改变权限。

当前 descriptor 为 `music@0.7.0` 与 `cat@0.1.0`，业务响应契约分别是 `music.script.v2` 与 `cat.script.v1`。Windows 的 `aggregate` 只是当前 wire 身份，不是第二个产品或脚本协议。

## 一套核心与协议适配

LX 适配器处理 `lx` 事件、声明的 source/action/quality 和请求返回值；音乐模块处理安装、版本与定向调用。Cat 适配器处理脚本启动、配置站点、HTTP 动作、私有卷、受控服务、标准 Cat 输出和管理协议。入口显式确定协议和业务归属，不通过作者、文件名、源码哈希或网络域名猜测。

核心可以管理多份隔离实例。当前音乐模块拥有自己创建的 ScriptHost 及其会话，关闭回收这些资源，不销毁其他实例复用的编译模块缓存。协议的业务动作不是额外宿主权限，也不是宿主隔离边界；扩展动作必须核对声明、处理器和实际返回语义，不能因为不是 `musicUrl`／`lyric` 就直接判定不兼容。

Cat 使用独立业务 owner 和协议适配器，复用同一进程、隔离、桥接与生命周期机制，不另建 runner，也不把 spider、账号或 Web 管理业务塞进 LX。公共核心可由其他平台接入，但本仓当前只完成 Windows 的进程、通信和发布装配。

这里共用的是运行器代码、协议适配机制与隔离后端，不是同一份 Node 可执行文件。各平台仍负责运行环境的引入／嵌入、进程或容器生命周期、通信和发布。本仓只实现 Windows 的接入，不据此声明 iOS 或 Android 已可运行。

## QuickJS 执行边界

用户脚本只在 QuickJS WebAssembly guest heap 中执行；宿主调用交换 JSON 结构和独立字节帧，guest 回调通过 ID 引用，不把 Node 对象或任意宿主 callable 直接交给脚本。不提供任意文件、进程、`require` 或宿主模块加载接口，也不在失败时回退到 `node:vm`。

每个启用的 LX／Cat installation 由独立 `worker_thread` 执行会话和 QuickJS；ScriptHost 记录、Cat 网关及平台适配器留在主线程。编译后的 `WebAssembly.Module` 由主线程共享给 worker，各实例不共享堆。调用等待统一为 60 秒；到期后探测对应 worker，清理预算内无响应才终止并报告 `TIMEOUT`，不自动重启卡死实例、不设单次同步执行时限或后台心跳。停用、卸载、更新和关闭先触达正在初始化的真实所有者，再等待串行事务和真实线程退出。`stats()` 同步返回最近事实快照，并按读取需求合并发起一次异步刷新，不同步阻塞 worker。

运行器的 60 秒不覆盖平台调用方自己的等待策略：runtime-bridge 默认 30 秒及 Windows C# 调用方超时保持不变，应用调用可能先结束等待。

桥接控制参数使用预先捕获的序列化方法和无原型信封，脚本修改 `JSON.stringify` 或继承的 `toJSON` 不能篡改内部请求身份；业务数据仍按 JSON 语义传递，不通过冻结脚本全局原型限制正常执行。

受信 `quickjs-engine.js` 通过 `node:fs/promises` 与 `import.meta.resolve('@jitl/quickjs-wasmfile-release-sync/wasm')` 读取打包内的锁定引擎文件；这不向 guest 授予文件访问。`WebAssembly.compile` 编译模块只执行一次并缓存复用，但每个 installation／generation 独立实例化 WASM，不共享线性内存，避免某个堆或模块损坏影响其他实例。

ScriptHost 在 `host.js` 一处定义脚本层限制，LX 与 Cat 同值且按单个脚本计：源码最多 16 MiB（UTF-8 字节）、QuickJS 栈 256 KiB、单脚本内存上限 `maxMemoryBytes`。脚本运行器不设对外单次消息或结果字节上限；QuickJS 内部桥接与脚本内数据处理只受单脚本内存上限约束。宿主整体缓冲且不受 guest 堆约束的位置以 `maxMemoryBytes` 为上界：Cat 聚合入口请求体、Cat 脚本 listener 响应汇总、LX 宿主网络响应体及解压输出；超出报 `RESOURCE_LIMIT`，请求失败而实例继续服务。`maxMemoryBytes` 默认为 256 MiB；依据是 11 份真实脚本的 QuickJS 单脚本峰值约 77 MiB，默认值约为该峰值的 3.3 倍。所有协议均启用 `setMemoryLimit`，超限即判该实例失败。已就绪实例在运行中超限时由 ScriptHost 自动以同一 revision 重启一次（保留私有卷并记录结构化诊断）；初始化超限或重启后再次初始化超限停在 failed，不循环。用户 enable／update 后重启计数复位。LX 与 Cat 的大控制参数／结果均走 64 KiB 字节分片，不设独立逻辑总量上限；原生桥接的 1 MiB 物理帧边界不变。LX 小请求和结果保持原有业务信封，大数据由传输适配层透明还原，见 [LX 传输契约](music-script-runtime.md#控制数据传输)。Cat 原生桥的字节参数与返回值均以 ArrayBuffer 帧传输，JSON 只描述结构和帧索引。不按实例、timer、网络、in-flight、storage、method pending 等固定数量配额拒绝工作，资源按请求和 session 生命周期回收。Cat 的 `os.totalmem()` 返回单脚本内存上限，`os.freemem()` 为上限减当前 guest 堆占用；其余 `os` 字段仍是兼容元数据。公共引擎拒绝超过 1 MiB 的栈配置。取消、超时或卸载 fence 当前结果并按请求、服务和 session 生命周期回收资源；不把取消信号当作不合作外部 I/O 已完成的证明，无法排空时明确报告清理失败。

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

LX 的 `validateURL` 检查 HTTP(S) URL 语法并拒绝 URL 中的用户信息，初始地址和每次重定向都重新检查；它不是域名或私网授权判断。接口不再叫 `authorizeURL`，安装记录也不维护 origin 白名单或按源码 SHA-256 补域名的策略。源码哈希可用于导入文件引用、更新标识及本次分片传输完整性；激活读取保存的源码不比较历史内容哈希，编辑后可重新启用或重启执行。其他业务的并发 CAS 和格式检查不因此移除。

LX 网络可以访问已开放的 HTTP(S) 服务，包括本机和私网地址；其受控桥接保留截止时间、取消、响应大小、重定向上限及跨 origin 不转发 Authorization/Cookie 的行为。

Cat 不维护域名授权名单，也不按公网／私网／链路本地／回环或局域网域名后缀过滤目标；DNS 答案直接返回。每个实例在独立 worker thread 中执行；实例已登记的 listener 端口和别名映射到该实例的实际回环端点，并附加实例访问头与请求作用域；未登记的回环端口直接连接真实 `127.0.0.1:port`，不附加实例凭据。HTTP、HTTPS、net、TLS 自调用仍核对 listener 协议。`socketPath`、fd 与 guest 自定义 `lookup`／`createConnection` 仍拒绝；平台网络适配器存在时 DNS 不可用。完整规则见 [Cat 契约](cat-script-runtime.md)。

LX 网络任务在 adapter 真正结束前保持登记；取消只回收结果和回调句柄，不设固定任务数量预算。`followRedirect` 接受布尔值，false 禁止自动跳转，true 仍受 `follow_max`／LX 宿主上限约束；`follow_max` 超过上限时按上限截断。`retry` 扩展字段兼容接受但不触发宿主自动重放，重试由脚本负责，不叠加非幂等请求的重放。Cat 同样按请求和 session 生命周期回收实际网络资源。

## Cat 回环服务与持久卷

Cat 安装启用后按需创建回环服务。每个脚本监听器由运行器分配实际端口并绑定 installation/generation；管理页面直接使用该实例端口，聚合入口只分派 Cat 业务请求及脚本代理 URL。停用、更新或关闭时回收端口和句柄并保留私有卷；卸载或从配置移除时在实例停止、资源回收后删除私有卷和不再被引用的源码，同一 id 重装从空卷开始，删除失败进入 `failures`，启动时不扫描残留卷。

## 验证与发布

打包闭包为公共 bootstrap、共享 music/Cat 模块、runtime-bridge、script-host、有限 Node 兼容依赖和锁定的 release-sync 引擎；Windows 只补齐启动／管道 bootstrap 和薄 entry。WASM 的精确允许路径、来源、manifest／许可／文件哈希见 [Windows 打包](../windows/runtime-packaging.md)。

引擎初始化、合成 LX 回归、Windows 管道集成与外部真实脚本的联网业务验收分别记录。语法或离线初始化通过不能代替声明动作、异步网络、加解密、取消和真实业务数据验收。外部脚本报告和退出条件见 [动态验收](source-acceptance.md)；任务进度及某批样本的数值写入临时工作记录，不作为协议或源码版本冻结条件。
