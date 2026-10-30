# 个人目录与目录页面契约

共同规则分别见[页面数据请求](../common/pages/data-requests.md)和[平台客户端上下文](../common/music/platform-client-context.md)。本文件只补充 Windows 页面入口、投影和实际交互契约。

## 页面归属

- 个人歌单、每日推荐、私人 FM、相似歌曲、专辑、心动推荐和队列推荐均属于同一个 Windows App 的目录能力，不是独立产品或第二套导航壳。
- `MusicCapability` 是页面状态 owner；`MusicCatalogView`、`MusicDiscoveryView` 和 `MusicRecordView` 只投影当前状态并转发用户意图。
- 页面不持有账号 Cookie、平台请求、曲库写入或播放引擎。

## 当前入口

| 用户入口 | 原生命令 | 约束 |
| --- | --- | --- |
| 个人歌单 | `music.catalog.personalPlaylists` | 明确平台、分页、账号代次 |
| 歌单详情 | `music.catalog.playlist` | 原 locator 和 snapshot，不猜私有权限 |
| 每日推荐 | `music.catalog.daily` | 网易云当前账号，按可见入口读取 |
| 私人 FM | `music.catalog.fm` | 明确 mode／submode，不把未知卡片当默认 FM |
| 相似歌曲 | `music.catalog.similar` | 使用当前录音的原平台身份 |
| 专辑 | `music.catalog.album` | 使用明确 album id，不按标题搜索 |
| 心动推荐 | `music.catalog.heartbeat` | 用户明确触发，绑定当前歌单与歌曲 |
| 队列推荐 | `music.catalog.queueRecommendations` | 读取当前队列版本和当前歌曲 |

## 状态与交互

- 页面状态按 `idle/loading/content/empty/error/cancelled` 区分；已有内容刷新时保留内容，但不能启动第二条并行加载链。
- 卡片主体、播放按钮和栏目级翻页按钮分别拥有命中区域。页面模型必须按卡片类型决定进入详情或直接播放，不能让所有卡片共用一种行为。
- 栏目级悬停和翻页按钮由栏目容器持有，指针从卡片移到按钮时不得丢失可见状态。
- 入口卡片默认保留标题暗色渐变和封面到底色的渐变衔接；悬停遮罩与默认遮罩独立，均不参与命中。
- 每日推荐保留上游有效的 `dayOfMonth`（1–31）和展开文案 `previewSubtitle`，折叠时仍使用当前 `subtitle`，不从本地日期或歌曲标题编造日历、文案。
- 页面切换、筛选、账号变化和关闭会取消当前代次；被取消或过期的结果不更新卡片、选择或错误状态。
- 个人入口只在显示条件满足时出现。无账号、无权限、空结果和协议错误分别呈现，不用公共推荐或旧缓存替代个人数据。

## 播放与保存

- 卡片播放和保存通过目录 `prepare`／`commit` 接到唯一曲库和队列 owner。
- 点击某一首时同时核对目标歌曲身份和队列版本；用户在准备期间产生新播放意图会撤销旧动作。
- 页面离开只清理页面请求和未提交动作，不停止已经由全局播放器接管的当前媒体。

## 请求与预览

Windows 预览卡片的前三行由已有批次／成员页投影，不作为独立预加载策略。分页、预览单飞、物理并发及退休规则见[目录与频道](music-catalog.md)。
