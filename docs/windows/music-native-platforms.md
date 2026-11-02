# 原生音乐平台目录契约

共同规则见[页面数据请求](../common/pages/data-requests.md)和[平台客户端上下文](../common/music/platform-client-context.md)。本文件记录目录 API 和传输；本地身份、平台登记和客户端文档见[Windows 客户端上下文](music-platform-client-context.md)。

## 所有权与装配

- `MusicPlatformCatalog` 是固定平台内容读取的原生 owner，公开契约为 `IMusicPlatformCatalog`。
- 网易云、QQ、酷狗从 `IMusicPlatformSessionSource` 借用当前账号会话；酷我和咪咕只使用匿名公共元数据链，绝不借用其他平台账号。
- Catalog 不持久化账号、不修改云端内容、不启动 Node、不调用本地服务，也不拥有页面状态。
- 账号 owner 在登录、退出、换号或会话失效时调用 `Invalidate`；消费者不能自行缓存 Cookie 或重建会话。
- WY／KG 请求显式消费当前客户端快照，客户端字段与可选账号只在 wire 边界组合；QQ 类型搜索按需消费同一客户端 owner 的匿名 QIMEI／GetSession，精确歌曲和歌单读取不额外登记。构造目录不初始化或登记客户端。
- WY 普通／VIP EAPI 的 Cookie 按加密 header 中的字符串原值构造；缺少可选 CSRF 时保留空字符串，不转换为 JSON 字面量。WEAPI、EAPI 和 VIP EAPI 各自保留原有固定包络。

## 平台能力

| 能力 | 网易云 `wy` | QQ `tx` | 酷狗 `kg` | 酷我 `kw` | 咪咕 `mg` |
| --- | --- | --- | --- | --- | --- |
| 歌曲搜索 | 是 | 是 | 是 | 是 | 是 |
| 歌单搜索 | 是 | 是 | 是 | 是 | 是 |
| 单曲元数据 | 是 | 是 | 是 | 是 | 是 |
| 歌单与成员分页 | 是 | 是 | 是 | 是 | 是 |
| 首页／频道 | 是 | 否 | 否 | 否 | 否 |
| 歌手详情／热门歌曲 | 是 | 否 | 否 | 否 | 否 |
| 个人歌单 | 是 | 是 | 是 | 否 | 否 |
| 每日推荐／风格推荐 | 是 | 否 | 否 | 否 | 否 |
| FM／相似／心动／队列推荐 | 是 | 否 | 否 | 否 | 否 |
| 账号能力 | 是 | 是 | 是 | 否 | 否 |

网易云另提供当前专辑读取。歌手详情由各平台的可选 `IMusicArtistCatalog` 提供。高潮片段和官方音频解析使用同一原生平台／传输边界，但拥有独立契约和授权检查，不能由目录元数据自动授予播放或下载权限。

## 公开请求

当前类型化入口为：

```text
SearchAsync
SongAsync
PlaylistAsync
HomeAsync
ChannelsAsync
ChannelAsync
PersonalPlaylistsAsync
DailyAsync
FmAsync
SimilarAsync
HeartbeatAsync
QueueRecommendationsAsync
AlbumAsync
ArtistAsync (IMusicArtistCatalog)
Describe
Invalidate
```

当前原生请求约束：

- 每次调用必须显式给出平台和业务目标；不接受上游 URL、任意 module/method、Cookie 或脚本安装 ID。
- 搜索和普通分页的页码从 1 开始，页大小最多 100；页码使用当前整数类型，不设置累计页数或固定歌曲数量截断。完整歌单按平台总量和分页结束条件读取，普通目录与搜索不套用暂存容量。
- 搜索的“页”由平台适配器 `MusicSearchPaging` 定义：网易云按偏移量取数，一个请求可覆盖多页但最多 100 条，响应声明 `offset`；其余平台每页一个请求。当前各平台页长均为 30（咪咕歌曲按分组计）。
- QQ 歌曲／歌单使用固定 Android 类型搜索配方，完整 comm 包含当前 QIMEI、匿名 uid／sid 及安装字段；登录账号仅追加自己的授权字段，匿名设备状态不作为登录证明。首次所需匿名登记由已有客户端 owner 共享，缓存及有效期沿用其规则，不做接口轮询、签名回退或登录重试。
- continuation、snapshot 和个人歌单 locator 都绑定当前 scope、目标和已见身份；不能跨账号、跨列表或跨页复用。
- `Refresh=true` 只绕过本次目标的当前内存缓存，不触发其他平台、其他栏目或隐藏页面请求。
- 失败页不前移游标；不完整页、重复身份、停滞游标和 snapshot 变化必须明确失败。

