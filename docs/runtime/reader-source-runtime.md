# Reader 书源运行时

Reader adapter 位于 `@meowfilm/source-runtime`，由 `source@0.3.0`／`source.runtime.v2` 管理，与 `music@0.8.0`、`cat@0.2.0` 一同装配。规则包不依赖 Node 或 Windows API；通用 script-host 为每个 source 分配一个原生线程、一个 QuickJS Runtime 和可信 core/用户 realm 两个 Context。平台能力和清理语义以[来源 capability ABI](source-capability-abi.md)为唯一来源，不另建运行器。

## 协议

| 输入 | 处理 |
| --- | --- |
| Legado JSON | 执行搜索、发现、详情、目录、正文、登录状态、登录入口和图片处理规则 |
| 异次元旧 JSON | 导入为内部 Legado 模型；不维护第二套规则引擎 |
| 轻悦时光 HTML | 接受内联 HTML 或空白分隔的远程 HTML 候选 URL；提取脚本后在 QuickJS 中提供来源 DOM、jQuery 风格选择器和受控 `flutter_inappwebview.callHandler` bridge |

协议自动识别也可由加载请求显式指定 `legado`、`yiciyuan`、`qysg` 或 `cat`。原始 HTML、JSON 字符串包装的 HTML、带来源名称／地址的轻悦时光远程 HTML 对象和 CatVod JS 均可在 source 入口识别；异次元实例对外报告实际执行协议 `legado` 和 `importedProtocol: yiciyuan`。这里的 `cat` 是[独立 CatVod Spider 输入](cat-source-runtime.md)，不表示支持 `cat.script.v2` 的 CatNode 服务或 Node 兼容层。

## 动作

adapter 只声明来源实际具备的动作：

```text
search
exploreKinds
explore
bookInfo
toc
content
loginStatus
login
image
payAction
```

动作输入和前一动作结果由应用显式传递；运行时不保存全局“当前书”。搜索、详情、目录和正文结果使用统一书籍、章节和正文结构，URL 按响应最终地址和来源地址归一。永久书源仓库、原生 Web 待办和受信图片见[书源仓库](source-library.md)，书架和阅读进度仍由原生客户端拥有。

Legado 图片规则与轻悦时光 `imagedecrypt` 返回的字节在内部 ABI 中保持 `Uint8Array`。产品图片通过 `library.image` 和受信 GET 二进制路由下载、解码与释放；既有低层 `invoke image` 的 body 保留原有 Buffer JSON 投影供直接规则诊断。字符串 body 原样保留，不递归转换其他字段或调用任意 `toJSON`，非法非 JSON 结果仍明确失败。

轻悦时光 `search`／`exploreKinds`／`explore`／`toc` 必须返回数组或既有列表 wrapper（`books`／`kinds`／`chapters`／`list`）。合法空数组和 JSON 字符串包装继续支持；畸形 JSON 或非列表值报 `SCRIPT_PROTOCOL_ERROR`（公开 `SOURCE_FAILURE`），不能伪装成成功空列表。正文等非列表动作及 CatVod 普通字符串语义不变。

## 规则与脚本

公共层实现 HTML/CSS、XPath、JSONPath、正则、切片、属性、`@put`／`@get`、变量、请求模板、Header、字符集和阶段上下文。正则匹配接替换时使用匹配文本，未替换时保留匹配结构。Legado 外观同步的宿主调用通过 AST 转换为异步 capability 调用；显式 `read().then/catch/finally` 的接收者保持 Promise，不改写为 `(await read()).then/catch/finally`，原显式 await 和同步外观调用继续传播。无法安全传播 `await` 的回调（含命名函数／函数变量传入同步 Array 方法，即使内部使用显式 Promise 链）、构造器或其他结构返回 `UNSUPPORTED_SCRIPT_ASYNC`，不返回 Promise 真值或空值冒充兼容。

受限 `java`／`Packages` 只提供已审计的兼容对象，例如请求、Cookie、缓存、规则、URL 和 Jsoup 子集；常见 URL 后缀请求选项、`java.setContent`、Cookie 方法、Jsoup base URL／`abs:` 属性、Elements 深克隆及 `Jsoup.connect(...).execute().body()` 的异步转换在该边界内实现。它们不映射真实 JVM 类。JAR、DEX、Rhino ClassShutter、任意反射和文件路径均不支持。

