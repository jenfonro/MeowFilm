# Windows 音乐平台客户端上下文

业务约束以[共同平台上下文契约](../common/music/platform-client-context.md)为准；本文件说明 Windows 的具体所有者、协议、存储和生命周期。页面请求时机见[共同页面契约](../common/pages/data-requests.md)，账号授权见[原生账号](music-native-accounts.md)。

## 所有者与依赖

- `MusicNativeApplication` 装配一个 `MusicPlatformClientContextOwner`，借用 App 的 `IProtectedDocumentStore`。构造无 I/O；显式 `InitializeAsync` 只加载或初始化本地身份，不要求账号，不登记平台、不领取网络密钥。
- 唯一活动客户端 writer 是该 owner；唯一活动客户端文档是 `music.platform-context.v1`。账号和账号详情仍由 `MusicAccountOwner` 写入 `music.accounts.v1`，两份文档不双向同步。
- `Platforms/ClientContexts` 管理本地标识、匿名登记、快照、受信响应更新及真实物理任务；账号流程只消费所需客户端能力并核对授权。
- 固定签名／加密请求构造在 `Platforms`；请求临时密钥、nonce 和时间戳不进入客户端文档。HTTP 不使用 Cookie jar，不隐式保存状态。
- 目录、官方播放、歌词、VIP、搜索发现、反馈、同步和登录显式注入同一个客户端 owner。只有真实使用客户端字段的请求捕获对应快照：QQ 类型搜索按需依赖匿名 QIMEI／GetSession；精确目录读取、登录验证、歌词和 vkey 不额外登记。同步或官方候选委托类型搜索时使用同一配方，QQ 反馈使用独立可撤销的账号＋设备 lease。

## 文档与首次导入

`music.platform-context.v1` 使用当前 `aggregate` 产品身份下的 DPAPI 保护存储，64 KiB 上限；原生 namespace 对脚本不可见。

根对象严格包含 `version=1`、`wy`、`kg`、`tx`：

| 节点 | 持久字段 |
| --- | --- |
| `wy` | generation、deviceId、nuid、nnid、wnmcid、可选 nmtid |
| `kg` | generation、seed、可选 dfid |
| `tx` | generation、installationId、androidId、openUdid、openUdid2、imei、可选 qimei 与 session |

QIMEI 保存自身代次、q16、q36 和取得时间；匿名 session 保存自身代次、uid、sid、取得时间及依赖的 QIMEI 代次。字段集合、类型、重复键、大小、整数范围、IMEI 校验位、子代次关系和文档 fingerprint 均在发布前校验。损坏、保护失败或不兼容版本拒绝读取；存储不可用另返回 `STORAGE_UNAVAILABLE`，都不重新生成身份。

只有第一次权威读取确认客户端文档真正不存在时，才调用只读 `AccountClientContextImport`：

1. 严格读取已发布账号 v1 文档中的 KG／QQ 旧设备条目和 WY 客户端 Cookie 字段。
2. 保留合法已有值和关联；缺少的必要字段按平台正常本地规则生成。WY 没有 `deviceId` 不代表其他合法客户端字段要丢弃。
3. 通过 missing fingerprint CAS 保存新文档，取得正确回执后才发布内存快照。

已有客户端文档不再读取迁移源；旧账号文档和产品身份保留，不删除、不重置。账号文档仍严格识别旧设备条目，以保持已发布数据可读取，但它们仅为迁移来源，后续账号事务原样保留，不再生成、更新或同步设备值。

WY 新标识采用现有原生规则：52 位大写 hex deviceId、64 位小写 hex nuid、携带时间戳的 nnid，以及六个小写字母加时间戳的 WNMCID。KG 使用稳定 128 bit seed，派生完整十进制 MID 与 DEV。QQ 使用稳定安装资料和合法 IMEI；这些是协议客户端资料，不读取或冒充本机硬件身份，也不表示设备已获认证。

## 按需平台协议

### 网易云

