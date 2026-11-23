# Windows 影视搜索接线与验证入口

行为以[共同影视搜索契约](../common/film/search.md)为唯一来源；规则与搜索程序分别见
[`film-rules`](../../packages/film-rules/README.md)、
[`film-search`](../../packages/film-search/README.md)。本文件只记录 Windows 消费者、装配和验证边界，不另定义算法。

原始列表开关置于综合／单曲等分类同行最右侧，TMDB 状态在开关左边；分类区域不足时独立横向滚动，状态和开关不覆盖标签。切换复用同一 Node 会话，不重新搜索。

类别文字保持系统 Regular，选中态在文字下方显示独立居中的短横线，不以按钮圆角边框充当高亮。搜索卡片沿用服务器版媒体卡展示：备注／年份／更新进度为海报右上角橙色圆角角标，标题下方的电影／剧集／聚合／站点名为居中浅底描边标签；长文本单行省略，空值不显示。搜索不显示评分，首页仍保留评分角标；数据文字直接消费 Node 快照，不在客户端重新推导进度。

影视搜索复用 Meowfilm 前端搜索网格的桌面尺寸：最小列宽180 DIP，横纵间距36 DIP，各列等分可用宽度，海报保持2:3比例；不足一列时收缩到视口，末行沿用完整网格列宽。搜索继续复用 `FilmCard`，仅搜索解除首页176 DIP的卡宽上限。搜索专属 `FilmSearchVirtualizingPanel` 通过 `IScrollInfo` 只实现视口及邻接行并回收容器；状态／更多计入同一滚动范围但不是卡片。标题、来源标签和角标保留系统常规字重，文字不整体缩放；进度角标为12 DIP、18 DIP行高，独立于海报悬停变换，徽标不再将文字与阴影合成到同一效果表面。

## 类型与装配

- App 只装配一个 `SearchResultsPage`，复用既有音乐视图与影视卡片。综合、单曲、歌单、影视分类及手动音乐／影视目标保持现行交互；普通数据刷新不覆盖手动目标。
- `FilmSourceService` 提供来源配置快照及变更事件。搜索范围为启用且开启搜索的猫源与 TMDB，不包含豆瓣。每源传准确安装／站点／generation，以及顶层 `aggregateImages` 布尔和 `order`；不改变已有四个选项、封面单选或列表排序语义。
- `FilmSearchService` 固定借用一个 `RuntimeConnection`，通过 `film.playback.v1` 调用 `film.search.open/snapshot/command/release/handoff`，以 `film` 事件通道订阅 `/v1/film/events/{sessionId}`。同一快照同时提供完整 `cards/rawCards` 与通道状态。
- Node 唯一执行规则、识别、相关度、分页、聚合与排序，持有来源原始响应和规则快照。客户端只显示选定数组并转发动作，不再直接发搜索内容请求、执行 JS、排序或从卡片生成缓存 seed。
- Capabilities 无规则／搜索 JS 内嵌资源；Platform 不再构建或复制独立规则 DLL。实际执行由运行器载荷的公共 script-host addon 提供，缺失运行器或方法时显示真实错误，无原生回退。
- 正式 entry、manifest 与原生 describe 精确装配 `music,cat,source,pan,tmdb,douban,film,netease,music-platform` 九模块。通用默认三模块不变；加入豆瓣模块不扩展搜索范围。运行器装配与依赖分别见[平台接入](app-runtime.md)、[打包](runtime-packaging.md)。
- `FilmMetadataService` 直接读写 TMDB／豆瓣[原生唯一设置关系](native-settings.md)，提交运行器清缓存动作并维护这些操作的关闭排空；`ResolveImage` 保留供装配使用的原引用投影。搜索与详情内容由 Node 搜索／播放会话提供，图片经 `IRuntimeImageClient` 和 `ArtworkService` 消费。有效配置消费、内容缓存、完整度与同 key producer 合并归 [Node 数据 owner](../runtime/film-playback.md)；原生图片缓存与解码预算不等于搜索结果条数限制。原生保存回执与 Node 实际应用分开确认。

## 搜索生命周期

`FilmSearchService` 在后台读取 JSON、映射展示字段并计算身份级集合变化，
Dispatcher 仅提交仍有效的 revision 及必要字段／集合操作；命令与 SSE 竞态按实际提交前驱重算。
`FilmSearchSession` 保留完整已拥有的快照与借用连接，逐卡引用原数据而不深拷贝原始成员。
搜索专属 `FilmSearchCardItem` 按 Node `businessId` 保持身份，
同身份只通知变化字段；共享 `FilmCardItem` 仍是普通不可变模型，仅在准确激活时适配。
状态快照不重映射或改绑卡片，SSE 沿现有容量一的最新快照机制消费。
首轮、加载更多和各源失败／空结果等状态均按 Node 回执显示；不在客户端增加自动重试、截条数或重复取数兜底。
切换原始／聚合模式只选择已准备的对应集合，保留各自滚动位置，不重新映射 JSON。

站点／元数据的未知展示状态投影为 Unknown，不当作完成或执行失败，也不停止有效会话；未消费的 round 或辅助分支不作全对象门禁，原始快照仍保留。加载控制消费真实 loading/hasMore，关联仍核对 sessionId/query/revision。原始／聚合两种视图均局部隔离无法取得实际选择身份的坏展示卡片，健康兄弟和完整原始快照继续保留；缺少有效列表或非空列表全部不可消费时报告真实格式错误，不伪装成功空页。

