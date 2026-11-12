# 原生音乐账号契约

客户端与授权的共同职责边界见[平台客户端上下文](../common/music/platform-client-context.md)，Windows 的设备初始化和登记见[客户端实现](music-platform-client-context.md)。本文件只记录账号授权、命令、交接和账号存储。

## 所有权与装配

- `MusicAccountOwner` 是 `music.accounts.v1` 的唯一 writer，拥有账号会话、登录交接、资料权益及账号绑定的 QQ 反馈 lease；不拥有安装标识、QIMEI 或匿名设备登记 writer。
- 网易云、QQ、酷狗的固定平台请求由 `MeowFilm.Capabilities.Accounts` 与 `MeowFilm.Capabilities.Platforms` 原生实现；账号业务不经 Node、脚本 RPC 或本地服务。
- `IDocumentStore` 由 App 持有并借给账号 owner。构造 owner 不读磁盘、不联网、不启动 Node。
- `music.accounts.v1` 仍使用 DPAPI，原命名空间、存储路径和密文格式保持不变；普通本地数据的明文存储不改变应用账号 Cookie 的保护。
- 账号 owner 与协议默认使用 `TimeProvider.System`；注入确定性时钟时装配须向两者传入同一时钟，资料取得时间校验与缓存新鲜度使用同一时间基准。
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

当前最多三个 entry，分别为 `wy`、`tx`、`kg` 平台账号。

账号 key 为 `["meowfilm","music",1,"platform-account","<platform>"]`。展示详情单独存于 `music.account-details.v1` 缓存，按平台直接读取/解码；设备状态仅存于独立客户端文档。凭据文档的根对象、entry、value、字段集合、类型、范围、规范整数、关系和 fingerprint 在业务读取前整文档校验。

- 文档不存在时创建内存中的当前空结构。
- 未知 key、额外或缺失字段、重复属性、损坏 value、错误 fingerprint、非规范整数或超过预算均以 `CORRUPT_STORAGE` 失败。
- 失败不触发网络、写回、清理或局部修复，原字节保持不变。
- 不兼容数据明确失败并保留；清理或重置需用户明确确认。

完整存储事务规则见[账号存储](music-account-storage.md)。

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

登录由自身维护请求生命周期，不受账号外层总并发或累计调用台账约束。短信同号码发送单飞并保留 60 秒冷却，取消或重新进入页面不能绕过冷却；冷却过期且无活动请求后由单个定时器回收记录，不保留十分钟发送次数或单号码登录次数台账。二维码保留创建冷却、180 秒有效期及平台轮询间隔，不额外计数 100 轮；只有二维码面板可见时才轮询，切换短信面板立即停止。

显式导入／验证、扫码和短信登录必须先由对应平台协议验证精确身份，在同一登录请求生命周期中获取一次资料与权益，再由唯一 owner 对账号凭据执行一次严格 CAS；绑定新账号的展示详情独立发布到可选缓存，异步落盘不影响登录成功。二维码、短信或登录响应自身的会员字段不是权益依据；各平台复用与手动刷新相同的固定权益接口，不重复验证刚确认的登录身份。协议返回的资料、二维码和 handoff 都是短期内存对象，不能直接成为已保存账号。

## 会话与消费者

