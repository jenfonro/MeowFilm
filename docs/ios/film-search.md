# iOS 影视搜索

## 装配与类型

业务规则、分页和数据交接的唯一契约见[共同影视搜索契约](../common/film/search.md)，Node 整合程序见[共享搜索程序](../../packages/film-search/README.md)。本端没有 Swift 清洗、季集识别、评分或聚合算法，也不装载客户端 JS 引擎。

- `MeowfilmAssembly.filmSearch` 装配 `FilmSearchService`，借用既有 `FilmSourceService` 与 App runtime；`FilmSearchTransport` 固定同一个 runtime epoch。`film.search.open` 只传 `{query}`，运行器内部通过 `film.video-sources` 与 `cat.sources` 原生设置块解析启用且允许搜索的站点、排序与聚合封面选项。来源换代、禁用及配置变化撤销旧会话的 UI 发布资格，不擅自重搜。
- `FilmSearchSession` 只持有 Node 身份、`FilmSearchSnapshot` 展示与控制状态、订阅及消费者生命周期。分页游标、成功页、TMDB 回退、规则快照、两模式顺序和成员关系全由 Node 管理；原生不复制或解析未消费的 `round`。`snapshot.sequence` 与 `revision` 相同，沿 `/v1/film/events/{sessionId}` 接收完整 `cards`／`rawCards`；不从原始页重算卡片。`search.command more` 分页，切换模式不请求。成功空、通道失败、不可用和取消分别展示；SSE 断开保留已得卡片并显示错误，显式重试用同 session 的 `search.snapshot` 和新订阅，不重发 `open`。
- `cancel()` 同步撤销旧 SSE／在途回执的 UI 发布资格、结束本地 loading，再串行发送 `stop`；不取消 Node 已接纳的内容 producer。stop 回执只接纳 sequence／revision、通道、loading、hasMore 等控制状态，保留已有卡片、原始条目及 metadata 展示；即使 open 或 SSE 晚到后 stop 返回了新卡，也要等明确 resume 才重新发布。保留真实 session ID 供停止、恢复和释放。同词按精确 UTF-8 查询复用当前或调用方持有的会话，只发 `resume`，不重取已成功页。新词停止旧会话；Service 弱跟踪仍由导航持有的会话，消费者释放时排空已接纳 open 并向其真实 ID 发送 `release`。搜索关闭不关闭共享 runtime。
- 原始 metadata 搜索响应、有效查询及详情只是当前 snapshot 的展示数据。`FilmSearchContext.metadataDetail` 缺少详情时按明确 TMDB 类型＋数值 ID 读取 snapshot 中的原 search 行；不根据标题猜关联，不把 partial 升级为 full。`FilmTMDBService` 保留既有独立元数据消费者接口，但不再被原生搜索会话逐项调度。缓存、原时间、revision 和完整度由 Node 保管，客户端不生成缓存 seed 或新鲜度。独立接口在主元数据请求成功后导出附属 `data.cache.entry`；明确的缓存存储／读取错误允许返回无 `cacheEntry` 的主结果，取消、关闭、协议及授权错误仍向上传递。
- `FilmSearchContext.detail` 优先采用已有详情的 original 海报／背景图；不可用时回退卡片图片，不新增请求，不修改列表的 w500 引用或所选业务身份／目标。
- `MeowfilmSearchComposition` 在应用层协调两个业务，将影视 `FilmSearchResultsContent` 注入既有 `MusicSearchViewController`／结果分页容器。`MeowUI.SearchTargetOption`、`SearchResultCategory`、`SearchResultContent` 只定义中性输入和内容生命周期；音乐算法不导入影视。页头、历史和四分类沿用既有搜索页。影视结果只是无页头的内容控制器，复用 `MeowUI.PosterCatalogCell`，搜索补文本角标和来源／聚合／类型标签；首页默认配置和评分语义不变。
- 实际 `MeowfilmCoordinator.showFilmSearchResult` 将 `FilmSearchContext` 交给真实 `FilmPlaybackModel` 和 `FilmDetailViewController`，正式播放进入 `FilmPlayerViewController`，不旁路到裸 URL 播放器。`FilmSearchCard` 携带轻量 `FilmEntry`（`targetId = "work:<sessionKey>"`、`keyword` 及可选 `tmdb`／`douban`／`siteEntry`／`seasonHint`）与 `selectionId`；`FilmSearchContext.playbackRequest` 直接提交 `entry` 与可选活跃 `searchReference: {sessionId, selectionId}`，不再调用 `search.handoff` 或在客户端中转 `cacheEntries`、爬虫 `raw` 行与成员列表。共同身份与跨词附加展示规则见[影视搜索契约](../common/film/search.md#搜索到播放详情的交接)。

原始列表为工具行右侧开关，TMDB 加载／失败／不可用状态在其左边；手机沿现有窄屏工具行布局，不把所有音乐分页标签挤到一行。切换只改同一 session 的展示模式和独立滚动位置；TMDB 失败可用“重试”继续原会话。

`FilmSearchResultsPresentation` 将 `businessID` 的精确 UTF-8 作为 UICollectionView
身份，不以代表项或规范等价字符串合并作品。展示字段不变不提交 diff，
身份顺序不变时仅 reconfigure 实际变化项；选择引用仍更新到当前完整卡片。
完整卡片未变的状态快照不重新映射展示内容，内容变化时复用未变项的投影。
错误通知只更新状态，成功快照不会重复发布已为空的错误。
UICollectionView 继续管理 cell 回收，海报保留现有 URL 幂等与资源生命周期。

`FilmSearchSession` 保留已取得快照，`retainingDisplay`／`retainingLoadingDisplay`
使停止及恢复加载／失败状态不清空已有卡片。App runtime 就绪后，
当前可见内容通过 `runtimeDidBecomeAvailable` 排空旧订阅并重新绑定读取；同一存活会话的
SSE 重连仍走 snapshot／观察，旧会话失效才新建搜索执行。新执行不改变未变卡片业务身份，
旧 reference 不跨 epoch 复活；展示／执行共同边界见[页面契约](../common/pages/data-requests.md#展示与执行生命周期)。

分类高亮沿用分页滑动插值，使用文字下方独立居中的 20×3 短横线，不改变按钮和文字布局。搜索卡片直接显示 Node 的 `textBadge`／`siteLabel`：海报右上角为橙色白字圆角角标，内缩 8、最大海报宽度 70%；标题下的来源／聚合／电影／剧集为居中浅底描边标签，深色模式沿服务器配色调整。两者长值单行省略、空值隐藏，标题仍保留 iOS 两行及原按压效果，搜索不显示评分；首页三张加第四张边缘及仅标题展示不变。

## 真实 TMDB 接线

HTTP／设置／图片的唯一协议见[元数据运行器](../runtime/metadata-data.md)，本端不复制其上游路由规则。`apps/ios/runtime/entries/aggregate.mjs` 保留现有 dataRoot 和 Cat 目录布局，显式装配 music／cat／source／pan／tmdb／douban／film／netease／music-platform 九模块。`RuntimePayload` 校验载荷身份和入口，App launcher 通过真实 describe 按 ID 核对所依赖模块的唯一身份、版本和 contract，见[公共 HTTP 契约](../runtime/runtime-bridge.md)；通用 runtime 默认三模块不变。

`FilmTMDBService.search` 请求 `/3/search/multi`，使用真实原查询、page=1 和 w500；语言／地区／成人选项由 Node 从配置补全；`detail` 按 movie／tv 与数字 ID 请求，使用 original 和 `append_to_response=credits,similar,images`，与智能播放完整详情同键，不请求 configuration。必要查询回退仍由原搜索会话决定。空凭据允许代理端认证，错误按运行器回执保留。设置直接读写原生 SQLite 类型行并通知当前运行器；只有成功保存使旧查询及 Node 详情缓存失效，下次显式同词创建新会话，旧详情保留原查询／已得展示数据。

`RuntimeHTTPClient.metadataImageURL` 保留合法 `/v1/tmdb/images/...` 或 `/v1/douban/images/...` 相对 URL；`MeowfilmAssembly.filmImages` 以该 URL 作为既有 `ArtworkImages` 的唯一缓存键，读时才通过 `readMetadataImage` 加入当前 endpoint／独立 request ID，不需要会话访问凭据，并按准确 provider 核对回执。编码数据不设传输／解码输入字节配额，完整结束后再交给 ImageIO 生成最长边不超过 1024 的缩略图；超过 4 MiB 的图片可显示但不进入共享图片缓存；这不等同于独立的原图像素或解码峰值内存上限。解码后容器和指定图片状态都必须完整，部分可解码图也不缓存。相对图片引用协议拒绝绝对、网络路径、穿越及 redirect，不限制运行器 endpoint 的域名或地址。502 模块错误、断流、错 ID、取消／关闭不能返回成功图片；取消仅撤销真实 URLSession task，完成以物理 didComplete 为准，owner 关闭等待 session 排空。普通外部图片仍由 `FilmHTTP.readArtwork` 的独立通道读取，上游 metadata API 凭据不会进入该通道。

## 验证边界

搜索会话、SSE 推送、跨查询详情缓存、TMDB 设置与图片加载以及关闭排空均通过真实 App `--debug` 模式验证（详见[Debug 模式](debug-mode.md)）；静态边界、Swift 编译、Simulator 构建与签名校验使用 `bash scripts/verify-native.sh`。构建、本地 `--debug` 观察与真实公网账号／代理及播放验收应分别记录，不能互相替代；智能匹配、正式媒体与后台／PiP 契约另见[影视会话](../runtime/film-playback.md)。
