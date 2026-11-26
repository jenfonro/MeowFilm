# Windows 影视搜索接线与验证入口

行为以[共同影视搜索契约](../common/film/search.md)为唯一来源；规则与搜索程序分别见
[`film-rules`](../../packages/film-rules/README.md)、
[`film-search`](../../packages/film-search/README.md)。本文件只记录 Windows 消费者、装配和验证边界，不另定义算法。

原始列表开关置于综合／单曲等分类同行最右侧，TMDB 状态在开关左边；分类区域不足时独立横向滚动，状态和开关不覆盖标签。切换复用同一 Node 会话，不重新搜索。

类别文字保持系统 Regular，选中态在文字下方显示独立居中的短横线，不以按钮圆角边框充当高亮。搜索卡片沿用服务器版媒体卡展示：备注／年份／更新进度为海报右上角橙色圆角角标，标题下方的电影／剧集／聚合／站点名为居中浅底描边标签；长文本单行省略，空值不显示。搜索不显示评分，首页仍保留评分角标；数据文字直接消费 Node 快照，不在客户端重新推导进度。

影视搜索复用 Meowfilm 前端搜索网格的桌面尺寸：最小列宽180 DIP，横纵间距36 DIP，各列等分可用宽度，海报保持2:3比例；不足一列时收缩到视口，末行沿用完整网格列宽。搜索继续复用 `FilmCard`，仅搜索解除首页176 DIP的卡宽上限。搜索网格在 `SearchResultsPage` 中由 WinUI `GridView` / `ItemsWrapGrid` 配合 `ArtworkItemsScope` 与 `Shelf.MeasureCards` 虚拟化呈现并回收容器；状态／更多位于同一滚动范围但不是卡片。标题、来源标签和角标保留系统常规字重，文字不整体缩放；进度角标为12 DIP、18 DIP行高，独立于海报悬停变换，徽标不再将文字与阴影合成到同一效果表面。

## 类型与装配

- App 只装配一个 `SearchResultsPage`，复用既有音乐视图与影视卡片。综合、单曲、歌单、影视分类及手动音乐／影视目标保持现行交互；普通数据刷新不覆盖手动目标。
- `FilmSourceService` 提供来源配置快照并持久化共享表 `film_video_*`（经 `film.video-sources` 原生设置块通知运行器）。搜索范围为启用且开启搜索的猫源与已配置 TMDB，不包含豆瓣；运行器内部直接解析活跃站点顺序、搜索开关与聚合封面选项，客户端不再在 `search.open` 上传 `sources`／`installationId`／`generation`。
- `FilmSearchService` 固定借用一个 `RuntimeConnection`，通过 `film.playback.v1` 调用 `film.search.open/snapshot/command/release`，以 `film` 事件通道订阅 `/v1/film/events/{sessionId}`。同一快照同时提供完整 `cards/rawCards` 纯展示卡片数组与通道状态。
- Node 唯一执行规则、识别、相关度、分页、聚合与排序，持有来源原始响应和规则快照。客户端只显示选定数组并转发动作，不再直接发搜索内容请求、执行 JS、排序或从卡片生成缓存 seed。
- Capabilities 无规则／搜索 JS 内嵌资源；Platform 不再构建或复制独立规则 DLL。实际执行由运行器载荷的公共 script-host addon 提供，缺失运行器或方法时显示真实错误，无原生回退。
- 正式 entry、manifest 与原生 describe 精确装配 `music,cat,source,pan,tmdb,douban,film,netease,music-platform` 九模块。通用默认三模块不变；加入豆瓣模块不扩展搜索范围。运行器装配与依赖分别见[平台接入](app-runtime.md)、[打包](runtime-packaging.md)。
- `FilmMetadataService` 直接读写 TMDB／豆瓣[原生唯一设置关系](native-settings.md)，提交运行器清缓存动作并维护这些操作的关闭排空；`ResolveImage` 保留供装配使用的原引用投影。搜索与详情内容由 Node 搜索／播放会话提供，图片经 `IRuntimeImageClient` 和 `ArtworkService` 消费。有效配置消费、内容缓存、完整度与同 key producer 合并归 [Node 数据 owner](../runtime/film-playback.md)；原生图片缓存与解码预算不等于搜索结果条数限制。原生保存回执与 Node 实际应用分开确认。

## 搜索生命周期

