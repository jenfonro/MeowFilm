# iOS 影视

`MeowFilmFeature` 持有影视目录模型、豆瓣首页适配、搜索、原生收藏、播放快照模型和 UIKit 页面。App 在 `MeowfilmAssembly.filmSources` 装配首页数据源，并通过 `FilmPlaybackTransport` 借用唯一 RuntimeSession 的 film 业务；智能播放会话、业务缓存与新增播放资料由 Node 持有，具体见[影视播放契约](../runtime/film-playback.md)。原生媒体复用共享 MeowPlayback／VideoKernel，不依赖音乐业务模块。页面请求遵守[共同契约](../common/pages/data-requests.md)。规则、猫源和视频源设置见[影视设置](film-settings.md)；管理清单已接入影视搜索，侧边栏中的视频源行仅用于列表展示，尚不作为源内容首页或独立搜索入口。

## 首页和数据源

底部“影视”进入保留的首页，使用所属主页面的 App 页头；四个主页面各自保留独立页头。影视页头保留左侧菜单，剩余标题区域为搜索胶囊；菜单打开影视侧边栏，搜索胶囊复用 App 的统一搜索路由，默认选择影视目标，返回恢复原影视首页及滚动位置。当前影视首页数据源仅有豆瓣；侧边栏“首页”返回影视首页，底部“首页”仍进入音乐推荐页。搜索复用现有页面的影视分类，不另建专用搜索页；视频源内容首页尚未接入。

