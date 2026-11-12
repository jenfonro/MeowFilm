# 原生音乐平台目录契约

共同规则见[页面数据请求](../common/pages/data-requests.md)和[平台客户端上下文](../common/music/platform-client-context.md)。本文件记录目录 API 和传输；本地身份、平台登记和客户端文档见[Windows 客户端上下文](music-platform-client-context.md)。

## 所有权与装配

- `MusicPlatformCatalog` 是固定平台内容读取的原生 owner，读取契约为 `IMusicPlatformCatalog`；网易风格偏好由同一实例的独立 `IMusicDailyStylePreferences` 契约拥有。
- 网易云、QQ、酷狗从 `IMusicPlatformSessionSource` 借用当前账号会话；酷我和咪咕只使用匿名公共元数据链，绝不借用其他平台账号。
- Catalog 不持久化账号、不启动 Node、不调用本地服务，也不拥有页面状态。只有显式风格确认通过独立偏好接口写入云端；目录读取没有隐式保存副作用。
- 账号 owner 在登录、退出、换号或会话失效时调用 `Invalidate`；消费者不能自行缓存 Cookie 或重建会话。
- WY／KG 请求显式消费当前客户端快照，客户端字段与可选账号只在 wire 边界组合；QQ 类型搜索按需消费同一客户端 owner 的匿名 QIMEI／GetSession，精确歌曲和歌单读取不额外登记。构造目录不初始化或登记客户端。
- WY 普通／VIP EAPI 的 Cookie 按加密 header 中的字符串原值构造；缺少可选 CSRF 时保留空字符串，不转换为 JSON 字面量。WEAPI、EAPI 和 VIP EAPI 各自保留原有固定包络。
- 原生平台响应共用 JSON 解析器：同一对象的重复属性使用最后值，包括未消费的推荐/追踪字段及内嵌 DSL/动作 JSON 字符串；不对整个响应做重复键拒绝，也没有歌单路径或 `alg` 特例。非法 JSON/UTF-8、HTTP 错误及消费字段的业务身份校验仍明确失败。本地文档、请求授权和 IPC 的严格契约不因此改变。

## 平台能力

| 能力 | 网易云 `wy` | QQ `tx` | 酷狗 `kg` | 酷我 `kw` | 咪咕 `mg` |
| --- | --- | --- | --- | --- | --- |
| 歌曲搜索 | 是 | 是 | 是 | 是 | 是 |
| 歌单搜索 | 是 | 是 | 是 | 是 | 是 |
| 单曲元数据 | 是 | 是 | 是 | 是 | 是 |
| 歌单与成员分页 | 是 | 是 | 是 | 是 | 是 |
| 首页／频道 | 是 | 否 | 是 | 否 | 否 |
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

## 网易云漫游与推荐

入口／循环／恢复行为见[播放来源、队列与自动续播](../common/music/playback-sessions.md)，本节只记录本端真实请求和解析边界。

