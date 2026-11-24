# 音乐平台请求与上下文

本文件是两端共同的平台请求、字段消费、匿名协议状态及账号交接契约。端侧 API 和装配事实见 [iOS 音乐](../../ios/music.md)、[Windows 平台接入](../../windows/music-platform-client-context.md)；运行器方法见 [music.platform.v1](../../runtime/music-platform.md)。共同契约描述应满足的行为，不代表已完成运行验证。

## 唯一职责

| 职责 | 所有者 | 交接边界 |
| --- | --- | --- |
| 五平台现有请求配方、签名、加解密和字段转换 | 共同 Node `music-platform` | 原生发送业务操作及其所需定位，消费规范结果 |
| 安装／匿名设备、匿名 Cookie、必要 prepare 和协议有效期 | Node 平台上下文 | 同一访问范围连续复用，必要持久保存确认后才使用 |
| 真实账号认证步骤 | Node 配方 | 候选认证结果交还原生，不自行建立账号库 |
| 凭据安全保存、账号归属、授权发布与撤销 | 原生账号 owner | 保存成功后向 Node 发布当前 session；换号／退出撤销旧操作资格 |
| 歌曲永久身份、元数据、队列、收藏和历史 | 原生音乐 owner | 见[歌曲资料与智能播放](smart-playback.md)，Node 不另建歌曲仓库 |
| 图片字节及通用歌词呈现 | 原生资源／呈现 owner | 见[共享图片与展示资源](presentation-resources.md)，无 Node 图片代理或图片副本 |
| 装配与资源关闭 | App composition／运行器生命周期 | 借用既有运行器和网易 token owner，不新建运行器或重复 owner |

## 普通请求与字段消费

```text
原生业务动作 → Node 取用实际参数 → 当前平台上下文＋固定配方
           → 上游协议 → 只转换该操作消费的字段 → 规范结果 → 原生业务
```

