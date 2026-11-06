# Runtime HTTP v1

`@meowfilm/runtime-bridge@0.2.0` 提供标准 Node `http` client/server、严格 JSON 和 HTTP 请求生命周期；业务路由由 `apps/runtime` 装配。通用运行器已切换契约，Windows/iOS 的旧启动及调用接入本轮未迁移，不能据此声明平台应用兼容。

## 会话与公共入口

`apps/runtime/entry.mjs` 导出 `createRuntime(config)` 和 `startRuntime(config)`。受信调用方提供 `sessionId`（UUID）、`requestKey`、`callbackUrl`、`callbackKey` 和 Cat 的 `dataRoot`；两个 key 分别为 256-bit 随机十六进制值。`createRuntime` 返回 `{start(),close(),baseUrl}`，`startRuntime` 返回已监听的同一对象。身份不接受脚本覆盖，不进入 CLI 参数、URL、日志或持久化。调用方持有会话存活句柄并负责终止时调用 `close()`；一次 HTTP keep-alive 断开不代表整个会话结束。

默认监听 `127.0.0.1:0`。每个请求携带：

```text
Authorization: Bearer <requestKey>
X-Meow-Session: <sessionId>
X-Meow-Request-Id: <UUID>
```

响应回显请求 ID。服务端核对精确回环 Host、key 和 session，不开放 CORS 或 Cookie 认证。重复进行中的请求 ID 拒绝；已完成 ID 不保留历史结果或做自动重放。内部单调序号用于请求归属，HTTP ID 不是授权凭据。

`apps/runtime/client.mjs` 的 `createRuntimeClient` 提供 `call(logicalMethod,params,options)`、`upload(module,id,sourceRevision,bytes,{basename?,signal?,timeout?})`、`describe()`、`shutdown()` 和本地 `close()`。`call` 在本地将明确逻辑业务名映射到 HTTP 路由，不提供第二套 wire 协议。

## 路由与 JSON

| HTTP 路由 | 输入 | 结果 |
| --- | --- | --- |
| `GET /v1/describe` | 无 body | `{protocol:"runtime.http.v1",product:"aggregate",modules:[{id,version,contract,methods}]}` |
| `PUT /v1/{music\|cat\|source}/sources/{installationId}/{sourceRevision}?basename=...` | `application/octet-stream` 原始源码；Cat 无 basename | `{installationId,sourceRevision,sourceBytes,contentHash}` |
| `POST /v1/{module}/{operation}` | `application/json`，body 即业务参数 | 业务值；`management.describe`、`management.sync` 是字面 operation 段 |
| `POST /v1/close` | `{}` | 停止全部模块、取消其他交换并等待实际排空，成功值为 `{closed:true,cleanupComplete:true}` |

成功统一 `{ok:true,contract,value}`，失败统一 `{ok:false,contract,code}`。value 是直接对象、数组、标量或 null，不是另一份序列化 JSON。三模块为 `music@0.8.0/music.script.v3`、`cat@0.2.0/cat.script.v2`、`source@0.2.0/source.runtime.v2`。

合法请求正文与成功业务值各自按现有 64 层 JSON 契约校验；调用器的位置参数列表和内部结果信封不占业务值深度。普通业务对象即使包含 `ok/value` 字段也不豁免计深，65 层仍拒绝。此格式边界不同于脚本执行的真实栈限制。

普通 JSON 没有总字节上限或大小分流；不增加正文摘要、压缩、加解密或预解析加工。请求与响应严格 UTF-8，业务 JSON 深度最多 64，支持不同码点序列的 Unicode 键并原样保留；重复解码键、孤立 surrogate、非有限数、不安全整数、循环、BigInt、getter 及其他非 JSON 值拒绝。信封本身不挤占业务值的 64 层深度。源码保持 1 至 16 MiB 的输入契约及各协议 BOM 行为；config/metadata 的独立格式边界不等于普通正文配额。

旧长度帧、stage/control/blob、Base64 传输、随机 token、offset、TTL 和 checksum 协议均已删除，没有兼容回退、续传或业务 release 方法。底层 HTTP 分段读写属于标准流传输。媒体代理沿独立入口流式转发，不经控制 JSON 聚合。

