# Runtime HTTP v1

`@meowfilm/runtime-bridge@0.2.0` 提供标准 Node HTTP(S) client／HTTP server、严格 JSON 和 HTTP 请求生命周期；业务路由由 `apps/runtime` 装配。iOS 与 Windows 均使用相同公开契约及原生 HTTP 客户端；iOS 平台载体与验证边界见[iOS 运行底座](../ios/runtime-preflight.md)，Windows 的私密启动和 HTTP 消费者见[Windows 接入](../windows/app-runtime.md)。公共契约通过不代表全平台应用验证。

## 会话与公共入口

`apps/runtime/entry.mjs` 导出 `createRuntime(config)` 和 `startRuntime(config)`。App 启动方提供生命周期 `sessionId`（UUID）及应用专用 `dataRoot`；各持久化业务 owner 分别管理自己的私有目录。`createRuntime` 返回 `{start(),close(),baseUrl}`，`startRuntime` 返回已监听的同一对象。生命周期 session 由启动方持有，作为运行代次和处理器上下文，不作为 HTTP 访问凭据。调用方持有会话存活句柄并负责终止时调用 `close()`；一次 HTTP keep-alive 断开不代表整个会话结束。

监听系统分配的端口，绑定全部可用接口；返回的 `baseUrl` 使用本机 `http://127.0.0.1:<port>`，外部可用设备地址和同一端口访问。普通外部请求无需启动密钥、session 或认证头。服务端不按域名、Host 或 Origin 拒绝请求，浏览器响应开放 `Access-Control-Allow-Origin: *`，OPTIONS 预检允许公开 HTTP 方法与调用方请求头，也响应浏览器的私网访问预检。

调用方可传 `X-Meow-Request-Id: <UUID>`，缺失时由服务端生成，响应通过同名头回传并允许浏览器读取。显式畸形 ID 和重复进行中的 ID 按协议错误拒绝；已完成 ID 不保留历史结果或做自动重放。内部单调序号用于请求归属，HTTP ID 不是授权凭据。

`apps/runtime/client.mjs` 的 `createRuntimeClient({baseUrl,timeout?})` 接受合法 HTTP(S) 根端点，包括域名、非回环和默认端口；不要求访问凭据。它提供 `call(logicalMethod,params,options)`、`upload(module,id,sourceRevision,bytes,{basename?,signal?,timeout?})`、`describe()`、`shutdown()` 和本地 `close()`。`call` 在本地将明确逻辑业务名映射到 HTTP 路由，不提供第二套 wire 协议。

## 路由与 JSON

| HTTP 路由 | 输入 | 结果 |
| --- | --- | --- |
| `GET /v1/describe` | 无 body | `{protocol:"runtime.http.v1",product:"aggregate",modules:[{id,version,contract,methods}]}` |
| `PUT /v1/{music\|cat\|source}/sources/{installationId}/{sourceRevision}?basename=...` | `application/octet-stream` 原始源码；Cat 无 basename | `{installationId,sourceRevision,sourceBytes,contentHash}` |
| `POST /v1/{module}/{operation}` | `application/json`，body 即业务参数 | 业务值；`sources.list`、`management.describe` 等是字面 operation 段 |
| `POST /v1/close` | `{}` | 停止全部模块、取消其他交换并等待实际排空，成功值为 `{closed:true,cleanupComplete:true}` |

成功统一 `{ok:true,contract,value}`，失败统一 `{ok:false,contract,code}`。value 是直接对象、数组、标量或 null，不是另一份序列化 JSON。通用默认三模块为 `music@0.8.0/music.script.v3`、`cat@0.2.0/cat.script.v2`、`source@0.2.0/source.runtime.v2`。iOS／Windows 正式 App 按此前三项、`pan@0.1.0/pan.runtime.v1`、`tmdb@0.1.0/tmdb.data.v1`、`douban@0.1.0/douban.data.v1`、`film@0.1.0/film.playback.v1` 的顺序精确装配七模块；元数据只读请求、持久设置和二进制图片路由见[元数据契约](metadata-data.md)，影视会话、事件与媒体资源见[影视播放契约](film-playback.md)。

受信 Node 装配可显式加入 [pan.runtime.v1](pan-runtime.md)，使用 `POST /v1/pan/{operation}`；pan 不支持源码上传。`pan-entry.mjs` 提供同进程 pan-only 入口，或 `createRuntime({...,pan:{}})` 共用 aggregate HTTP 会话。通用默认 `createRuntime()` 不装配 pan；正式 App 显式装配它，平台握手精确匹配七模块及完整方法清单，不接受缺项／多项／重复项。

合法请求正文与成功业务值各自按现有 64 层 JSON 契约校验；调用器的位置参数列表和内部结果信封不占业务值深度。普通业务对象即使包含 `ok/value` 字段也不豁免计深，65 层仍拒绝。此格式边界不同于脚本执行的真实栈限制。

