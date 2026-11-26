# iOS 小说

小说使用现有 UIKit 页面、导航、搜索组件和配色，不嵌 Reader 网页外壳。
共同业务语义与排除项见[阅读契约](../common/novel/reading.md)，RPC、Cookie 与 Web
helper ABI 见[书源仓库](../runtime/source-library.md)；本页只说明端侧装配与验证入口。

## 使用与 owner

- 底栏“小说”直接显示 `NovelHomeViewController`，与影视首页共用 `MeowUI/Catalog/` 下的
  `PosterHomeViewController`、`PosterCategoryViewController` 与 `PosterCatalogCell`，
  不设独立分类页。保留 App 原有“菜单＋搜索小说”头部；首页每一横向海报栏目对应当前书源
  `exploreKinds` 返回的分类分组（空 URL 标题归拢其后连续 URL 项，无分组标题的 URL 项
  各自独立成栏），首屏批次与滚动进入视口按需准入栏目并限制并发请求数。点击栏目标题进入
  `NovelCategoryViewController`（`novel.data`），多子分类栏目在顶部展示 `FilterTagsView`
  “分类”筛选行，单入口栏目隐藏筛选区；默认首项直接复用首页已加载的第 1 页书籍，切换筛选项
  或上拉／点击“加载更多”按页码请求并去重。不推测题材／字数等组合请求参数。
- 小说侧边栏显示“首页”及全部已添加书源，观察 `NovelFeature.sources` 的既有顺序和名称，
  可发现的源直接选择同一个 `NovelDiscoverySession`，停用或没有发现能力的源禁用。
  不另建来源仓库。旧首页的书架／历史切换和独立分类入口已移除，不迁移或增加兼容入口；
  本地书架与阅读历史收口在底栏“我的”页的「小说」标签（`NovelPersonalLibraryViewController`／`NovelPersonalCategoryViewController`，复用 `PosterHomeViewController` 固定呈现「我的收藏」与「历史记录」两栏，点击书卡统一进入 `NovelDetailViewController`）。
- `NovelDiscoverySession` 拥有当前来源、栏目列表与各栏首页状态，`NovelCategoryModel`
  拥有栏目详情页的子分类选择、页码与书籍列表。隐藏取消在途请求，返回恢复未完成工作；
  revision 变化失效旧代，关闭等待已接受请求排空。来源失效不自动跳到另一个源，错误通过统一
  重试浮层呈现；`NovelFeature` 只发布最新一次来源清单刷新，防止启动与页面并发读取时迟到
  清单回退已接受的 revision。
- 首页与栏目详情使用 `PosterCatalogCell` 海报卡片（封面走 `NovelImageStore` 受信图片通道，
  无封面显示系统书籍占位图标），搜索结果使用 `NovelBookCell`（74×104 封面、书名、作者、
  单行简介、原始字数、来源名／实际候选来源数量，章节可覆盖在封面底部）。字段缺失不编造、
  不额外逐本抓详情，普通文字 Regular，封面经 `NovelImageStore` 后台解码、按稳定资源身份缓存并
  合并并发请求；滚出视口只归还视图借用，滚回直接命中缓存零重复 IPC，页面卸载或源版本变化时
  沿受信释放路径排空。
  搜索支持音乐／影视／小说三个目标，各结果分类保留独立消费者，返回详情后保留原查询。
  搜索逐源记录失败与页码，来源 revision 变化取消旧代；隐藏查询返回后才恢复。
  `NovelSourceClient` 在搜索／发现页内隔离不可消费的书籍，保留健康兄弟；空页正常结束追加，
  非空页全部不可用或缺少列表则失败，不提前推进页码。
- `NovelSource` 在 list／get／import／save 回执中共用来源投影；
  weight／lastUpdateTime／respondTime 只接受可选数字，hasExplore 只接受可选布尔值，
  不可消费时视为未提供，排序和管理菜单沿用缺省展示。安装身份、revision、启用状态
  和 actions 继续强类型解码，管理指标不授予执行能力。当前 Node producer 已归一这些指标；
  原生可选投影不改变运行器保存的定义或来源退休／租约边界。
