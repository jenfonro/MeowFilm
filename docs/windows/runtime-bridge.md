# Runtime bridge v0.1.0

这是 meowfilm 的进程内协议基础设施，不包含业务路由。

## 当前契约

- 调用方注册明确方法并注入二进制传输。
- 帧格式为 4 字节大端正文长度加 UTF-8 JSON，协议版本为 1。
- 每个 envelope 包含 runtime session 身份和单调 request ID。
- 支持双向 request、result、error、cancel 及嵌套原生回调。
- 单帧最多 1 MiB，深度最多 64，只接受 JSON 值和安全整数。
- active handler、outgoing request 和 queued bytes 分别限额。
- 总排队字节不超过四个最大帧，并保留 `min(maximumBytes, maximumPending * 256)` 的控制容量；控制容量耗尽时关闭 peer，不能丢失取消或错误回复。
- 对象键是 ASCII schema 字段；Unicode 业务键通过字符串值或 `{key,value}` entry 数组传递。
- 重复解码键和非法 Unicode 明确拒绝。
- 取消只 fence 结果，不假装不合作 handler 已经停止。
- 错误载荷只包含有限错误码，不返回供应方原文、凭据或堆栈。
- 媒体字节不得通过该控制桥传输。
- connected-stream adapter 的同步 `onClose` 只负责安排并捕获异步清理；流关闭不代表领域工作已经结束。

该包不是内嵌 Node 引擎，不提供 eval、文件访问、socket listener、来源选择或隐式领域路由。Windows 命名管道、原生服务和实际业务方法由各自 owner 单独装配与验证。
