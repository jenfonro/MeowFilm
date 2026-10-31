# 共用 Cat 脚本契约

## 身份与工厂

实现位于 `apps/runtime/modules/cat-script.mjs` 与 `packages/script-host/src/cat-*.js`：

```text
id: cat
version: 0.1.0
contract: cat.script.v1
```

`createCatScriptModule({dataRoot})` 只接收平台选定的稳定绝对数据目录。平台负责进程与通信；公共模块负责源码传输、设置、安装、QuickJS guest、私有卷、服务端口、Cat 调用和关闭。

## RPC 与分片

方法均以 `cat.script.` 为前缀：

| 方法 | 作用 |
| --- | --- |
| `stage.begin/chunk/abort` | 分片暂存 `source` 或大型 JSON 请求 |
| `blob.read/release` | 分片读取大型 JSON 结果 |
| `start/status` | 启动运行器并读取当前设置，或返回当前状态 |
| `refresh` | 重新读取同一设置文件并完整校验后应用 |
| `install/enable/uninstall` | 保存源码、更新设置并管理一个 installation |
| `invoke` | 定向调用 installation/generation/site/action |
| `management.describe/sync` | 探测脚本原生管理协议并同步一个明确 provider |

源码和 JSON 分片均为 64 KiB、连续 offset、规范 Base64 与完整 SHA-256。未完成 transfer 按活动 token 保留并在超时、显式释放或运行器关闭时回收，不设置并发 token 数量门槛，也不把分片帧大小当作整份源码大小上限。小结果返回 `data` JSON 字符串，大结果返回 `blob` 描述符；调用方校验长度和 SHA-256 后必须释放 token。

源码严格按无 BOM UTF-8 解码。`install` 将字节以 SHA-256 命名为不可变 `sources/<hash>.js`，设置只引用 hash；不存在旧格式迁移或内联源码回退。

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

`port=0` 由系统选择聚合端口。缺省 provider 表示未启用对应拦截。客户端可原子改写该文件后调用 `refresh`；刷新先校验全部设置和引用源码，失败不发布部分状态、不覆盖文件，也不替换当前有效运行快照。冷启动文件损坏明确返回存储错误。

## QuickJS 与有限 Node 风格能力

用户源码仍在独立 QuickJS WASM 线性内存中执行。`require` 只解析运行器明确实现的内置模块子集；不存在任意 npm 包、宿主模块搜索、`NODE_PATH` 穿透、原生 addon、`child_process`、宿主 `worker_threads`、`node:vm` 或真实 `process` 对象回退。

兼容层提供脚本样本需要的 Buffer、URL、`Event`／`EventTarget`／`CustomEvent`、Abort、流、HTTP(S)、DNS、net/tls、crypto、zlib、path、util、有限 console/timer/Web API 和 CommonJS 启动语义。Web 事件对象和监听器完全位于 guest，不桥接宿主 EventTarget。传给 guest 的原生对象由角色表限制可见成员，宿主回调、句柄和接收者身份均经桥接核对。

每个获准进入脚本 listener 的 HTTP 请求拥有独立子生命周期，并继承所属 Cat 调用的设置、拦截与持久写回上下文；不同 Cat 调用可以各自等待 I/O，不经过单调用串行队列。启动作用域创建的模块、对象和服务属于 session；请求作用域创建的原生对象、服务、listener、文件句柄、guest 代理、回调、派生异步上下文和未完成桥接 Promise 在请求结束、取消或失败时一并释放，session 资源在实例停止时释放。相同对象被多个仍存活作用域使用时按引用保留。运行器不因当前累计句柄、活动资源、内存或调用并发数量拒绝 Cat 请求；这些对象仍逐一登记并按所属生命周期确定性回收。`os.totalmem()` 等固定值仅是隔离环境兼容元数据，不是分配预算。桥接参数编码或 callback 执行失败只关闭所属请求或 session，不得逃逸为宿主进程未捕获异常。

启动优先采用明确导出的 `start` 或函数入口；兼容 `Ndr` 等已知短包装入口时，包装器本身或其引用的局部函数必须具有服务启动信号。局部目标即使位于 IIFE 闭包内也可由源码结构确认，弱命名函数不能压过直接导出的 spider。