- `NovelLibrary` 使用 `novel.library.v1` 原子文档，保存本地 UUID、书架／历史、
  锚点、排版和净化规则。decode 后先校验语义；写入使用 CAS，失败不发布内存快照、
  不覆盖旧字节。文档层不设业务字节配额，不限制书籍、来源或搜索结果数量。
  原始源定义仅在运行器，原生编辑器临时取回定义，不维护第二安装仓库。
  已有书籍按本地 ID 优先、来源 locator 次之确认成员，加入／移出书架直接本地持久化，
  不改书籍元数据、identity 或锚点；只有新书的身份归并调用 Node。
- `NovelBook`／`NovelChapter` 保留运行器业务字段及 variable 的 JSON 类型，
  从 info／toc 往返 content／payAction；这些字段不是安装定义。
  `NovelBlock` 仅按 type 读取实际文字或图片材料；未用 src／text 等成员不阻断正文，
  真实缺失材料或未知块类型仍拒绝整章，不发布部分正文或重排目录序号。
- `NovelRuleProcessor` 借用现有 `NovelRuntime`，通过 source 的 `rules.process`
  一次发送完整规则及转换快照。Node 负责启用筛选、order 稳定排序、逐条预算
  （缺省 3000ms）和末尾简繁；规则执行依赖运行器可用。原生文档仍独立保存
  书架、历史、排版与净化配置，Node 不另建配置仓库。
  导入预览和普通保存不调用规则检查 RPC，关闭规则处理器不阻止本地配置编辑；
  正则编译等错误只在实际 transform 使用时报告，已保存配置不因阅读刷新失败回滚。
  普通配置与执行的边界遵循[设置存储契约](../common/settings-storage.md)，不为小说再建运行器配置副本。
  首次普通载入处理失败可读原文，重算失败保留上次完整正文与锚点；
  目录刷新、换源和支付刷新须处理完整成功，不提交中间结果。
  每个保留阅读会话记录成功应用的 rules／conversion；
  设置变更不打断首载／换源，处理完成及旧进度保存排空后均核对最新输入再提交。
  隐藏／后台取消不清除未完成需求，返回可见／前台时复用原文补算；
  首载尚无正文时保留首载或已明确选章的意图，返回后以新代次重新准备，隔离旧代迟到结果。
  待选歧义目录及已明确选章的恢复均绑定产生目录的书源快照／revision；
  当前源或候选换源更新／停用后，待选失效并提示重新载入，不把旧目录交给新版来源，
  不替换当前可读状态；旧章节选择页的回调不再发起请求。
  待选歧义章节、未知作者确认及购章不由 resume 自动确认，关闭清除未完成意图。
  取消购章确认返回可见阅读页也补算，不依赖 page sheet 触发隐藏回调。
  成功后的相同输入及单纯排版变化不重复处理或下载。
  处理器关闭取消并等待自身接受的 RPC，不关闭共享运行器。远端错误保留明确分类，
  取消及关闭拒绝迟到结果。共同三组 fixtures 通过公开 RPC 消费，不进入产品资源。
- `RuntimeHTTPClient` 普通调用默认 30s；仅 `source/rules.process` 按需复用专用
  HTTP session，request／resource timeout 均以 URLSession 的 0 语义关闭。
  Node 的共享串行队列可先执行其他合法批次，客户端不计算预算或增加批次总时限；
  逐条执行预算仍由 Node 控制。调用方取消停止实际 task，客户端关闭立即撤权、
  取消并等待专用 session 的真实 invalidation；不延长其他 source 方法。
- 阅读器提供覆盖／平移／无动画分页／连续滚动。页号是派生状态，文字 UTF-16／组合
  字形与图片相对锚点才是重排事实；字体、字重、边距、Safe Area 和高清图片晚到
  不代表用户移动。正文变化重新解析锚点，必要时按章内比例回退。
  目录刷新、冷恢复与换源共用唯一 URL／规范化章名匹配，歧义显式选章，不夹取旧序号。
  同名未知作者候选可选，但需详情核验与用户确认；双已知不同作者拒绝切换。
  刷新候选的 book／variable、目录、正文、章节序号及锚点在完整准备且代次有效后同步提交；
  下载／处理失败或取消保留原显示及可保存进度，目录缩短或重排不提前替换当前章节。