`FilmSearchService` 在后台读取 JSON、映射展示字段并计算身份级集合变化，
Dispatcher 仅提交仍有效的 revision 及必要字段／集合操作；命令与 SSE 竞态按实际提交前驱重算。
`FilmSearchSession` 保留完整已拥有的快照与借用连接，逐卡引用原数据而不深拷贝原始成员。
搜索专属 `FilmSearchCardItem` 按卡片稳定 `Id` 保持身份，同身份只通知变化字段；共享 `FilmCardItem` 仍是普通模型，携带轻量 `FilmEntry` 供激活进入播放页。
状态快照不重映射或改绑卡片，SSE 沿现有容量一的最新快照机制消费。
首轮、加载更多和各源失败／空结果等状态均按 Node 回执显示；不在客户端增加自动重试、截条数或重复取数兜底。
切换原始／聚合模式只选择已准备的对应集合，保留各自滚动位置，不重新映射 JSON。

站点／元数据的未知展示状态投影为 Unknown，不当作完成或执行失败，也不停止有效会话；未消费的 round 或辅助分支不作全对象门禁，原始快照仍保留。加载控制消费真实 loading/hasMore，关联仍核对 sessionId/query/revision。原始／聚合两种视图均局部隔离无法取得实际选择身份的坏展示卡片，健康兄弟和完整原始快照继续保留；缺少有效列表或非空列表全部不可消费时报告真实格式错误，不伪装成功空页。

同词分类切换／返回优先复用仍有效的 Node session。普通离开立即停止旧 UI 发布并发 `command stop`；停止回执仅更新 loading／cancelled 等状态，不替换 cards/rawCards。停止前已接纳的 open 若迟到，只绑定分配 ID 以便 stop/release，不发布其快照。同一 owner 返回通过 snapshot 和 `command resume` 恢复，不重取成功页；旧连接撤权后 `RebindAsync` 排空旧执行再重新 open，保留已加载卡片及其身份，加载／失败快照不先清空成功展示。

新词、来源／顺序／封面配置变化或 TMDB 设置成功保存撤销旧会话发布资格，不自动搜索。退役会话先 stop 再 release，不再以播放页 retainer 延长搜索执行；已建立卡片的打开不依赖它存活。晚到响应不能改写新查询，设置保存失败保留有效查询。

事件连接失败保留当前显示并报告真实错误，同时显式发送 stop 禁止后续新调度；运行器 epoch 撤权后不向替代 owner 重放旧命令。普通 stop、取消等待及 release 不撤销共享 producer；有限在途内容可按原身份完成并入 Node 缓存。真正运行器关闭／来源撤权由对应 owner 取消并排空。

进程内退役停止准入，等待已接纳 open／command／handoff／release 和本端事件订阅结算；不恢复已退役消费者。关闭消费者不接管 App 运行器生命周期，物理进程排空仍由 App owner 负责。

## 搜索到真实播放接收

`FilmSearchContext.PlaybackRequest` 直接携带所点击卡片的轻量 `FilmEntry`（`targetId = "work:<sessionKey>"`、`keyword`、可选 `tmdb`/`douban`/`siteEntry`/`seasonHint`）以及活跃会话的 `searchReference: {sessionId, selectionId}`，不再经客户端调用 `search.handoff` 中转 `cacheEntries`、爬虫 `raw` 行或构造 `FilmPlaybackOpen`／`FilmSearchPlayback`。`FilmCapability.ReceiveSearchPlaybackAsync` 经 Shell 直接进入真实 `film-playback`，由 Node 运行器内部根据 `entry` 与可选 `searchReference` 复用或补全固定关键字会话；较旧点击的迟到回执不能覆盖较新导航。共同身份、原始拆分卡直选及跨词附加展示语义见[影视契约](../common/film/search.md#搜索到播放详情的交接)。

搜索派生文本可按 `FilmRuleText` 使用 UInt16 数组。展示 helper 接受数组并为 WinUI 渲染文本，原 JSON 和选择身份不变，不从显示字符串反推选择。

Shell 保存模块不透明位置、公共页 `PageId` 及输入草稿，搜索与后续播放页使用同一历史顺序，
不再用 origin／返回搜索捷径跳过中间播放页。`IDesktopNavigationRetention` 保留可达的
`FilmPlaybackPage` 展示实例，不保活搜索执行，也不新增 TTL 或业务缓存。
具体端侧恢复见[播放页](film-playback.md)，共同生命周期见[页面契约](../common/pages/data-requests.md#展示与执行生命周期)。

晚到恢复更新的公共限制见[读取恢复](../runtime/film-playback.md#读取恢复)。

## 验证边界

搜索会话、SSE 推送、元数据详情缓存、视口回收与界面呈现统一通过真实应用 `--debug` 模式验证（详见[Debug 模式](debug-mode.md)）；构建与边界检查由 `npm run verify`、`node scripts/windows/verify-projects.mjs` 及正常 App 编译执行。静态检查、编译构建与真实运行／在线来源验收应分别报告，不能互相代替。