没有服务启动入口时，CommonJS 对象中明确导出的函数就是 direct spider 的 action 表面；不以少数核心动作白名单判断，扩展 action 同样发布，但 `start`、`stop` 和配置页面控制名不作为业务 action。direct `detail` 优先接收标准请求的 `id`；调用方只提供 `ids` 时仍原样传递该值。未发布的原型成员或任意路径不能被当作 action 调用。

Fastify 通过标准 `fastify.initialization` 诊断频道和 `onRoute` hook 收集实际方法与完整路由，按服务端口及 site API 前缀发布 `actions`／`methods`。中文路径按 URL 段解码比较；前缀内的命名参数可以匹配当前 site，但动作段必须明确。不把通配路由、动态兜底或其他站点／管理路由当作当前站点的动作，也不依赖扫描几个固定动作名。启动等到框架 ready 及配置服务真实监听后才读取配置。

没有框架注册信息时，在 guest 内按需使用 Acorn 解析源码，只识别常量模板／拼接、明确路由调用、`METHOD path` Map 和首尾锚定且动作枚举明确的正则分派。绑定按词法作用域解析，不执行表达式、不扫描字符串内的伪路由、不按固定动作列表猜能力。解析后的 AST、启动源码引用与临时路由记录及时释放。调用只接受已发布的 action。

标准 Cat HTTP `category` 输入为 `{id,page,filter,filters}`，`search` 为 `{wd,quick,page}`，`detail` 为 `{id}`，`play` 为 `{flag,id,flags}`。HTTP 服务保留这些字段；direct spider 在调用边界转为其位置参数，亦接受直接调用习惯的 `tid/pg/extend`。同一路由优先使用 POST，否则使用其实际注册方法；GET／HEAD 将字段编码为查询参数，对象与数组使用 JSON，不丢弃输入。

启动期经有限 `vm` 兼容层动态执行的源码及其框架注册也参与发现，不离开 guest、不保留第二份完整源码，不按源码字节数或动作数量截断。运行期动态代码不会扩大已发布能力。实例关闭时回收该 runtime 登记的全部 QuickJS context，再释放 WASM runtime。

`process` 保留 Node 的 `[object process]` 类型标识，供依赖正确选择 HTTP adapter；这不增加宿主信息或进程能力。旧式 `url.parse` 对象、TLS 证书链、`crypto.X509Certificate` 及 ECDH 密钥交换按有限数据角色传递，支持证书链自引用、证书公钥加密及共享密钥计算，但不开放宿主原型访问。可导出的 `crypto.KeyObject` 以受控 DER／raw 数据保存在 guest，传给加解密 API 时在所属调用内重建；`export`／`equals` 经有限操作执行，宿主持有临时 Buffer 后清零。这样脚本跨请求缓存密钥不依赖已结束请求的 opaque handle，也不会让临时密钥永久挂到 session。不可导出语义的 `CryptoKey` 仍是不透明角色，不按 KeyObject 泛化。

HTTP 请求与 `url.format` 在 guest 编码前按各自真正消费的字段投影参数，并在宿主侧核对相同边界。库自用的 transport 模块、循环元数据及无关 getter 不跨边界、不产生无主回调；`lookup/fd/socketPath` 及试图替换受控连接的 `createConnection` 仍拒绝。显式受控 Agent 或 `agent:false` 按 Node 语义由 Agent 建连，其不消费的请求级 `createConnection` 不传递、不执行。原生方法的 guest wrapper 可作为普通受控回调注册、执行和移除，不能直接把原生 callable 句柄作为回调解码。协议与所用 HTTP 模块不匹配时按 Node 保留 `ERR_INVALID_PROTOCOL`，不自动替换协议。

框架的 `inject` 使用 guest-local `IncomingMessage`／`ServerResponse`、响应头和流，不为模拟请求开启额外端口。相同原型的标准方法处理真实响应时仍需本实例有效 native handle，guest 自建对象不能借此获取宿主响应或套接字。真实响应的 `headers`／`rawHeaders` 首次读取后在 guest 保持对象身份，脚本删除或补充响应头不会在下次读取时被宿主副本恢复；请求结束时仍随所属作用域释放。

公共 QuickJS 初始化会在任何 SDK 执行前校正锁定引擎的 `BigInt.asUintN` 无符号语义；宽度按一次 ToIndex 转换，负数结果按标准补足无符号位。该修正在公共引擎层完成，LX、Cat 和后续协议共享，不依赖脚本或平台。