- FM：`POST EAPI /api/v1/radio/get`，普通 `mode=DEFAULT|FAMILIAR|EXPLORE|PUZZLE_MODE_RCMD`，场景 `SCENE_RCMD` + `subMode`（含 `GAMES/ELECTRONIC`）；`limit="3"/openAidj="false"/aidjReqTimes="0"/unplaySongIds`。预览 limit 为 1；规则 `ruleId/tabTagId` 使用原 `tag/tab/fm` 配方、每批 5 首。43 个场景保留官方 `FRANCH/PUNK/HOT_SONG` 等键。首页不带 mode 的私人 FM 由本机账号选择存储恢复，不改动卡片的动作身份。
- 相似：同一路径的 WEAPI，`mode=SIMILAR/sourceType=artist|song/sourceIds/limit="3"/unplaySonIds`。这里的 `unplaySonIds` 少一个 g，与普通 FM 不混用。
- 心动：`MusicHeartbeatRequest`；首页 `entry=favorites` 保留卡片 `songId`，current 带实际 `songId/playlistId`。同一个账号 lease 解析 `specialType=5` 且归属当前账号的真实喜欢歌单，不按标题或本地收藏猜测，也不接受普通远端歌单替代此上下文。
- 喜欢成员完整读取，唯一成员数接受 `trackCount` 或 `trackCount+cloudTrackCount` 的已有口径；缺详情按 ID 每请求至多 100 首补齐，不按累计数量截断。首批 `startMusicId` 与实际入口种子 `songId` 相同，使用首页展示曲或当前曲，不替换成喜欢列表首曲；current 核对其确为该歌单成员。
- `/api/playmode/intelligence/list` 使用真实 `playlistId/songId/startMusicId/type/count`，type 为 `fromPlayAll/fromPlayOne`，count 为完整成员长度，不作为结果硬容量。完整消费返回结果；`songInfo=null` 时用顶层 id 从已读元数据或 `/api/v3/song/detail` 补齐，身份／标题不一致明确失败。响应 context 为 `playlistId/startMusicId/songId/fromHome/count/accountId`，顶层 count 是结果长度。续批保留原账号、playlistId、startMusicId 与入口类型，songId 为实际尾曲；入口种子仍校验成员，推荐尾曲不按喜欢成员拦截。协议参数由合成测试验证，不冒充实网算法效果验收。
- 单曲推荐：`MusicQueueRecommendationsRequest(Platform, SeedSongId, Offset)` → `POST EAPI /api/homepage/scene/more/rcmd/song`。参数固定为 `currentPlaySongId="<seed>"/songIds="[<seed数值>]"/songCount="1"/offset="<游标>"/source="curlist_more_rcmd"/extJson="{}"`。首次 offset 0，后续只使用响应 offset，seed 两字段不随插歌／当前推荐曲变化；没有旧路由 fallback 或伪造搜索上下文。
- scene 响应读取 `data.songList/algMap/sourceMap/offset/hasMore`；offset 必须为非负整数，hasMore 必须为布尔值，仍有更多时游标必须前移。完整解码 songList，algMap／sourceMap 接受对象或 JSON 对象字符串，算法映射优先于曲内 alg。明确缺游标／耗尽字段时失败，不猜测分页。
- `recommendations[].source` 表示 provider；独立 feedbackSource 在心动为 aiMode，单曲推荐使用逐曲 sourceMap、缺失为 moreRcmd。读取候选本身不入队或发送反馈。
- 以上读取不缓存、不写账号／云收藏；沿用当前账号失效、物理请求收尾、4 MiB 原始响应和调用截止时间边界。固定参数分页已验证可用，但不据此承诺算法只由 seed 决定或永不耗尽。

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
- 酷我歌曲封面依次选择符合现有公开持久元数据规范、且端口满足图片传输既有 `80/443` 规则的 `pic`、`img`；空值或无效地址不遮挡后续候选。两者均不可用时，将 `web_albumpic_short` 拼到已验证的 `http://img1.kwcdn.kuwo.cn/star/albumcover/` 下，短路径不得改写来源、越出目录或包含查询、片段、控制字符及其编码形式，不限定尺寸目录或图片后缀。结果同时进入 `cover_url` 与来源 `musicInfo.img`，搜索消费者和 `SongRow.Cover` 直接读取；确无有效封面时 `cover_url` 为空，来源不写 `img`。映射不增加逐首详情请求，不改变录音身份、音质、排序、分页或跨平台代表项选择。
- 歌单卡片保留平台、原生 ID、名称、封面、数量、创建者和可用动作。个人目录额外返回绑定当前账号的 locator；locator 不是凭据或可转移授权。
- 酷狗公开歌单使用真实全局 ID：搜索响应 `gid` 与详情响应 `global_collection_id` 对应同一身份；两字段同时出现须一致。`specialid`、个人 `listid` 或 LX `id_*` 不替代全局 ID。搜索 `img`／`song_count` 和详情 `pic` 直接归一到当前卡片字段，详情分页仍请求 `global_collection_id` 并核对响应身份。成员分页允许后续页缺少 `list_info`，此时以 `data.count` 为总量，名称和封面为空；分页 snapshot 只绑定平台、scope、请求 ID 和总量，不包含名称、版本或当前页内容。`list_info` 存在时，其 `global_collection_id` 若出现须等于请求 ID；其 `count` 与 `data.count` 同时出现时必须一致，缺少 `count` 时使用 `data.count`。
- `ChannelsAsync` 发布中性描述符 `id/name/kind=channel/placement/available/pageCode`；`placement` 为 `primary|more`，各组沿响应数组顺序展示。网易专属 ID、上游种类与分组名单只存在于平台适配器，页面不据其筛选。
- 网易云频道目录由同一原生读取操作持有，目录展示、首次默认频道、直接打开频道及续页共用有效目录和在途读取，不各自重读目录。匿名及已登录结果均按当前账号/客户端身份隔离，沿用下文的目录内存缓存期限和容量；目录刷新重读目录，频道内容刷新只刷新该频道内容，未失效的目录仍复用。目录请求确认的客户端更新用于后续内容请求，账号失效、客户端重载和关闭仍保留原有检查。
- 酷狗首页由 `/v2/special_recommend`（`specialrec.service.kugou.com` router）、`newsong_publish` 和 `/ocean/v6/rank/list` 组成；续页只读推荐歌单。歌单以 `has_next` 推进，默认推荐可能多于请求页长，不按页长截断或伪造总量。
- 酷狗分类直接读取 `/pubsongs/v1/get_tags_by_type` 的组与标签；主栏为精选、歌单广场、排行榜、新歌，更多为有标签的 `category-{tag_id}` 组。歌单广场默认推荐，分类组默认首标签；continuation 绑定会话 scope、平台目标、页码与所选标签，换标签从第一页开始。
- 酷狗榜单摘要只使用目录附带的 `songinfo`，没有 hash 的摘要不是可播歌曲。卡片动作是 `channel`，身份为 `rank-{rankid}`，`pageCode=rank_cid`；打开后核对当前榜单身份，以固定 `kg-tid:369` 请求 `/openapi/kmr/v2/rank/audio`，分页绑定 `rank_cid` 与 total，不走公开歌单读取。榜单录音使用 `audio_info.hash_128`，新歌使用 `hash`，毫秒时长统一转为秒。
- 网易云歌手频道返回独立 `area`、`type`、`initial` 标签组、当前组合 token、圆形歌手卡数据和绑定该 token 的 offset continuation；换筛选从首批重新读取，旧 continuation 不能跨组合复用。
- 网易云 Cube 页面先读取当前 `pageProtocol.pages`，再按页面声明的 `ruleId` 读取推荐歌曲。`algTagInfos: null` 是当前“无算法标签”结果，等价于空集合；其他非数组、非空形态仍明确失败。
- 网易云歌手详情严格核对请求 ID、响应歌手 ID、非空名称、有效头像和 `hotSongs` 数组；热门歌曲经过同一 `PlatformSongs.Wy`／`NativeMusicSong` 归一化，并拒绝重复录音身份。一次详情读取不附带专辑扇出。
- 网易云 VIP 频道由推荐歌曲接口与榜单接口共同组成；榜单只接纳上游名称明确匹配黑胶 VIP／SVIP 的播放列表，普通榜单不能因处于 VIP 页面而被重新分类。
- 分页结果保留平台、页码、页大小、总量、`more` 和 continuation。未知总量使用 `null`，不能用当前页长度伪造。搜索页的 `more` 需平台声明还有后续；没有显式游标的平台短页即末页，QQ 按平台 `nextpage`，空页始终结束；即使平台 `total` 更大也如此。

