# 来源运行时 capability ABI

本文件是[共同契约索引](../common/README.md)中来源宿主能力的唯一来源。`@meowfilm/source-runtime` 的 Reader 与 CatVod Spider adapter 通过版本化 capability ABI 请求宿主能力；纯规则包不导入 `node:*`、Node／平台全局、JVM、文件系统或平台 WebView 类型。

适用入口为 `source@0.1.0`／`source.runtime.v1`。LX 的原有网络适配器与请求 `context.storage`、CatNode 的有限 Node 兼容和私有卷继续遵循各自契约，不改由本 ABI 包装。Node-independent 只描述该纯规则包，不描述当前 script-host 或 CatNode 承载。

## 版本与调用

当前版本为 `hostApiVersion = 1`。每个请求都包含 `version: 1`，业务输入和结果必须是预算内的 JSON；原始字节使用规范 Base64。ABI 字节与宿主响应缓冲受单脚本内存预算约束，控制帧和源码分片另外遵守[共用运行时](script-runtime.md#quickjs-执行边界)的线协议大小。取消信号和 installation／generation scope 由宿主调用上下文携带，不暴露为 guest 对象。source scope 固定含 `installationId`、`generation`、`protocol`、`siteId: source` 和宿主生成的 `requestId`；guest 输入不能覆盖这些字段。

当前能力：

| 能力 | 语义 |
| --- | --- |
| `http.request` | 异步 HTTP(S)，输入 method、URL、headers、body、timeout、redirects、cookie profile；输出 status、headers、final URL 和原始 body |
| `cookie.get/set/clear` | 按 profile 隔离的 Cookie |
| `storage.get/set/delete` | 按 profile 隔离的 JSON 键值存储 |
| `encoding.encode/decode` | 非 UTF-8 字符编码 |
| `crypto.digest/hmac/aes/random` | 固定算法集合的摘要、HMAC、AES-CBC／ECB及有界随机字节 |
| `web.interact` | 登录、验证码、动态页面等可选平台交互 |
| `virtual.register/unregister` | generation 绑定的虚拟资源路由 |

未知能力、未知字段、越界数据、非法 Base64、非 HTTP(S) URL、URL userinfo、非法算法或错误响应均明确失败，不静默降级。请求头拒绝 Host、Content-Length、Transfer-Encoding、Upgrade 和代理认证等逐跳／传输控制字段；合法 Connection 提示由宿主处理而不透传。

## 作用域与生命周期

profile 使用 source 领域及明确代次：

```text
source:<installationId>:<generation>
```

Reader/CatVod 的 HTTP 与显式 Cookie 操作共用 source profile 下的 `:http` Cookie jar，并与 `:storage`、`:web` 等状态分离。source 不与 LX 或 CatNode 共用 Cookie、存储或虚拟路由。更新、失败候选、卸载和关闭必须通过 owner 的 `releaseProfiles(prefix, except)` 释放退休 generation；缺少清理能力的 owner 不能装配。虚拟路由注销失败属于清理失败，不设置 profile 固定数量门槛。

capability 由同一 WorkerSession 转交主线程中的平台 owner，物理 I/O 的归属保留至实际完成。取消和超时只封闭逻辑结果；guest `AbortController` 可先结束脚本等待，动作 scope 关闭会撤销回调，但不把不合作 I/O 算作已完成，也不通过固定并发或网络任务数拒绝新工作。HTTP owner 在真实 I/O 完成后、写回 Cookie 或发布成功前再次检查取消与截止；迟到响应不能覆盖或重建退休 profile，内部截止保留 `TIMEOUT`。来源初始化使用同一有界启动 scope；轻悦时光远程 HTML 候选和其外部脚本都经 `http.request` 登记为 adapter 物理任务，并继承同一启动取消信号。关闭不能以 signal 已触发冒充资源已回收，未排空的任务和退休 profile 清理失败保持可观察。

Promise／原生 await／异步迭代与清理回调保留登记任务身份；A 已结束或取消后由 B 唤醒的旧延续仍属 A，发能力时返回 `STALE_SESSION`，不会进入 B 的 owner scope。初始化保存的 Cat `req`／`local` 按有效当前动作工作；Legado 动作 helper 与 DOM handle 仍绑定所属请求，旧延续也不能借取 B 的 helper／handle 绕过身份检查。纯 JavaScript 计算不因此被禁用，正常动作内的 await／then／timer 及 thenable 继续工作。

默认 Node owner 对 storage、Cookie 和虚拟路由保留单项及累计内存预算；规则核心保留 DOM 文档／节点／字符串、分页、远程 HTML 候选和外部脚本等解析预算，以及会话变量和待注册路由元数据的累计字节预算。这些是解析资源边界或单脚本内存边界，不是实例、调用、timer、网络、method、stage 或 profile 的固定计数配额，不能借 capability 循环绕过内存预算。

同一动作产生的一批虚拟路由在返回业务结果前统一注册。任一路由注册失败时，运行时注销本批已尝试项并丢弃未提交项；只有注销明确返回 `true` 才清除债务，`false` 或抛错的项由 session close 再次回收，仍失败则报告清理失败。

## 网络与隔离

ABI 不维护域名白名单。`http.request` 校验 HTTP(S) 语法、预算内的响应字节和重定向次数；Node owner 为每个请求建立一个覆盖完整重定向链的截止信号，跨 origin 重定向不转发 Authorization/Cookie。它不承诺阻断本机或私网地址。source 调用与初始化的默认等待上限均为 60 秒，清理为 3 秒；单次 HTTP 截止受所属调用/启动信号约束，不延长整个动作生命周期。

guest 只得到能力调用结果，不得到 Node socket、`fetch` 实现、Cookie jar、存储对象或宿主 callable。文件、进程、模块加载和任意 JVM 类不属于 ABI。

## 平台实现

Windows 当前由 `createNodeSourceCapabilities` 实现 HTTP、Cookie、内存作用域存储、编码、加解密和虚拟路由；真实 `web.interact` 未注入时返回 `CAPABILITY_UNAVAILABLE`。自定义 storage 必须同时实现 get/set/delete 和 `releaseProfiles`，不能把未清理状态当作成功。该实现不承诺原生永久落盘或向源开放 CatNode 私有卷。

Android/iOS 接入需另行实现承载与相同 ABI 的网络、持久化和 WebView broker，当前未完成这些端侧接入。Reader/CatVod 的规则与动作维护一份；只有新增现有 ABI 无法表达的系统语义时才扩展 ABI，并由各平台分别实现。不能据此宣称 CatNode 的 Node 兼容层可脱离 Node。
