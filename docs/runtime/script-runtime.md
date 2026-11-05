# 共用脚本运行时

## 职责与目录

```text
apps/runtime/bootstrap.mjs
  被动装配方法表、双向调用与模块关闭
apps/runtime/modules/music-script.mjs
  音乐源码／控制参数／结果分片、安装身份、版本与存储会话绑定
apps/runtime/modules/cat-script.mjs
  Cat 源码/结果分片、安装、设置刷新、调用与管理同步
apps/runtime/modules/source-runtime.mjs
  Reader/CatVod 来源暂存、版本绑定、调用与 profile 退休
packages/script-host/
  共用 WorkerSession、LX/CatNode/portable 来源适配、QuickJS 与资源回收
packages/source-runtime/
  Node 无关的 Reader 规则、CatVod Spider adapter 与来源 capability ABI
packages/runtime-bridge/
  有界 JSON 帧、双向 RPC、取消与连接生命周期
apps/windows/runtime/
  Windows 启动参数、命名管道 bootstrap 与薄 entry
apps/ios/runtime/
  iOS fd 启动参数、同进程失败边界与薄 entry
```

公共层不依赖 Windows 源码、命名管道或原生业务私有实现。Windows entry 导入公共 `createMusicScriptModule`、`createCatScriptModule` 与 `createSourceRuntimeModule`，不保留平台目录中的业务模块副本。官方平台账号、目录、曲库、歌词、缓存和播放仍由原生业务 owner 承担，不因使用共用脚本核心而获得或改变权限。

Windows 装配 `music, cat, source`，iOS 装配 `music, cat`：

| descriptor | 响应契约 | 输入与职责 |
| --- | --- | --- |
| `music@0.7.0` | `music.script.v2` | 原始 LX 音源 |
| `cat@0.1.0` | `cat.script.v1` | CatNode 的有限 Node 兼容、HTTP 服务、direct Spider、私有卷与管理同步 |
| `source@0.1.0` | `source.runtime.v1` | Legado、异次元、轻悦时光及独立 CatVod Spider 输入 |

Windows 的 `aggregate` 只是当前 wire 身份，不是第二个产品或脚本协议。

## 一套核心与协议适配

LX 适配器处理 `lx` 事件、声明的 source/action/quality 和请求返回值；音乐模块处理安装、版本与定向调用。CatNode 适配器处理脚本启动、配置站点、HTTP 动作、私有卷、受控服务、标准 Cat 输出和管理协议。source adapter 处理 Reader 规则流程及 CatVod Spider。入口显式确定协议和业务归属，不通过作者、文件名、源码哈希或网络域名猜测。

核心可以管理多份隔离实例。当前音乐模块拥有自己创建的 ScriptHost 及其会话，关闭回收这些资源，不销毁其他实例复用的编译模块缓存。协议的业务动作不是额外宿主权限，也不是宿主隔离边界；扩展动作必须核对声明、处理器和实际返回语义，不能因为不是 `musicUrl`／`lyric` 就直接判定不兼容。

CatNode 与 source 使用各自业务 owner，复用同一进程、WorkerSession、worker 协议和 QuickJS 后端，不另建 runner，也不把 Spider、账号或 Web 管理业务塞进 LX。`source.runtime.v1` 的 `cat` 只表示 portable CatVod Spider 输入；它不提供 `cat.script.v1` 的 Node 兼容、私有卷和服务能力，也不能代替该契约。

Node-independent 只指 `@meowfilm/source-runtime` 的纯规则与 ABI 包：不导入 `node:*` 或 Node／平台全局。script-host 的 worker、QuickJS/WASM 加载和 CatNode 兼容层仍由 Node 承载，不声称其能无 Node 运行。来源能力的跨平台语义以[capability ABI](source-capability-abi.md)为唯一来源；LX 原有网络/存储与 CatNode 能力不改走这套 ABI。

