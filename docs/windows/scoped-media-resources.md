# 原生媒体资源租约

`INativeMediaResourceLease`／`NativePlayback.OpenResourceAsync` 将一个媒体输入的真实所有权与释放回执交给 Media，与普通 HTTP(S) 和不可变 read lease 共用原生生命周期。它不是 URL 访问许可，也不要求来源是回环、指定 Origin 或某个资源 URL 图。

## 输入与回执

- HTTP(S) 地址及请求头由调用方提供，Media 校验实际 URI／HTTP／媒体格式，不分类公网、私网、域名、DNS 结果或端口来决定能否访问。
- 默认 HTTP、重定向、HLS／DASH 子清单、初始化、分段和密钥没有域名名单、Origin 或授权 URL 图准入。显式输入头快照随该输入请求传递；不保存／重放响应 Cookie，不把地址、头或密钥写入日志。
- 公共 lease 不再有 `Authorizes(Uri)`。`Revoked` 只表达来源／账号／运行器 owner 的失效，不承担地址许可。
- `RuntimeMediaAuthority(runtimeRevoked, releaseResource)` 管理同一运行器代次的准确 resource ID 和唯一释放 task。ID 是现行 media.release RPC 的 24 字节 base64url（32 字符）；它不与媒体 URL 路径／Origin 比较。地址可与 release ID 无路径对应关系。
- 不持久化短效媒体地址、请求头或运行会话。媒体格式、Range、表示一致性、资源预算与取消仍按对应输入处理。

## 生命周期

调用 open／prepare 即转移独占所有权，包括参数错误、取消和 dispatcher 不可用；失败不先毁可用 donor。外层已接管操作在校验前登记，close 先取消再等待真实 worker、局部资源、拒绝请求和准确 release 回执结束，随后释放 donor／surface／engine，不持 command gate 反向等待。

来源／账号／运行器撤权仍主动取消，暂停不会延缓；每份 receipt 和 child 恰好实际释放一次。重复 dispose／close 共享原回执，失败固定为不带 inner、地址或私有路径的 `MEDIA_RELEASE_FAILED`，继续尽力排空其余资源，不能假成功。UI 订阅或智能搜索早停不拥有实际媒体生命周期。