侧边栏主菜单首先为“首页”，其后为视频源列表，不提供元数据来源快捷切换。视频列表观察 App 持有的 `FilmSettingsModel` 快照，使用 `FilmSourceSnapshot.homeVideos` 保留目录顺序及 `FilmSourceKey` 精确身份，仅筛选启用且首页可见的项，不按检测结果过滤。源行是只读展示，不发起源内容请求、自动检测或管理详情导航；首页数据切源尚未接入。菜单样式与底部固定设置沿用[App 外壳](architecture.md#ui-所有权)。

`FilmSourceDescriptor.id` 标识来源；`FilmItem`／`FilmItemIdentity` 原样保留 Node 返回的 `businessID` 和 `playbackTarget`，相等／散列使用已提供的业务身份；非执行展示缺少业务身份时仅按来源／影片／类型区分显示项。共同身份语义见[稳定业务身份](../common/film/search.md#稳定业务身份)。来源自行返回栏目与影片，页面不按豆瓣分类解释其他来源。豆瓣读取与 Windows 一致的四个栏目：热门电影、热门剧集、新番放送、热门综艺，每栏请求 12 个摘要，不预取详情或播放地址。`DoubanFilmSource` 按实际影片 id／title 及必需的业务身份／播放目标隔离不可消费行，保留健康兄弟，从返回目标读取类型，不按栏目造业务目标；真实空数组成功，非空但全部不可消费的栏目失败，刷新失败保留已完成内容。图片地址沿用豆瓣图片代理归一化。

`FilmHomeModel` 持有所选来源、请求及本页面内按来源区分的快照。重复进入不重复读取；切源立即撤销旧请求发布权、取消读取，恢复对应快照或发起首次读取。旧成功或错误不能覆盖新来源。失败在统一错误浮层中提供“重试”，浮层退出后显式重新读取；“确定”仅关闭通知，不发起请求，之后仍可下拉刷新。页面正文只保留加载、失败或空集合状态，不放重试控件。页面从导航生命周期释放时关闭请求。覆盖详情或切换主页面保留首页及滚动位置。

豆瓣 API 与影视图片使用独立匿名 `URLSession`，不使用音乐账号、Cookie 或系统凭据。API 等待后续数据的空闲超时为 15 秒，图片为 30 秒；两类请求的完整资源加载时限均为 30 秒。响应分块读取至完成，不设总字节配额；图片缓存准入与 ImageIO 缩略预算由各自消费者保留。HTTP(S) 重定向继续使用对应的匿名请求，不按域名、Origin、回环或端口阻断；图片地址保留原协议与端口，既有豆瓣图片 CDN 映射除外。真实空结果、网络错误与加载状态分别处理。

## 详情和收藏

详情以显式传入的 `FilmDetail` 为初始展示；带 `FilmPlaybackModel` 的播放实现随后投影 Node 会话的真实元数据、季集与来源快照，加载和错误不冒充成功空数据。豆瓣首页摘要本身仍只有海报、标题、评分与摘要副标题，不授予播放地址或虚构剧集。缺失内容保持空状态；浏览、选源与播放通过会话命令处理，不在 UIKit 重写智能选路或填入示例。

`FilmPlaybackModel` 将高频播放态（`FilmMediaStateStore`）与低频目录／元数据／选集浏览态（`FilmPresentationStateStore`）物理隔离：全屏播放器消费媒体态，详情页与目录页只订阅浏览态，视频播放进度不再唤醒后台详情页重绘或调用 `applyTheme()`。详情页选集列表、演职员表与相似推荐均由虚拟化 `UICollectionView`（`FilmEpisodeCollectionView`、`FilmCastRailView`、`FilmRelatedRailView`，位于 `FilmDetailVirtualLists.swift`）+ Diffable 数据源承载，支持复用与按目录保留滚动位置。

来源按钮按原生当前展示的 `browsing.sourceId` 或独立的聚合来源 provider 显示真实选择；初始未知／在途时禁用未取得的线路菜单并显示“加载中...”，成功没有可执行视频时显示“无可用视频源”，首次读取失败显示单一“加载失败”状态，错误原因及重试进入统一浮层，不展示未解析分享为假文件。菜单保留同名不同详情项。切换未加载目录先同步发布 loading，目录区域与选集互斥；成功切片按来源、typed 线路、资料身份、季度保留，已加载 A/B 切回不发浏览 RPC。UIKit 按业务身份复用按钮，按目录保留卡片滚动位置及卡片／列表选择。目录缓存只保存展示资料，不保存 current、attempt、stage 或业务有效期；迟到结果只能更新对应切片。显式刷新保留成功切片、控件与位置，等待新成功结果替换；刷新失败保留成功展示并报告错误，首次失败不伪造成功。只有运行器确认的来源失效撤销该来源切片，关闭执行或恢复连接不销毁页面展示。

元数据菜单隔离无法消费的未选行，保留健康项的顺序和 typed metadataId；选中项仍须对应可识别的 provider、kind 和准确身份。`metadataSourceId` 缺省或 null 表示无选择，非空字符串须精确命中健康行；非法类型、空串或失效引用报格式错误，不将数字转成同值字符串或静默置空。菜单行的辅助 seasons 仅作可选分季投影，不阻断主元数据已有的完整分季，合法季 0 保留。浏览列表已有 error 时，不解码或展示该列表的 episodes／originalEpisodes；真实 lines、refreshing 状态及独立 current 媒体继续消费，成功列表仍要求可用文件身份与名称。

主标题、简介、总季／集数及海报、背景、logo、评分、日期、演员与相关作品全部消费运行器独立的 `workInfo`；其 `metadata` 为必需的完整作品投影，不从浏览 `metadata`、supplement 或站点详情补字段。主资料固定 TMDB 优先、无 TMDB 使用可靠豆瓣；TMDB 空简介的可靠豆瓣填缺由运行器完成。总量缺失保持未知，不从当前文件数推断。`originalDetail.item` 的 sourceID、ID、kind、businessID／playbackTarget 不变，资料 provider 不能更换收藏对象。顶部仅在作品投影实际变化时更新同一套元素，季度目录另读取 `metadataSourceId` 对应的确认资料。

资料视图偏好保存于原生 `film.preferences.metadataViewProvider`，默认 `tmdb`，仅手选资料视图才写入；只有一种可靠资料时双向临时回退到该 provider，不改偏好。站点菜单的 TMDB／豆瓣聚合项只选择聚合浏览来源，不改资料视图。`browse.metadata` 总携带 `keepSource: true`；资料切回命中成功切片时仍保存用户偏好，但零浏览 RPC。设置表单的旧草稿不能覆盖独立保存的资料偏好。成功目录的精确选择只看自身读取状态和连接准入，不受上一个正式目标的 loading／unavailable 阶段阻挡。详情总播放、元数据集数、候选及原始目录文件统一发送 `select.target`，原样携带运行器产出的完整 `playbackTarget` 和展示资料的 `metadataSourceId`，不发送坐标-only／candidateId-only 命令，也不先补浏览 RPC。原始目录固定保留自己的来源、线路、资料及季度选择，另一页面浏览不改变其文件定位。

浏览命令（包括季度、资料及显式刷新）携带 `sourceId`、typed `lineId`、`metadataSourceId`、`season` 完整上下文，使运行器仍在 B、页面已本地切回 A 时，新读取可精确定位 A。运行器负责原子采用上下文并只读取所选目录；缓存命中不发送命令。`browse.metadata` 不进入原生媒体 intent，不取消 native preparation、阻塞提交或切换实际 current。独立播放器有真实输出时 `pauseOrResume()` 只控制当前原生媒体；无输出时仅在实际播放 owner 可执行时发送 `play`，不使用本地浏览目录作为播放目标。媒体准备的内容类型读取主作品 `workInfo`／实际 target，保留同一电影换源时的位置与现行准确剧集定位。

`MeowfilmAssembly.makeFilmPlayback` 直接消费条目携带的稳定 `playbackTarget`／`businessID`；缺少执行身份明确失败，不按旧条目 provider／ID 补造目标。
无 `searchReference` 的元数据重入保留原始 `target`、metadata seed 和入口信息，
以 `resumeTarget` 携带当前准确 `source`／坐标；不限豆瓣入口。搜索入口已可用卡片固定目标，
reference 只复用活跃事实；原本没有元数据的 source／group 不补造元数据 provenance。

`FilmPlaybackModel`／`MeowfilmFilmMedia` 的媒体接缝把真实 attempt 交给共享 MeowPlayback 的两阶段 prepare／commit；播放状态来自实际内核输出，不用本地计时模拟。默认主核 `MeowVideoKernel` 基于锁定 FFmpeg 8.1.2 与自研异步 VideoToolbox／`AVSampleBufferDisplayLayer`／实时 SPSC 音频管线，可选 `MeowAetherKernel` 保留相同的 prepare、实际首帧、命令及关闭协议，具体选择与回退见下文。核心关闭等待真实输入、解码、轨道、音频与渲染工作退出后才释放媒体 receipt。直连输入的 HTTP headers／Cookie 通过每实例内核选项注入，运行器 receipt 保持空原生上下文。Node 仍拥有业务会话及媒体资源授权，见[共同原生播放契约](../common/film/playback.md)。影片收藏继续由稳定 `FilmLibrary` 持有，用原 namespace `meowfilm.film.library.v1` 保存身份记录，记录包含当前 `businessID/playbackTarget`；收藏与重开直接消费稳定目标，不提供旧收藏双身份匹配或缺字段补全。读取隔离不可用及歧义身份条目，无关增删保留原始坏行和精确扩展；坏根或非空全坏明确失败。它不复制进 Node v2 历史／人工匹配／屏蔽／偏好／聚合注册库，也不写音乐曲库。

视频定位能力由对应 session 经 `PlaybackVideoDecoding → PlaybackController` 透传：原生主核准备后的有限 HLS／DASH 时间线使用实际探测到的 demuxer 时间定位能力，不要求主清单支持 HTTP Range；普通 progressive 输入使用核心的字节定位能力。Aether 使用真实引擎的 `canSeek`，候选回退后改为透传实际原生主核能力。未准备、失败或已撤销的 session 不接受 seek，已知时长不等于可定位。不按 URL 后缀或调用方 format hint 推断能力，也不新增 live／DVR 时间线支持。

媒体准备或真实播放失败的 `media.failed` 回执携带已有原生适配器的有限 `code`，不发送异常正文、URL 或凭据。运行器区分取回执、准备和播放阶段，保留真实原生失败原因；候选续选的过程错误不重复弹出最终错误。客户端沿用准确 attempt、取消、资源释放与系统 owner 边界，不因错误码扩展改变手动准确播放或自动准备策略。

iOS 请求继续携带 `platform: ios`，正常新进入的自动准备由运行器 `iosAutoPrepare` 与既有入口范围控制。
已有展示的重开由 `FilmPlaybackRequest.restoreOnly` 和 `restoreBrowsing` 传递原 snapshot 的
来源结果、typed 线路、元数据来源、季度及真实来源指针，协议见[读取恢复](../runtime/film-playback.md#读取恢复)。
仍有同一执行 snapshot 时 `open()` 只恢复观察，不再额外提交恢复刷新。

地址重试控件仅在运行器提供 `retryMediaAttemptId` 时可用，并提交该准确身份；不按通用失败状态猜测媒体候选。`initializationError` 阻止新播放，资料初始化失败沿现有刷新入口执行 `browse.refresh`，不改用地址重试。
收到 `prepareOnly` 也不启动原生媒体或写入观看事实，进入详情不自动实播或呈现全屏播放器。

`FilmPlaybackModel.snapshot` 持有实时执行数据，`presentation` 独立保留已加载展示；
`FilmDetailViewController`／原始目录读取后者；原生已提交输出的暂停、缓冲、播放、结束和失败优先读取实际 owner，下一候选准备及目录读取不覆盖它。无输出时才使用实时执行阶段；没有活动输出或可执行意图时禁用播放及媒体操作。
`close/resetSession` 不清展示，读取恢复完成前保留已成功内容；成功目录切片与当前展示选择独立于执行会话。详情轨道按相同业务身份和条目内容复用视图，
不以新 session 或来源 generation 清空选集。页面可见性通过 `presentationOwners` 登记，
隐藏不直接关闭；真正 inactive 仍收口执行／媒体，不把保留展示当作媒体保活。
共同保留和导航边界见[页面生命周期](../common/pages/data-requests.md#展示与执行生命周期)。

App 的 RuntimeSession 就绪回调由 `MeowfilmAssembly` 转给影视搜索、当前详情模型及仍持有媒体资源的模型；
`runtimeDidBecomeAvailable()` 只为可见展示在旧 executor／原生媒体排空后
`renew` transport 并用 `restoreOnly` 重开。Transport 在释放／关闭回执结算前仍固定旧 client，
不把旧回执发给新 epoch；隐藏页再次激活才按需绑定。

详情模型 `MeowfilmAssembly.filmPlayback` 与实际媒体模型 `filmMediaPlayback` 分开。每个模型使用绑定自身的媒体适配 Session，正式资源记录保留其模型；创建／激活 B 详情不覆盖 A 的媒体记录，A 的真实 PiP 可保留。系统上下集路由到准确 current 的模型，PiP 恢复通过媒体 session 定位 A 的独立播放器，而不是当前正在浏览的 B。新正式媒体提交才交接 owner，原媒体资源排空后释放记录；后台、关闭及运行器生命周期同时覆盖详情和仍活跃的媒体模型。详情页不新增内嵌播放器。

iOS 影视只使用共享原生媒体 owner，不提供外部播放器入口、URL 模板或应用启动注入。进入后台或离开播放器时，仅真实 PiP／系统 owner 可通过 `keepMedia` 保留当前媒体；普通关闭等待原生输出和准确资源回执排空。页面重开沿用既有会话恢复与显式播放流程，媒体回执由实际原生资源的释放完成确认。

片源错误确认和 `select.target` 验证等待结果时只暂停候选提交，不提前取消原候选或重新取址。拒绝后恢复同一 pending／receipt 的提交协调；接受后按权威快照撤销旧候选。媒体提交前重新对齐当前播放位置／暂停意图，并在最终同步准入检查后才切换输出。

运行器不可逆退出时，业务释放回执可能无法取得，`isReleased` 与释放错误如实保留。`canLeave` 另外要求本次关闭流程完成、该会话的解码／输出／I/O 已实际排空，才允许退出失效页面；普通 RPC 或 SSE 断连不能绕过这一门槛。

搜索交接规则只见[共同契约](../common/film/search.md)；本端装配与验证边界见[影视搜索](film-search.md)。本节描述当前 iOS 消费者实现，恢复晚到更新的公共限制见[读取恢复](../runtime/film-playback.md#读取恢复)；静态边界校验、原生编译与 `--debug` 实跑验证需分别取得证据，不能把接线完成视为整端验收。

## 内核选择与候选回退

先由 Node 返回播放地址与媒体 receipt，再在原生候选内部使用内核；不让用户退出播放页或重新匹配取址。首选原生内核（`native`）／AetherEngine（`aether`）的选择、生效时机与倍率校正见[播放器设置](film-settings.md#播放器设置)。`PlaybackVideoEvent` 携带实际 `engine`、`chapters`、`tracks`、缓冲、分辨率与诊断遥测，`MeowfilmFilmMedia` 投影为 `FilmMediaState`，播放页与 `--debug` 据此反映真实运行态。

`makePlaybackVideoDecoder(engine:)` 直接返回对应主核（`VideoSession`）或可选核（`AetherVideoSession`），不再使用多层包装视图壳。未提交阶段的 `.unsupportedFormat` 一次 Aether → 原生内核回退由 `PlaybackVideoLease.play(...)` 显式编排：覆盖 prepare、提交位置对齐或首帧渲染阶段；先记录 `film-playback` 的 `decoder.fallback` 结构化日志，撤出旧视图并等待 Aether 本地排空（`closeLocal()`），再创建原生主核并在同一媒体 receipt 上按最新 donor 位置重新对齐、渲染首帧与提交。该替换不更改业务 session／attempt 或媒体 receipt，不执行第二次取址、提前 `media.failed`／release，也不在回退过程中提交 started。

网络／输入失败、超时、普通输出失败、seek 失败及取消不作为能力回退理由；原生主核失败也不再回退。首帧及最终准入检查成功并提交后，不再换核。取消期间不创建替代，旧输出的排空仍属于原候选释放路径；这些语义须在 `--debug` 实跑中确认，不能只凭状态标记证明。

Aether 7.68.0 的 DASH 路径仅覆盖未加密 static、单 Period、SegmentBase／sidx 的有限结构，不提供 SegmentTemplate、SegmentList 或加密 DASH 能力；本地 wrapper 对非空 ClearKey 明确返回不支持。运行器受控流解决网络输入和请求上下文归属，不补清单解析、转码或解密能力。上游有限 typed 能力错误经宿主补丁分类，是否在实际清单／错误包装路径到达 `.unsupportedFormat` 仍须实测，不承诺任意失败都自动回退。

运行器 receipt 保持空原生请求上下文，来源凭据留在[Node 媒体流](../runtime/film-playback.md#停止撤权与本地流)。可信原生直连输入在原生主核使用逐实例 `AVDictionary` 并在 open 后即时擦除，在 Aether 使用本次 `LoadOptions.httpHeaders` 并关闭 `nativeRemoteHLS` 避免绕回 `AVPlayer` 共享 `HTTPCookieStorage.shared`。

Aether 候选配置 autoplay=false、静音、关闭上游系统 owner 并设 15s prepare 截止；原生路径读取 `AVPlayerLayer.displayedPixelBuffer()`，软件路径读取 `sampleBufferRenderer.displayedPixelBuffer()` 作为首帧检查，不把 ready 当作首帧。15秒等待截止归类超时，不触发能力回退。关闭先取消准备／seek／渲染／进度任务，再等待有界 `stopAndDrain()`；依赖隔离及宿主补丁见[Aether 构建边界](../../native/AetherKernel/README.md)。Apple 编译、首帧／seek／排空及回退须分别核对真实结果；HDR／Dolby Vision 标签和配置不证明显示效果或 profile7／FEL 支持。

## 真实章节与跳过

原生主核和 Aether 均将容器真实章节映射为零基时间轴上的 `PlaybackVideoChapter(title,start,end)`；不按影片名、固定秒数或无标记音视频猜测片头片尾。`FilmPlaybackChapterModel` 通过当前 session／attempt／resource 的 `media.chapters` 借用 Node 识别明确完整标签，准确授权、输入范围和响应语义只见[运行器章节识别契约](../runtime/film-playback.md)。

「自动识别片头片尾」默认开启，但识别本身不自动 seek。播放位置落入有效识别区间时提供「跳过片头」或「跳过片尾」，用户操作经当前原生媒体 seek 到区间终点，不伪造 ended。关闭开关、换资源／attempt、取消或关闭使旧识别结果失效；无真实标记不展示跳过。实际章节读取、识别接线与 seek 完成须分别验证。

## 布局和交互

首页竖海报为 2:3，一屏三张完整卡片及第四张边缘；宽度按当前安全区视口计算。卡片只显示海报与标题，不显示评分（评分在详情元信息中）。卡片使用 `pic.normal`；同一条目规范化后的 `pic.large` 作为 `FilmItem.largePosterURL`，供详情顶部大图使用。详情按详情背景图、`largePosterURL`、卡片海报的顺序读取，大图失败时回退到下一项。顶部大海报放在固定裁剪的图片框中，透明度渐变遮罩挂在图片框上，在其下缘衔接页面背景；标题、元信息与播放、收藏位于图片框下方，不叠在海报或渐变上。海报静止时略微放大，上滑时图片框与正文同速上移，框内图片随标题接近页头缩回原尺寸，始终铺满图片框，两侧不露空白，渐变固定在图片框下缘。页头背景在海报位于其下方时保持透明，标题接近页头下缘时淡入，到达时不透明并显示片名，因此滑动时页头下缘不出现色带；下拉回弹时图片框保持在顶部。海报可见且页头未过半显现时，状态栏和返回键为浅色，并配随页头显现淡出的顶部暗色渐变；之后或没有海报时恢复页面默认样式。减少动态效果时取消缩放。

音乐与影视共用 `MeowUI.PressFeedback`：触摸时立即缩小和降低透明度，释放、滚动取消、离屏及复用恢复。单元格通过 `layoutAtRest` 在静止几何下完成系统布局，按压缩放不改变内容尺寸，标题不会重新折行。共享 `ArtworkImages` 与 `ArtworkView` 负责缓存、后台解码、取消与复用，同一缓存内相同 URL 的并发读取和解码合并，最后一个消费者取消需等实际读取停止；音乐、影视分别注入自己的数据读取规则和独立缓存。音乐保留显式重试和可见新挂载时的可恢复错误重试，非法地址、坏图及资源超限不重发同URL；影视不增加自动挂载重试。

## 分类浏览

首页支持分类浏览的数据源将栏目标题与紧随其后的 SF Symbol `chevron.forward` 合为一个无边框按钮，点击整块标题入口通过 `FilmCategorySource` 与 `FilmCategoryModel` 打开对应 UIKit 页面，不再显示独立“查看更多”。按钮保留 `film.home.more.<section>` 标识、“查看更多栏目名”无障碍名称和标题语义，点击区域沿用 44pt 页头高度；不支持分类的数据源只显示静态栏目标题。首页刷新继续使用原生下拉刷新及既有加载状态，不增加栏目级请求。卡片进入既有详情导航。豆瓣电影、剧集、动漫、综艺使用原有 `recent_hot` 数据来源，动漫保持 `tv_animation`，不接入服务端番剧日历。

分类页安全区内每行三张 2:3 海报卡片，沿用首页图片与按压反馈。筛选名称固定在左侧，每个维度一行，右侧使用共享 `MeowUI.FilterTagsView` 胶囊：单行横滑，溢出才显示箭头，展开后换行并撑开布局，选中后收起该行。音乐通过自己的业务类型适配器使用同一控件，原有标识和行为保持不变。

电影组合“分类＋地区”；剧集在“剧集”分类下支持地区，选择动漫或纪录片时隐藏地区（上游的 `type` 无法同时表达这两种类型与地区）。切回剧集保留先前地区选择。综艺支持国内／国外；动漫固定为现有豆瓣动漫来源，不展示无效筛选。

分类每次请求 24 条，成功页按响应原始条数推进偏移，短页结束；混合页的坏行及重复项仍占用原始偏移。非空全坏页失败，不推进或结束分页；真实空页成功结束，首屏刷新成功为空时清空旧内容。显示条目按来源与影片 ID 去重，不受首页 12 条预览裁剪影响。下滑接近底部或点击“加载更多”读取下一页；筛选变化清空旧列表并从首屏重载，取消和请求身份共同阻止过期结果写入。分页失败保留已加载影片，通过统一错误浮层中的“重试”显式重读同一偏移，失败期间底部分页禁用且滚动不自动重试；“确定”关闭浮层不发请求，仍可下拉刷新首屏。离开页面生命周期时取消请求，详情返回保留当前筛选与滚动位置。

## 影视播放器

`FilmPlayerViewController` 通过 `present(from:)` 全屏模态进入，默认横屏并支持两侧横屏；呈现前记录原页面方向，退出完成后恢复，避免读取已经被播放器旋转的场景方向。全屏是页面呈现方式，不再提供全屏切换按钮。画面始终由同一原生媒体 surface 承载，显示控制层、旋转、打开设置和 PiP 恢复不创建第二播放器。

控制层采用服务器 `ArtPlayer.vue` 的双层胶囊样式：深色半透明外壳包裹整组，内层图标按钮为圆形、文字按钮为细描边短胶囊，点击区域至少 44pt。顶部保留返回与片名／来源；进度条下方两组分别为播放／暂停、上下集、音量和时间，以及网盘、画质、换源、片源错误、PiP 和设置。组宽不足时分行，不拉伸为整屏裸文字按钮；仍需收缩时优先截断可变来源／画质名称，保留固定操作名。音量入口打开设置中的音量滑块；倍速、画面比例、短视频和地址重试仍在设置内。网盘与画质显示当前候选，不从线路标签猜测标准网盘身份。片源错误仍先确认再提交捕获的准确 attempt。

画面按触摸起点左／中／右等分，单击等待双击识别失败，按[播放器偏好](film-settings.md#播放器设置)执行；默认单击显隐播放栏、双击播放／暂停，两边选择快进／快退时分别后退／前进指定步长。手势不接管控件或系统边缘返回区域；长按使用绑定准确 session／attempt 的临时倍率。

播放器偏好按实际消费字段去重：同值 reload／保存不重发偏好；手势配置未变不取消临时倍率和进行中的手势，章节开关未变不重置章节识别，控制中心可见性仅在该字段变化时更新。真实手势偏好、attempt 或内核变化仍收口原交互。

一次 pan 在开始移动时锁定方向及操作：横向调进度，左侧纵向调亮度，右侧纵向调音量，中区纵向仅在短视频模式与切集开关同时开启时进入相邻集流程。禁用的操作不转成另一种手势；进度拖动只预览，正常结束且身份仍有效才提交一次 seek，取消不跳转。亮度调整在隐藏、后台或离开时归还原屏幕值；长按、亮度、音量、进度分别受对应触觉开关控制。

正常播放空闲2.2秒隐藏控制；暂停、缓冲、未起播、错误、设置／原生菜单、拖动进度、长按及 VoiceOver 期间暂停自动隐藏，手动显隐仍由点按动作控制。交互结束后重新计时，离开可见生命周期停止计时并取消进行中的手势。普通关闭等待原生媒体与会话排空，真实 PiP 则沿用[系统 owner 生命周期](system-media.md#影视卡片与内核边界)。手势交互与 PiP 接缝需经真实运行与设备验证确认，不以文档或编译代替实机结论。