## 当前输出

歌曲是精简的统一模型，曲库、队列、历史、歌单、同步、播放、歌词和搜索共用：

```json
{
  "songId": "song:<hash>",
  "title": "", "artist": "", "album": "", "duration": 0, "cover_url": "",
  "sources": [
    { "sourceKey": "[\"tx\",\"<mid>\",\"\"]", "source": "tx", "musicInfo": {} }
  ]
}
```

- 根字段只用于显示；本地歌曲 `sources` 为空。
- `sources[0]` 是主录音，其余按 `sourceKey` 排序；同一平台可有多条录音，`sourceKey` 不重复，全部录音必须相互兼容。
- `musicInfo` 是该录音的洛雪音源标准字段与官方播放、歌词、同步所需定位字段（如 `songmid`、`hash`、`albumAudioId`、`copyrightId`、`contentId`、音质 `types`／`_types`）。录音自身的歌名、歌手、专辑、时长和封面只记录在 `musicInfo` 的 `name`、`singer`、`albumName`、`duration`、`img`。
- 来源 `duration` 保留精确秒数用于录音匹配；格式化 `interval` 不重复持久化。Windows 脚本调用边界为 `musicUrl` 和 `lyric` 的 `musicInfo` 派生洛雪 `MM:SS` 字段，保留精确值且不修改来源记录。
- 平台录音 ID 为 `musicInfo.songmid`；不保存其他重复身份别名、未被消费的哈希别名、歌词地址或映射刷新元数据。
- 根对象没有平台 `id`；平台录音身份只在 `sources` 中。
- `sourceKey` 是当前规范 JSON 数组字符串，用于精确录音选择。
- 搜索、歌单、推荐和频道返回的歌曲都经过同一 `NativeMusicSong` 校验，不能把原始平台对象直接送入 UI 或持久化。
- 歌单卡片保留平台、原生 ID、名称、封面、数量、创建者和可用动作。个人目录额外返回绑定当前账号的 locator；locator 不是凭据或可转移授权。
- 酷狗公开歌单使用真实全局 ID：搜索响应 `gid` 与详情响应 `global_collection_id` 对应同一身份；两字段同时出现须一致。`specialid`、个人 `listid` 或 LX `id_*` 不替代全局 ID。搜索 `img`／`song_count` 和详情 `pic` 直接归一到当前卡片字段，详情分页仍请求 `global_collection_id` 并核对响应身份。成员分页允许后续页缺少 `list_info`，此时以 `data.count` 为总量，名称和封面为空；分页 snapshot 只绑定平台、scope、请求 ID 和总量，不包含名称、版本或当前页内容。`list_info` 存在时，其 `global_collection_id` 若出现须等于请求 ID；其 `count` 与 `data.count` 同时出现时必须一致，缺少 `count` 时使用 `data.count`。
- 网易云歌手频道返回独立 `area`、`type`、`initial` 标签组、当前组合 token、圆形歌手卡数据和绑定该 token 的 offset continuation；换筛选从首批重新读取，旧 continuation 不能跨组合复用。
- 网易云 Cube 页面先读取当前 `pageProtocol.pages`，再按页面声明的 `ruleId` 读取推荐歌曲。`algTagInfos: null` 是当前“无算法标签”结果，等价于空集合；其他非数组、非空形态仍明确失败。
- 网易云歌手详情严格核对请求 ID、响应歌手 ID、非空名称、有效头像和 `hotSongs` 数组；热门歌曲经过同一 `PlatformSongs.Wy`／`NativeMusicSong` 归一化，并拒绝重复录音身份。一次详情读取不附带专辑扇出。
- 网易云 VIP 频道由推荐歌曲接口与榜单接口共同组成；榜单只接纳上游名称明确匹配黑胶 VIP／SVIP 的播放列表，普通榜单不能因处于 VIP 页面而被重新分类。
- 分页结果保留平台、页码、页大小、总量、`more` 和 continuation。未知总量使用 `null`，不能用当前页长度伪造。搜索页的 `more` 需平台声明还有后续；没有显式游标的平台短页即末页，QQ 按平台 `nextpage`，空页始终结束；即使平台 `total` 更大也如此。

咪咕歌曲搜索使用原生 group 单位：单页 group 数不超过请求页长，完整展开各 group 的录音版本，不另设 32／1000 版本额度；group 排名、总量和 copyright 身份必须完整保留。其他平台与歌单搜索仍使用 item 单位。