- `AcquireAsync` 只从保护存储观察当前账号并签发带平台、用户、scope 和 generation 的会话。
- 需要在注册自身 session 前确认账号初始状态的原生消费者，复用账号 owner 的唯一首次权威观察；一次成功后不因歌曲／歌单切换或同身份请求重复读取整份文档。任一账号 CAS 结果未确认时该复用失效，必须重新观察权威文档。
- 首次观察的并发等待者共享同一物理读取。调用方取消只停止等待并返回 `CANCELLED`；即使全部退订，owner 仍持有物理观察，后来的消费者可加入，关闭会真正取消并排空。
- 每个消费者在发布结果或执行敏感请求前使用同一 owner 的 `IsCurrent` 核对会话。
- 登录、退出、账号替换、跨进程文档变化、可信认证失败和关闭都会推进或撤销当前代次；旧请求、旧失败和旧成功均无权覆盖新账号。
- `Revoke` 只能撤销传入的精确当前会话，不能按平台名撤销后来签发的会话。
- 无账号时仅允许显式定义的 guest 访问；设备缓存、会员资料和脚本均不能伪造登录。
- WY／KG 登录在请求边界消费当前本地客户端；QQ 普通授权请求没有 QIMEI／匿名 session 依赖。QQ 反馈每次实时核对账号后组合客户端 owner 的设备状态，独立客户端变化不撤销普通账号授权。

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
- 登录取得的资料与权益在账号提交成功前完成获取，凭据提交成功后写入展示缓存内存，不依赖设置页是否激活；显示消费者只读取绑定当前账号的本地详情，不再追加登录后的远程刷新。
- 资料和权益绑定账号 generation、scope、userId 与账号 value fingerprint；换号后的迟到结果不能写入。
- 明确会员的权益缓存保留到到期前一天；已进入最后一天时新取得的结果最多保留到实际到期。明确非会员缓存 24 小时；会员没有可靠到期日时同样最多缓存 24 小时，不编造日期。QQ／酷狗更早的已知产品开始或结束边界仍截断缓存。
- 缓存期内权益变化只由用户“刷新资料”更新；启动、首次进入设置、再次显示页面和缓存到期不触发自动远程刷新。到期只使本地显示新鲜度失效，其他自动刷新触发留待后续实现。
- `Vip == null` 表示未知。`AudioLevels` 只用于展示，不授予播放、下载或离线权限。
- 权益接口的网络、上游、限流或非认证响应失败仍是未知，不能伪装为非会员；身份验证失败不提交新账号，无法确认 CAS 不发布登录成功、不重放登录。
- `missing`、`fresh`、`stale`、`deferred` 是显示新鲜度，不改变账号认证状态。

## 设置页账号表格

- 平台列始终展示对应平台的本地官方标志与名称；账号列只展示当前账号身份。未登录的主行是“未登录”，登录后主行来自当前账号昵称，头像复用 App 注入的公共图片加载器。
- 平台标志与用户头像分列，头像加载中或缺失时不以品牌标志垫底，不在首次读取或页面重建时用平台标志覆盖用户头像。
- `MusicAccountTableRow` 只投影原生三平台账号和同步开关。酷我、咪咕是公共目录展示行，状态为“无需登录”，不虚构登录、资料、权益或同步能力。
- 登录后的次行展示当前会员摘要：`VIP有效 · yyyy.MM.dd` 或 `SVIP有效 · yyyy.MM.dd`；明确非会员或已到期时仅展示 `VIP无效`。平台没有提供到期日时不编造日期，未知或尚需核对的权益保留 `权益未确认`。
- “刷新”直接复用 `RefreshDetailsAsync`，更新昵称、头像与权益；同一在途刷新不重复准入，页面退出只停止等待与显示，owner 继续刷新既有本地缓存；再次刷新可加入同一在途读取。按当前预览使用紧凑次级动作与近黑登录按钮，退出确认区分确认与取消，保留主题及交互状态。
- “退出”只打开对应按钮下方的确认气泡；取消、Esc、点击外部、隐藏或卸载页面均不提交。确认绑定点击时的平台、用户与 generation，经 `MusicCapability.LogoutAccountAsync(account, confirmed)` 提交到唯一账号 owner，不依赖另一个平台的选择状态、不再弹出第二次系统确认。
- 换号或账号操作退休原确认；旧确认不能退出替换账号。退出仍使用现有本地 CAS，不触发远端注销，不删除本地曲库歌曲。
- 后台账号状态回读只更新账号快照，不清除当前登录、扫码、短信或保存结果提示；显式读取成功后才清除该提示。
- 标志来源与转换记录随根 `LICENSES/NOTICE.md` 由当前 Windows 许可打包入口携带。

## 写入、取消与关闭

- 客户端文档与账号文档各由其稳定 owner 的 FIFO 操作队列形成变更；共享存储再按准入次序完成 CAS。账号详情使用独立可选缓存，成功刷新直接发布内存并安排落盘；缓存写入不进入凭据的 pending/reconcile，写失败不会产生业务 `COMMIT_UNKNOWN`。
- 账号详情/VIP读取不由最后一个等待者关闭；App 关闭准备显式退休并排空这些读取。VIP签到等用户写事务保持独立语义，不因本次读取调整而重放。


