# Windows 脚本启动与 HTTP 会话

`apps/windows/runtime/` 保留受信 `bootstrap.mjs`、`entries/aggregate.mjs` 和私密设置管道适配 `native-settings.mjs`。薄 entry 使用公共 `apps/runtime/entry.mjs` 按序装配 music／cat／source／pan／tmdb／douban／film／netease／music-platform 九模块，并指定 source 平台为 Windows。前六个 provider 构造后经 `connectFilmPlayback` 接入 film 及变更撤权 hook，再追加按需网易 token 模块和借用该 token owner 的音乐平台模块；不复制脚本执行、协议或业务实现。通用 `createRuntime()` 默认三模块不变。

## 私密启动与存活句柄

启动参数恰好为按序 `--meow-pipe meowfilm-<UUID> --meow-session <UUID>`；UUID 使用规范小写格式，不接受远程管道或任意地址。原生 launcher 创建单实例 `CurrentUserOnly` 管道服务器，启动独占子进程并通过 Job 管理，在发送配置前核对管道客户端 PID。session 表示运行代次，不是认证秘密。

App 通过已验证的管道发送一行 UTF-8 JSON `{sessionId,dataRoot,nativeSettings:true}`，Node 读取原生设置并启动公共监听器后回复一行 `{protocol:"runtime.http.v1",baseUrl:"http://127.0.0.1:<port>"}`。未装配原生设置的独立运行器会话仍发送 `{sessionId,dataRoot}`。普通配置及 ready 扩展不阻断启动，也不授予设置能力；只有 `nativeSettings:true` 选择该能力。`dataRoot` 由原生 `AppDataPaths.ScriptRuntime` 决定，是本机绝对目录。HTTP 不使用启动访问密钥；子进程清空非必要继承环境。