- 只有实际展示正文／锚点变化才接受进度写入，静止重绘不重复落盘。隐藏／后台暂停
  自动阅读并保存；后台保存借用系统 background task。关闭排空接受的保存与图片任务。
  首载、补作者及候选换源／刷新在准备阶段取得与 book 准确对应的 Node identity，
  随成功正文一起接受；换源准备不提前保存新来源，失败／取消保留旧来源与进度。
  新正文实际展示后，保存 owner 捕获已接受的 book／identity／锚点，一次本地原子 record，
  display／flush／close 不再调用身份 RPC，运行器不可用仍可保存。保留本地 ID、
  书架、置顶与历史关系；未知作者补全后，第三源同已知作者仍匹配同一本地书。
  菜单打开时只在自动阅读状态实际变化时暂停，不在静止 render 中反复发布 false。
  阅读时隐藏 dock 和音乐控件，不停止音乐；退出恢复。
- App 正常 shutdown 仍等待同一 `NovelFeature.close()`；deinit 只取安装时捕获该实例的
  `@MainActor @Sendable` 关闭动作并异步等待，不跨 actor 读取 `@Published novel`。
  保留 `$novel` 消费者与错误／排空路径，不依赖 isolated deinit 或假定析构线程。
- `NovelRuntime` 借用 App 唯一 HTTP client。正文图片只走受信 opaque reference，
  图片传输不设总字节配额；区别于公共卡片的缩略图，ImageIO 解码原尺寸，离开视口取消并释放。
  HTTP 成功获取收据不因晚取消被丢弃；消费者与 owner close 共用实际释放收据，
  释放失败可观测，关闭等待真实完成。
- 每次受控 Web 使用全新 nonpersistent `WKWebsiteDataStore`，以 owner 的 cookies
  精确播种，不拆 Cookie header 或扩大 Path／Domain。基线与最终 WK store 比较，
  按 Cookie origin 回传新增／修改／删除；取消、源退休不回传完成结果。
  URL 正则通过同一处理器的固定 matchUrl RPC 执行；页面关闭只取消并排空自己的请求，
  不关闭共享处理器。等待远端匹配期间取消或退休同样禁止 Cookie 回写。
  Foundation 把 SameSite=None 与未声明都投影为 nil：仅在 name／domain／hostOnly／path
  全部相同、实际策略不可见时，从 seed 延续已知 None 到基线，再从基线延续到最终快照；
  实际 Lax／Strict 优先，其余 nil 保持未声明。快照只枚举实际 store，不复活删除项。
  公开 API 无法无损区分网页新建 None／未声明、既有 None 撤除、Lax／Strict→None
  或同键删后重建；此平台边界已接受，不使用私有 API 或历史响应头猜测。
  JavaScript 保留 null／false／number，参数保持原槽位；仅 request／browser 包装页面
  响应，webviewajax 保留脚本原值；Legado browser 返回页面响应，
  sourceRegex 返回资源 URL，override 命中先取消导航。未核实 QYSG 扩展不猜签名或
  套用 Legado StrResponse。无 URL 的验证码可直接输入。
- 购章先显示书名、来源和章节，只有原生“确认购买”按钮调用 payAction；取消无购买
  调用，成功刷新目录并按原章节 URL 定位。新目录元信息／已购标记优先，目录缺失的
  content／pay 开放字段保留，支付上下文的最新 variable 带入下一次 content；
  成功支付回执独立保存在会话内待刷新上下文中，后续失败／取消不回滚支付，也不发布
  候选显示。目录／正文新返回的字段及正文 variable 即使净化失败仍保留于该上下文；
  显式刷新、同章跳转或再次确认仅重试目录／正文，不重发已成功的 payAction。
  已转到另一章时，目录刷新仍匹配当前锚点；成功提交支付章正文后清除待刷新上下文，
  源 revision 失效或会话关闭同样清除，resume 不自动重发购买。
  不使用真实账号或真实支付验证。

日志只记录脱敏状态和错误，不输出令牌、Cookie 值、正文或私密查询。

## 文件与验收映射

以下列出 iOS 功能文件及对应验收，路径均相对仓库根目录。
共享运行器、纯规则程序和 Node 原生引擎由对应运行器模块拥有。

