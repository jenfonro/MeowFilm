# CatVod JS 运行时

Cat adapter 与 LX、Reader adapter 共用实例管理、QuickJS 隔离和 capability ABI，但保留独立 Spider 契约。它不把猫源伪装成音乐或书源动作。

## 脚本入口与动作

支持常见 `export default`、导出声明、`export { Spider as default }`、`__JS_SPIDER__`、`__jsEvalReturn`、`spider`、`Spider` 和 `rule` 入口，并按脚本实际方法声明：

| 公共动作 | Spider 方法 |
| --- | --- |
| `init` | `init` |
| `home` | `homeContent` |
| `homeVideo` | `homeVideoContent` |
| `category` | `categoryContent` |
| `search` | `searchContent` |
| `detail` | `detailContent` |
| `play` | `playerContent` |
| `proxy` | `proxy` |
| `action` | `action` |
| `destroy` | `destroy` |

返回字符串若是 JSON 对象或数组会解析为结构化值；其他返回值按 JSON 边界透传。

## Guest SDK

QuickJS realm 提供受限 `Buffer`、URL、定时器、HTTP helper、作用域存储、DOM helper 和 CryptoJS 常用摘要/HMAC/AES/DES 子集。Cat 源先以 Acorn AST 识别并改写真实模块语法；注释或字符串中的 `require(`、`process.`、`import(` 不会误报。真实静态/动态 import、外部 re-export、`import.meta`、`require(...)` 和 Node/Deno/Bun 成员调用明确拒绝。

脚本在初始化期保存的 `req`、`local`、DOM helper 或 `js2Proxy` 不保留 startup 权限；后续只在当前动作的 task scope 内执行。动作结束后再次使用旧异步回调会因 scope 失效而失败。

外部依赖不能通过 npm 或系统路径加载。需要新增兼容 helper 时，应在公共 Cat adapter／guest SDK 中实现并加入平台无关测试；不能让脚本绕过 ABI 调用 Node。

## 虚拟代理

`js2Proxy` 生成 `meow-source://<profile>/<route>`，路由元数据通过 `virtual.register` 绑定当前 generation。应用可将该地址映射到内部资源处理；脚本不能调用 `http.createServer`、监听端口或获得命名管道。

`proxy` 动作收到虚拟 URL 时，运行时解析为已登记的原 URL、headers 和 site key。更新、卸载或关闭会注销全部路由；注销失败使清理失败，不保留可误用的旧 generation。

## 排除项

本实现只支持 CatVod JS。TVBox Android JAR／DEX、任意 JVM 插件和 Android API 不在当前范围；Windows/iOS 也不会通过伪造 Java 环境执行这些二进制。未来 JAR worker 必须作为独立平台能力和安全边界立项。