启动控制行不设置应用字节配额，连接及握手有期限；管道的 64 KiB 缓冲不限制消息总长度。装配原生设置时，管道另外承载按块读取及网盘凭据 CAS 回写，协议与范围见[原生设置](native-settings.md#私密配置能力)；它不是通用 RPC、业务文件访问或心跳通道。未装配该能力时收到任意额外字节即关闭会话，不等待完整 JSON 行；坏帧、EOF、error 或 close 都关闭实际公共运行器。普通 HTTP keepalive 连接结束不代表会话退出。

## 完整 HTTP 请求

`RuntimeHttpClient` 接受合法 HTTP(S) 根端点，包括域名、非回环和默认端口；App 默认使用启动握手返回的本机 URL。ready 接收边界验证端点后在内部复用同一 `Uri`；公开 string 入口仍验证外部输入。不走系统代理、不跟随重定向、不自动重放业务。每次请求携带独立 UUID `X-Meow-Request-Id`，核对响应 ID、内容类型、状态及严格信封；公开契约见 [Runtime bridge](../runtime/runtime-bridge.md)。

- `GET /v1/describe` 核对唯一 protocol／product，以及 `AppIdentity.ScriptModules` 所需模块的存在性、唯一性和 id／version／contract；缺少必需身份或契约不匹配阻断启动。模块顺序、未用扩展、额外模块及未消费的 methods 元数据不阻断启动，不预检全局业务方法。
- Windows 按契约 prefix 选择 owner 并验证 operation 语法，不维护业务操作批准表。公共注册将同 owner 的合法可调用 handler 登记到真实 methods Map，拒绝重复 owner、冲突、非法名称及不可用资源能力；描述不会凭空生成 handler。合法但未登记的方法发到实际路由后返回 `METHOD_NOT_FOUND`，不会改派其他 owner。
- 普通调用为 `POST /v1/{module}/{operation}`，参数直接是 JSON，成功信封为 `{ok:true,contract,value}`，失败为 `{ok:false,contract,code}`；结果不再是 JSON 字符串。
- 源码经 `UploadSourceAsync` 完整 raw PUT，由 installationId 与明确 sourceRevision 绑定。异步上传借用调用方字节直到实际发送任务结束，包括取消；业务层不管理 begin/chunk/read/release。
- `ReadImageAsync` 只接受运行器相对图片引用，经同一客户端携带请求 ID；成功读二进制、错误解包 `502` 所属模块信封，取消等待物理响应释放。元数据与小说消费者统一使用 `ReadImageAsync(reference, cancellationToken)` 完整读取，不传入图片字节配额；编码缓存的单项准入和原生解码像素预算由图片消费者分别管理；图片契约见[元数据运行器](../runtime/metadata-data.md)与[书源仓库](../runtime/source-library.md)。
- 单请求取消终止其 HTTP 交换，不撤销其他请求或共同启动。App 的 epoch fence 拒绝旧代次迟到结果；恢复不会重放业务。
- 小说纯规则只走 `source.runtime.rules.process`／`POST /v1/source/rules.process`。一次 transform 发送完整 rules 与 conversion，Node 按规则逐条计时；该路由不使用短固定 HTTP 批次总时限，由调用 owner 或连接取消并等待实际交换结束。其他 source 方法仍保持默认 30 秒，网易 token 保持 60 秒。

业务 JSON 保留 64 层、严格 UTF-8 与 JSON 语法，不加总大小配额。`System.Text.Json` 保留原始数字与扩展，不将全树数字转成 double，不拒绝普通重复未知键，也不提前解码全树字符串或未知 Unicode 键；字符串在实际取用时由 `System.Text.Json` 解码。所消费的 ok／contract／value 或 code、描述身份及关闭回执要求唯一成员；SSE sessionId／sequence、上传 sourceRevision、设置请求 id 等实际身份和关联数字在消费处精确取整并核对范围，避免浮点舍入冒充关联成功。外层扩展不参与成员数门禁，错误呈现仍只使用有限 code，不呈现附带私密正文。源码上传不另设字节配额。没有通用反向文档 RPC；只有上述私密设置能力向受信 Node owner 提供指定设置块及所需凭据，不向用户脚本提供原生存储入口。

## 所有权与关闭

App 装配后显式调用 `AppRuntimeClient.Start()`，启动唯一公共宿主；`FilmCapability` 随后恢复已启用猫源。宿主启动不执行音乐音源脚本，停用猫源不执行，不批量检测。原生音乐页面、曲库、共享缓存和播放器不依赖单个音源脚本；官方联网配方、账号认证和平台歌词通过共同 `music-platform` owner 执行，需要该运行器可用，不保留原生 HTTP 回退。影视智能会话、业务数据缓存和新增本地播放资料则由同一 Node 的 film owner 持有，原生媒体消费它的真实 attempt，边界见[影视播放契约](../runtime/film-playback.md)。

正常关闭先请求 `/v1/close` 并核对 `closed`、`cleanupComplete`，再退役 HTTP、管道和进程，等待真实退出；异常或启动失败同样释放已取得的资源。管道断开或取消信号本身不是清理成功回执。CLI 只输出固定 `WINDOWS_RUNTIME_START_FAILED`，不输出来源、凭据或底层异常；不自动重启。

`AppRuntimeClient` 与 `AppRuntimeConnectionHost` 通过只读 `IRuntimeLifetime.ShutdownCompletion` 暴露最终关闭的同一物理排空任务：未开始时为 `null`，开始后无论成功或失败均保留原任务，不授予消费者关闭运行器的权限。强制释放先退役共享运行器，再释放原生所有者；音乐平台客户端的最终会话重置、小说图片引用释放此时等待该任务，不再向已关闭的宿主发送 RPC。正常运行时仍要求各自 RPC 回执；关闭竞态中收到 `CLOSED` 也必须取得并等待最终关闭任务，单纯 `Closed` 状态、连接撤权或未确认的传输失败不能充当资源释放成功。

factory 在交出 session 前负责排空已取得的资源；普通启动失败且清理成功不阻止最终关闭。该清理本身失败时，Node 启动或连接捕获通过脱敏 `RuntimeStartupCleanupException` 明确告知 epoch，最终关闭保留失败，不吞作普通启动错误，也不重复 Dispose 同一 session。

LX/source 使用逐实例原生线程及 QuickJS Runtime，CatNode 使用逐实例 Node Worker；具体边界由[共用核心](../runtime/script-runtime.md)定义。[影视规则](film-settings.md)与搜索由 Node film owner 持有，小说纯规则由 Node source owner 执行；客户端不保留独立规则引擎或嵌入程序。source 的 CatVod 是独立 Spider 输入，不替代 CatNode；原生[小说页面与阅读器](novel.md)消费同一 source 书源仓库，继续持有书架、历史、偏好及净化配置。关闭小说 owner 只取消并排空本 owner 接纳的调用，不关闭共享运行器。管道 PID/Job 及执行隔离不等于恶意脚本安全沙箱。

恢复见[显式恢复](runtime-recovery.md)，最终载荷见[打包](runtime-packaging.md)，source 能力见[来源 capability ABI](../runtime/source-capability-abi.md)。