- 操作只取用需要的参数。未使用的额外合法 JSON 字段不拒绝，也不透传为地址、请求头或凭据；不存在的模块、平台或操作仍无法派发。
- 固定 URL、方法、请求头、版本和签名输入由配方直接定义，不逐层证明常量或构造另一份原生 wire 请求。
- 普通内容读取只提取当前字段，不设置字段完整性、类型、成员数量、非空全坏或可执行身份放行门禁。缺少文字、图片、数字或动作只影响对应材料，不否定卡片、栏目或页面；非法 JSON、真实传输失败和资源超限仍失败，程序错误不伪装为空成功。
- 平台定位保留字符串精度，不按猜测的位数、字符集或名称长度拒绝。协议实际要求的编码、数值转换和批次仍执行。
- 首页直接使用可呈现栏目、卡片和真实游标；允许增加、删除、重排及推荐变化。动作直接使用必要定位，不以前次首页摘要、成员列表、seed 证明或 handle 一致作为准入条件。
- 首页顶部入口与频道推荐组统一为 `sections` 中的 `feature-rail`；顺序、标题、动作及 `requiresAccount`、`display`、`available` 由 Node 配方发布。每日推荐（含风格日推）和心动推荐在没有当前已发布账号会话时为 `display:false`、`available:false`，有账号时才显示；FM 与相似推荐保留真实匿名能力。客户端消费显示声明，不另建固定入口或按动作类型重算登录可见性；展示快照不授予当前账号权限。
- 分页使用真实 cursor、nextpage、结束标记及 consumed 位置；短页不自动证明结束。同组多版本、顺序和重复 occurrence 不因展示数量裁剪。
- 分页只解析当前成员和可关联详情，不提前消费后页元数据；canonical 返回保留真实身份，不能给未确认的别名关联补造成员位置。身份缺失的歌曲仍可保留展示，但不制造永久歌曲或可执行定位；仓库采用结果不反向删除父卡或其他展示材料。
- 普通全集读取只取得所需内容，不前后双读确认快照；实际规范 ID、条数和正常内容变化不与本地预估二次比较。真实远端写事务单独保留其提交保障。
- 运行器在首页／频道转换中不因缺标题、图片或歌手自行追加摘要／详情请求。原生业务 owner 按可见展示需求复用已读集合根资料，必要时显式请求已有摘要配方；只有明确请求成员时才执行所需的多步成员读取。已有 URL 不联网证明，资料回填不改变队列 revision、顺序、当前 occurrence 或播放位置，详见[共享图片与展示资源](presentation-resources.md#缺少图片引用的按需读取)。
- 官方音频按真实 URL、品质、完整版／试听事实消费；游客可使用配方确实支持的能力。网络或解析失败不推断需要登录、无资源或错源。专有歌词解密和 chorus 字段转换由 Node 完成。
- 歌词主轨与翻译／罗马音分别消费；附属轨失败保留主轨、健康兄弟及有限错误码，取消、超时、会话失效和资源超限继续终止。Base64 接受等价空白与省略 padding，仍拒绝非法字符、坏密文、压缩流和时钟材料。签名始终使用上游原文，不先作数值规范化。
- 播放反馈按平台和 start／end 取实际编码字段。QQ 使用已取得的曲目时长；网易不消费调用者 duration／position；酷狗 start 只消费时长，end 保留真实录音时长与播放位置关联，不消费 media。附加回执成员不改变真实成功／拒绝及一次结算。

## 固定内容协议

平台适配只在 Node 执行。两端各自只有一份下列字段消费规则，不认识上游 DSL、容器、字段别名或图片地址配方。固定协议不意味着五平台均支持所有操作：目前首页／频道由网易、酷狗提供，其他平台不虚构配方。

| 结果 | 共同字段与职责 |
| --- | --- |
| BrowsePage | `home/channel/artistActivity/charts` 返回 `platform/id/title/sections/tags/selectedTag/continuation`；不再同时返回另一套顶层 `entries`／`feed` 展示树 |
| Section | `id/title/layout/items/action/tabs/selectedTab/tags/selectedTag/errorCode/avatars/requiresAccount`；`layout` 是最终组件分类，不按 `type`、成员形状或平台二次分类 |
| Card | `key/id/kind/label/title/subtitle/contextText/cover/coverRef/action/song`，以及 `presentation/preview/decoration/count/playCount/coverLabel/dayOfMonth/display/available/requiresAccount/canPlay` 等独立展示材料 |
| ChannelDirectory | `{platform,items:[descriptor]}`；descriptor 使用 `id/name/channelKind/pageCode/cover/placement/available/display`，不从 id 前缀猜频道类型 |
| Song | `title/artist/album/duration/cover_url/sources`；独立歌曲列表不嵌套第二层 `song`，排行可附加 `rank`。`sources` 保存真实来源，不伪造永久 songId |
| Collection | `id/name/cover/count/playCount/creator/source/engine`；详情另保留其现有根资料、歌曲 `items` 与分页材料 |

- `layout` 为 `feature-rail/banner/playlist-rail/playlist-grid/song-columns/song-list/artist-grid/channel-grid/ranking/ranking-small/ranked-songs/artist-updates/vip-songs/vip-ranked-songs/album-ranking`。`TOP_3/ONLY_COVER/rail/list` 等上游／适配中间值不进入端侧分派。
- `kind` 为 `song/playlist/album/artist/channel/chart/daily/fm/similar/heartbeat/song-collection/unsupported`，表达内容与动作类别，不能由 `sources`、`available` 或动作形状重算。未知／缺动作保留展示，仅不执行操作。
- `title` 是卡片业务标题。`label` 只表达首页入口上方栏目标签；频道为空。`contextText` 独立保留动态业务文字材料，未提供时为空。Windows 首页上方显示 `label`、底栏优先采用 `contextText`（FM 按该歌曲材料组合漫游文案），缺失时使用 `subtitle`；频道底栏显示 `title`。iOS 保留自己的既有标题／副标题位置。
- `count` 是歌曲数量，`playCount` 是可选数值播放量，缺失为 null，真实 0 保留；两者不混用。`coverLabel` 为封面独立标识，例如频道 FM 的“欧美漫游”，不占首页 `label` 的语义。
- `cover` 是该卡片展示图，不因 `song.cover_url`、动作目标或仓库采用而被覆盖。`coverRef` 为同一展示图的显式元数据定位；目前网易普通歌单为 `{kind:"playlist",id}`，其余为 null，banner 始终为 null。
- 显式 `playlists` 操作接收 `{ids:[...]}`，返回 `{platform,items:[Collection]}`。客户端不为集合图读取完整成员、不取第一首歌图冒充集合图。
- 结构诊断仅包含固定提取路径、阶段、JSON 类型及数量，不含账号、ID、URL、原始响应或其他业务值；诊断附于 `diagnostics`，不参与放行或客户端分类。正常缺项、真实网络失败和程序错误分别处理。

## 匿名连续性、账号发布与缓存

- 全新空数据按当前格式初始化。当前实现自己写入的上下文正常保存和重载；不探测、识别、读取、迁移、采用、兼容或自动清理旧版本数据。这个前提不授权删除用户数据。
- 匿名上下文独立于账号授权；首页、动作、详情和音频沿同一平台当前协议身份连续执行，不因入口或失败换游客重试。
- 仅在真实协议需要时执行登记／prepare。跨启动所需状态先确认保存；保存失败、冲突或未知不能伪造成功、重生成身份或换存储位置绕开。
- 登录、显式刷新、二维码和验证码只执行各自真实认证配方，不以无关 profile／rights 或普通首页成功作为前置条件。建立账号身份所需认证与精确归属仍保留。
- 最终凭据在原生安全保存确认后发布。Node 仅持有请求所需的已发布会话和临时挑战，不作为第二份持久账号库或自主刷新器；普通 DTO、脚本、日志不包含凭据。
- 登录后重新取得当前首页等数据；换号／退出取消旧业务读取并隔离迟到结果，不借此清空永久歌曲或公共无凭据图片，也不无依据重置匿名身份。
- 现有 API 结果缓存直接复用，显式刷新取得当前结果；不为命中再请求上游证明正确。忽略的参数不进入等价请求的缓存键，不给原本无缓存的搜索新增结果缓存。
- 同资源读取共享真实工作、订阅者各自取消；失效后旧结果不能回填。关闭停止新准入并等待实际传输、存储及已发送写入结算，取消等待不等于物理结束。

## 失败及保留边界

普通错误属于当前操作。主动取消、替代、离页及换号不重放旧错误；必要的已提交设置未知提示另见[设置保存回执](settings-receipts.md)。不由普通 ID／条数／快照比较制造“内容已更改”。

| 边界 | 保留的真实职责 |
| --- | --- |
| 通信／字段消费 | JSON 解码及固定字段提取，不将内容完整性或类型证明当作读取成功条件 |
| 控制面 | 实际派发、resume／cancel 关联和真实保存回执，不套到普通歌曲字段 |
| 协议与认证 | 真实签名、加解密、prepare、候选认证和账号归属 |
| 传输与资源 | TLS、动态携密目标限制、deadline、取消和关闭；普通业务传输不设应用字节配额，缓存和播放缓冲预算独立 |
| 安全保存与发布 | 原生凭据保护、条件保存与授权撤销，未知不假报成功 |
| 本地提交 | 队列／曲库／设置原子提交、真实保存回执、当前操作归属及迟到隔离 |
| 远端写事务 | 真实确认、journal／outbox 和未知写只读核对，不自动重放，不扩散到普通读 |
| 歌曲与媒体 | 永久仓库匹配、明确试听排除及实际媒体使用要求；不匹配只是不合并 |
| 当前外部文件 | 用户明确选择的现行[账号文件契约](account-transfer.md)及文件授权，不成为内部旧数据兼容入口 |

每个判断须有上述实际消费者，不能仅以“安全”“校验”或“平台差异”为名恢复普通业务门禁。实网、合成测试、静态核对和原生构建分别记录，见[逐端自查](self-check.md)。
