# 原生音乐平台目录契约

## 所有权与装配

- `MusicPlatformCatalog` 是固定平台内容读取的原生 owner，公开契约为 `IMusicPlatformCatalog`。
- 网易云、QQ、酷狗从 `IMusicPlatformSessionSource` 借用当前账号会话；酷我和咪咕只使用匿名公共元数据链，绝不借用其他平台账号。
- Catalog 不持久化账号、不修改云端内容、不启动 Node、不调用本地服务，也不拥有页面状态。
- 账号 owner 在登录、退出、换号或会话失效时调用 `Invalidate`；消费者不能自行缓存 Cookie 或重建会话。

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

共同规则：

- 每次调用必须显式给出平台和业务目标；不接受上游 URL、任意 module/method、Cookie 或脚本安装 ID。
- 搜索和普通分页的页码从 1 开始，页大小最多 100，最多 100 页。
- continuation、snapshot 和个人歌单 locator 都绑定当前 scope、目标和已见身份；不能跨账号、跨列表或跨页复用。
- `Refresh=true` 只绕过本次目标的当前内存缓存，不触发其他平台、其他栏目或隐藏页面请求。
- 失败页不前移游标；不完整页、重复身份、停滞游标和 snapshot 变化必须明确失败。

## 当前输出

歌曲只使用当前根字段：

```text
songId
title
artist
album
duration
cover_url
source_data
sources
quarantinedSources
```

- 根对象没有平台 `id`；平台录音身份只在 `source_data` 和 `sources` 中。
- `sourceKey` 是当前规范 JSON 数组字符串，用于精确录音选择。
- 搜索、歌单、推荐和频道返回的歌曲都经过同一 `NativeMusicSong` 校验，不能把原始平台对象直接送入 UI 或持久化。
- 歌单卡片保留平台、原生 ID、名称、封面、数量、创建者和可用动作。个人目录额外返回绑定当前账号的 locator；locator 不是凭据或可转移授权。
- 网易云歌手频道返回独立 `area`、`type`、`initial` 标签组、当前组合 token、圆形歌手卡数据和绑定该 token 的 offset continuation；换筛选从首批重新读取，旧 continuation 不能跨组合复用。
- 网易云 Cube 页面先读取当前 `pageProtocol.pages`，再按页面声明的 `ruleId` 读取推荐歌曲。`algTagInfos: null` 是当前“无算法标签”结果，等价于空集合；其他非数组、非空形态仍明确失败。
- 网易云歌手详情严格核对请求 ID、响应歌手 ID、非空名称、有效头像和 `hotSongs` 数组；热门歌曲经过同一 `PlatformSongs.Wy`／`NativeMusicSong` 归一化，并拒绝重复录音身份。一次详情读取不附带专辑扇出。
- 网易云 VIP 频道由推荐歌曲接口与榜单接口共同组成；榜单只接纳上游名称明确匹配黑胶 VIP／SVIP 的播放列表，普通榜单不能因处于 VIP 页面而被重新分类。
- 分页结果保留平台、页码、页大小、总量、`more` 和 continuation。未知总量使用 `null`，不能用当前页长度伪造。

咪咕歌曲搜索使用原生 group 单位：一个 group 可展开最多 32 个录音版本，单页最多 100 group／1000 个原始版本；group 排名、总量和 copyright 身份必须完整保留。其他平台与歌单搜索仍使用 item 单位。

## 会话与账号隔离

- 借用会话包含平台、scope、generation、userId 和 Cookie；凭据属性不可序列化、不可日志化，`ToString()` 只返回脱敏文本。
- 每个账号平台请求在派发前、HTTP 后和发布前重新核对当前会话。
- 会话源不可用返回 `CREDENTIALS_UNAVAILABLE`，不能降级为匿名请求。
- 酷我和咪咕的匿名 scope 与账号平台完全分离。即使其他平台已登录，也不会把该会话带入公共请求。
- 网易云需要账号或设备上下文的能力必须显式返回 `AUTH_REQUIRED` 或 `DEVICE_CONTEXT_REQUIRED`。
- 账号改变会同步清除对应缓存、围住活动操作并取消其传输；旧成功和旧失败均无权提交。

## 网络、缓存与预算

- 固定 allowlist origin、scheme、方法和路径由 `MusicPlatformHttp` 与平台请求构造器拥有；其中酷我当前公开配方包含受限 HTTP 路由，其余能力不能据此扩大明文网络权限。全部请求禁止自动重定向、系统代理、Cookie jar 和任意 URL。
- 默认调用截止时间为 25 秒；公共平台也不会超过 25 秒。
- 最多八个 Catalog 操作、四个全局物理请求、每平台两个物理请求；单操作最多八次请求。
- 调用方取消或失效后，不合作的传输仍占用物理预算直到真实结束，不能通过提前完成 Task 制造额外请求。
- 原始响应最多 4 MiB，公开结果最多 512 KiB；JSON、字段、数组、图片 URL、文本和 ID 另有更窄预算。
- Catalog 只有有界内存缓存，不写持久文档：最多 64 项、2 MiB。缓存 key 包含平台、账号 scope／generation／userId、操作和完整当前输入。
- 搜索缓存最长一天，单曲／歌单最长一小时，其他普通目录最长十分钟；每日推荐使用平台返回的到期时间。显式 refresh 成功后替换同一当前 key。
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
