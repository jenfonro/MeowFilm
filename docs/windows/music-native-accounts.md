# 原生音乐账号契约

共同的请求、匿名协议状态和授权交接规则见[平台请求](../common/music/platform-client-context.md)，Windows 适配见[平台客户端](music-platform-client-context.md)。本文件只记录原生账号 owner、命令、安全保存和展示事实；外部文件以[账号文件契约](../common/music/account-transfer.md)为准。

## 所有权与装配

- `MusicAccountOwner` 是 `music.accounts.v2` 的唯一 writer，拥有账号会话、登录交接和资料展示；不拥有安装标识、QIMEI 或匿名设备状态。
- `MusicNativeApplication` 将唯一 `MusicPlatformClient` 注入 `MusicAccountProtocol`。后者以 `account.verify/account.profile/qr.create/qr.poll/qr.cancel/sms.send/sms.login` 消费共同 Node 结果，不保留原生平台 HTTP、签名或设备配方。
- `NativeDatabase` 由 App 持有并借给账号 owner。构造 owner 不读磁盘或联网。
- 账号、展示资料及协议状态按明确 owner 保存在共享 SQLite relations。凭据使用明文 typed fields，不使用 DPAPI；不读取或清理旧数据。
- 页面只调用原生能力接口，不读取 Cookie、设备材料或保护文档，也不拥有第二份账号状态。

## 当前账号关系

`MusicAccountRepository` 通过共享数据库保存 `windows_music_account` 与 `windows_music_account_profile`。每个平台一行，当前账号命令选择 `wy`、`tx`、`kg`；字段包括 version、generation、cookie、scope、validatedAt 与关联资料。凭据为明文类型字段，匿名平台设备状态由 Node owner 管理。

常规读取只投影所请求平台；列表读取当前三个槽位。`AccountDocument` 是供既有领域逻辑使用的有界内存投影，JSON 不落盘；`ValueFingerprint` 仅绑定内存账号基线，数据库并发由 `StorageRevision` 与事务 CAS 核对。缺少账号表示未登录，损坏字段或当前 schema 错误明确失败，不回退旧数据、不自动重置。

提交在原生存储队列内重读最新行，核对 owner revision 与每个变更账号 generation，再只保存实际变化的类型字段／资料关系。完整事务规则见[账号存储](music-account-storage.md)。

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

平台账号为 `wy`、`tx`、`kg`；具体认证／短信能力和冷却由共同 Node 操作执行。一次 poll 只消费一次规范结果，轮询由可见登录浮层拥有，每次查询完成后至少间隔 3 秒再查询，遵守网易云／酷狗的轮询冷却。关闭、切平台、隐藏或退出退休其代次。Node 的 QR/SMS 实际派发、冷却和未知结果不由页面重建重置。

显式导入／验证、扫码和短信登录先完成 Node 的真实认证及资料读取，再由唯一原生 owner 对凭据执行 CAS，成功保存后才成为当前授权。展示详情独立发布到可选缓存，缓存落盘失败不逆转凭据提交。短期二维码、候选凭据和 handoff 不等于已保存账号。

二维码 PNG 不设 Base64 字符、编码字节或 chunk 数量配额；原生解码前仍验证 PNG 结构、CRC、允许的 chunk 类型、1024×1024 尺寸及像素对应的解压预算。发布纯像素，不保留元数据或网络地址。显式保存完整编码 PNG，不设文件字节配额；确认、有效代次、新建文件及安全路径限制保持不变。

## 会话与消费者

- `AcquireAsync` 只从共享 SQLite 的账号关系观察当前账号并签发带平台、用户、scope 和 generation 的会话。
- 需要在注册自身 session 前确认账号初始状态的原生消费者，复用账号 owner 的唯一首次权威观察；一次成功后不因歌曲／歌单切换或同身份请求重复读取整份文档。任一账号 CAS 结果未确认时该复用失效，必须重新观察权威文档。
- 首次观察的并发等待者共享同一物理读取。调用方取消只停止等待并返回 `CANCELLED`；即使全部退订，owner 仍持有物理观察，后来的消费者可加入，关闭会真正取消并排空。
- 每个消费者在发布结果或执行敏感请求前使用同一 owner 的 `IsCurrent` 核对会话。
- 登录、退出、账号替换、跨进程文档变化、可信认证失败和关闭都会推进或撤销当前代次；旧请求、旧失败和旧成功均无权覆盖新账号。
- `Revoke` 只能撤销传入的精确当前会话，不能按平台名撤销后来签发的会话。
- 无账号时仅允许显式定义的 guest 访问；设备缓存、会员资料和脚本均不能伪造登录。
- 原生授权经同一 `MusicPlatformClient` 发布到 Node；平台设备关联由 Node 执行。普通响应不再触发原生客户端快照／Cookie 二次校验。

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
- “等待扫码”“等待确认”只在登录浮层内显示，轮询开始时保留当前状态；`AccountNotice` 从 `AccountStatus` 派生并排除这两种进行中状态，供统一通知出口发布操作结果与错误。
- 标志来源与转换记录随根 `LICENSES/NOTICE.md` 由当前 Windows 许可打包入口携带。