## 完整源码候选

上传不启动脚本、不修改 enabled 状态，也不建立持久来源事实源。只在 body 完整且格式有效后按 `(session,module,installationId,sourceRevision)` 发布完整候选；中断上传销毁本次缓冲，不影响旧完整候选。

每个安装只保留当前执行记录和一个未采用完整候选；已取入操作及退休执行版本由实际所有者持有至结束。新完整上传原子替换未采用候选，不建立全版本缓存。尚持有的同键同内容幂等，不同内容报模块完整性错误；同一份源码身份 hash 只计算一次并复用。source 的规范化执行源码与原始上传内容身份分别保留。

load/install 必须指定 sourceRevision；不存在的版本明确失败，不采用最新候选。sourceRevision 是正安全整数，可跳号。字段、身份、revision CAS 前置拒绝不消费候选；真正取入试运行后失败释放候选，重试须显式重新上传。未采用候选在下一次完整上传、卸载或会话关闭释放，无超时或数量额度。卸载/会话关闭后不保留上传历史，实际源码的后续编辑可再次导入。

## 取消、反向能力与清理

客户端复用连接并允许并行连接，没有固定并发配额或全局请求队列。未发请求取消仅在本地结束；已发请求通过 HTTP abort 关闭未完成交换的连接。服务端在读取 body 前登记请求所有者，监听 body `aborted` 和未完成 response 的 `close`；正常 `finish` 后的 close 不取消成功调用。body 已发完但结果尚未返回、响应背压阶段均继续受取消控制。不提供 DELETE 取消、取消墓碑或 ACK 协议。

结果 fence 与物理排空分别处理。非合作 handler、网络或存储工作在实际 owner 保留至真实完成，关闭等待这些所有者；不能用本地 await 取消证明远端 I/O 已结束。同 installation 的管理修改有序，其他安装和 status 独立推进。

每 session 使用一个反向 HTTP server，只允许：

```text
POST /v1/capabilities/platform.musicScript.storage.get
POST /v1/capabilities/platform.musicScript.storage.set
POST /v1/capabilities/platform.musicScript.storage.delete
```

Node 使用独立 callbackKey、相同 session 和新的关联请求 ID；callbackUrl 必须是受信回环端点，不跟随重定向。body 为 `grant,installationId,siteId,generation,key`，set 另有 value。反向信封 contract 为 `runtime.http.v1`，value 直接适配存储 owner 的业务值，错误保留既有安全存储码。scope 当前性、grant、site、代次和取消在回调前后重检。存储事务、overlay、CAS 仍属于客户端 owner；callback server 在连接取消后继续登记未完成物理存储任务。两方向采用同一 HTTP 生命周期。

Node 调用方从 `apps/runtime/client.mjs`（或公共 `entry.mjs`）导入 `createCallbackServer`，传入上述方法的业务 handler。方法名单与存储错误码由 `apps/runtime/contracts.mjs` 定义；回包投影由该业务装配处理。共享 `@meowfilm/runtime-bridge` 仅提供 `createHttpServer` / `createHttpClient` 等通用 HTTP 能力，不内置音乐存储方法或存储错误策略。

## 错误

业务已受理后的成功和失败均为 HTTP 200。未进入业务的坏格式 400、访问拒绝 401/403、未知路由 404、不支持 Content-Type 415、源码超限 413。公共错误 contract 为 `runtime.http.v1`，code 限 `INVALID_REQUEST/METHOD_NOT_FOUND/PERMISSION_DENIED/CLOSED/CANCELLED/TIMEOUT/INVALID_RESPONSE`；业务安全码见各模块契约。未知业务错误映射为模块未分类失败，不输出底层异常、源码、凭据或供应方正文。

客户端严格检查响应 contract、请求 ID、信封和允许的 code；截断/断连为 CLOSED，坏响应为 INVALID_RESPONSE，无重试或降级。最终集成构建与定向行为验证范围见[运行器验证边界](script-runtime.md#构建发布与验证边界)，契约描述本身不替代验证结果。