旧服务器模式的 `/msg` Dart bridge 不存在，`catDartServerPort()` 固定返回 `0`。账号和 profile 由私有卷及明确的管理同步持久化，不能把脚本主服务端口误报为并不存在的 bridge 端口。

`fs` 和 `fs/promises` 只映射 installation 私有卷：

```text
<dataRoot>/volumes/<installationId>/
```

- guest 路径为虚拟 POSIX 路径；不接受盘符、反斜杠、设备名、链接、hardlink 或卷外 realpath。
- `/source.js` 是当前源码的只读挂载，不暴露宿主路径或 fd。
- 写入、rename、可写 fd 关闭和流关闭执行实际 flush；停止、更新、端口变化和进程重启保留私有卷。
- 私有卷身份只绑定 installationId，不绑定源码 hash、generation 或请求端口。

## 网络与监听

公网访问不需要域名授权或 origin 白名单。外部目标仍必须是合法 HTTP(S)/TCP/TLS 公网地址：

- 拒绝私网、链路本地、回环、未指定、保留/文档地址及 `.local`、`.lan`、`.internal` 等本地域名。
- DNS 在连接时取得全部答案；只要包含非公网地址即失败，防止同一名称重绑定到宿主网络。
- guest 不得提供任意 `lookup`、`createConnection`、socketPath 或 fd 绕过检查。

脚本自己的服务只监听 `127.0.0.1`。首个真正调用 `listen` 的 HTTP 或 HTTPS listener 成为主服务；运行器预留 HTTP listener，并为未使用预留 listener 的主服务及其他 listener 分配实际端口。脚本请求的固定端口仅登记为该实例内别名，因此多个 installation 可同时请求同一端口。主服务配置省略端口时，按协议将默认 80 或 443 映射到该服务。脚本自调用、`server.address()` 和 Cat 输出中的已登记本地 URL 使用实际端点。HTTP、HTTPS、net 和 TLS 自调用必须与 listener 类型一致，未启动或协议错配的本地端点不做降级映射。

运行器读取 Cat 配置时会在该 installation 已登记的 HTTP(S) listener 上依次尝试 `/full-config` 与 `/config`，并以实际返回有效站点配置的 origin 解析相对 site API、选择主要管理端点，不把一个辅助 listener 的 401 当成其他 listener 不可用。

对于明确提供 `internalAuthSecret` 的 Fastify 本机服务，在请求已经通过运行器外层访问检查后，通过脚本自己的 `x-internal-auth` 约定调用；该值留在当前 guest，不作为运行器凭据发布，也不发送到其他实例或上游。未提供该内部调用约定的脚本、交互式页面账号及上游账号仍遵循各自鉴权。

`net.createServer` 只兼容脚本启动时的端口可用性探测：它在 installation 私有的虚拟端口空间中提供 `listen`、事件、`address`、`close` 和 ref/unref 语义，不创建可接收入站数据的宿主 TCP listener。探测关闭后，同一虚拟端口可由受控 HTTP(S) listener 接管；运行器不因此开放任意 raw TCP 服务能力。

仅调用本 installation 已登记的本地 HTTPS/TLS listener 时允许自签名证书。公网 HTTPS、TLS 和 HTTP/2 始终启用证书链校验，guest 不能通过 `rejectUnauthorized` 或 `checkServerIdentity` 关闭该边界。

## 聚合入口、页面与代理

运行器状态返回：

- 聚合 Cat origin 与受信客户端访问 token；
- 每个 ready instance 的 generation、sites、当前实际 endpoints 和管理打开 URL。

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

同步只面向调用方指定的 installation、generation 和 provider，凭据直接发送到该实例实际回环端口。成功写入还必须收到私有卷持久写回执；不按作者、文件名或源码 hash 猜协议，也不向所有脚本广播账号。

更新先停止该 installation 的旧 generation，再装载并启动新 generation，避免两个 generation 并发写同一私有卷。若脚本 stop 返回 Promise，运行器在有界清理时限内等待其完成，以便关闭服务并落盘；超时或失败后仍强制回收实例自有资源。停用、卸载和关闭回收 listener、socket、timer、流、fd、代理身份和 guest 内存；卸载不隐式删除源码内容或私有卷。
