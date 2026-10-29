# 原生媒体资源租约

`resource-media-v1` / `INativeMediaResourceLease` / `NativePlayback.OpenResourceAsync` 是 Media 的受限原生输入契约，与普通 URL、不可变离线 read lease 共用媒体生命周期。

不持久化地址、随机 capability 或运行会话。无能力的调用方不能换普通 URL 绕过拒绝。共享边界保留其地址、预算、授权、撤权和实际释放规则。

## 生产约束

- 只接受已由原生协议适配校验的 lease；root 固定 literal `127.0.0.1`、
  HTTP 和实际登记端口，拒绝别名、其他本地地址、用户信息和 fragment。
- `Authorizes(Uri)` 由可信的原生领域适配实现，必须逐次核对资源与安装／版本／
  会话／站点的绑定。共享层不解释业务源或站点字段。
- Header 在首次准入前快照，普通头校验保持；每个 root、重定向、manifest、
  分段／密钥和实际连接仍经过权限与地址政策。即便适配器错误地返回 true，
  也不能授权其他 loopback 端口、私有地址或 DNS 重绑定。
- 公共子资源同时需要 lease 许可及原有 public IP／DNS 政策。跨 origin 继承
  原生已有的凭据去除规则，不发送原 root 的 Cookie、Authorization 或 capability。
- 生产 resource session 使用资源租约的授权策略。
  普通数据 API 不经 Node。这不是全量媒体代理或当前 App 的普通业务入口。
- 可变清单使用 resource lease；媒体格式、加密、预算和容器校验复用自适应媒体层。

## 生命周期

调用 `OpenResourceAsync` 即转移独占所有权，包括参数错误、取消、dispatcher
不可用。新请求预检失败不打断旧播放。已接受会话直到原生 Stop、回调、传输和
监听完成才回收 lease；替换等待真实 release，不只看取消信号。

`Revoked` 必须可取消，且在实例停用、更新成功、卸载、运行时失联、保护或权限
失效时主动触发；暂停／缓存不延缓撤权。caller cancellation 同样绑定完整会话。
未知保存／未知桥回执由协议适配先阻断旧资源。Dispose 恰调用一次；回收失败
归一为不含地址／凭据的稳定错误并阻断播放器继续使用，不把失败当回收成功。