## 写入、取消与关闭

- 账号文档由其稳定 owner 的队列形成变更，共享存储按准入次序完成 CAS。账号详情使用独立可选缓存，成功刷新直接发布内存并安排落盘；缓存写入不进入凭据的 pending/reconcile，写失败不会产生业务 `COMMIT_UNKNOWN`。
- 账号详情/VIP读取不由最后一个等待者关闭；App 关闭准备显式退休并排空这些读取。VIP签到等用户写事务保持独立语义，不因本次读取调整而重放。


- 凭据写入在账号 owner 的存储队列内重读当前账号行、校验当前字段并执行 revision CAS；generation 与凭据在同一事务提交。
- 同平台基线变化返回 `STORAGE_CONFLICT`；提交结果无法确认返回 `COMMIT_UNKNOWN` 或对应 handoff 未确认错误。两者都不自动重试或重放登录。
- 调用方取消只撤销尚未进入原子提交点的工作。已准入写入必须等待真实结算，不能把取消解释为回滚。
- 页面取消已开始的账号核对读取属于取消结算；应用关闭准备等待它完成，不再次取消该确认读取或把正常离页改报为未知提交。
- 显式重新观察账号状态只读取权威文档；失败 owner 不通过丢弃 pending、影子状态或额外 writer 恢复。
- 关闭先停止新调用、撤销首次观察和全部已签发会话，再等待网络、协议、存储和通知真实结算；已发布但尚未进入物理存储计数的首次观察同样属于关闭排空范围。最后才释放协议，共享数据库由 App 在全部 owner 排空后释放。
- 原生账号读取／安全提交与 Node publication 分别排空；`MusicAccountProtocol.DisposeAsync` 阻止新调用、取消并等待其已登记工作。共同匿名工作随运行器模块关闭，不建立原生设备回执协调器。

## 安全边界

- Cookie、Token、手机号、验证码、二维码私有材料、设备标识、请求体和原始平台响应不得进入日志、普通 DTO 或异常文本。
- 平台 HTTP、认证、动态携密目标和响应资源边界在共同 Node 层执行；原生保留最终账号授权和安全存储，不自动刷新凭据、跨平台切换或执行远端注销。
- 对外错误只暴露有限 code 与可选冷却时间。上游 message、URI、header、body 和 cause 不透传。
- 账号详情、播放上报、收藏同步和歌单同步共享同一账号 owner，但各自保留独立的请求、取消和权限边界。

## 账号文件传输

文件与批次行为以[共同账号文件契约](../common/music/account-transfer.md)为准。Windows 使用独立的 `IMusicAccountTransferSource` 原生接口，不新增 JSON／Node 命令；`MusicAccountTransferFile` 持有显式用户文件的完整字节，读写和解码不设文件大小配额，使用后清零。`MusicAccountTransferPlan` 持有 owner 捕获的完整账号值 fingerprint 与授权 epoch，普通序列化和调试字符串不包含凭据。界面只能取得平台冲突摘要及最终逐平台结果。

- 准备先完整检查文件结构，再在账号磁盘队列中捕获各平台基线。跳过平台不调用网络、不写账号；计划只能消费一次，策略不跨批次保存。
- 候选通过 `MusicAccountProtocol.ValidateTransferAsync` 进入共同 Node 认证，不先发布为当前账号；失败候选不替换已保存授权，不恢复原生设备 writer。
- 候选身份与现有登录资料／权益读取完成后，仍由 `MusicAccountOwner` 在同一个物理队列中核对准备值和 epoch、执行文档 CAS 并读回。只有成功读回才推进账号资格，复用 `SessionChanged` 的目录、搜索、歌词、曲库同步、反馈、Chorus 和播放失效链。新账号得到本机生成的新 generation／scope。
- 文件传输由 `MusicNativeApplication` 的同一 pending/lifetime 关闭边界管理；逐项未知回执会登记 `accountReceiptUnknown`，正常关闭失败，只有显式账号读取可以核对，不自动重试。已准入写入不传页面取消 token，等待真实写入与读回；取消停止后续平台。
- 用户显式导出只读取已保存且未撤销的账号，不联网；JSON 作为用户选择的外部交换文件，不属于内部持久化。

Windows 以 Cookie 字符串承载当前协议凭据；外部文件的字段、别名、数字及字符串语义沿共同 B09 契约，不把它改成内部文档迁移入口。无法安全表达或缺少真实认证材料时逐平台失败，不从其他安装补造。

本轮只有静态接线核对，账号、文件传输和关闭竞态尚无运行通过结论。

原生文件解码仅投影当前支持的凭据字段与 QQ 文件别名，再检查实际 Cookie 字符和身份。未知嵌套 credential 不进入 Cookie 或导出。网易设备补齐、酷狗 GUID→MID 关系与设备回退、QQ 可选数值的请求转换归共同 Node 配方；原生不预先要求设备字段齐全或所有可选数字符合统一拼写。最终 `account.verify`、真实 platform/userId 对比、账号基线及 CAS 保持不变。