普通 JSON 没有总字节上限或大小分流；不增加正文摘要、压缩、加解密或预解析加工。请求与响应严格 UTF-8，业务 JSON 深度最多 64，支持不同码点序列的 Unicode 键并原样保留；重复解码键、孤立 surrogate、非有限数、不安全整数、循环、BigInt、getter 及其他非 JSON 值拒绝。信封本身不挤占业务值的 64 层深度。源码保持 1 至 16 MiB 的输入契约及各协议 BOM 行为；config/metadata 的独立格式边界不等于普通正文配额。

旧长度帧、stage/control/blob、Base64 传输、随机 token、offset、TTL 和 checksum 协议均已删除，没有兼容回退、续传或旧分块传输的 release 方法。影视的 `session.release`／`session.media.release` 释放业务会话／媒体资源，不是恢复旧传输协议，见[影视播放契约](film-playback.md)。底层 HTTP 分段读写属于标准流传输。媒体代理沿独立入口流式转发，不经控制 JSON 聚合。

## 原始流响应

`createHttpServer` 的路由可显式设置 `responseType: 'stream'`。该设置改变成功响应的字节传输及受理后失败的 HTTP 状态；请求正文、`validate(input)` 与 handler 上下文仍沿用现有规则。未设置时继续使用 JSON 信封，上传也保持现行行为。公共 bridge 只处理 HTTP 与 Node 流，不识别来源、图片路径、供应方内容或代理地址，业务模块负责这些判断。

路由仍提供 `contract`、允许失败码的 `codes` 集合及 `handler(input, context)`。成功 handler 结果为下面四个字段，`body` 必须是尚未结束或销毁的 Node `Readable`，`dispose` 必须是函数：

```js
{
  status: 200,
  headers: {
    'content-type': 'application/octet-stream',
    // 可选：'content-length'、'cache-control'、'etag'、'last-modified'
  },
  body, // Node Readable，直接输出字节
  dispose: async () => { /* 关闭并等待本次资源真正释放 */ },
}
```

成功仅支持 HTTP 200。`content-type` 必填且非空；仅允许 `content-type/content-length/cache-control/etag/last-modified`，名称不区分大小写且不得重复。值为合法 HTTP header 字符串，`content-length` 也接受非负安全整数；长度必须是 Node HTTP 能精确表示的十进制非负安全整数，发送时按声明检查实际长度。这是传输数值边界，不是额外的响应大小配额。`Location`、`set-cookie`、其他任意 header（包括 `x-meow-request-id`）均拒绝，bridge 自行回显请求 ID。缓存头仅透传标准 HTTP 元数据，不建立运行器缓存或新增缓存策略。

字节通过 Node `pipeline` 和遵守实际 HTTP 写回调的流目标发送，保留首块提前与背压，不聚合为完整响应。响应头在首块或空流结束时提交。首块前的源流错误、成功结果校验失败及未知 handler 异常返回 HTTP 502、`application/json` 的 `{ok:false,contract:"runtime.http.v1",code:"INVALID_RESPONSE"}`；公开 `BridgeError` 保留其安全码。模块主动失败仍返回经过 `contract/codes` 校验的 `{ok:false,contract,code}`，HTTP 状态为 502。正文格式、关闭状态和路由等未受理错误继续使用现有 400/403/404/413/415 规则。本节的 502 规则专用于 stream 路由。响应头已提交后发生失败只终止流，不再次发送 JSON、重定向或追加错误正文。

每份收到的原始流结果由 bridge 承接清理：成功、验证失败、源流错误、断连、取消及取消后迟到的 handler 结果，都只调用一次可用的 `dispose()` 并等待其 Promise 实际完成；同时销毁并等待所交付 Node 流的真实销毁完成，而不只等待 `end`。`autoDestroy:false` 和 `emitClose:false` 不改变支持范围：bridge 在接管尚未销毁的流时观察其 `_destroy` 完成回调，即使不发出 `close`，也要等该回调完成；`dispose` 的其他资源仍独立排空。仅当接管时流已开始销毁但尚未 `closed`，无法事后接入销毁回调时，bridge 保留 `close` 监听，并用单个 25ms 定时器持续检查公开 `body.closed`；只有观察到实际销毁完成才结束等待，并清除定时器与监听。这不是总超时或按等待时长判成功，不依赖 `dispose` 等待同一资源，也不发出伪造 `close`；正常有效流不使用轮询。缺少必需 `dispose` 的成功结果拒绝，仍清理已交付的 Node 流。标准失败信封不交付流资源，无需 `dispose`。handler 抛出前尚未交付的资源由 handler 自行清理。

模块用 `context.signal` 取消尚未交付的上游工作，用 `dispose` 关闭并等待其上游 socket/response 等实际资源。bridge 在传输结束、源流失败或取消时启动清理；传输和清理均完成才移除请求所有者。客户端已收到全部字节或连接已断开时，`state.active` 仍可能为 1。`abortActive()` 和 `close()` 等待 handler、流与 `dispose`，不能提前报告排空；关闭后拒绝新工作。清理异常在未提交响应头时脱敏为失败，提交后终止该交换。完整响应后的清理拒绝不能改写已送达的成功响应，也不证明资源释放成功；请求所有者在清理 Promise 结束后仍须排空。