- 请求边界组合当前 `MusicWyClientContext` 与可选账号 Cookie；账号权限由真实账号 lease 判断。
- 当前安装身份补齐没有设备字段的合法账号请求。明确关联外国设备的导入 Cookie 保留自己的设备字段，不混入本安装的 nuid、WNMCID 或 NMTID。
- 固定成功响应中的相关 Set-Cookie 经来源、字段、重复项、有效期和设备身份校验；本安装的变化在客户端串行 CAS 边界内按响应实际字段合并到最新状态，写入前再次核对请求资格。并行响应不以各自的旧快照覆盖另一响应已更新的字段，无变化不写入。
- WY 请求资格绑定本次权威观察和安装身份，不绑定每次普通 Cookie 更新的存储代次。普通 Cookie 更新可递增持久 generation，但不会使同一观察下的其他正常在途请求失效；权威重读、真实身份变化、冲突/未知提交、取消或关闭仍围住旧请求。观察身份只在内存中存在，不改变客户端文档格式。
- 账号协议在有效二维码 key／轮询状态、合法登录交接、准确且不泄漏输入的用户资料或完整会员结果核对之后才交接对应客户端响应；一般传输成功或 `code=200` 不单独授权回写。后续步骤使用已确认的当前快照，失败的身份／资料结果不写客户端状态。
- [原生歌词](music-native-lyrics.md)保留自己的有界 JSON／文本解析，先核对歌词成功状态和录音身份，再把瞬时响应 Cookie 交给同一 writer；不另建歌词设备状态或账号写路径。
- 反馈的账号／录音校验、官方音频的资源／密钥／媒体校验和 VIP 的内容／签到结果校验先于各自响应交接；传输不隐式更新，同步委托目录读取的快照交接见[原生同步](music-library-sync.md)。
- 外国设备响应不更新本安装身份；不把其关联偷偷重绑到当前安装。授权 Cookie 由登录交接留在账号 owner，客户端文档不保存 MUSIC_U、MUSIC_A 或 csrf。
- 空 `deviceId` 视为未提供关联，使用正常本地身份；非法非空标识拒绝请求。主授权 MUSIC_U 失效仍拒绝，清除匿名 MUSIC_A 或可选 csrf 不单独证明账号失效。
- XEAPI 密钥协商限于当前操作，不猜测跨操作有效期，不保存临时 AES 材料。每日推荐读取真实推荐成员批次；账号 Cookie 缺 `deviceId` 不再禁用该读取，匿名身份仍不能绕过推荐的账号权限。
- 目录 XEAPI 的密钥签名／nonce 关系与密钥内容先校验，再交接该响应；每日推荐完整批次及成员身份先解码校验，再交接成员响应。后续请求使用该操作已确认的快照，非法密钥、缺失批次或重复成员不更新客户端状态。
- 全部网易云目录读取使用 `Call.Wy<T>`／`Read<T>` 的业务 decoder；搜索、歌曲批次、歌单成员、专辑／歌手、首页／频道、FM 和个人目录先校验本响应的身份、可展示数据及实际分页关系，再回写客户端状态。旧的仅返回成功 JSON 的读取签名已移除；多步响应各自确认，不靠最终页面失败后回滚或延后回写队列。
- 官方副歌元信息保留缺失或不可用时返回空 `point` 的既有契约；只有准确、唯一且可用的录音时间段才交接该响应，缺失或错误录音不更新客户端状态。
- [搜索发现](music-search-discovery.md)的热搜／榜单／默认词及成员补读分别先校验内容和身份，再交接对应响应；只读缓存保留实际确认的客户端快照，复用及本地轮换不会绕过该快照的失效。

### 酷狗

- `EnsureKgAsync` 在确有需要的二维码登录操作中共享一次有界匿名登记；登记前稳定 seed 已确认保存。
- AES／RSA 登记协议由 `MusicPlatformClientProtocol` 执行，合法空登记结果不编造 dfid。明确下一次用户操作可用相同 seed 再登记。
- 二维码 challenge 绑定实际客户端快照；轮询及授权交接核对该快照，失效 challenge 不发布账号。
- 普通目录、播放和同步不因展示而登记设备；账号缺旧 MID 时使用当前本地身份，只有明确 MID 关联的账号 dfid 才可参与组合。同 MID 使用已确认的最新本地 dfid，不同 MID 不混入本安装 dfid；非法身份拒绝请求，query、body 和 header 保持一致。