同词分类切换／返回优先复用仍有效的 Node session。普通离开立即停止旧 UI 发布并发 `command stop`；停止回执仅更新 loading／cancelled 等状态，不替换 cards/rawCards。停止前已接纳的 open 若迟到，只绑定分配 ID 以便 stop/release，不发布其快照。同一 owner 返回通过 snapshot 和 `command resume` 恢复，不重取成功页；旧连接撤权后 `RebindAsync` 排空旧执行再重新 open，保留已加载卡片及其身份，加载／失败快照不先清空成功展示。

新词、来源／顺序／封面配置变化或 TMDB 设置成功保存撤销旧会话发布资格，不自动搜索。退役会话先 stop 再 release，不再以播放页 retainer 延长搜索执行；已建立卡片的打开不依赖它存活。晚到响应不能改写新查询，设置保存失败保留有效查询。

事件连接失败保留当前显示并报告真实错误，同时显式发送 stop 禁止后续新调度；运行器 epoch 撤权后不向替代 owner 重放旧命令。普通 stop、取消等待及 release 不撤销共享 producer；有限在途内容可按原身份完成并入 Node 缓存。真正运行器关闭／来源撤权由对应 owner 取消并排空。

进程内退役停止准入，等待已接纳 open／command／handoff／release 和本端事件订阅结算；不恢复已退役消费者。关闭消费者不接管 App 运行器生命周期，物理进程排空仍由 App owner 负责。

## 搜索到真实播放接收

`FilmSearchContext.PlaybackRequest` 直接读取卡片 `playbackTarget` 和 Node snapshot 的原授权
`SourceSnapshot`。`FilmCapability.ReceiveSearchPlaybackAsync` 在执行仍有效时，以 opaque
`displayKey` 调用 Node handoff 复用数据，再经 Shell 进入真实 `film-playback`；
搜索已退役／连接已撤权时使用卡片固定请求。`HandoffOrLocatorAsync` 对现有受控
失效类别降为该请求，不从显示标题重建目标。较旧点击的迟到回执不能覆盖较新导航。
同一已持有 context 可复用原播放页，其他进入建立各自导航实例；共同身份与交接只见
[影视契约](../common/film/search.md#稳定业务身份)。

`FilmSearchPlayback` 解码活跃 handoff 的 target/query/sources/results/resultHints/search/metadata/cacheEntries/searchReference，并绑定实际 producer；固定卡片路径不伪造这些回执：

- 原始行交接的 `source` 保原单源 typed ID，数字 `0` 与字符串 `"0"` 不互换；不接受聚合成员或 TMDB 简介扩展。元数据页后续选择准确站点文件产生的 `source` 仍独立保留运行器给出的展示资料。
- 未可靠关联的 `group` 保全部准确成员，不退化为代表项；可靠关联使用 Node 的 movie/tv 与元数据身份。
- 原始行、原查询、已成功页及真实缓存 receipt 原样保留，不从显示标题重建查询、不生成新鲜度证明。换 owner 仍沿既有播放接缝降为 hints，不给旧事实盖新代次。
- 播放 open 与 `search.update` 携带 `searchReference:{sessionId,selectionId}`。Node 重新读取该选择的事实与规则；客户端不上传 rules 或推导 metadataTarget。
- `FilmPlaybackOpen` 的 `sources` 保留与当前安装相交的完整原搜索授权，不因当前执行目标为单源而收窄；`results/resultHints` 只转交 Node handoff 给出的真实卡片详情／成员。完整授权与分页事实不变成额外菜单条目，重入与迟到 reference 也不能借新全局来源扩大原授权。
- 搜索列表停止后仍可向保留的播放选择通知有效 reference revision，但不发布其 cards/rawCards。播放端只转发 reference，不重搜、不预取全部详情，Node 负责迟到成员／可靠关联及已准备文件保护；退役搜索不再产生可复用 reference，页面最终释放时解除订阅。
- 初次搜索 open 不生成 `resumeTarget`，允许 Node 跟随迟到 handoff。已有确认播放 snapshot 的同 owner 重进发送当前准确 `resumeTarget`，保留已选元数据、季集、globalEpisode 和 source/candidate；换 owner 不携带失效 searchReference 或仅用于跟随搜索的 resume marker。已确认元数据入口手选准确 source 则独立保存入口 `target` 与精确 `resumeTarget`，不依赖 searchReference 或 owner 仍有效；普通 source/group 入口不补造元数据 provenance。目标不由标题猜测。

搜索派生文本可按 `FilmRuleText` 使用 UInt16 数组。展示 helper 接受数组并为 WinUI 渲染文本，原 JSON 和 opaque 身份不变，不从显示字符串反推选择。原始来源 `raw` 保持 Cat 契约。

Shell 保存模块不透明位置、公共页 `PageId` 及输入草稿，搜索与后续播放页使用同一历史顺序，
不再用 origin／返回搜索捷径跳过中间播放页。`IDesktopNavigationRetention` 保留可达的
`FilmPlaybackPage` 展示实例，不保活搜索执行，也不新增 TTL 或业务缓存。
具体端侧恢复见[播放页](film-playback.md)，共同生命周期见[页面契约](../common/pages/data-requests.md#展示与执行生命周期)。

晚到恢复更新的公共限制见[读取恢复](../runtime/film-playback.md#读取恢复)。

## 验证边界

搜索会话、SSE 推送、元数据详情缓存、视口回收与界面呈现统一通过真实应用 `--debug` 模式验证（详见[Debug 模式](debug-mode.md)）；构建与边界检查由 `npm run verify`、`node scripts/windows/verify-projects.mjs` 及正常 App 编译执行。静态检查、编译构建与真实运行／在线来源验收应分别报告，不能互相代替。