`dispose()` 的真实拒绝保留为本会话清理失败，不保存底层异常或请求历史。之后每次 `abortActive()` 和 `close()` 都在实际工作 settle 后以 `BridgeError('INVALID_RESPONSE')` 拒绝，即使当时 `state.active` 已为 0；`close()` 仍执行一次 `onClose` 并等待其完成。普通上游、流传输及响应格式错误不记为会话清理失败。由此 HTTP `/v1/close` 可报告标准失败信封，不能在清理已失败时返回 `cleanupComplete:true`；完整图片仍保持已送达的原始成功响应，不二次发送 JSON。

`createHttpClient` 保持 JSON 客户端契约，不提供原始流消费能力；原始流调用方使用其平台 HTTP API；请求 ID 可用于相关性，不需要会话认证。

## 完整源码候选

上传不启动脚本、不修改 enabled 状态，也不建立持久来源事实源。只在 body 完整且格式有效后按 `(session,module,installationId,sourceRevision)` 发布完整候选；中断上传销毁本次缓冲，不影响旧完整候选。

每个安装只保留当前执行记录和一个未采用完整候选；已取入操作及退休执行版本由实际所有者持有至结束。新完整上传原子替换未采用候选，不建立全版本缓存。尚持有的同键同内容幂等，不同内容报模块完整性错误；同一份源码身份 hash 只计算一次并复用。source 的规范化执行源码与原始上传内容身份分别保留。

音乐文件 `sources.add/update`、Cat 文件 `install/update` 及 source `load` 必须指定 sourceRevision；不存在的版本明确失败，不采用最新候选。音乐首次文件导入由调用方预分配 UUID，网络导入由运行器分配。sourceRevision 是正安全整数，可跳号，不是安装目录 revision 或执行 generation。字段、身份及各模块前置拒绝不消费候选；真正取入操作后失败释放候选，重试须显式重新上传。未采用候选在下一次完整上传、删除或会话关闭释放，无超时或数量额度。实际源码由业务安装目录持久保存，不把上传候选当作第二份安装事实。

## 取消与清理

客户端复用连接并允许并行连接，没有固定并发配额或全局请求队列。未发请求取消仅在本地结束；已发请求通过 HTTP abort 关闭未完成交换的连接。服务端在读取 body 前登记请求所有者，监听 body `aborted` 和未完成 response 的 `close`；正常 `finish` 后的 close 不取消成功调用。body 已发完但结果尚未返回、响应背压阶段均继续受取消控制。不提供 DELETE 取消、取消墓碑或 ACK 协议。

结果 fence 与物理排空分别处理。非合作 handler、网络或存储工作在实际 owner 保留至真实完成，关闭等待这些所有者；不能用本地 await 取消证明远端 I/O 已结束。同 installation 的管理修改有序，其他安装和 status 独立推进。

音乐私有 KV 由运行器管理，不再通过客户端反向存储 RPC；公共装配不要求反向 listener、callback key 或 grant。脚本仍只在有效处理器请求内使用 `context.storage`，校验 installation/site/generation、请求当前性与取消；旧 context 不获得后续请求的权限。客户端可以保存来源列表、检测结果等界面快照，但运行器安装与私有数据仍是唯一事实来源。

共享 `@meowfilm/runtime-bridge` 的 `createHttpServer` / `createHttpClient` 是通用 HTTP 能力，可由各自调用方组合；其中没有音乐存储分派或客户端缓存策略。

## 错误

普通 JSON 业务已受理后的成功和失败均为 HTTP 200；上传保留路由声明的状态（如源码超限 413）。原始流路由例外：成功为 HTTP 200，受理后且未提交响应头的失败为 HTTP 502，提交后失败只断流，详见上文。未进入业务的坏格式 400、已关闭 403、未知路由 404、不支持 Content-Type 415、源码超限 413。公共错误 contract 为 `runtime.http.v1`，code 限 `INVALID_REQUEST/METHOD_NOT_FOUND/PERMISSION_DENIED/CLOSED/CANCELLED/TIMEOUT/INVALID_RESPONSE`；业务安全码见各模块契约。未知业务错误映射为模块未分类失败，不输出底层异常、源码、凭据或供应方正文。

通用 handler 抛出的未知值（包括 `null`／`undefined`）返回 `INVALID_RESPONSE`；错误响应本身写入失败则关闭该交换，不等待未发送的响应。正常响应背压仍由原请求持有至完成或连接关闭。

客户端严格检查响应 contract、请求 ID、信封和允许的 code；截断/断连为 CLOSED，坏响应为 INVALID_RESPONSE，无重试或降级。最终集成构建与定向行为验证范围见[运行器验证边界](script-runtime.md#构建发布与验证边界)，契约描述本身不替代验证结果。
