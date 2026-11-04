# Reader 书源运行时

Reader adapter 位于 `@meowfilm/source-runtime`，由 `source@0.1.0`／`source.runtime.v1` 管理，与 `music@0.6.0`、`cat@0.1.0` 一同装配。规则包不依赖 Node 或 Windows API；Windows 承载复用 script-host 的同一 WorkerSession、worker 协议与 QuickJS 后端。平台能力和清理语义以[来源 capability ABI](source-capability-abi.md)为唯一来源，不另建运行器。

## 协议

| 输入 | 处理 |
| --- | --- |
| Legado JSON | 执行搜索、发现、详情、目录、正文、登录状态、登录入口和图片处理规则 |
| 异次元旧 JSON | 导入为内部 Legado 模型；不维护第二套规则引擎 |
| 轻悦时光 HTML | 接受内联 HTML 或空白分隔的远程 HTML 候选 URL；提取脚本后在 QuickJS 中提供来源 DOM、jQuery 风格选择器和受控 `flutter_inappwebview.callHandler` bridge |

协议自动识别也可由加载请求显式指定 `legado`、`yiciyuan`、`qysg` 或 `cat`。原始 HTML、JSON 字符串包装的 HTML、带来源名称／地址的轻悦时光远程 HTML 对象和 CatVod JS 均可在 source 入口识别；异次元实例对外报告实际执行协议 `legado` 和 `importedProtocol: yiciyuan`。这里的 `cat` 是[独立 CatVod Spider 输入](cat-source-runtime.md)，不表示支持 `cat.script.v1` 的 CatNode 服务或 Node 兼容层。

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
```

动作输入和前一动作结果由应用显式传递；运行时不保存全局“当前书”。搜索、详情、目录和正文结果使用统一书籍、章节和正文结构，URL 按响应最终地址和来源地址归一。当前范围是底层规则和结果契约，不包含阅读 UI、书架、阅读进度或其他平台客户端接入。

## 规则与脚本

公共层实现 HTML/CSS、XPath、JSONPath、正则、切片、属性、`@put`／`@get`、变量、请求模板、Header、字符集和阶段上下文。正则匹配接替换时使用匹配文本，未替换时保留匹配结构。Legado 外观同步的宿主调用通过 AST 转换为异步 capability 调用；无法安全传播 `await` 的回调（含命名函数／函数变量传入同步 Array 方法）、构造器或其他结构返回 `UNSUPPORTED_SCRIPT_ASYNC`，不返回 Promise 真值或空值冒充兼容。

受限 `java`／`Packages` 只提供已审计的兼容对象，例如请求、Cookie、缓存、规则、URL 和 Jsoup 子集；常见 URL 后缀请求选项、`java.setContent`、Cookie 方法、Jsoup base URL／`abs:` 属性、Elements 深克隆及 `Jsoup.connect(...).execute().body()` 的异步转换在该边界内实现。它们不映射真实 JVM 类。JAR、DEX、Rhino ClassShutter、任意反射和文件路径均不支持。

请求的独立 `contentType` 在未显式给出同名 Header 时写入实际请求头；大小写无关的显式 Header 优先，正文编码采用最终类型。`fetch` 的 ArrayBuffer／typed-array 视图按原始字节发送，保留视图 offset，不发送内部 JSON 标记。

轻悦时光 bridge 的 HTTP、Cookie、缓存、编码和静态 DOM 能力可直接执行；来源页面的非脚本 DOM 会作为每次动作的隔离文档。远程 HTML 只通过公共 `http.request` 获取，默认按声明顺序回退最多 8 个候选；非 2xx、网络失败、单次请求超时、无有效脚本响应或候选外部脚本失败可尝试下一候选，资源超限、取消、宿主能力失败和整体启动截止立即终止。最终响应 URL 是相对外部脚本、`fetch` 和 `location` 的基准，不使用定义文件 URL 冒充页面地址。

guest 提供实际样本使用的 `fetch`、`Headers`、`Response`、`AbortController`、URL、DOMParser、location、performance、`matchMedia`、受限 `document.write`、随机数、SHA-1、jQuery 常用遍历/克隆和 CryptoJS 摘要、HMAC、AES、DES/WordArray 子集。同一文档节点跨查询保持身份，重复查询不重复消耗句柄，同父 jQuery `parent()` 去重；缺失属性分别保持 DOM `null`、jQuery `undefined` 和 Jsoup 空字符串语义。WordArray 的普通数组按 32-bit words 解释，缺省长度为每 word 四字节；typed array 仍按字节解释。结果中的 BigInt、循环结构或抛错 getter 明确报 `SCRIPT_EXCEPTION`，不拖成超时或归为上游响应错误。

浏览器全局预留轻悦时光旧脚本使用的可选 `toc` 别名；它仅避免脚本把未声明别名回写到 `window` 时在初始化阶段崩溃，不新增动作，目录动作仍由协议函数 `chapter` 提供。

jQuery、内置 CryptoJS／js-sha1 及已知 AdGuard、Cloudflare、`bt-stats.js` 遥测或浏览器注入脚本不会从网络重复加载；脚本要求未内置的 CryptoJS 顶层算法时，仍按外部脚本预算经受信 HTTP 获取，不伪装成已支持。浏览器、验证码、动态页面和应用交互转为 `web.interact`。当前 Windows 未注入 Web broker，因此需要该能力的动作明确失败。

`AbortController` 可立即终止 guest 对 `fetch` 的等待；底层 capability 由主线程跟踪至宿主 I/O 真正完成，不因动作结束提前记为已回收，也不设置网络任务数量门槛。`cache.all*`、登录信息和书籍变量仍按 source/generation 隔离，不采用上游“所有书源共享缓存”的语义。

## 生命周期与 RPC

`apps/runtime/modules/source-runtime.mjs` 提供：

```text
source.runtime.stage.begin
source.runtime.stage.chunk
source.runtime.stage.abort
source.runtime.load
source.runtime.invoke
source.runtime.status
source.runtime.unload
source.runtime.close
```

上传接受 `.json`、`.js`、`.html`、`.htm`，整份源码限 16 MiB，按长度、连续 offset、最多 64 KiB 的规范 Base64 分片、严格 UTF-8 和 SHA-256 校验。暂存从创建或最近有效分片起 5 分钟过期，由主动 timer 按最早过期时间回收；abort、加载消费和 close 释放暂存并重排/取消 timer，不设活动 token 数量门槛。控制请求和响应各限 1 MiB；这是 source 线协议边界，不是脚本网络响应大小。

加载使用 installation、revision、expectedRevision 和 generation 做 CAS；候选失败保留原实例，发布后清理失败触发回滚或明确 `SOURCE_CLEANUP`。开发版不提供旧数据迁移或旧契约兼容层。

卸载清理失败仍报告 `SOURCE_CLEANUP`；host 已移除的实例不再保留 RPC binding，其他实例的状态与调用不受影响。退休 profile 和物理任务的清理债务独立保留，不因 binding 删除冒充完成。应用关闭同时检查模块抛错与失败信封，仍等待全部模块清理，并以固定 `INTERNAL` 报告失败；重复关闭复用同一结果。

每个实例使用独立 worker、QuickJS/WASM heap、profile、DOM registry、变量和虚拟路由。源码、heap 和栈采用公共 `SCRIPT_LIMITS`：16 MiB、256 MiB 和 256 KiB；初始化和请求默认 60 秒，清理 3 秒。不设单段同步执行时限，也不按实例、并发、timer、网络任务、method 或 profile 固定数量拒绝工作。guest 卡住时由主线程按同一 WorkerSession 机制探测/终止，关闭等待真实线程退出；就绪实例的 OOM 一次重启行为见[共用运行时](script-runtime.md#quickjs-执行边界)。

初始化整体受同一启动截止信号约束，候选 HTML 和外部脚本请求均登记为 adapter 物理任务；调用串行维护本实例阶段语义。HTTP 响应和 ABI 字节受单脚本内存预算约束，Reader 的 DOM 文档/累计节点/字符串、分页、候选 HTML、外部脚本和规则解析预算继续保留。一次结果中的多个 DOM 值共享同一节点和字节额度；解析资源边界不等于固定并发或网络任务配额。

## 兼容边界

加载成功只证明协议和初始化可执行，不证明上游仍有效。真实验收必须串联搜索、详情、目录、正文及来源声明的其他动作，并分别归因上游失效、账号/地区配置、宿主能力缺失、引擎不兼容、资源限制和清理失败。见[外部来源验收](source-acceptance.md)。