请求的独立 `contentType` 在未显式给出同名 Header 时写入实际请求头；大小写无关的显式 Header 优先，正文编码采用最终类型。`fetch` 的 ArrayBuffer／typed-array 视图按原始字节发送，保留视图 offset，不发送内部 JSON 标记。

轻悦时光 bridge 的 HTTP、Cookie、缓存、编码和静态 DOM 能力可直接执行；来源页面的非脚本 DOM 会作为每次动作的隔离文档。远程 HTML 只通过公共 `http.request` 获取，默认按声明顺序回退最多 8 个候选；非 2xx、网络失败、单次请求超时、无有效脚本响应或候选外部脚本失败可尝试下一候选，资源超限、取消、宿主能力失败和整体启动截止立即终止。最终响应 URL 是相对外部脚本、`fetch` 和 `location` 的基准，不使用定义文件 URL 冒充页面地址。

guest 提供实际样本使用的 `fetch`、`Headers`、`Response`、`AbortController`、URL、DOMParser、location、performance、`matchMedia`、受限 `document.write`、随机数、SHA-1、jQuery 常用遍历/克隆和 CryptoJS 摘要、HMAC、AES、DES/WordArray 子集。同一文档节点跨查询保持身份，重复查询不重复消耗句柄，同父 jQuery `parent()` 去重；缺失属性分别保持 DOM `null`、jQuery `undefined` 和 Jsoup 空字符串语义。WordArray 的普通数组按 32-bit words 解释，缺省长度为每 word 四字节；typed array 仍按字节解释。结果中的 BigInt、循环结构或抛错 getter 明确报 `SCRIPT_EXCEPTION`，不拖成超时或归为上游响应错误。

浏览器全局预留轻悦时光旧脚本使用的可选 `toc` 别名；它仅避免脚本把未声明别名回写到 `window` 时在初始化阶段崩溃，不新增动作，目录动作仍由协议函数 `chapter` 提供。

