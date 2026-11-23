# 音乐账号存储与生命周期契约

状态归属与受控更新遵循[共同平台上下文](../common/music/platform-client-context.md)。本文件只说明账号关系；当前 Windows 平台客户端的装配与关闭见[音乐平台接入](music-platform-client-context.md)。

## 唯一 owner 与数据身份

- `MusicAccountOwner` 是 `music.accounts.v1` 的唯一原生 writer；底层使用永久 `NativeDatabase`（`meowfilm-data.sqlite3`）的 `windows_music_accounts` 强类型表，账号展示详情缓存使用缓存库（`meowfilm-cache.sqlite3`）的 `windows_music_account_details` / `windows_music_account_audio_levels`。
- Cookie、设备密钥及协议材料与账号字段同库同事务以明文 typed columns 保存，不使用 DPAPI、Keychain、应用层加密或第二凭据仓库。新 schema 不探测、迁移或兼容旧存储。
- 凭据由账号 owner 严格事务管理；账号详情是独立可选展示缓存，不能由页面、平台 HTTP 或脚本直接写入。
- 账号类型行保存原生凭据；匿名设备／prepare 状态由 Node 保存，不由原生账号 owner 代管。
- 缺少所选账号行表示尚未保存；当前结构或类型字段损坏明确报 `CORRUPT_STORAGE`。选中平台时校验其必需字段、账号身份及 generation，不预检无关账号。
- 账号使用当前固定类型字段；不将未知 entry 或扩展写成内部文档，不兼容旧格式。其他平台账号不因单项更新重写。

## 当前账号关系

当前保存网易云、QQ、酷狗三个账号槽位。账号展示快照归 `windows_music_account_details` 与 `windows_music_account_audio_levels`，不参与凭据事务。

账号 Cookie、设备密钥及协议材料只存在于所属 SQLite 账号字段和有界内存会话中，不进入日志、错误、普通 DTO 或脚本存储。

## 命令与会话

- 账号命令包括列表、显式导入／验证、退出、取消、三平台二维码及网易云／酷狗验证码，真实协议由共同 Node 执行。
- `AcquireAsync`／`AcquireSettledAsync` 返回带平台、用户身份和 generation 的借用会话；消费者必须用同一 owner 的 `IsCurrent` 核对。
- 登录、退出、账号替换、观察到账号变化和关闭会推进或撤销相应授权资格；同值重读和无关匿名客户端变化不制造账号代次变化。
- 无账号时经共同 Node 使用当前匿名上下文，官方标准音频按真实配方资源事实消费。

## 写入与冲突

- 写入在同一 owner 的磁盘串行边界内读取当前账号行、核对预期账号及 owner revision 并 CAS；安全保存确认后才向共同 Node 发布当前授权。
- 原生持久化归口 `MeowFilm.Capabilities/Music/Accounts/Storage/`：`MusicAccountRepository` 直接读写强类型 `windows_music_accounts`（含登出后保留单调 `generation` 的可空凭据列与 `CHECK` 约束），`AccountDetailsStore` 直接读写 `windows_music_account_details` 与 `windows_music_account_audio_levels`，不再经 JSON 文档中转。
- 真实候选认证成功后由账号 CAS 保存凭据并发布会话；资料／权益为独立展示观察，不作为普通平台请求或登录的额外证明。
- 冲突返回 `STORAGE_CONFLICT`；提交回执不可信时返回 `COMMIT_UNKNOWN`。两者都不自动重试、不重放登录或覆盖另一 writer。
- 重新观察持久状态必须创建或使用新的明确读取操作；失败 owner 不通过“丢弃 pending”恢复为可写状态。
- 用户取消只取消尚未进入原子提交点的工作；已确认提交不能由取消推断回滚。

## 账号详情

- 账号详情读取和刷新绑定账号 generation。资料、权益、取得时间和有效期直接解码；读取或解码失败视为 miss，不影响账号读取，也不做历史数据识别或迁移。
- 登录时获取一次资料和权益并缓存；本地读取不触发远程刷新。明确会员缓存到到期前一天，最后一天内新取得的结果最多保留到实际到期；明确非会员和缺少可靠到期日的会员最多缓存 24 小时，更早的已知产品边界仍有效。
- 缓存期内的权益变化只有用户显式刷新才更新；缓存到期不自动重试或写回。
- 旧账号回执、过期请求或账号切换前的网络结果不能写入新账号。
- 展示缓存使用共享数据库中的独立关系和有界内存快照；构造默认独立内存缓存，不与账号凭据行混合。刷新结果先更新内存，慢写和写失败不隐藏账号、不设置业务 pending，也不阻断新的凭据事务。借用的缓存由 App 排空释放。
- 缓存资料只能用于展示已明确的 freshness；不能据此授予播放、下载或会员权限。

## 关闭与隐私

- 关闭停止新调用，取消可合作网络工作，等待已进入的存储与同步通知结算，再释放协议和敏感缓冲区。
- 错误只暴露有限 code；Cookie、手机号、验证码、二维码私有材料、路径和上游响应不进入异常文本。
- 产品没有账号数据“重置”或自动修复入口。清理用户数据必须另行取得明确授权。

## 用户账号文件

文件格式和冲突策略见[共同账号文件契约](../common/music/account-transfer.md)。JSON 是用户显式选择的交换格式，不是账号存储格式；传输不复制本地 scope、generation 或详情缓存。

文件导入的 owner 计划保存准备时账号值 fingerprint 与授权 epoch。隔离候选验证失败不会推进 epoch、写入账号或撤销现有 lease。提交在原存储队列中重新读取当前账号，以完整账号基线及数据库 revision CAS 防止同 generation 内容变更或另一 writer 被覆盖。成功读回后才发布原有通知；失败已知冲突保留旧授权，未知写入回执保留 Uncertain 并要求明确读取核对。取消／关闭会排空已准入写入，不以取消推断回滚，不建立跨平台事务或重放机制。