咪咕歌曲搜索使用原生 group 单位：单页 group 数不超过请求页长，完整展开各 group 的录音版本，不另设 32／1000 版本额度；group 排名、总量和 copyright 身份必须完整保留。其他平台与歌单搜索仍使用 item 单位。

## 网易云每日风格偏好

现有 `MusicNativeApplication` 的 Catalog owner 可将同一 `MusicPlatformCatalog` 实例以 `IMusicDailyStylePreferences` 交给页面；该接口不扩展 `IMusicPlatformCatalog` 为通用写接口，也不单独创建会话、HTTP 或客户端 owner。

- `OpenDailyStyleEditorAsync(token)` 用当前网易账号读取 `/api/homepage/daily/song/config/get`，返回 `MusicDailyStyleEditor(AccountScope, AccountGeneration, UserId, Categories)`。分类为 `MusicDailyStyleCategory(Id, Name, Tags)`，标签为 `MusicDailyStyleTag(Id, Name)`；顺序、身份和名称来自 `data.categorys[] / tagVOList[]`。缺失、重号或无名的目录明确失败。Editor 没有凭据；打开或取消编辑不写偏好。
- `SaveDailyStyleAsync(editor, MusicDailyStyleSelection(CategoryId, TagIds), token)` 只接受开启编辑时的账号身份，在派发前和每次响应后沿用会话复核。换号返回 `NotSent / STALE_SESSION`，不会改借新号写入。页面仍须用自身编辑代次拒绝旧账号回执。
- 保存复制草稿，要求非空、唯一、同一分类的目录成员，并在真正写入前重读配置核对成员。没有硬编码分类、标签、单选类别或选中数量上限。`tags` 为 JSON 字符串，内含数值 `categoryId` 和数值 `tagIds[]`；正整数及 JavaScript 安全整数范围属于固定上游数值协议。
- 一次确认只发送一次 `/api/homepage/daily/song/tag/save`，不自动重试、不做补偿写。token 在准入前已取消则返回 `NotSent / CANCELLED`；准入后继续等待真实写入及回读，调用方取消不能伪装回滚。账号失效、截止或关闭仍围住传输，并等待物理收尾。
- 普通风格读取保留既有 1 分钟缓存和同 key 在途合并。保存准入及写入结算后，仅推进该账号风格分支的内存代次、删除该分支旧 cache/flight，并围住活动读取，使旧请求以 `SNAPSHOT_CHANGED` 失去发布资格。随后以同一账号在新有效代次读取 `/api/homepage/category/daily/song/list`，可合并该代次读取并建立新的 1 分钟缓存；不能接回保存前的 flight 或缓存。默认日推和其他分支不变。

