# 网易订阅实时 token

`netease@0.1.0`／`netease.token.v1` 只提供 Watchman Web SDK 的实时 token。账号 Cookie、订阅／退订的远端 ID、平台 HTTP 请求及未知写结果围栏仍由原生账号和曲库同步所有者管理。

## 契约

- `netease.token.get` → `POST /v1/netease/get`，输入严格为 `{}`。
- 成功：`{ok:true,contract:"netease.token.v1",value:{token}}`。token 非空且为合法 cookie-octet；不接受 Cookie、账号标识、SDK URL 或脚本输入。
- 失败码：`INVALID_REQUEST`、`CLOSED`、`CANCELLED`、`TIMEOUT`、`NETWORK_ERROR`、`UPSTREAM_HTTP`、`UPSTREAM_FORMAT`、`SDK_FAILURE`、`SDK_CLEANUP`。不返回 SDK 异常文本或原始响应。
- iOS／Windows App 明确装配本模块；通用 `createRuntime()` 默认脚本模块不变，受信装配可使用 `createNeteaseTokenModule()`。无旧四模块握手回退。

## 生命周期与信任边界

`@meowfilm/netease-token` 在首次调用时才创建 Worker、载入 `jsdom@24.1.3` 并获取 `https://acstatic-dun.126.net/tool.min.js`；App 启动、脚本列表和普通曲库操作不初始化 SDK。SDK 按固定产品和业务标识加载官方资源，不传用户账号 Cookie。它可以发送自己的设备／浏览器环境信息；这是官方 SDK 的远端执行依赖，不是离线加密，也不将 jsdom 宣称为安全沙箱。

固定脚本下载使用 Node 原生 HTTPS（10 秒下载预算、不跟随重定向），不依赖嵌入 Node 18 的 `fetch`／WASM。SDK 内的 DOM 网络由 jsdom 承载；整次初始化和取 token 仍受下述 Worker 预算约束。

同一运行器复用 SDK 实例，取 token 串行执行，但每次调用 SDK 生成新 token，不缓存 token、不落盘、不记录 SDK 控制台。排队取消只移除该请求；正在执行时取消、45 秒超时、SDK 失败或协议异常会终止所属 Worker，实际退出后才处理下一项。关闭拒绝未完成请求并等待 Worker 释放；清理失败不继续复用资源。下次合法请求可以重新初始化，原生不得自动重放已派发订阅。

## 原生消费

网易订阅／退订使用真实歌单 ID，token 进入 EAPI 加密 `header["X-antiCheatToken"]` 及对应 Cookie 头。原生获取 token 后重新核对账号、同步授权和取消；失败或过期授权不发送写请求。已派发写操作仍由完整账号目录读回确认，HTTP 成功不代替订阅状态确认。

替身协议测试、真实无账号 SDK 获取和真实账号订阅是不同证据；不使用真实账号写入验证此模块。