## 会话与账号隔离

- 借用会话包含平台、scope、generation、userId 和 Cookie；凭据属性不可序列化、不可日志化，`ToString()` 只返回脱敏文本。
- 每个账号平台请求在派发前、HTTP 后和发布前重新核对当前会话。
- 会话源不可用返回 `CREDENTIALS_UNAVAILABLE`，不能降级为匿名请求。
- 酷我和咪咕的匿名 scope 与账号平台完全分离。即使其他平台已登录，也不会把该会话带入公共请求。
- 网易云每日推荐仍需要真实账号；客户端身份由独立 owner 正常初始化，不因账号 Cookie 缺 deviceId 而禁用曲目读取。真实存储、身份、未知回执或权限失败保持明确错误。
- 账号改变会同步清除对应缓存、围住活动操作并取消其传输；旧成功和旧失败均无权提交。

## 网络、缓存与资源

- 固定 allowlist origin、scheme、方法和路径由 `MusicPlatformHttp` 与平台请求构造器拥有；其中酷我当前公开配方包含受限 HTTP 路由，其余能力不能据此扩大明文网络权限。全部请求禁止自动重定向、系统代理、Cookie jar 和任意 URL。
- 默认调用截止时间为 25 秒；装配方可指定操作截止时间，HTTP 不再追加统一的 25／60 秒硬上限。
- 网络层与 Catalog 不做任何并发限流：不设全局、每平台或每服务器连接上限，也不因请求量返回 `RATE_LIMITED`。请求量只由业务逻辑控制（例如搜索按“类型 × 平台”的并发、缓存与分页）；上游真实返回的限流仍报告 `RATE_LIMITED`。
- 所有 `SendAsync`／`SendXeapiAsync` 都在请求、响应及流实际收尾后结算；取消或失效不提前释放不合作传输，关闭流程等待其排空。
- Catalog 为单个原始响应指定 4 MiB 缓冲上限；HTTP 使用调用方的真实缓冲上限，不再施加重复的全局 4 MiB 限制。JSON 保留严格 UTF-8、重复键和嵌套校验，不再遍历附加节点／数组／字符串总额度。规范元数据仍按字段、身份和单曲模型校验；内存中的派生目录结果与累计 continuation 不套用 512 KiB 截断。
- Catalog 不直接写持久文档；客户端受信响应更新交给客户端唯一 writer。目录内存缓存最多 64 项、2 MiB，key 包含平台、账号 scope／generation／userId、操作、完整输入及实际使用的客户端身份／代次；成功响应推进客户端后按最终代次保存缓存。单个结果超过缓存容量时直接返回且不缓存，不拒绝本次读取。
- Catalog 不缓存搜索；搜索结果只由搜索 owner 缓存一次（见[原生搜索契约](music-search.md)）。单曲／歌单最长一小时，其他普通目录最长十分钟；每日推荐使用平台返回的到期时间。显式 refresh 成功后替换同一当前 key。
- 鉴权、权限或会话失效会清除对应平台缓存。缓存命中不绕过当前会话复核，也不授予播放、下载或离线许可。

## 生命周期与错误

- `Invalidate(platform)` 可只退休一个平台；`Invalidate()` 退休全部平台。它不联网、不远端注销、不修改账号文档。
- `Dispose()` 停止新准入，围住活动调用并取消传输；共享账号 owner 和共享传输由 App 按装配顺序释放。
- `MusicPlatformException` 只暴露有限 `Code`、`Platform` 和 `Operation`，没有 inner exception、URI、请求／响应正文、header 或凭据。
- `AUTH_REQUIRED`、`FORBIDDEN`、`STALE_SESSION`、`IDENTITY_MISMATCH`、`SNAPSHOT_CHANGED`、`PAGINATION_STALLED`、`LIMIT`、`RATE_LIMITED`、`TIMEOUT`、`CANCELLED`、`CLOSED`、`INVALID_RESPONSE` 等保持各自语义，不转换为空成功。

## 当前格式纪律

- 活动代码只支持当前请求 DTO、歌曲 DTO、分页、continuation、locator 和错误格式。
- 当前格式改变时同时更新平台 producer、搜索／页面 consumer、缓存 key、契约文档和测试夹具。
- 不兼容持久数据保持原样并明确失败，不能通过更换 App 数据身份规避。

## 来源与许可

平台协议、固定源码提交、本地修改和许可见 `LICENSES/NOTICE.md`。