这里共用的是运行器代码、协议适配机制与 QuickJS 适配，不是同一份 Node 可执行文件。各平台仍负责运行环境的引入／嵌入、进程或容器生命周期、通信和发布。Windows 使用子进程／命名管道；iOS 使用 App 内嵌 NodeMobile.framework／socketpair，`MeowRuntime` 提供通用帧与启动机制，`NativeMusicScriptService` 持有音源文档和授权。iOS 产品入口见[音源管理](../ios/music.md#音源管理)，诊断流程与未覆盖边界见[内嵌 Node 预检](../ios/runtime-preflight.md)。不据此声明 Android 已接入，也不新增猫源／阅读 UI。

公共包的运行时 API 基线包含 Node 18.20.4；根工作区的构建工具仍使用 Node 24，Windows 随包 Node 锁定不变。iOS 采用 Node Mobile 18.20.4 与原版 release-sync asmjs QuickJS，只装配 `music`、`cat`：原版 asmjs 没有任务上下文入口，Node Mobile 也不带 ICU，`source` 不在该装配中。其他平台继续要求可用的 WebAssembly，不因缺失或报错自动降级。Node 18 已结束上游维护，兼容性测试不能代替安全更新。

公共帧、逻辑 JSON 值与源码的严格 UTF-8 检查不依赖 ICU：先验证 UTF-8 字节，再解码；源码保留原有的字节往返一致性及协议各自的 BOM 规则。私有卷写入使用显式 fsync，不依赖较新 Node 才实现的 `flush` 选项。

## QuickJS 执行边界

用户脚本只在独立 QuickJS guest heap 中执行，桌面为 WASM，iOS 为 asmjs 数字堆；宿主调用交换 JSON 结构和独立字节帧，guest 回调通过 ID 引用，不把 Node 对象或任意宿主 callable 直接交给脚本。不提供任意文件、进程、`require` 或宿主模块加载接口，也不在失败时回退到宿主 `eval` 或 `node:vm`。

每个启用的 LX、CatNode 或 source installation 通过同一 WorkerSession 在独立 `worker_thread` 中执行会话和 QuickJS；ScriptHost 记录、Cat 网关及平台 capability 物理任务留在主线程。WASM 后端编译后的 `WebAssembly.Module` 由主线程共享给 worker；iOS 不编译或传递 WASM，直接在 worker 内加载 asmjs。两种后端的实例均不共享 guest 堆。guest 卡住不阻塞主线程或其他实例。调用等待默认为 60 秒；source 初始化也为 60 秒、清理为 3 秒，LX 保持自身初始化/清理时限。到期后探测对应 worker，清理预算内无响应才终止并报告 `TIMEOUT`，不自动重启卡死实例、不设单次同步执行时限或后台心跳。停用、卸载、更新和关闭先触达正在初始化的真实所有者，再等待串行事务和真实线程退出。`stats()` 同步返回最近事实快照，并按读取需求合并发起一次异步刷新，不同步阻塞 worker。

运行器的 60 秒不覆盖平台调用方自己的等待策略：runtime-bridge 默认 30 秒及 Windows C# 调用方超时保持不变，应用调用可能先结束等待。

桥接控制参数使用预先捕获的序列化方法和无原型信封，脚本修改 `JSON.stringify` 或继承的 `toJSON` 不能篡改内部请求身份；业务数据仍按 JSON 语义传递，不通过冻结脚本全局原型限制正常执行。

受信 `quickjs-module.js` 按真实 `process.platform` 选择固定后端，不接受脚本提供的后端选项。非 iOS 读取 script-host 内 `src/quickjs/emscripten-module.mjs`／`.wasm` 的任务上下文派生产物；npm core／FFI／release-sync 保持原版 `0.32.0`。固定源码、补丁、工具链、产物与许可校验见 [Windows 打包](../windows/runtime-packaging.md#受信-quickjs-wasm)。文件读取不向 guest 授予文件访问。`WebAssembly.compile` 编译模块只执行一次并缓存复用，但每个 installation／generation 独立实例化 WASM，不共享线性内存，避免某个堆或模块损坏影响其他实例。iOS 动态加载锁定的原版 asmjs variant，每次独立创建模块与数字堆，不把用户源码交给 Node 执行。

派生引擎在 Promise reaction、原生 await、异步生成器请求及 FinalizationRegistry 登记时保存 SDK 的任务身份，在执行期间切入并于返回／异常后恢复；不包装公开 `Promise.prototype.then`，不新增微任务或改变排队顺序。portable 来源以此保留旧异步工作的身份，不能借后续动作发 capability；原生入口仅供可信 SDK 初始化捕获，任何用户代码执行前从全局删除。该机制不改变 LX 的显式特权存储契约，也不授予 CatNode 新权限。

ScriptHost 在 `host.js` 的 `SCRIPT_LIMITS` 一处定义脚本层限制，LX、CatNode 与 source 同值且按单个脚本计：源码最多 16 MiB（UTF-8 字节）、QuickJS 栈 256 KiB、单脚本内存上限 `maxMemoryBytes`。脚本运行器不设对外单次消息或结果字节上限；QuickJS 内部桥接与脚本内数据处理只受单脚本内存上限约束。宿主整体缓冲且不受 guest 堆约束的位置以 `maxMemoryBytes` 为上界：Cat 聚合入口请求体、Cat 脚本 listener 响应汇总、LX 宿主网络响应体及解压输出；超出报 `RESOURCE_LIMIT`，请求失败而实例继续服务。source ABI 的请求/响应字节同样受单脚本内存预算约束，错误按 `source.runtime.v1` 契约返回。`maxMemoryBytes` 默认为 256 MiB；依据是 11 份真实脚本的 QuickJS 单脚本峰值约 77 MiB，默认值约为该峰值的 3.3 倍。所有协议均启用 `setMemoryLimit`，超限即判该实例失败。已就绪实例在运行中超限时由 ScriptHost 自动以同一 revision 重启一次（保留 CatNode 私有卷并记录结构化诊断）；初始化超限或重启后再次初始化超限停在 failed，不循环。用户 enable／update 后重启计数复位。

LX 与 Cat 的大控制参数／结果均走 64 KiB 字节分片，不设独立逻辑总量上限；原生桥接的 1 MiB 物理帧边界不变。LX 小请求和结果保持原有业务信封，大数据由传输适配层透明还原，见 [LX 传输契约](music-script-runtime.md#控制数据传输)。source 控制请求和响应各限 1 MiB，源码以 64 KiB 分片上传，暂存 TTL 为 5 分钟并由主动 timer 回收，不设 token 数量门槛。Cat 原生桥的字节参数与返回值均以 ArrayBuffer 帧传输，JSON 只描述结构和帧索引。线协议帧与上传分片大小不等于源码、网络响应或 guest 堆的大小上限。

不按实例、timer、网络、in-flight、storage、method pending、stage token 或 profile 固定数量配额拒绝工作，资源按请求和 session 生命周期回收。Reader 的 DOM 文档/节点/字符串、分页和规则解析预算仍是明确的解析资源边界，ABI JSON、HTTP 语法、重定向和截止时间仍是数据/协议边界。CatNode 的 `os.totalmem()` 返回单脚本内存上限，`os.freemem()` 为上限减当前 guest 堆占用；其余 `os` 字段仍是兼容元数据。公共引擎拒绝超过 1 MiB 的栈配置。取消、超时或卸载 fence 当前结果并按请求、服务和 session 生命周期回收资源；不把取消信号当作不合作外部 I/O 已完成的证明，无法排空时明确报告清理失败。source 的能力与 profile 退休规则见[capability ABI](source-capability-abi.md)。

asmjs 保留独立数字堆、受控桥及 Worker 终止，但不具备 WASM 完整的越界／非法间接调用 trap；iOS 已接受此差异，不宣称两种后端具有相同故障隔离。iOS 系统沙箱限制 App 对系统和其他 App 的访问，不能替代本 App 内的数据、网络与资源防护。

脚本边界不依赖 AppContainer、完整虚拟机或 Node permission model；宿主网络／加解密适配是受信代码，不属于 guest 沙箱。JS 引擎仍需维护与安全更新，不能承诺抵御引擎本身的漏洞。

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
- Node `AsyncLocalStorage` 不能推断 guest Promise 的特权；派生引擎的任务身份也不授予 LX ambient 存储权限。显式 `context.storage` 是 v2 公开行为变化，不提供全局存储的兼容回退。

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

打包闭包为公共 bootstrap、按装配选择的共享模块、runtime-bridge、script-host、source-runtime、有限 Node 兼容与规则解析依赖，以及锁定的 release-sync 引擎。`windows-native` 装配 music/Cat/source，含原 npm 与任务上下文派生的两份 WASM；`ios-embedded` 装配 music/Cat，仅含原版 asmjs，不复制派生 WASM pair。两者都核对 `scripts/quickjs-runtime.json` 中的来源、manifest／许可和引擎文件哈希，不扩展其他 native/WASM 放行范围。Windows 补齐启动／管道 bootstrap 和薄 entry，见 [Windows 打包](../windows/runtime-packaging.md)；iOS 载荷及诊断宿主见 [内嵌 Node 预检](../ios/runtime-preflight.md)。

引擎初始化、合成 LX 回归、Windows 管道集成与外部真实脚本的联网业务验收分别记录。语法或离线初始化通过不能代替声明动作、异步网络、加解密、取消和真实业务数据验收。外部脚本报告和退出条件见 [动态验收](source-acceptance.md)；任务进度及某批样本的数值写入临时工作记录，不作为协议或源码版本冻结条件。