### QQ

- `EnsureQqAsync` 独立拥有 QIMEI／匿名 GetSession 共享链，最多 25 秒；QIMEI 有效期 24 小时，session 依赖对应 QIMEI 代次且只在取得当日本地日期有效。未来时间不视为当前缓存。
- 歌曲／歌单类型搜索与反馈复用该共享链，不另建搜索设备文档、注册台账或网络额度。搜索 comm 与 GetSession 使用同一 Android 客户端字段构造；GetSession 不携带账号授权或旧 uid／sid，搜索携带已确认的当前匿名 uid／sid，账号授权仅在请求边界追加。
- 两个成功步骤分别经客户端 CAS 确认；未知 QIMEI 回执不继续 GetSession，未知 session 回执不派发依赖它的搜索或发布反馈 lease。
- 账号 owner 每次取得反馈 lease 都实时验证准确账号，核对其授权代次，再组合适用设备快照；缓存、QIMEI 或 GetSession 不能作为登录证明。
- 搜索或账号等待者取消只撤销自身取得资格；换号或账号 owner 关闭只撤销旧账号资格，不拥有或中止其他等待者共享的匿名工作。客户端 owner／应用关闭才取消并排空该物理工作。
- 客户端变化失效依赖旧快照的 QQ 搜索请求及反馈 lease，但不撤销仍有效的普通账号授权。

## 内存、CAS 与恢复

- 初始化和各平台登记分别单飞；调用方取消只停止其等待，不取消其他等待者的共享工作。
- 普通取得复用内存快照。KG／QQ 更新核对准确快照引用与平台代次；WY 核对请求所属权威观察，并在串行边界对最新快照应用响应字段。整文档写入仍使用实际 fingerprint 做 CAS，保留未变化平台的快照引用。
- CAS 冲突清除旧客户端基线，明确失败；后续权威读取只观察新状态，不重放原更新。
- 调用存储后取消、错误或回执指纹不匹配视为 `COMMIT_UNKNOWN`。未核对前不能继续取得客户端状态、派发依赖它的请求或报告关闭准备成功。
- `ReloadAsync` 是明确的权威重读，不自动重放写入、不再次导入。权威 missing 读解除未知回执，但已经准入过身份写入后不再次生成安装身份，返回 `DEVICE_CONTEXT_REQUIRED`，直到有效客户端文档经权威重读恢复。
- 应用显式初始化重试及 `music.accounts.list` 的应用级路径可以核对客户端未知回执；单独账号 owner 的 list 不协调另一个 writer。
- 重读立即使旧快照不可使用，并与初始化等待共享当前重读；新观察不复活旧快照。

## 关闭与验证

客户端 owner 停止新准入、取消本范围、等待真实协议／存储任务结算后才释放内部资源。它不释放借用的 store 或 HTTP。取消应用关闭准备会建立新生命周期并克隆快照，不恢复旧 lease 或重放操作；最终释放客户端 owner 先于共享 HTTP。

长期验证入口：

- `MusicPlatformClientContextTests`：本地初始化、一次导入、严格格式、持久稳定性、并发、CAS、未知回执、重读、取消、关闭及 WY 请求组合。
- `MusicNativeKgAccountTests`、`MusicQqFeedbackContextTests`、`MusicQqFeedbackIntegrationTests`：固定平台协议、真实 DPAPI、授权／设备分离和故障生命周期。
- `MusicWyAccountClientTests`：账号响应校验后交接、二维码／SMS 多步骤状态复用，以及错误身份／资料不写客户端状态。
- `MusicNativePlatformTests`、`MusicSearchDiscoveryTests`：推荐设备交接、展示请求复用、目录逐分支合法／非法响应交接与消费者边界。
- `MusicFeedbackProtocolTests`、`MusicOfficialStandardTests`、`MusicSyncClientContextTests`：业务校验后更新、错误结果保留基线，以及合法委托更新的明确交接。

实际通过结果写入任务验证记录；上述实现说明和测试入口不等于已完成编译、UI 或实网验证。跨端核对使用[逐端自查](../common/music/self-check.md)。