| 文件 | 验收与职责 |
| --- | --- |
| `native/MeowKit/Sources/MeowNovelFeature/NovelModels.swift` | A2/A3/A7/A8：本地身份、完整业务字段、锚点、排版、规则 |
| `native/MeowKit/Sources/MeowNovelFeature/Library/NovelLibrary.swift` | A3/A5/A7/A10：原子文档、书架／历史独立、语义校验 |
| `native/MeowKit/Sources/MeowNovelFeature/NovelFeature.swift` | A1–A10：装配、管理操作、错误及关闭 |
| `native/MeowKit/Sources/MeowNovelFeature/Browse/NovelSearchSession.swift` | A2/A6/A10：多源分页、归并、换源候选、代次 |
| `native/MeowKit/Sources/MeowNovelFeature/Runtime/NovelRuntime.swift` | A4/A8/A10：library RPC、原字段 payload、图片收据／释放排空 |
| `native/MeowKit/Sources/MeowNovelFeature/Runtime/NovelRuleProcessor.swift` | A2/A5/A6/D7：公开纯规则 RPC 消费、取消与排空 |
| `native/MeowKit/Sources/MeowNovelFeature/Runtime/NovelWebBroker.swift` | A9/A10：活动轮询、取消／退休 fence |
| `native/MeowKit/Sources/MeowNovelFeature/Reading/NovelReadingSession.swift` | A3/A5/A6/A9/A10：正文、目录、换源、购章、保存 |
| `native/MeowKit/Sources/MeowNovelFeature/Reading/NovelAnchorResolver.swift` | A5/A6：内容改变后锚点核验与比例回退 |
| `native/MeowKit/Sources/MeowNovelFeature/Reading/NovelReaderLayout.swift` | A6/A7：CoreText 四模式布局、字重、文字／图片锚点 |
| `native/MeowKit/Sources/MeowUI/Catalog/PosterCatalogCell.swift`、`PosterHomeViewController.swift`、`PosterCategoryViewController.swift` | A1/A10：影视与小说共享的海报卡片、横向栏目首页与网格筛选详情页 |
| `native/MeowKit/Sources/MeowNovelFeature/Browse/NovelDiscoverySession.swift` | A1/A2/A10：首页分类栏目映射、按需准入与并发限制、`NovelCategoryModel` 筛选分页、代次与取消 |
| `native/MeowKit/Sources/MeowNovelFeature/Artwork/NovelImageStore.swift` | A1/A2/A10：受信封面与正文图片缓存、并发合并、后台解码与释放排空 |
| `native/MeowKit/Sources/MeowNovelFeature/Browse/NovelBrowsePages.swift` | A1/A2/A10：`NovelHomeViewController`、`NovelCategoryViewController`、统一搜索与错误浮层 |
| `native/MeowKit/Sources/MeowNovelFeature/Library/UI/NovelPersonalLibraryViewController.swift` | A1/A3/A10：「我的 → 小说」两栏收藏与历史记录页、二级网格与清空历史确认 |
| `native/MeowKit/Sources/MeowNovelFeature/Browse/NovelBookCell.swift` | A1/A2/A10：搜索书籍列表单元、动态字体与受信封面消费者 |
| `native/MeowKit/Sources/MeowNovelFeature/Detail/NovelDetailViewController.swift` | A1/A3/A6：详情与真实目录选择／刷新／顺倒序 |
| `native/MeowKit/Sources/MeowNovelFeature/Reader/NovelReaderViewController.swift` | A1/A6/A7/A9/A10：原生阅读、高清图、换源与购章确认组件 |
| `native/MeowKit/Sources/MeowNovelFeature/Settings/NovelSettingsPages.swift` | A1/A7：小说设置、完整排版与背景 |
| `native/MeowKit/Sources/MeowNovelFeature/Settings/NovelSourcesViewController.swift` | A4/A9：导入导出／编辑／管理与登录 |
| `native/MeowKit/Sources/MeowNovelFeature/Settings/NovelRulesViewController.swift` | A5：净化 CRUD、分组、顺序与导入导出 |
| `native/MeowKit/Sources/MeowNovelFeature/Web/NovelWebViewController.swift` | A9/A10：受控 WK、Web ABI、脚本类型与取消 |
| `native/MeowKit/Sources/MeowNovelFeature/Web/NovelWebCookies.swift` | A9：精确种子、baseline diff、跨域与删除 |
| `native/MeowKit/Sources/MeowMusicFeature/Search/UI/MusicSearchViewController.swift`、`MusicSearchResultsViewController.swift` | A1/A10：多个附加类别的独立消费者与取消 |
| `native/MeowKit/Sources/MeowRuntime/RuntimeHTTPClient.swift` | A4/A7/A8/A10：版本、受信图片通道、成功获取收据 |
| `native/MeowKit/Sources/MeowRuntime/RuntimeModule.swift` | A4/A10：允许 source 的 CORRUPT_STORAGE |
| `native/MeowKit/Package.swift` | A1/A5/D7：小说 product 与原生依赖 |
| `apps/ios/Composition/MeowfilmResources.swift`、`MeowfilmAssembly.swift` | A1/A3/A4/A10：唯一宿主、原子库、后台保存、关闭 |
| `apps/ios/Composition/MeowfilmSearchComposition.swift` | A1：三目标／五分类装配 |
| `apps/ios/Navigation/MeowfilmCoordinator.swift`、`MeowfilmPrimaryPage.swift`、`MeowfilmRootContainerViewController.swift`、`MeowfilmSettingsViewController.swift` | A1/A9/A10：入口、路由、Web 呈现、阅读 dock 生命周期 |
| `apps/ios/Presentation/MeowfilmDockView.swift`、`MeowfilmPrimaryShellViewController.swift`、`MeowfilmSidebarView.swift` | A1：现有原生容器、小说底栏／发现书源选择 |
| `apps/ios/runtime/entries/aggregate.mjs` | A4：正式 source 装配传入 dataRoot |
| `scripts/generate-ios-project.py`、`scripts/check-source-scope.py` | A1/A5/A9：product 装配、明确源码边界、App 全局 ATS HTTP 配置 |
| `docs/ios/music.md`、`novel.md` | A1/A4–A10：版本、端侧行为与验证边界 |

