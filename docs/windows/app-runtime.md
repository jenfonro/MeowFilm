# Windows 脚本启动与 HTTP 会话

`apps/windows/runtime/` 只保留 `bootstrap.mjs` 和 `entries/aggregate.mjs`。薄 entry 使用公共 `apps/runtime/entry.mjs` 装配三个模块，并指定 source 平台为 Windows；不复制脚本执行、协议或业务实现。

## 私密启动与存活句柄

启动参数恰好为按序 `--meow-pipe meowfilm-<UUID> --meow-session <UUID>`；UUID 使用规范小写格式，不接受远程管道或任意地址。原生 launcher 创建单实例 `CurrentUserOnly` 管道服务器，启动独占子进程并通过 Job 管理，在发送配置前核对管道客户端 PID。session 表示运行代次，不是认证秘密。

App 生成每次启动独立的 32 字节随机 request key。通过已验证的管道发送一行 UTF-8 JSON `{sessionId,requestKey,dataRoot}`，Node 启动公共监听器后回复一行 `{protocol:"runtime.http.v1",baseUrl:"http://127.0.0.1:<port>"}`。`dataRoot` 由原生 `AppDataPaths.ScriptRuntime` 决定，是本机绝对目录。key 不放入 argv、环境变量、URL、持久存储或日志；子进程清空非必要继承环境。

启动控制行最多 64 KiB，连接及握手有期限；此边界不限制业务 HTTP body。握手后管道只作为持有的存活句柄，不发送 RPC、业务数据、心跳或反向能力请求。额外字节、EOF、error 或 close 都关闭实际公共运行器。普通 HTTP keepalive 连接结束不代表会话退出。

## 完整 HTTP 请求

`RuntimeHttpClient` 使用 `127.0.0.1` HTTP，不走系统代理、不跟随重定向、不自动重放业务。每次请求携带 Bearer key、`X-Meow-Session` 和独立 UUID `X-Meow-Request-Id`，核对响应 ID、内容类型、状态及严格信封；公开契约见 [Runtime bridge](../runtime/runtime-bridge.md)。

- `GET /v1/describe` 核对 `runtime.http.v1`、`aggregate`、准确模块及方法清单：`music@0.8.0` / `music.script.v3`、`cat@0.2.0` / `cat.script.v2`、`source@0.2.0` / `source.runtime.v2`。
- 普通调用为 `POST /v1/{module}/{operation}`，参数直接是 JSON，成功信封为 `{ok:true,contract,value}`，失败为 `{ok:false,contract,code}`；结果不再是 JSON 字符串。
- 源码经 `UploadSourceAsync` 完整 raw PUT，由 installationId 与明确 sourceRevision 绑定。异步上传借用调用方字节直到实际发送任务结束，包括取消；业务层不管理 begin/chunk/read/release。
- 单请求取消终止其 HTTP 交换，不撤销其他请求或共同启动。App 的 epoch fence 拒绝旧代次迟到结果；恢复不会重放业务。

业务 JSON 保留 64 层、严格 UTF-8、Unicode、无重复键和安全数值契约，不加总大小配额。源码的 16 MiB 是公共输入契约。原生文档、账号和敏感信息不通过反向 RPC 暴露给运行器。

## 所有权与关闭

App 装配后显式调用 `AppRuntimeClient.Start()`，启动唯一公共宿主；`FilmCapability` 随后恢复已启用猫源。宿主启动不执行音乐脚本，停用猫源不执行，不批量检测；原生页面、账号、目录、曲库、歌词、缓存及播放不以脚本服务可用为前提。

正常关闭先请求 `/v1/close` 并核对 `closed`、`cleanupComplete`，再退役 HTTP、管道和进程，等待真实退出；异常或启动失败同样释放已取得的资源。管道断开或取消信号本身不是清理成功回执。CLI 只输出固定 `WINDOWS_RUNTIME_START_FAILED`，不输出来源、凭据或底层异常；不自动重启。

LX/source 使用逐实例原生线程及 QuickJS Runtime，CatNode 使用逐实例 Node Worker；具体边界由[共用核心](../runtime/script-runtime.md)定义。客户端[影视规则引擎](film-settings.md)独立于此脚本宿主。source 的 CatVod 是独立 Spider 输入，不替代 CatNode；本次不新增原生阅读 UI。管道 PID/Job 及执行隔离不等于恶意脚本安全沙箱。

恢复见[显式恢复](runtime-recovery.md)，最终载荷见[打包](runtime-packaging.md)，source 能力见[来源 capability ABI](../runtime/source-capability-abi.md)。
