# 来源运行时 capability ABI

`@meowfilm/source-runtime` 通过版本化 capability ABI 请求宿主能力。协议 adapter 只依赖该 ABI，不导入 `node:*`、Windows API、JVM、文件系统或平台 WebView 类型。

## 版本与调用

当前版本为 `hostApiVersion = 1`。每个请求都包含 `version: 1`，业务输入和结果必须是有界 JSON；原始字节使用规范 Base64。取消信号和 installation／generation scope 由宿主调用上下文携带，不暴露为 guest 对象。Reader/Cat scope 固定含 `installationId`、`generation`、`protocol`、`siteId: source` 和宿主生成的 `requestId`；guest 输入不能覆盖这些字段。

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

profile 首段按领域隔离：

```text
source:<installationId>:<generation>
music:<installationId>:<generation>:<siteId|startup>
```

Reader/Cat 的 HTTP 与显式 Cookie 操作共用 source profile 下的 `:http` Cookie jar，并与 `:storage`、`:web` 等状态分离。LX Cookie 按 installation、generation 和 site 隔离。相同 installationId 不会让 source 与 music 共用 Cookie、存储或虚拟路由。更新、失败候选、卸载和关闭必须通过 owner 的 `releaseProfiles(prefix, except)` 释放退休 generation；缺少清理能力的 owner 不能装配。虚拟路由注销失败属于清理失败。

取消和超时只封闭逻辑结果。guest `AbortController` 可先结束脚本等待，但对应 capability 的物理 Promise 在真实完成前持续占用并发和清理预算；动作 scope 关闭只撤销回调，不返还该额度。来源初始化也使用有界启动 scope；轻悦时光远程 HTML 候选和其外部脚本都经 `http.request` 登记为 adapter 物理任务，并继承同一启动取消信号。关闭不能以 signal 已触发冒充资源已回收。默认 Node owner 对 storage、Cookie 和虚拟路由同时设置单项及累计内存预算；公共规则核心还限制来源字节、DOM 文档／节点／字符串、远程 HTML 候选数、会话变量和待注册虚拟路由元数据的累计大小，不能借 capability 循环绕过 QuickJS heap 上限。

同一动作产生的一批虚拟路由在返回业务结果前统一注册。任一路由注册失败时，运行时注销本批已尝试项并丢弃未提交项；无法确认注销完成的项保留为清理债务，由 session close 再次回收。

## 网络与隔离

ABI 不维护域名白名单。`http.request` 校验 HTTP(S) 语法、响应大小和重定向次数；Node owner 为每个请求建立一个覆盖完整重定向链的截止信号，跨 origin 重定向不转发 Authorization/Cookie。它不承诺阻断本机或私网地址。

guest 只得到能力调用结果，不得到 Node socket、`fetch` 实现、Cookie jar、存储对象或宿主 callable。文件、进程、模块加载和任意 JVM 类不属于 ABI。

## 平台实现

Windows 当前由 `createNodeSourceCapabilities` 实现 HTTP、Cookie、内存作用域存储、编码、加解密和虚拟路由；真实 `web.interact` 未注入时返回 `CAPABILITY_UNAVAILABLE`。自定义 storage 必须同时实现 get/set/delete 和 `releaseProfiles`，不能把未清理状态当作成功。Node 是 Windows 承载实现，不是公共协议依赖。

Android/iOS 后续实现相同 ABI 时，可替换网络、持久化、Keychain／Keystore 和 WebView broker，而不改 LX、Reader 或 Cat adapter。只有新增现有 ABI 无法表达的系统语义时才扩展 ABI，并需要各平台分别实现；协议内部规则或动作变化不要求平台重复改写。
