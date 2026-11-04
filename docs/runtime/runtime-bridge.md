# Runtime bridge v0.1.0

这是 meowfilm 的平台中性通信基础设施，不包含业务路由。

## 当前契约

- 调用方注册明确方法并注入二进制传输。
- 帧格式为 4 字节大端正文长度加 UTF-8 JSON，协议版本为 1。
- 每个 envelope 包含 runtime session 身份和单调 request ID。
- 支持双向 request、result、error、cancel 及嵌套原生回调。
- 单帧最多 1 MiB，深度最多 64，只接受 JSON 值和安全整数。
- active handler 和 outgoing request 按实际生命周期跟踪，不设置数量额度。
- Node 出站编码缓冲不超过四个最大帧，Windows 原生对端不超过八个，均为取消／错误保留一个最大帧的容量；这是实际保留字节的背压，不是 HTTP 或业务并发政策。控制容量耗尽时关闭 peer，不能丢失取消或错误回复。
- 对象键是 ASCII schema 字段；Unicode 业务键通过字符串值或 `{key,value}` entry 数组传递。
- 重复解码键和非法 Unicode 明确拒绝。
- 两端提供复用同一语义校验的独立 JSON 值编解码；它不施加物理帧字节边界，供业务 owner 完成逻辑分片后使用。帧编码／解码仍严格保持 1 MiB，不把整份分片数据送入单帧。
- 取消移除尚未发送的队列节点并释放编码字节；已开始的 write／handler 保持归属直到真实结束，不把结果 fence 当作物理完成。
- 错误载荷只包含有限错误码，不返回供应方原文、凭据或堆栈。
- 媒体字节不得通过该控制桥传输。
- connected-stream adapter 的同步 `onClose` 只负责安排并捕获异步清理；流关闭不代表领域工作已经结束。

该包不是内嵌 Node 引擎，不提供 eval、文件访问、socket listener、来源选择或隐式领域路由。Windows 命名管道、原生服务和实际业务方法由各自 owner 单独装配与验证。
