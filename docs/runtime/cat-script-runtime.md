# 共用 Cat 脚本契约

## 身份与工厂

实现位于 `apps/runtime/modules/cat-script.mjs` 与 `packages/script-host/src/cat-*.js`：

```text
id: cat
version: 0.2.0
contract: cat.script.v2
```

`createCatScriptModule({dataRoot})` 接收受信调用方选定的稳定绝对数据目录。公共独立入口见 [Runtime HTTP](runtime-bridge.md)；模块负责完整源码候选、设置、安装、Node Worker、私有卷、服务端口、Cat 调用和关闭。Windows/iOS 的旧接入本轮未迁移，不提供旧 wire 兼容。

## HTTP 与完整导入

逻辑方法均以 `cat.script.` 为前缀，由公共客户端映射为 `POST /v1/cat/{operation}`：

| operation | 作用 |
| --- | --- |
| `start/status` | 启动运行器并读取当前设置，或返回当前状态；`start` 不等待各脚本实例就绪 |
| `refresh` | 重新读取同一设置文件，校验设置结构后按目标实例应用 |
| `install/enable/uninstall` | 保存源码、更新设置并管理一个 installation |
| `invoke` | 定向调用 installation/generation/site/action |
| `management.describe/sync` | 探测脚本原生管理协议并同步一个明确 provider |

源码一次 `PUT /v1/cat/sources/{installationId}/{sourceRevision}`，body 为 `application/octet-stream`，不需要 basename；仅完整、有效的上传成为候选。`install` 指定 installationId/sourceRevision，config 直接为 object，version/enabled 沿现有默认值，不增加 Cat revision CAS。`invoke.payload` 和 `management.sync.credentials` 直接为 object，不接受 JSON 字符串替代；invoke 的 generation 可选，payload 缺省空 object。