- 凭据写入在账号 owner 的物理存储串行边界内重读当前文档、校验完整当前格式并执行整文档 fingerprint CAS。
- 同平台基线变化返回 `STORAGE_CONFLICT`；提交结果无法确认返回 `COMMIT_UNKNOWN` 或对应 handoff 未确认错误。两者都不自动重试或重放登录。
- 调用方取消只撤销尚未进入原子提交点的工作。已准入写入必须等待真实结算，不能把取消解释为回滚。
- 页面取消已开始的账号核对读取属于取消结算；应用关闭准备等待它完成，不再次取消该确认读取或把正常离页改报为未知提交。
- 显式重新观察账号状态只读取权威文档；失败 owner 不通过丢弃 pending、影子状态或额外 writer 恢复。
- 关闭先停止新调用、撤销首次观察和全部已签发会话，再等待网络、协议、存储和通知真实结算；已发布但尚未进入物理存储计数的首次观察同样属于关闭排空范围。最后才释放协议，共享保护存储由 App 在全部 owner 排空后释放。
- 共享匿名设备工作归属客户端 owner；账号取消／关闭撤销旧取得资格，应用再由客户端 owner 排空其真实任务。账号 list 本身不重读另一 writer 的未知客户端回执。

## 安全边界

- Cookie、Token、手机号、验证码、二维码私有材料、设备标识、请求体和原始平台响应不得进入日志、普通 DTO 或异常文本。
- 平台 HTTP 使用固定 origin、方法、路径、操作截止、实际响应缓冲及重定向策略，不再附加总并发或步骤计数限额；不自动刷新凭据、不跨平台切换、不注册未要求的设备，也不执行远端注销。
- 对外错误只暴露有限 code 与可选冷却时间。上游 message、URI、header、body 和 cause 不透传。
- 账号详情、播放上报、收藏同步和歌单同步共享同一账号 owner，但各自保留独立的请求、取消和权限边界。

## 账号文件传输

文件与批次行为以[共同账号文件契约](../common/music/account-transfer.md)为准。Windows 使用独立的 `IMusicAccountTransferSource` 原生接口，不新增 JSON／Node 命令；`MusicAccountTransferFile` 持有显式用户文件的有界字节，`MusicAccountTransferPlan` 持有 owner 捕获的完整账号值 fingerprint 与授权 epoch，普通序列化和调试字符串不包含凭据。界面只能取得平台冲突摘要及最终逐平台结果。

- 准备先完整检查文件结构，再在账号磁盘队列中捕获各平台基线。跳过平台不调用网络、不写账号；计划只能消费一次，策略不跨批次保存。
- 候选使用独立的短期 `MusicAccountProtocol` 执行状态，借用现有受限 HTTP 与客户端 owner。失败候选不会调用当前账号的 `Start`／`Fence`，也不会 replace/cancel 既有登录、资料读取或已签发 lease；网易响应设备 Cookie 检查格式与 deviceId 关联，不写安装文档。
- 候选身份与现有登录资料／权益读取完成后，仍由 `MusicAccountOwner` 在同一个物理队列中核对准备值和 epoch、执行文档 CAS 并读回。只有成功读回才推进账号资格，复用 `SessionChanged` 的目录、搜索、歌词、曲库同步、反馈、Chorus 和播放失效链。新账号得到本机生成的新 generation／scope。
- 文件传输由 `MusicNativeApplication` 的同一 pending/lifetime 关闭边界管理；逐项未知回执会登记 `accountReceiptUnknown`，正常关闭失败，只有显式账号读取可以核对，不自动重试。已准入写入不传页面取消 token，等待真实写入与读回；取消停止后续平台。
- 明文导出只读取已保存且未撤销的账号，不联网。正常账号文档继续使用 DPAPI。目的文件直接由用户选择，不建立应用自有凭据临时文件。

Windows 仍以 Cookie 字符串承载协议凭据：合法网易 `_ntes_nnid` 逗号原样保留；QQ 可选空字符串和规范 snake_case 字段保留；酷狗文件 t1 按原始字符串处理，原生登录响应中的数字 t1 则无浮点损失地转为十进制字符串。包含 Cookie 分隔符、控制字符或非 ASCII 等现有通道无法安全表达的值作为该平台 `INVALID_CREDENTIALS` 失败，不进行 URL 编码、截断或替换。历史账号缺失且无法证明关联的酷狗 seed 不从无关安装补造，导出保留实际已知材料，导入按材料完整性逐项失败。

QQ 的可选授权 token 可以保留空字符串，数值协议字段则不能：`expired_at`、`musickey_create_time`、`key_expires_in`、`login_type` 必须是无多余前导零的非负十进制整数，且不超过 `2^53-1`。登录交接、文件导入及旧存储导出均校验此契约；无效数值使该项导入失败或阻止导出成功，不补零、不丢弃字段。