Legado `java.webView(html,url,script="",delayTime=1000)` 与两个五参数变体 `webViewGetSource`／`webViewGetOverrideUrl` 保持 Reader 参数顺序；变体第四参数分别为匹配正则，第五参数为延时。sourceRegex 返回匹配资源 URL，overrideUrlRegex 返回导航 URL，不将资源 URL 当正文下载。`startBrowser`／`startBrowserAwait`／`showBrowser` 将原生页面结果包装为 StrResponse，脚本可调用 `.body()`／`.url()`／`.raw()`／`.code()`。Web 原参数中的 null 槽位不压缩；QYSG 未知签名不套用 Legado，参考实现证据及 Cookie 载体见[书源仓库 Web ABI](source-library.md#已核实的-web-helper-abi)。

jQuery、内置 CryptoJS／js-sha1 及已知 AdGuard、Cloudflare、`bt-stats.js` 遥测或浏览器注入脚本不会从网络重复加载；脚本要求未内置的 CryptoJS 顶层算法时，仍按外部脚本预算经受信 HTTP 获取，不伪装成已支持。浏览器、验证码、动态页面和应用交互转为 `web.interact`；source 模块默认装配可取消的 Web broker，由原生客户端处理 `web.pending/respond`。

`AbortController` 可立即终止 guest 对 `fetch` 的等待；底层 capability 由主线程跟踪至宿主 I/O 真正完成，不因动作结束提前记为已回收，也不设置网络任务数量门槛。`cache.all*`、登录信息和书籍变量仍按 source/generation 隔离，不采用上游“所有书源共享缓存”的语义。仓库恢复仅向当前 generation 注入持久私有数据副本；失败候选与退休请求不写回。

## 生命周期与 RPC

`apps/runtime/modules/source-runtime.mjs` 提供：

```text
source.runtime.load
source.runtime.invoke
source.runtime.status
source.runtime.unload
source.runtime.close
source.runtime.library.*
source.runtime.web.pending
source.runtime.web.respond
source.runtime.rules.process
```

`PUT /v1/source/sources/{installationId}/{sourceRevision}?basename=...` 一次上传原始源码，接受无路径 `.json/.js/.html/.htm`，长度 1 至 16 MiB，严格 UTF-8 并保留既有 BOM 行为。POST body 是普通 JSON，响应统一 `{ok:true,contract:"source.runtime.v2",value}` 或 `{ok:false,contract,code}`；invoke 的 value 即业务值，控制正文没有普通字节配额。

完整候选按 installation/sourceRevision 拥有，同键同内容幂等，不同内容报 SOURCE_INTEGRITY。新完整上传替换未采用候选，部分 body 不发布；指定版本不存在报 SOURCE_NOT_FOUND。字段/身份/CAS 前置拒绝不消费候选，取入试运行后失败释放；未采用候选在下一次完整上传、卸载或关闭释放，无分片、token、TTL、Base64 或传输摘要。原始 uploadHash 与规范化 contentHash 分别计算并复用，不能混淆。

load 必填 `installationId,sourceRevision,revision,expectedRevision`；protocol/version/metadata 可选，默认自动识别、1.0.0、空对象，metadata 保留 32 KiB 格式边界。invoke 必填 `installationId,revision,generation,action,payload`，可选 timeoutMs 为 1 至 60000；unload 必填 installationId/revision，generation 可选。

`rules.process {request}` 单独承载小说身份、归并、章节匹配、净化／简繁、规则校验及固定 URL 正则匹配，返回原纯规则结果，不接受客户端程序。Node owner 接受完整规则列表，稳定排序后逐条使用原规则预算，最后统一简繁；默认 3000ms、URL 匹配 2000ms，不增加规则预算业务上限或整个 transform 的固定截止。`TIMEOUT`／`CANCELLED` 保留原分类，失败实际释放后可重建，close 等待所有已接受规则任务排空。RPC 字段、错误及所有权以[书源仓库的小说纯规则契约](source-library.md#小说纯规则)为准；原生本地配置不迁入 Node 存储。

加载使用 installation、revision、expectedRevision 做 CAS，调用与卸载核对 generation；候选失败保留原实例，发布后清理失败触发回滚或明确 `SOURCE_CLEANUP`。开发版不提供旧数据迁移或旧契约兼容层。

首次 load 尚未发布 binding 时，同 revision 且省略 generation 的合法 unload 会先捕获并停止真实初始化 owner，再等待同安装事务。过期 revision/generation 不得抢占候选；针对该候选的排队卸载和迟到取消不得触及同 ID 的继任者。首次候选已被取消并清理后，unload 不保证成功返回；以撤权与实际资源退出为准。

卸载清理失败仍报告 `SOURCE_CLEANUP`；host 已移除的实例不再保留 RPC binding，其他实例的状态与调用不受影响。退休 profile 和物理任务的清理债务独立保留，不因 binding 删除冒充完成。应用关闭同时检查模块抛错与失败信封，仍等待全部模块清理，并以公共 `INVALID_RESPONSE` 报告失败；重复关闭复用同一结果。

每个实例的原生 Runtime、两个 Context、profile、DOM registry、变量和虚拟路由独立。源码 16 MiB 与 QuickJS 栈边界保留；移除脚本内存配额、普通响应/state 累计配额与 OOM 自动重启。初始化/请求默认 60 秒、清理 3 秒，不设单段同步时限或固定实例/并发/timer/网络/profile 数量。主控可取消卡死执行并等待真实原生线程退出。

初始化整体受同一截止信号约束，候选 HTML 和外部脚本请求按实际 I/O 所有权登记。同一安装的执行生命周期事务有序，永久仓库管理事务统一串行提交；动作可在 await 等待期间交错推进，各自保留请求身份、阶段上下文和取消 fence，status 不等待慢实例。HTTP abort/未完成 response close fence 结果，正常 finish 不取消完成工作。Reader DOM 文档/节点/字符串、HTML 候选、外部脚本及规则解析预算保留；目录与正文分页使用去重队列，不设固定页数截断，仍可取消或超时。`maximumParseBytes` 只用于解析，不限制普通 HTTP body、变量、Cookie、storage 或路由状态。

iOS 与 Windows 宿主均通过当前 HTTP 装配 source，原生阅读业务消费书源仓库、正文块及受信图片；平台装配与验证边界分别见[iOS 运行底座](../ios/runtime-preflight.md)和[Windows 平台接入](../windows/app-runtime.md)。不提供旧后端或 wire 兼容路径；通用验证不代替两端阅读 UI 验证，见[通用验证范围](script-runtime.md#构建发布与验证边界)。

## 兼容边界

加载成功只证明协议和初始化可执行，不证明上游仍有效。真实验收必须串联搜索、详情、目录、正文及来源声明的其他动作，并分别归因上游失效、账号/地区配置、宿主能力缺失、引擎不兼容、资源限制和清理失败。见[外部来源验收](source-acceptance.md)。
