# 来源运行时 capability ABI

本文件是[共同契约索引](../common/README.md)中来源宿主能力的唯一来源。`@meowfilm/source-runtime` 的 Reader 与 CatVod Spider adapter 通过版本化 capability ABI 请求宿主能力；纯规则包不导入 `node:*`、Node／平台全局、JVM、文件系统或平台 WebView 类型。

适用入口为 `source@0.3.0`／`source.runtime.v2`。LX 的原有网络适配器与请求 `context.storage`、CatNode 的有限 Node 兼容和私有卷继续遵循各自契约，不改由本 ABI 包装。Node-independent 只描述该纯规则包，不描述当前 script-host 或 CatNode 承载。

小说应用纯计算使用同一 source 模块的 `rules.process {request}`，由 Node owner 通过 `NativeJsonProgram` 执行受信 `@meowfilm/novel-rules` 程序；它不是 guest capability，也不向来源脚本开放任意程序执行。净化／简繁与 Web URL 正则不再由客户端另持引擎；规则配置和阅读本地状态仍由原生拥有。固定 operation、逐条预算和错误契约见[小说纯规则](source-library.md#小说纯规则)。独立计算不持有 source profile，URL 匹配结果不能越过原 Web 请求取消、generation 退休和 Cookie 写回 fence。

## 版本与调用

当前版本为 `hostApiVersion = 2`，请求携带 `version: 2`。结构数据保持 JSON，原始 body/算法/编码字节为 Uint8Array，保留 typed-array view 的 byteOffset/byteLength，不以 Base64 或 JSON 数字数组搬运普通字节；脚本显式要求的 Base64 编码算法不在删除范围。

存储等能力的 JSON 业务值保留 64 层格式边界；内部参数列表、scope 和异步完成信封不挤占业务值深度。宿主适配中的树转换不依赖逐层 JavaScript 递归；这不移除用户脚本自身的执行栈限制，也不放宽循环、非法 Unicode 或非有限／不安全数值的拒绝。

Portable guest 常态执行栈仍为 256 KiB。可信 core 仅在同步、纯字符串的 Cat 脚本标准化与 capability 脚本转换（含完整 AST 遍历）期间使用 QuickJS-NG 默认的有限 1 MiB 栈作用域；不重置栈顶，不执行 guest、宿主回调、thenable 或 pending jobs，正常、异常及取消退出均恢复原预算。该入口由 core 私有捕获，guest 不可访问或通过跨 realm helper 借用；独立 JSON 程序的 512 KiB 预算不变。普通预处理语法错误为 `SCRIPT_PROTOCOL_ERROR`，Acorn 栈耗尽包装及原生栈溢出为 `RESOURCE_LIMIT`，取消／超时保留原分类。

原生 owner 线程显式保留 2 MiB 物理栈，覆盖已有最大 1 MiB 执行预算及宿主调用／异常展开余量，不依赖平台默认线程栈大小；该空间不增加脚本执行预算，关闭仍等待线程实际退出并 join。

普通 HTTP、ABI bytes 和会话状态没有运行器自设内存配额。`maximumParseBytes` 是解析入口预算，仅用于 DOM/规则等实际解析，不作为通用请求或状态上限。取消信号和 installation/generation scope 由宿主上下文携带，不暴露宿主对象。source scope 固定含 installationId、generation、protocol、siteId: source 和宿主生成的 requestId，用户输入不能覆盖。

当前能力：

| 能力 | 语义 |
| --- | --- |
| `http.request` | 异步 HTTP(S)，输入 method、URL、headers、body、timeout、redirects、cookie profile；输出 status、headers、final URL 和原始 body |
| `cookie.get/set/clear` | 按 profile 隔离的 Cookie |
| `storage.get/set/delete` | 按 profile 隔离的 JSON 键值存储 |
| `encoding.encode/decode` | 非 UTF-8 字符编码，在 source core/native 所属线程同步计算 |
| `crypto.digest/hmac/aes/random` | 固定算法集合的摘要、HMAC、AES-CBC／ECB及随机字节，在 source core/native 所属线程同步计算 |
| `web.interact` | 登录、验证码、动态页面等可选平台交互 |
| `virtual.register/unregister` | generation 绑定的虚拟资源路由 |

未知能力、未知字段、非法 typed bytes、非 HTTP(S) URL、非法算法或错误响应均明确失败，不静默降级。HTTP(S) URL 可包含调用方提供的 userinfo，Host／Origin 等显式请求头保留；Content-Length、Transfer-Encoding、Upgrade 和代理认证等逐跳／传输控制字段仍拒绝，合法 Connection 提示由宿主处理而不透传。

## 作用域与生命周期

profile 使用 source 领域及明确代次：

```text
source:<installationId>:<generation>
```

Reader/CatVod 的 HTTP 与显式 Cookie 操作共用 source profile 下的 `:http` Cookie jar，并与 `:storage`、`:web` 等状态分离。source 不与 LX 或 CatNode 共用 Cookie、存储或虚拟路由。更新、失败候选、卸载和关闭必须通过 owner 的 `releaseProfiles(prefix, except)` 释放退休 generation；缺少清理能力的 owner 不能装配。虚拟路由注销失败属于清理失败，不设置 profile 固定数量门槛。

异步 capability 由所属原生线程转交 Node 主控中的能力 owner，显式同步算法在所属执行线程运行，物理 I/O 的归属保留至实际完成。取消和超时只封闭逻辑结果；guest `AbortController` 可先结束脚本等待，动作 scope 关闭会撤销回调，但不把不合作 I/O 算作已完成，也不通过固定并发或网络任务数拒绝新工作。HTTP owner 在真实 I/O 完成后、写回 Cookie 或发布成功前再次检查取消与截止；迟到响应不能覆盖或重建退休 profile，内部截止保留 `TIMEOUT`。来源初始化使用同一有界启动 scope；轻悦时光远程 HTML 候选和其外部脚本都经 `http.request` 登记为 adapter 物理任务，并继承同一启动取消信号。关闭不能以 signal 已触发冒充资源已回收，未排空的任务和退休 profile 清理失败保持可观察。

Promise／原生 await／异步迭代与清理回调保留登记任务身份；A 已结束或取消后由 B 唤醒的旧延续仍属 A，发能力时返回 `STALE_SESSION`，不会进入 B 的 owner scope。初始化保存的 Cat `req`／`local` 按有效当前动作工作；Legado 动作 helper 与 DOM handle 仍绑定所属请求，旧延续也不能借取 B 的 helper／handle 绕过身份检查。纯 JavaScript 计算不因此被禁用，正常动作内的 await／then／timer 及 thenable 继续工作。

默认 Node owner 的 storage、Cookie、虚拟路由，以及规则会话变量不保留旧单项/累计脚本内存预算；既有业务字段和数据格式仍检查。DOM 文档/节点/字符串、远程 HTML 候选与外部脚本解析预算继续存在，不得转用于普通 HTTP body 或变量。目录与正文使用多链接去重队列，取消和截止控制执行，不设固定页数截断。资源按请求、generation 和 profile 生命周期回收，不以配额删除为由遗弃物理工作。

同一动作产生的一批虚拟路由在返回业务结果前统一注册。任一路由注册失败时，运行时注销本批已尝试项并丢弃未提交项；只有注销明确返回 `true` 才清除债务，`false` 或抛错的项由 session close 再次回收，仍失败则报告清理失败。

## 网络与隔离

ABI 不维护域名白名单或按 origin／本机／私网分类的访问门禁。`http.request` 校验 HTTP(S) 语法、typed bytes 和重定向次数；Node owner 为每个请求建立一个覆盖完整重定向链的截止信号。调用输入中的 Authorization、Cookie、Host、Origin 跨 origin 重定向继续保留；自动 Cookie jar 则每跳按实际目标和 profile 选择，不沿用上一目标的 jar Cookie，也不读取其它账号。source 调用与初始化的默认等待上限均为 60 秒，清理为 3 秒；单次 HTTP 截止受所属调用/启动信号约束，不延长整个动作生命周期。

guest 只得到能力调用结果，不得到 Node socket、`fetch` 实现、Cookie jar、存储对象或宿主 callable。文件、进程、模块加载和任意 JVM 类不属于 ABI。

## 平台实现

通用 Node 主控的 `createNodeSourceCapabilities` 负责异步 HTTP、Cookie、作用域存储和虚拟路由等 I/O/profile 所有权；不承担编码或加解密同步计算。`encoding.*`、`crypto.*` 由 source core 在实例所属 native 线程执行，不为这些计算往返 Node 主线程。独立 owner 未注入 `web` 时返回 `CAPABILITY_UNAVAILABLE`；source 模块默认注入可取消的 `web.pending/respond` broker，由平台实际呈现。自定义 storage 必须同时实现 get/set/delete 和 `releaseProfiles`，不能把未清理状态当作成功。持久仓库另使用 profile snapshot/restore 与接受写入回调恢复私有状态，不向源开放 CatNode 私有卷；RPC、Cookie 回传格式和受信二进制图片见[书源仓库](source-library.md)。

`web.interact` 的 request 由 owner 注入权威 `cookies:[{name,value,domain,hostOnly,path,secure,httpOnly,sameSite,expires}]`，仅含当前源 HTTP profile 未过期 Cookie；显式 Cookie header 只合并到临时种子，不提前持久化。平台使用 cookies 初始化隔离 Web store，不从 header 扩大作用域。结果封套为 `{value,url,headers,cookieChanges?:[{url,setCookie}]}`；每条变化按自身 HTTP(S) origin 处理，与响应 headers 的 Set-Cookie 合为一次可回滚持久化事务，受取消及 generation fence 约束，再仅返回 value。sameSite 为 null／Lax／Strict／None，expires 为 null／Unix 毫秒；旧持久 Cookie 缺 httpOnly／sameSite 时默认 false／null。参数顺序、标量类型、前缀与删除规则见[Web ABI 有据表](source-library.md#已核实的-web-helper-abi)。

iOS 通过内置 `MeowScriptHost.framework`、Windows 通过受信原生 Node addon 承载原生核心，均使用 HTTP v1 和此能力 ABI，source 与音乐、Cat 共用各自平台的 Node 主控；平台装配与验证边界分别见[iOS 平台预检](../ios/runtime-preflight.md)和[Windows 平台接入](../windows/app-runtime.md)。不将通用验证视为全平台通过，也不据此声明 Android 已接入。Reader/CatVod 的规则与动作维护一份；只有新增现有 ABI 无法表达的系统语义时才扩展 ABI，并由各平台分别实现。不能据此宣称 CatNode 的 Node 兼容层可脱离 Node。

当前 ABI 文档描述通用迁移契约；通用原生构建与定向回归已通过（见[通用验证范围](script-runtime.md#构建发布与验证边界)），不能把接口或合成夹具视为平台业务通过。
