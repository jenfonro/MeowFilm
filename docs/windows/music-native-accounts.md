# 原生音乐账号契约

## 所有权与装配

- `MusicAccountOwner` 是 `music.accounts.v1` 的唯一 writer，并实现账号会话、资料权益、酷狗设备和 QQ feedback 设备的统一生命周期。
- 网易云、QQ、酷狗的固定平台请求由 `MeowFilm.Capabilities.Accounts` 与 `MeowFilm.Capabilities.Platforms` 原生实现；账号业务不经 Node、脚本 RPC 或本地服务。
- `IProtectedDocumentStore` 由 App 持有并借给账号 owner。构造 owner 不读磁盘、不联网、不启动 Node。
- 页面只调用原生能力接口，不读取 Cookie、设备材料或保护文档，也不拥有第二份账号状态。

## 唯一当前文档

文档 envelope 只接受：

```json
{
  "version": 1,
  "entries": [
    { "key": "[...]", "value": {} }
  ]
}
```

当前最多八个 entry：

1. `wy`、`tx`、`kg` 三个平台账号；
2. 三个平台各自的账号资料与权益；
3. 一个酷狗设备；
4. 一个 QQ feedback 设备。

账号 key 为 `["meowfilm","music",1,"platform-account","<platform>"]`。资料和设备使用各自固定的当前 key。根对象、entry、value、字段集合、类型、范围、规范整数、关系和 fingerprint 在任何业务读取前整文档校验。

- 文档不存在时创建内存中的当前空结构。
- 未知 key、额外或缺失字段、重复属性、损坏 value、错误 fingerprint、非规范整数或超过预算均以 `CORRUPT_STORAGE` 失败。
- 失败不触发网络、写回、清理或局部修复，原字节保持不变。
- 不兼容数据明确失败并保留；清理或重置需用户明确确认。

完整存储事务规则见 `music-account-storage.md`。

## 账号命令

原生应用路由只接受以下账号操作：

- `music.accounts.list`
- `music.accounts.import`
- `music.accounts.validate`
- `music.accounts.logout`
- `music.accounts.cancel`
- `music.accounts.qr.begin`
- `music.accounts.qr.poll`
- `music.accounts.sms.send`
- `music.accounts.sms.login`

平台只允许 `wy`、`tx`、`kg`。短信链仅用于网易云；二维码按各平台当前原生协议执行。一次 poll 只完成一次查询，是否继续轮询由可见登录浮层的生命周期决定，关闭、切平台、隐藏或退出立即取消该代次。

导入或扫码取得的凭据必须先由对应平台协议验证精确身份，再由唯一 owner 执行 CAS。协议返回的资料、二维码和 handoff 都是短期内存对象，不能直接成为已保存账号。

## 会话与消费者

- `AcquireAsync` 只从保护存储观察当前账号并签发带平台、用户、scope 和 generation 的会话。
- 需要在注册自身 session 前确认账号初始状态的原生消费者，复用账号 owner 的唯一首次权威观察；一次成功后不因歌曲／歌单切换或同身份请求重复读取整份文档。任一账号 CAS 结果未确认时该复用失效，必须重新观察权威文档。
- 首次观察的并发等待者共享同一物理读取。调用方取消后立即退出活动等待者计数，但须等待该物理读取真实结算后才返回 `CANCELLED`；只有最后一个活动等待者取消才撤销物理 token，其他等待者继续取得同一观察结果。
- 每个消费者在发布结果或执行敏感请求前使用同一 owner 的 `IsCurrent` 核对会话。
- 登录、退出、账号替换、跨进程文档变化、可信认证失败和关闭都会推进或撤销当前代次；旧请求、旧失败和旧成功均无权覆盖新账号。
- `Revoke` 只能撤销传入的精确当前会话，不能按平台名撤销后来签发的会话。
- 无账号时仅允许显式定义的 guest 访问；设备缓存、会员资料和脚本均不能伪造登录。

## 资料与权益

`IMusicAccountDetailsSource` 提供：

```csharp
Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default);
Task<MusicAccountDetails> RefreshDetailsAsync(
    string platform,
    long expectedGeneration,
    CancellationToken token = default);
```

- `ReadDetailsAsync` 只读本地当前文档，不联网。
- `RefreshDetailsAsync` 是用户触发的显式平台读取，必须匹配页面显示的准确 generation。
- 资料和权益绑定账号 generation、scope、userId 与账号 value fingerprint；换号后的迟到结果不能写入。
- `Vip == null` 表示未知。`AudioLevels` 只用于展示，不授予播放、下载或离线权限。
- `missing`、`fresh`、`stale`、`deferred` 是显示新鲜度，不改变账号认证状态。

## 写入、取消与关闭

- 所有写入在账号 owner 的物理存储串行边界内重读当前文档、校验完整当前格式并执行整文档 fingerprint CAS。
- 同平台基线变化返回 `STORAGE_CONFLICT`；提交结果无法确认返回 `COMMIT_UNKNOWN` 或对应 handoff 未确认错误。两者都不自动重试或重放登录。
- 调用方取消只撤销尚未进入原子提交点的工作。已准入写入必须等待真实结算，不能把取消解释为回滚。
- 显式重新观察账号状态只读取权威文档；失败 owner 不通过丢弃 pending、影子状态或额外 writer 恢复。
- 关闭先停止新调用、撤销首次观察和全部已签发会话，再等待网络、协议、存储和通知真实结算；已发布但尚未进入物理存储计数的首次观察同样属于关闭排空范围。最后才释放协议，共享保护存储由 App 在全部 owner 排空后释放。

## 安全边界

- Cookie、Token、手机号、验证码、二维码私有材料、设备标识、请求体和原始平台响应不得进入日志、普通 DTO 或异常文本。
- 平台 HTTP 使用固定 origin、方法、路径、请求预算、响应预算、重定向策略和并发限制；不自动刷新凭据、不跨平台切换、不注册未要求的设备，也不执行远端注销。
- 对外错误只暴露有限 code 与可选冷却时间。上游 message、URI、header、body 和 cause 不透传。
- 账号详情、播放上报、收藏同步和歌单同步共享同一账号 owner，但各自保留独立的请求、取消和权限边界。