生成的 `apps/ios/Meowfilm.xcodeproj/project.pbxproj`、共享 schemes、
`Info-Meowfilm.plist` 和 `Meowfilm-Simulator.entitlements` 沿用生成器和既有位置，
由生成器维护，不手改生成文件。

## HTTP 路由与验证边界

HTTP 路由要求 `library.releaseImage` 以原大小写进入受信 source HTTP 路由。首段为小写字母，后续点分段允许 lowerCamelCase，并支持影视多段方法及全串匹配；路径、编码分隔符、query／fragment、空白／控制字符等非法 operation 在发请求前返回 `invalidRequest`，语法合法但未发布的方法由服务端返回 `METHOD_NOT_FOUND`，source 响应契约精确校验。App 沿用全局 `NSAllowsArbitraryLoads`；运行器 HTTP 无需认证头，源站 Cookie 仍由对应书源 owner 管理。

SameSite=None 采用公开 API 投影边界，保持精确延续、未变零写回、删除、逐 origin 隔离与取消／退休语义，不扩展其它 Cookie 属性的例外。

小说发现、搜索、书架、规则 RPC、阅读器锚点／排版、Web 呈现及导航生命周期统一通过真实 App `--debug` 模式验证（详见[Debug 模式](debug-mode.md)）；原生边界、构建与签名校验使用 `bash scripts/verify-native.sh`。构建与本地运行验证不代表全部第三方真实书源、账号、支付或真机验收。

## 原生书源与替换管理

管理与导入行为、当前书源协议字段和净化表单遵循[共同管理契约](../common/novel/reading.md#书源与替换管理)。

`NovelSourcesViewController`、`NovelRulesViewController` 使用 MeowUI 的 `ManagementListView`、`ManagementFormView`、`ManagementDialog` 与 `ManagementImportView`。管理／编辑页使用公共 App 页头，页面背景和表面遵循[设置语义配色](architecture.md#设置语义配色)。来源管理保留原有搜索／圆形工具栏、24 点外距、22 点整表圆角、紧凑行和底部批量栏，仅做主题与必要的动态字体／窄屏适配；同一列表供视频源、音源复用。源字段横向标签与垂直表单分离，整体表单字段无独立外框并使用分隔线；替换规则编辑保留原字号／间距并补齐同一分隔，不靠切换 boxed 模式改变字体。键盘不覆盖输入。地址输入使用标题在上、下划线输入在下的公共字段结构；地址与导入确认以居中浮窗覆盖当前页面，保留后方内容而不另建空白页面；取消等待当前操作实际排空。排版数字／颜色输入也复用该浮窗，无效值留在窗内提示；二维码图片使用系统 CoreImage 解码。