普通请求一次 JSON POST，成功 `{ok:true,contract:"cat.script.v2",value}`；value 是原业务对象、数组、标量或 null。没有普通 JSON 大小分流、总字节配额、正文压缩或自动加工；旧 stage/control/blob、Base64、分片 token、TTL、offset 和传输 checksum 已删除。HTTP 取消和实际排空见 [公共传输](runtime-bridge.md#取消反向能力与清理)。

源码保持 1 至 16 MiB、严格无 BOM UTF-8、非空。同 installation/sourceRevision 的仍持有内容幂等，内容冲突报 INVALID_ARGUMENT，不存在版本报 NOT_FOUND，编码错误报 SCRIPT_PROTOCOL_ERROR，超限报 RESOURCE_LIMIT。新完整上传只替换尚未采用候选，部分上传不能覆盖；字段前置拒绝不消费，安装取入后失败释放，重试须显式上传。当前执行版本、已取入操作及未采用候选各由实际所有者持有，不缓存全版本历史；卸载/关闭释放候选。

`install` 将实际给定源码原子写入普通文件 `sources/<importhash>.js`，设置的 `sourceHash` 只作为导入时的文件引用和内容标识；上传生成的可信内容 hash 在安装内复用，不接受用户提交 contentHash。不加密源码，不保护其字节不可变，读取时不比较历史 hash 或导入长度。显式重新导入会原子替换该文件，即使设置和导入内容标识未变也重新应用目标实例。不存在旧格式迁移或内联源码回退。

## 设置文件

`<dataRoot>/settings.json` 是唯一持久设置事实来源：

```json
{
  "version": 1,
  "port": 0,
  "providers": {
    "baidu": true,
    "quark": true,
    "uc": true,
    "tianyi": true,
    "yidong": true
  },
  "mappings": [
    { "provider": "quark", "aliases": ["夸克"] }
  ],
  "scripts": [
    {
      "installationId": "source-1",
      "sourceHash": "<sha256>",
      "enabled": true,
      "version": "1.0.0",
      "config": {}
    }
  ]
}
```

`port=0` 由系统选择聚合端口。缺省 provider 表示未启用对应拦截。客户端可原子改写该文件后调用 `refresh`；刷新先校验设置结构，设置损坏时不发布部分状态、不覆盖文件，也不替换当前有效运行快照。有效设置只对配置变更、新增、移除或失败的目标实例应用，未变的正常实例不重读源码、不重新启动。provider、映射和聚合端口变更通过设置快照发布，不要求全量重读源码。冷启动设置损坏明确返回存储错误。

`start` 完成数据根、设置读取和聚合 server 监听后返回与 `status` 相同的快照，各已启用脚本随后在各自 Worker 启动；慢实例不阻止另一实例或 status，不增加并发数量配置。每个目标实例启动时读取一次当前源码，按实际字节检查 16 MiB 上限并解码，不做全量源码预检；禁用实例不读取源码，也无需创建 host revision。源码缺失、非法编码、超限或引擎语法/执行错误只使对应实例失败并列入 `failures`，不阻止聚合入口或其他实例就绪。合法手工编辑即使改变长度，也可在停用后重新启用或进程重启时运行；运行中不监视文件变化。

每个 enabled installation 使用独立 worker thread 直接运行 CatSession、Node 脚本、私有卷访问和 listener；主线程保留 ScriptHost 记录、CatRuntime 网关、设置文件与平台适配器。实例就绪即可调用；尚未启动或正在启动的实例在 `status` 中报告 `starting`，对它的 `invoke`、管理和会话访问立即返回 `STARTING`，不隐藏等待；聚合 HTTP 入口对 `STARTING` 返回 503。禁用实例的调用和管理访问返回 `DISABLED`。同 installation 的安装、启用、刷新目标和卸载有序，设置提交按发布顺序处理；不以全局初始化队列阻塞其他 installation。与目标一致的实例不重复启动。更新先试运行候选，失败或初始化取消时保留旧 generation 及其有效运行状态；候选就绪并通过发布检查后切换，再停止和排空旧 owner。私有卷仍按实例归属和既有事务规则管理，不能把候选失败当作旧实例已卸载。

## Worker 中的直接 Node 执行

每个 enabled installation 使用独立 Node Worker/V8 isolate，CommonJS 命名上下文由 Node `vm` 承载；不使用 QuickJS、QuickJS handle 桥或第二层字节 JSON/Base64 编码。这不是新增的恶意脚本安全沙箱，也不以 vm 宣称安全隔离。LX/source 的 native QuickJS 拓扑见 [公共运行器](script-runtime.md)。

受控 loader 仅解析明确支持的内置模块、审核过的 SDK 和 installation 私有文件；不搜索任意宿主 npm 包、NODE_PATH、native addon、child_process 或 worker_threads。process 保持 Node 类型身份，但 env、路径和文件访问沿投影边界；不向脚本发布主进程会话密钥、平台账号或宿主文件能力。

Buffer、typed arrays、KeyObject、X509Certificate、ECDH、Web 事件、Promise 和 stream 在 Worker 内使用真实 Node 对象。公开算法和 base64url 语义遵循 Node；显式 Base64 编解码工具仍可用，但不是运行器传输通道。os/platform/arch/cpus/内存字段采用 Node 实际含义，不伪造 guest 堆余额。不存在单实例内存配额、粘性耗尽或 OOM 自动重启。

每个入站 HTTP 交换有独立取消和传输所有权；正常 finish 后的 close 不取消成功调用。不同调用可同时等待 I/O。请求回调创建的 timer、出站网络、Agent、stream、fd 和后台 Promise 归实例所有，按自然完成或实例 stop 回收；正常返回不撤销后台网络，但特权存储租约在请求结束时失效，不能借后台任务延续权限。主动取消关闭当前传输并拒绝迟到结果，不把仍运行的物理任务当作已结束。

所有资源登记在所属生命周期，一次终止后等待真实 close，再解除所有权。清理失败如实返回 SCRIPT_CLEANUP；有界清理超时仅留下不持有 session 图的迟到错误守卫，不通过重复 destroy/off 隐藏失败。finish/close 监听器在传输所有权释放前执行。实例停用、更新、卸载和关闭回收所有残余资源与 Worker；后台 callback 异常只产生结构化诊断，不因已结束请求而伤及另一调用。

聚合入口请求体、listener 响应和普通调用结果没有由内存配额派生的字节上限。媒体代理继续流式背压，不把媒体转成控制 JSON。注入网络适配器若解压响应，会移除失效的压缩编码/长度头，损坏压缩体报告网络失败；不能以移除配额为由改变错误或取消归属。

启动优先采用明确导出的 `start` 或函数入口，其次识别已求值函数的明确服务启动信号及其包装器。已有可选入口或直接导出的 spider 时不扫描整份 bundle；只有无法从已求值作用域找到入口的闭包包装器，才由 Acorn 一次词法索引、定向解析目标函数和单调用包装链。静态发现不执行表达式、不解析整份 bundle AST，不把字符串／注释内的代码当作声明，也不使用脚本名或作者名白名单。

没有服务启动入口时，CommonJS 对象中明确导出的函数就是 direct spider 的 action 表面；不以少数核心动作白名单判断，扩展 action 同样发布，但 `start`、`stop` 和配置页面控制名不作为业务 action。direct `detail` 优先接收标准请求的 `id`；调用方只提供 `ids` 时仍原样传递该值。未发布的原型成员或任意路径不能被当作 action 调用。

Fastify 通过标准 `fastify.initialization` 诊断频道和 `onRoute` hook 收集实际方法与完整路由，按服务端口及 site API 前缀发布 `actions`／`methods`。中文路径按 URL 段解码比较；前缀内的命名参数可以匹配当前 site，但动作段必须明确。不把通配路由、动态兜底或其他站点／管理路由当作当前站点的动作，也不依赖扫描几个固定动作名。启动等到框架 ready 及配置服务真实监听后才读取配置。

没有框架注册信息时，在所属 Worker 内按需使用 Acorn 解析源码，只识别常量模板／拼接、明确路由调用、`METHOD path` Map 和首尾锚定且动作枚举明确的正则分派。绑定按词法作用域解析，不执行表达式、不扫描字符串内的伪路由、不按固定动作列表猜能力。解析后的 AST、启动源码引用与临时路由记录及时释放。调用只接受已发布的 action。

标准 Cat HTTP `category` 输入为 `{id,page,filter,filters}`，`search` 为 `{wd,quick,page}`，`detail` 为 `{id}`，`play` 为 `{flag,id,flags}`。HTTP 服务保留这些字段；direct spider 在调用边界转为其位置参数，亦接受直接调用习惯的 `tid/pg/extend`。同一路由优先使用 POST，否则使用其实际注册方法；GET／HEAD 将字段编码为查询参数，对象与数组使用 JSON，不丢弃输入。

启动期通过受控 `vm` 接口动态执行的源码及框架注册也参与发现，仍留在同一 Worker，不保留额外完整源码或按动作数量截断。整份源码在完整上传和 ScriptHost install/update 保留 16 MiB 输入校验；运行期动态代码不扩大已发布能力。

HTTP 请求与 `url.format` 仅消费实际使用的字段，库自用 transport 元数据、循环对象和无关 getter 不成为额外权限。`lookup/fd/socketPath` 及替换受控连接的 `createConnection` 仍拒绝；显式受控 Agent 或 `agent:false` 按 Node 建连，其不消费的请求级 createConnection 不执行。HTTP 模块和 URL 协议错配保留 ERR_INVALID_PROTOCOL，不自动替换协议。框架 inject 在 Worker 中使用 Node HTTP/流语义，不为模拟请求额外开启端口；真实 headers/rawHeaders 的对象身份和解压器修改保留。

TLS 证书链可保留循环引用，缓存的真实 KeyObject 可跨请求使用；不再通过桥导出重建或要求私有句柄回执。公开加解密能力不扩大文件、环境和私有卷权限。

`onDiagnostic` 只报告固定错误类别、稳定代码和受限符号/位置。验收工具可显式启用默认关闭的 `onScriptError`，瞬时归因原始消息后丢弃正文；它不改变结果或权限，异常原文不进入产品报告。

旧服务器模式的 `/msg` Dart bridge 不存在，`catDartServerPort()` 固定返回 `0`。账号和 profile 由私有卷及明确的管理同步持久化，不能把脚本主服务端口误报为并不存在的 bridge 端口。

`fs` 和 `fs/promises` 只映射 installation 私有卷：

```text
<dataRoot>/volumes/<installationId>/
```

- guest 路径为虚拟 POSIX 路径；不接受盘符、反斜杠、设备名、链接、hardlink 或卷外 realpath。
- `/source.js` 是当前源码的只读挂载，不暴露宿主路径或 fd。
- 内容写入、可写 fd 关闭和流关闭执行实际 flush；rename、copy 和元数据修改在平台允许的文件句柄上刷新。Windows 的 FlushFileBuffers 需要可写句柄，只读文件仍遵循原生 Node 的元数据、copy 和 rename 语义，不为额外 flush 要求写权限。停止、更新、端口变化和进程重启保留私有卷。
- 私有卷身份只绑定 installationId，不绑定源码 hash、generation 或请求端口。

## 网络与监听

网络访问不区分公网、私网、链路本地、回环或 `.local`／`.lan`／`.internal` 等名称，也不维护域名授权或 origin 白名单。DNS 答案不过滤；有平台网络适配器时 DNS 不可用，以适配器归属为准。

- `socketPath`、fd 仍拒绝；guest 自定义 `lookup`／`createConnection` 仍拒绝。这些是文件／IPC 与连接控制边界，不是地址限制。
- 实例已登记的 listener 端口和别名仍映射到该实例的实际回环端点，并附加实例访问头和请求作用域；HTTP、HTTPS、net、TLS 自调用必须与 listener 协议一致，协议错配报错。
- 未登记的回环端口直接连接真实 `127.0.0.1:port`，不附加实例访问头或作用域 token。该规则同时用于 `http.request`、`outboundURL` 与 net/tls `connection`。

脚本自己的服务只监听 `127.0.0.1`。首个真正调用 `listen` 的 HTTP 或 HTTPS listener 成为主服务；运行器预留 HTTP listener，并为未使用预留 listener 的主服务及其他 listener 分配实际端口。脚本请求的固定端口仅登记为该实例内别名，因此多个 installation 可同时请求同一端口。主服务配置省略端口时，按协议将默认 80 或 443 映射到该服务。脚本自调用、`server.address()` 和 Cat 输出中的已登记本地 URL 使用实际端点。HTTP、HTTPS、net 和 TLS 自调用必须与 listener 类型一致，未启动或协议错配的本地端点不做降级映射。

运行器读取 Cat 配置时会在该 installation 已登记的 HTTP(S) listener 上依次尝试 `/full-config` 与 `/config`，并以实际返回有效站点配置的 origin 解析相对 site API、选择主要管理端点，不把一个辅助 listener 的 401 当成其他 listener 不可用。

对于明确提供 `internalAuthSecret` 的 Fastify 本机服务，在请求已经通过运行器外层访问检查后，通过脚本自己的 `x-internal-auth` 约定调用；该值留在当前 guest，不作为运行器凭据发布，也不发送到其他实例或上游。未提供该内部调用约定的脚本、交互式页面账号及上游账号仍遵循各自鉴权。

`net.createServer` 只兼容脚本启动时的端口可用性探测：它在 installation 私有的虚拟端口空间中提供 `listen`、事件、`address`、`close` 和 ref/unref 语义，不创建可接收入站数据的宿主 TCP listener。探测关闭后，同一虚拟端口可由受控 HTTP(S) listener 接管；运行器不因此开放任意 raw TCP 服务能力。

仅调用本 installation 已登记的本地 HTTPS/TLS listener 时允许自签名证书。公网 HTTPS、TLS 和 HTTP/2 始终启用证书链校验，guest 不能通过 `rejectUnauthorized` 或 `checkServerIdentity` 关闭该边界。

## 聚合入口、页面与代理

运行器状态返回：

- 聚合 Cat origin 与受信客户端访问 token；
- 每个 instance 的 `disabled`／`starting`／`ready`／`failed` 状态；ready instance 另有 generation、sites、当前实际 endpoints 和管理打开 URL。

聚合入口只处理 `/cat/<installation>/<site>/<operation>` 和 `/proxy/...`。Cat 业务请求必须携带运行器 access header；脚本代理 URL 使用不可猜测路由身份并只允许 GET/HEAD。代理保留状态、Range、必要响应头、取消和流式背压；本实例的 Location、HLS URI 与 MPD BaseURL 才会继续映射。`/website`、`/admin`、配置及运行器内部路径不得借代理暴露。

脚本管理页面不挂到聚合前缀。客户端先访问 instance 的 `managementUrl`，运行器设置该 installation 独有的随机 HttpOnly/SameSite cookie；HTTPS listener 还设置 Secure。浏览器会向同一主机的其他端口发送该 cookie，因此端口不是隔离边界：每个 installation 只认可自己的随机值，Cookie 请求还必须具有精确同源 `Origin`，或在无 `Origin` 时明确携带 `Sec-Fetch-Site: none/same-origin`；进入 guest 前会剥离全部内部 `__meow_*` cookie。随后页面按原路径加载 HTML、JS、CSS 和 AJAX，运行器不解析或重写页面正文。

## Cat 输出与 provider 拦截

脚本原始 Cat 根对象保持原结构和未知字段。运行器只在有增强信息且脚本未占用 `_meow` 时加入同级字段：

```json
{
  "list": [],
  "page": 1,
  "_meow": {
    "version": 1,
    "resources": [],
    "media": [],
    "routes": [],
    "captures": []
  }
}
```

不会返回 `{cat:{...}, own:{...}}` 两份数据，也不会覆盖脚本自有 `_meow`。增强包括已认领本地代理路由、真实宽高/分辨率/fps/文件名和可选 M3U8 variant；增强失败保留原始结果。

成功但没有正文的响应（例如 void `init` 或未提供搜索结果）返回 JSON `null`，不伪造列表；非 JSON 的文本响应保留为字符串，声明 JSON 却返回非空无效 JSON 仍报错。已声明路由返回的 404 属于脚本 HTTP 响应，不冒充宿主缺少 API。Fastify、direct spider 的错误及 HTTP JSON 的明确错误字段按所属请求输出脱敏诊断，只保留固定分类与受限错误码，不输出原异常文本；HTTP 状态、脚本错误与真实网络证据分别保留。

百度、夸克、UC、天翼和移动的开关只控制已识别分享流程的捕获。别名映射辅助识别线路，不单独证明已接管。开关关闭、接口未知、身份不足或不能准确归属时执行脚本原网络路径；不在运行器内实现 provider 登录、列表、转存或直链解析。

## 管理同步与生命周期

管理协议按实际路由探测：

- `website-v1`：`/website/pans/list` 与对应 `/website/<provider>/...`；
- `website-api-v1`：`/website/api/credentials` 与对应 `/website/api/...`。

同步只面向调用方指定的 installation、generation 和 provider，主线程只向该实例 worker 发送一次管理 RPC，探测、凭据发送和持久写回执等待全部在该 worker 内完成；凭据不写入日志或事件。成功写入还必须收到私有卷持久写回执；不按作者、文件名或源码 hash 猜协议，也不向所有脚本广播账号。

更新先停止该 installation 的旧 generation，再装载并启动新 generation，避免两个 generation 并发写同一私有卷。若脚本 stop 返回 Promise，运行器在有界清理时限内等待其完成，以便关闭服务并落盘；超时或失败后仍强制回收实例自有资源。停用、卸载和关闭回收 listener、socket、timer、流、fd、代理身份和 Worker 内存。

卸载或刷新时脚本从配置移除，实例停止、资源回收后在同一次调用内递归删除 `volumes/<installationId>`，并删除不再被任何 installation 引用的 `sources/<importhash>.js`；更新成功后也清理不再引用的旧源码文件，返回时删除已完成。删除依据设置中的原始文件引用，不使用 host 的实际执行内容摘要；状态中的 `sourceHash` 在已有 host revision 时报告该实际摘要，手工编辑后可与设置引用不同。共享引用仍保留，未知文件不扫描删除。目录身份、普通文件、符号链接和硬链接隔离检查继续保留。同一 installationId 重新安装从空卷开始；若上次删除失败留有残卷，安装时先删除，仍失败则安装失败、不启动。停止、停用、更新、端口变化、进程重启均保留私有卷。删除失败如实进入 `failures`，实例仍算已卸载，不影响其他实例；删除只作用于已校验的卷目录，不跟随符号链接。运行器启动和冷启动不扫描或清理 `volumes`。

`refresh`／`uninstall` 在各自变更全部处理完后返回 `applied`／`failures`，`install`／`enable` 在目标实例处理完后返回其状态。关闭时停止尚未开始的启动工作，先向已有及正在初始化的实例发停止，再等待在途事务、真实线程退出和 server 回收，不先等完整初始化截止时间；后台启动失败记入 `failures`，不产生未处理 rejection。

主线程每次 `settings.publish` 后按发布顺序向各 worker 推送只读设置快照和实际聚合端口；worker 不持久化或读取第二份设置文件，之后发出的调用使用新快照，调用开始时读取设置快照的语义不变。脚本结果的 mapUrl、本地代理 id 生成和可选 M3U8 元数据增强及变体改写在所属 worker 内完成；主线程转发媒体时的 Location／HLS／MPD 改写只查已登记代理与真实 endpoints 快照，不生成新的代理 id，也不为改写发起同步 RPC。

## 构建与验证边界

通用 payload 唯一 profile 为 `common-native`，入口 `app/entry.mjs`，使用同一 HTTP v1。原生引擎固定输入见 `scripts/quickjs-native.json` 与 `scripts/quickjs/build-native.mjs`（LX/source 使用；Cat 不进入 QuickJS）。Windows/iOS 原 launcher 和旧调用接入属于后续迁移。通用原生构建、打包 payload 三协议启动与定向回归已通过，真实第三方脚本未覆盖；适用环境及边界见[通用验证范围](script-runtime.md#构建发布与验证边界)。