保存结果是 `MusicDailyStyleSaveResult(Status, Readback, SelectionMatches, ErrorCode)`：

| Status | 含义 |
| --- | --- |
| `NotSent` | 偏好写入尚未派发；参数、配置、账号或准入失败 |
| `Rejected` | 已解码的服务业务响应拒绝写入；`ErrorCode` 保留服务错误分类 |
| `Confirmed` | 服务确认写入成功；仍需检查真实回读及选择是否匹配 |
| `Unknown` | 已派发但无法确认写入结果，可能已生效；先读取核对，不自动重试 |

`Readback` 仅在真实回读并通过最终账号检查后非空，沿用 `DailyAsync(Style)` 的 `category/tags/items/scope` 批次形状。`SelectionMatches` 在有回读时按分类和无序标签集合比较，否则为 null。写入确认后回读失败仍是 `Confirmed`，但 `Readback` 为 null 且提供错误；写入失败后回读成功可展示实际选择，同时保留原写入状态与错误。回读或最终会话检查失败时 `ErrorCode` 报该失败，状态仍保留已知写入事实。

数量依据：固定上游 [recommend_category_configs.js](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced/blob/19c7dad6db7d660954c7a615a686ff0acb030f91/module/recommend_category_configs.js)、[recommend_category_set.js](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced/blob/19c7dad6db7d660954c7a615a686ff0acb030f91/module/recommend_category_set.js) 和 [recommend_category_songs.js](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced/blob/19c7dad6db7d660954c7a615a686ff0acb030f91/module/recommend_category_songs.js) 确认上述字段和 XEAPI 配方，没有声明选中数量限制。尚未证实服务是否另有限制，不将 iOS 既有 `3000=1 / 其他=5` 常量当作协议；真实服务限制通过服务错误保留。

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
- Catalog 为单个原始响应指定 4 MiB 缓冲上限；HTTP 使用调用方的真实缓冲上限，不再施加重复的全局 4 MiB 限制。JSON 保留严格 UTF-8、语法和嵌套校验，重复属性按最后值消费，不再遍历附加节点／数组／字符串总额度。规范元数据仍按字段、身份和单曲模型校验；内存中的派生目录结果与累计 continuation 不套用 512 KiB 截断。
- Catalog 不直接写持久文档；客户端受信响应更新交给客户端唯一 writer。目录内存缓存最多 64 项、2 MiB，key 包含平台、账号 scope／generation／userId、操作、完整输入及实际使用的客户端身份／代次；成功响应推进客户端后按最终代次保存缓存。单个结果超过缓存容量时直接返回且不缓存，不拒绝本次读取。
- Catalog 不缓存搜索；搜索结果只由搜索 owner 缓存一次（见[原生搜索契约](music-search.md)）。单曲／歌单最长一小时，其他普通目录最长十分钟；每日推荐使用平台返回的到期时间。显式 refresh 成功后替换同一当前 key。
- 鉴权、权限或会话失效会清除对应平台缓存。缓存命中不绕过当前会话复核，也不授予播放、下载或离线许可。

## 生命周期与错误

- 已准入的 Catalog 读取由服务持有；调用方 token 只取消等待，不传入网络或响应客户端状态保存。同账号、平台、操作和参数的在途读取合并（刷新亦可加入正在读取的同一 key）；返回页面可重新订阅。当前调用完成后按既有缓存策略保留，不新增另一层内容缓存。
- 页面导航、来源切换和悬停退出只退休展示资格；未发送的下一页停止调度。已准入的一页及其必要详情补全由服务完成或超时。账号失效及真实服务关闭仍可终止物理请求。
- App 在失效后调用 Catalog 的物理排空屏障，再释放客户端 owner 与共享 HTTP；订阅返回不代表后台请求已经收尾。服务在无订阅者时仍核对账号并处理鉴权失效，旧身份不能污染新缓存。

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

## 原生音乐榜详情

Windows 已知音乐榜通过可选 `IMusicChartCatalog` 和 `MusicChartRequest` 读取，参数及页面行为见[目录与频道](music-catalog.md#横幅与榜单)。数字专辑／数字单曲的 albumType 分别为0／1，官方榜每周期请求50项；50是上游榜单请求协议，不是首页目录条目数。歌曲指数按真实返回ID分批补齐歌曲详情，每批至多100首是既有详情接口资源边界。没有通用H5、购买或有声书播放承载。
