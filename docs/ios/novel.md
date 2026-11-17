# iOS 小说

小说使用现有 UIKit 页面、导航、搜索组件和配色，不嵌 Reader 网页外壳。
共同业务语义与排除项见[阅读契约](../common/novel/reading.md)，RPC、Cookie 与 Web
helper ABI 见[书源仓库](../runtime/source-library.md)；本页只说明端侧装配与验证入口。

## 使用与 owner

- 底栏“小说”直接显示发现首页，分类合并在首页。保留 App 原有“菜单＋搜索小说”头部，
  头部下方为频道及当前频道的单选子项；频道按书源顺序排列，首次选择第一个可用入口。
  `FilterTagsView` 在溢出时提供展开箭头，展开覆盖列表而不增加列表顶部占位；选择、外点和
  离开页面均收起，被遮挡内容暂时退出无障碍遍历。不推测题材／字数等组合请求参数。
- 小说侧边栏显示“首页”及全部已添加书源，观察 `NovelFeature.sources` 的既有顺序和名称，
  可发现的源直接选择同一个 `NovelDiscoverySession`，停用或没有发现能力的源禁用。
  不另建来源仓库。旧首页的书架／历史切换和独立分类入口已移除，不迁移或增加兼容入口；
  原有本地书架／历史数据及独立业务保持不变，入口暂时孤立，不在本轮补接。
- `NovelDiscoverySession` 拥有当前来源、频道、入口、页码与结果；无 URL 的标题形成分组，
  同名分组保留独立身份，仅 URL 项保留入口且不显示原始请求模板。切换目标清除旧结果，
  同目标刷新只在频道及第一页全部成功后替换；分页去重且成功后才推进，失败可重试同一页。
  隐藏取消实际请求，返回保留已完成选择并恢复未完成工作；revision 变化失效旧代，关闭等待
  接受的请求排空。来源失效不自动跳到另一个源，错误通过统一重试浮层呈现；
  关闭错误后仍可重新打开浮层重试失败页。`NovelFeature` 只发布最新一次来源清单刷新，
  防止启动与页面并发读取时迟到清单回退已接受的 revision。
- 首页与搜索结果共用 `NovelBookCell`：74×104 封面、书名、作者、单行简介、原始字数、
  来源名／实际候选来源数量，章节可覆盖在封面底部。字段缺失不编造、不额外逐本抓详情，
  普通文字 Regular，使用 Dynamic Type 和自适应行高，封面沿受信图片获取／取消／释放路径。
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
  不覆盖旧字节。沿用文档层 8 MiB 资源边界，不限制书籍、来源或搜索结果数量。
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
  不套公共卡片缩略图的 4 MiB 限制；ImageIO 解码原尺寸，离开视口取消并释放。
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
| `native/MeowKit/Sources/MeowNovelFeature/NovelSearchSession.swift` | A2/A6/A10：多源分页、归并、换源候选、代次 |
| `native/MeowKit/Sources/MeowNovelFeature/Runtime/NovelRuntime.swift` | A4/A8/A10：library RPC、原字段 payload、图片收据／释放排空 |
| `native/MeowKit/Sources/MeowNovelFeature/Runtime/NovelRuleProcessor.swift` | A2/A5/A6/D7：公开纯规则 RPC 消费、取消与排空 |
| `native/MeowKit/Sources/MeowNovelFeature/Runtime/NovelWebBroker.swift` | A9/A10：活动轮询、取消／退休 fence |
| `native/MeowKit/Sources/MeowNovelFeature/Reading/NovelReadingSession.swift` | A3/A5/A6/A9/A10：正文、目录、换源、购章、保存 |
| `native/MeowKit/Sources/MeowNovelFeature/Reading/NovelAnchorResolver.swift` | A5/A6：内容改变后锚点核验与比例回退 |
| `native/MeowKit/Sources/MeowNovelFeature/Reading/NovelReaderLayout.swift` | A6/A7：CoreText 四模式布局、字重、文字／图片锚点 |
| `native/MeowKit/Sources/MeowNovelFeature/NovelDiscoverySession.swift` | A1/A2/A10：发现频道、来源与入口选择、刷新／分页、代次和取消 |
| `native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelBrowsePages.swift` | A1/A2/A10：合并发现首页、搜索、标签展开与错误浮层 |
| `native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelBookCell.swift` | A1/A2/A10：共用书籍信息、动态字体与受信封面消费者 |
| `native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelDetailViewController.swift` | A1/A3/A6：详情与真实目录选择／刷新／顺倒序 |
| `native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelReaderViewController.swift` | A1/A6/A7/A9/A10：原生阅读、高清图、换源与购章确认组件 |
| `native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelSettingsPages.swift` | A1/A7：小说设置、完整排版与背景 |
| `native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelSourcesViewController.swift` | A4/A9：导入导出／编辑／管理与登录 |
| `native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelRulesViewController.swift` | A5：净化 CRUD、分组、顺序与导入导出 |
| `native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelWebViewController.swift` | A9/A10：受控 WK、Web ABI、脚本类型与取消 |
| `native/MeowKit/Sources/MeowNovelFeature/UIKit/NovelWebCookies.swift` | A9：精确种子、baseline diff、跨域与删除 |
| `native/MeowKit/Sources/MeowMusicFeature/Search/UIKit/MusicSearchPages.swift` | A1/A10：多个附加类别的独立消费者与取消 |
| `native/MeowKit/Sources/MeowRuntime/RuntimeHTTPClient.swift` | A4/A7/A8/A10：版本、受信图片通道、成功获取收据 |
| `native/MeowKit/Sources/MeowRuntime/RuntimeModule.swift` | A4/A10：允许 source 的 CORRUPT_STORAGE |
| `native/MeowKit/Package.swift` | A1/A5/D7：小说 product、原生依赖与测试 target |
| `apps/ios/Composition/MeowfilmResources.swift`、`MeowfilmAssembly.swift` | A1/A3/A4/A10：唯一宿主、原子库、后台保存、关闭 |
| `apps/ios/Composition/MeowfilmSearchComposition.swift` | A1：三目标／五分类装配 |
| `apps/ios/Navigation/MeowfilmCoordinator.swift`、`MeowfilmPrimaryPage.swift`、`MeowfilmRootContainerViewController.swift`、`MeowfilmSettingsViewController.swift` | A1/A9/A10：入口、路由、Web 呈现、阅读 dock 生命周期 |
| `apps/ios/Presentation/MeowfilmDockView.swift`、`MeowfilmPrimaryShellViewController.swift`、`MeowfilmSidebarView.swift` | A1：现有原生容器、小说底栏／发现书源选择 |
| `apps/ios/runtime/entries/aggregate.mjs` | A4：正式 source 装配传入 dataRoot |
| `native/MeowKit/Tests/MeowNovelFeatureTests/NovelDiscoveryTests.swift` | R28/A1/A2/A10：分组、首选、切源、分页、刷新、取消、revision、迟到清单与关闭 |
| `native/MeowKit/Tests/MeowNovelFeatureTests/NovelBookPresentationTests.swift` | R28/A1/A2：原始展示字段往返、缺失或非文本字段不造值 |
| `native/MeowKit/Tests/MeowNovelFeatureTests/NovelSourceClientTests.swift` | R28/A2/A3/A8：来源投影、搜索／发现健康兄弟、空页与正文材料 |
| `native/MeowKit/Tests/MeowNovelFeatureTests/NovelLibraryTests.swift`、`NovelRuleProcessorTests.swift`、`NovelSessionTests.swift`、`NovelAnchorTests.swift`、`NovelImageLifetimeTests.swift`、`NovelWebBrokerTests.swift` | A2–A10：行为、同 fixtures、取消／保存／释放定向回归 |
| `native/MeowKit/Tests/MeowNovelFeatureTests/NovelRuleConsumerTests.swift`、`NovelRulesRPCFixture.swift` | A5/A10/D7：完整快照、RPC 错误、真实 HTTP 消费与借用 owner 排空 |
| `native/MeowKit/Tests/MeowRuntimeTests/RuntimeHTTPClientTests.swift` | A4/A7/A8/A10：HTTP 版本、二进制图片与取消 |
| `apps/ios/RuntimeTests/Novel/NovelUIKitTests.swift`、`NovelWebUIKitTests.swift`、`NovelTestPageTheme.swift` | A1/A2/A6/A7/A9/A10：真实 App 进程内的 UIKit／WK 夹具与截图 |
| `apps/ios/UITests/Music/MusicUIKitRegressionTests.swift` | A1/A10：附加类别独立消费者与三目标回归 |
| `apps/ios/UITests/Acceptance/MeowfilmNavigationAcceptanceTests.swift` | A1：四底栏与小说原生入口 |
| `apps/ios/RuntimeTests/MeowfilmNovelHostTests.swift` | A3/A4/A8/A9/A10：真实 App source library、私有状态、两进程恢复 |
| `apps/ios/RuntimeTests/MeowfilmNovelNavigationHostTests.swift` | R28/A1/A6/A9/A10：真实 coordinator 发现→详情→阅读／dock 返回／设置、音乐状态 |
| `apps/ios/RuntimeTests/MeowfilmSidebarTests.swift` | R28/A1/A10：来源状态更新、选择／dismiss、关闭订阅及侧栏尺寸矩阵 |
| `apps/ios/RuntimeTests/MeowfilmFilmSearchHostTests.swift`、`MeowfilmRuntimeHostTests.swift` | A1：搜索目标与设置分类消费者同步 |
| `scripts/generate-ios-project.py`、`scripts/check-source-scope.py` | A1/A5/A9：product 装配、明确源码边界、App 全局 ATS HTTP 配置 |
| `scripts/tests/test_source_scope.py`、`test_ios_product_configuration.py` | A1/A5/A9：依赖边界与生成策略回归 |
| `scripts/ios-node-preflight/probe.mjs` | A4：source@0.3.0／source.runtime.v2 预期 |
| `docs/ios/music.md`、`runtime-preflight.md`、`novel.md` | A1/A4–A10：版本、端侧行为与验证边界 |

生成的 `apps/ios/Meowfilm.xcodeproj/project.pbxproj`、共享 schemes、
`Info-Meowfilm.plist` 和 `Meowfilm-Simulator.entitlements` 沿用生成器和既有位置，
由生成器维护，不手改生成文件。

## 精确验证选择

以下保留现行回归源码的精确选择，不代表源码存在即已通过；R28 的实际结果与待补项
见文末独立小节，不与 D7 旧阶段混计。Apple 平台验证由主会话集中安排，
模拟器全机并发1，按共享登记、原子锁串行排队；
不运行全量、不清账号／歌曲库／小说数据、不调用发行 `build-ios.sh`、不安装真机。
先按正常流程完成 `build-for-testing` 和 `scripts/check-simulator-signing.py`；
所选测试完整执行、exit 0、零失败／零 skip 才计为通过。

HTTP 验收要求 `library.releaseImage` 以原大小写进入受信 source HTTP 路由。
首段为小写字母，后续点分段允许 lowerCamelCase，并支持影视多段方法及全串匹配；路径、
编码分隔符、query／fragment、空白／控制字符等非法 operation 在发请求前返回
`invalidRequest`，语法合法但未发布的方法由服务端返回 `METHOD_NOT_FOUND`，
source 响应契约精确校验。App 沿用全局 `NSAllowsArbitraryLoads`；
运行器 HTTP 无需认证头，源站 Cookie 仍由对应书源 owner 管理。

### Swift 原生

入口为 `scripts/test-native.sh test --filter '<pattern>'`，pattern 为：

```text
MeowNovelFeatureTests|MeowRuntimeTests.RuntimeHTTPClientTests
```

`MeowNovelFeatureTests` 的既有阅读／库／规则回归包括：`NovelLibraryTests`、
`NovelRuleProcessorTests`、`NovelSessionTests`、`NovelAnchorTests`、
`NovelImageLifetimeTests`、`NovelWebBrokerTests`、`NovelRuleConsumerTests`。其中明确覆盖稳定锚点不再写入、
原字段到 payAction、未知作者换源、缩短／歧义目录、源 revision 后同词刷新、
净化失败保留原文、取消后释放及 close 与迟到消费者共用收据。
macOS 的原生 XCTest 规则夹具启动隔离的真实 source HTTP owner，依赖正常 Node 工作区
与对应宿主原生 addon 已准备；UIKit／Web 测试借用真实 App 的既有运行器。
原两项原生引擎 budget 用例随已移除引擎删除，实际正则中断、失败重建、排队开始计时
及 close 排空覆盖迁往 `packages/script-host/test/native-json.test.js`、
`tests/source-rules.test.mjs` 的运行器真实引擎测试；端侧保留同 fixtures、超时原文保护、
会话代次／状态及图片／Cookie 断言。旧资源 allowlist 正向用例随 Swift 资源包装移除，
现行本地 package 校验和共享层业务依赖禁止断言保留。

R28 首页／搜索受影响范围的定向 pattern 为：

```text
MeowNovelFeatureTests.(NovelDiscoveryTests|NovelBookPresentationTests|NovelSourceClientTests|NovelImageLifetimeTests)
```

| 类选择 | 项数 | 对应覆盖 |
| --- | --- | --- |
| `NovelDiscoveryTests` | 17 | 分组、选择、分页／刷新提交、实际取消、revision、来源清单迟到隔离与排空 |
| `NovelBookPresentationTests` | 2 | 展示字段持久化／部分详情合并、缺失或非文本不编造 |
| `NovelSourceClientTests` | 7 | 来源投影、搜索／发现条目隔离、空页／坏页及正文材料 |
| `NovelImageLifetimeTests` | 4 | 获取取消、读取／释放失败、owner 关闭与迟到消费者共用释放收据 |

其中清单迟到回退的精确方法为
`NovelDiscoveryTests/testLateSourceListCannotRollBackAcceptedRevisionOrRestartDiscovery`。
这 30 项不代替下列既有阅读、库、规则或 HTTP 回归。

timeout 的 0 语义另以隔离 Mac loopback 探针核对：macOS 15.7.2／Swift 6.2.1 下，
正值 request／resource 对照均以 `NSURLErrorTimedOut` 结束，双 0 请求等待约 68s
后成功，跨过默认 60s，并实际等待 session invalidation。原始输出在
`tmp/test-results/novel/ios-urlsession-zero-probe.log`；这是该系统上的实测依据，
不是 iOS 真机／模拟器或本轮仓库回归已通过的声明。

购章上下文连续性由
`NovelSessionTests/testPurchaseRefreshKeepsContentAndPayContextWithFreshCatalogFlagsAndLatestVariable`
经真实 ReadingSession 的 content→payAction→toc→content 路径验证：夹具在支付后倒序
目录，支付／正文各有目录不返回的嵌套字段，新目录覆盖旧元信息与已购标记，下一次
content 的 chapter／book／顶层 variable 均保留支付后的最新值；同时覆盖 bool 支付
返回与 UIKit 原生取消按钮的零购买调用。

保留阅读会话与净化／简繁失效恢复的定向选择：

```text
NovelSessionTests/testRetainedReadersApplyGlobalRulesAndConversionOnReturnWithoutDownloadingAgain
NovelSessionTests/testSuspendedConversionKeepsOldBodyAndAnchorAndResumesOnlyTheLatestInput
NovelSessionTests/testFailedReprocessingRetriesOnResumeAndSuccessfulUnchangedInputDoesNotRunAgain
NovelSessionTests/testInitialLoadDiscardsAnObsoleteFailureAndCommitsOnlyTheLastSettings
NovelSessionTests/testSourceSwitchKeepsOldSessionUntilTheLatestRulesAndConversionComplete
NovelSessionTests/testLatestSourceSwitchProcessingFailureKeepsPersistedSourceAndDisplayedProgress
NovelSessionTests/testInitialProcessingFailureRemainsRetryableWithoutAnotherContentRequest
NovelSessionTests/testCancelledSourceSwitchReprocessesOnlyTheRetainedSourceOnResume
```

以上新增覆盖以 continuation 门控真实规则调用的进度，不依赖定时碰撞；校验多保留
会话、取消与迟到隔离、失败后重试、相同输入不重复调用、首载／换源只提交最新输入，
并直接核对换源失败／取消的本地 snapshot，以及新正文 display 前后来源与锚点的保存边界。
实际执行结果见文末验证状态，不以静态存在作为通过证据。

显示保存后补作者与第三源匹配的定向选择：

```text
NovelLibraryTests/testRecordedAuthorEnrichmentPersistsIdentityAndThirdSourceRetainsTheSameLocalID
```

该用例使用真实共享 identity，重开本地库后从第三源 retain，核对仍是同一本地 ID，
且书架、置顶、阅读锚点与历史时间未被元数据匹配改写；Session 换源用例同时核对
实际 display 保存 owner 将未知作者的新匹配键与来源、锚点一起提交。

四项终审返修的精确选择（D7／main 冲突适配旧阶段当时未执行，保留回归源码）：

```text
NovelSessionTests/testDisplayedProgressFlushAndCloseRemainLocalWhenTheRuntimeIsUnavailable
NovelSessionTests/testExistingShelfMembershipByIDAndLocatorWorksWithoutTheRuntime
NovelSessionTests/testPreparedSourceIdentityPersistsLocallyAndStillMatchesAThirdSource
NovelSessionTests/testCatalogBodyFailureAndCancellationKeepTheDisplayedSnapshotUntilSuccessfulRetry
NovelSessionTests/testCatalogProcessingFailureCannotPublishACandidateCatalogOrBody
NovelSessionTests/testAcceptedPurchaseBodyFailureKeepsOldDisplayAndExplicitRefreshNeverRepays
NovelSessionTests/testAcceptedPurchaseBodyCancellationCannotPublishLateStateOrRepeatPayment
NovelSessionTests/testAcceptedPurchaseProcessingFailureRetainsTheLatestContentContextForRetry
NovelSessionTests/testCatalogRefreshAfterAnAcceptedPurchaseKeepsTheCurrentChapterAndPaidContext
NovelSessionTests/testSuspendedInitialDownloadRestartsWithANewGenerationAndClosePreventsRevival
NovelSessionTests/testSuspendedInitialTransformPublishesOnlyTheResumedLatestInput
NovelSessionTests/testResumeDoesNotConfirmAmbiguousInitialChapterOrAnUnknownAuthor
```

覆盖实际 Feature／ReadingSession 的本地保存与 membership、准确 identity 跨来源恢复、
候选目录与支付的失败／取消、开放字段与最新 variable 连续性、初载下载／处理恢复及
明确选章恢复；保留已有写入失败、排空和未知作者确认断言，不降低原覆盖。

待选目录跨 revision 返修的精确选择（D7 旧阶段当时未执行，保留回归源码）：

```text
NovelSessionTests/testAmbiguousInitialSelectionIsInvalidatedWhenItsSourceChangesOrIsDisabled
NovelSessionTests/testAmbiguousCatalogSelectionIsInvalidatedWithoutReplacingDisplayedProgress
NovelSessionTests/testAmbiguousCandidateSelectionIsInvalidatedWhenOnlyTheCandidateSourceChangesOrIsDisabled
```

分别覆盖初载、目录刷新及候选换源尚未选择时的 revision 更新／停用；
候选源变化时当前源保持不变。断言失效选择及旧页回调不请求／提交旧目录，
原正文、书籍、目录、锚点与持久进度保留；已选章后换代的原回归继续保留。

HTTP 路由与图片生命周期的完整类选择：

```text
MeowRuntimeTests.RuntimeHTTPClientTests|MeowNovelFeatureTests.NovelImageLifetimeTests
```

其中 HTTP 路由的精确方法为：

```text
RuntimeHTTPClientTests/testPublishedLowerCamelCaseOperationUsesExactSourceHTTPRoute
RuntimeHTTPClientTests/testUnsafeOperationNamesAreRejectedBeforeSendingHTTP
RuntimeHTTPClientTests/testSourceRulesHaveNoTransportDeadlineWithoutExtendingOtherCalls
RuntimeHTTPClientTests/testCancellingRulesHTTPStopsTheRealTaskAndKeepsTheSharedClientAvailable
RuntimeHTTPClientTests/testOwnerCloseWaitsForRulesSessionInvalidationAndRevokesItImmediately
```

正例经实际 `RuntimeHTTPClient`／`URLSession` 与既有 URLProtocol 夹具核对原大小写
路由、无认证头、请求体和 source 响应；负例要求非法输入既返回 `invalidRequest`，
也不进入 HTTP 夹具。
rules 专用传输覆盖不计算批次时限、取消真实 task 后仍可复用、关闭立即撤权且
等待实际 invalidation；普通 source 调用仍保留原 30s。

iOS App-hosted 真实 socket 的 >60s 等待与关闭排空用例由主会话提供，精确选择为：

```text
MeowfilmRuntimeTests/MeowfilmNovelHostTests/testRulesHTTPWaitsBeyondSixtySecondsAndDrainsOnClose
```

该选择在 D7 旧阶段已由主会话纳入集中验证 helper，当时尚未执行；已获准只读核对独占文件
`apps/ios/RuntimeTests/MeowfilmNovelHostTests.swift` 的68秒真实 socket／close drain
源码及宿主接线，静态未见问题，未修改该文件。Mac 探针、源码检查及 URLProtocol
超时配置断言均不替代这项 iOS 实测。

### UIKit（target：MeowfilmAcceptanceUITests）

只选以下类／方法：

```text
MusicUIKitRegressionTests/testAdditionalSearchCategoryIsLazyAndTargetSubmissionAlwaysSelectsRequestedCategory
MusicUIKitRegressionTests/testNewMusicSubmissionDuringPendingAdditionalSwipeDoesNotStartSupersededDomain
MusicUIKitRegressionTests/testGlobalSearchAddsAllFiveCategoriesWhenMusicPlaylistSearchIsDisabled
MusicUIKitRegressionTests/testThreeTargetsKeepIndependentDomainConsumersAndCancelHiddenCategoryWithoutLosingSession
MeowfilmNavigationAcceptanceTests/testPrimaryPagesUseAppOwnedHeaders
```

### 小说 UIKit／WK（target：MeowfilmRuntimeTests）

`apps/ios/RuntimeTests/Novel/` 的下列用例在真实 App 进程中执行；按本次受影响范围显式选测：

```text
MeowfilmRuntimeTests/NovelUIKitTests/testDiscoveryEmptyAndLoadedListReturnsFromDetailsWithoutReloading
MeowfilmRuntimeTests/NovelUIKitTests/testDismissedDiscoveryFailureCanReopenRetryAndKeepTheFailedPage
MeowfilmRuntimeTests/NovelUIKitTests/testDiscoveryChannelAndCurrentKindsExpandOverBooksAndCloseOnSelectionOrOutside
MeowfilmRuntimeTests/NovelUIKitTests/testHomeAndSearchUseSharedMetadataCellsAcrossThemesWidthAndDynamicType
MeowfilmRuntimeTests/NovelUIKitTests/testSharedBookCellRejectsLateCoverAndReleasesEveryTrustedReference
MeowfilmRuntimeTests/NovelUIKitTests/testReaderFourModesNativeLayoutProgressPurchaseAndBackgroundPause
MeowfilmRuntimeTests/NovelUIKitTests/testVisibleReaderWithOpenMenuDoesNotRepublishAutomaticWhileIdle
MeowfilmRuntimeTests/NovelUIKitTests/testTextImageAnchorsSurviveFontSafeAreaAndImageArrivalWithoutBreakingGraphemes
MeowfilmRuntimeTests/NovelUIKitTests/testSelectedFontWeightChangesRealGlyphAttributesAndPagination
MeowfilmRuntimeTests/NovelUIKitTests/testActualReaderKeepsTextAnchorAcrossEveryModeFontAndSafeAreaReflow
MeowfilmRuntimeTests/NovelUIKitTests/testActualReaderKeepsImageAnchorWhenHighResolutionArrivesFontAndSafeAreaChange
MeowfilmRuntimeTests/NovelUIKitTests/testRetainedReaderAppliesChangedRulesAndConversionWhenItBecomesVisibleAgain
MeowfilmRuntimeTests/NovelUIKitTests/testCancelledPurchaseConfirmationResumesPendingDisplaySettingsWithoutPurchasing
MeowfilmRuntimeTests/NovelUIKitTests/testHiddenDiscoveryCancelsAndResumesOnlyTheCurrentRevision
MeowfilmRuntimeTests/NovelWebUIKitTests/testRealWebKitSeedsKeepPrivateHostOnlyDomainPrefixAndUnchangedCookiesDoNotWriteBack
MeowfilmRuntimeTests/NovelWebUIKitTests/testRealRedirectCookieChangesKeepBothOriginsDeletionAndHostPrefixScope
MeowfilmRuntimeTests/NovelWebUIKitTests/testRealWebKitKnownNoneDoesNotOverrideVisiblePoliciesOrLeakAcrossCookieScopes
MeowfilmRuntimeTests/NovelWebUIKitTests/testWebModesKeepScalarTypesOriginalArgumentPositionsAndResourceOverrideNavigation
MeowfilmRuntimeTests/NovelWebUIKitTests/testCancellationDuringURLRulesRPCWaitsForTheMatchAndNeverReturnsCookies
MeowfilmRuntimeTests/NovelWebUIKitTests/testVerificationWithoutURLAndExplicitCollectionCancellationCannotStallOrCompleteTwice
```

R28 选测其中发现空／已载入列表、失败页重试、频道／子项展开、首页／搜索共用信息行、
受信封面及隐藏恢复六项；其余阅读器／WK 用例不因 R28 结果计为重新通过。
失败页用例关闭错误框并离开／返回后，核对“重试”重新打开统一浮层、保留原书籍，
实际请求页码为 `[1, 2, 2]`，不跳过失败页。

夹具只使用实际 `foregroundActive` UIWindowScene、scene bounds 和提高一级的
windowLevel，结束后恢复此前 key window；不伪发 didBecomeActive。截图同时断言
scene 在前台和 `drawHierarchy` 返回成功。高清图片夹具固定 renderer scale=1，
expiry 使用秒对齐的当前时间＋30天并保留精确相等断言。
用户已接受上述 None 有损投影边界。原 None 精确断言通过带权威上下文的生产投影
核对，未变零写回与修改 __Host 值回传 SameSite=None 的断言保留；
新增实际 WK 回传验证 Lax／Strict 优先、未声明不误补、完整作用域隔离及删除不复活。

WK 用例使用合成本地 HTTP，验证真实 store 的 host-only／domain、`/private`、
HttpOnly／SameSite／expiry、未变不回写、删除、A→B、`__Host-` 与安装隔离。
同键 host-only／Domain 变更先回传删除再回传新值，避免 owner 删除替换后的 Cookie。
Web modes 与 verification 方法同时验证真实脚本标量、null 参数槽位、browser 页面
响应、资源／导航 URL、无 URL 验证码、重复完成与不结束 Promise 的取消；URL RPC
取消方法验证等待匹配收口且不返回 Cookie，不使用公网源。
静止菜单用例订阅公开的 session.$automatic，等待页面就绪后触发相同本地快照重绘，
要求观察窗口内无新 automatic 发布；稳定锚点另有不重复写盘断言。
Web modes 同时覆盖 webviewajax 的 false／null／number。

### 真实 App 宿主（target：MeowfilmRuntimeTests）

```text
MeowfilmNovelHostTests/testActualAppRulesRPCUsesCurrentSourceAndFilmMethodsAndKeepsNativeContentMetadata
MeowfilmNovelHostTests/testActualAppLibrarySearchDetailCatalogContentPrivateValuesAndRevisionRetirement
MeowfilmNovelHostTests/testActualAppWebBrokerScalarCookieIsolationAndRetiredPendingResponse
MeowfilmNovelNavigationHostTests/testActualNovelDiscoveryDetailReaderDockRestoreAndSettingsRoutes
MeowfilmFilmSearchHostTests/testActualCoordinatorEntryUsesMusicAndFilmPageDefaultsWithoutSubmitting
MeowfilmFilmSearchHostTests/testActualSearchTapRetainsLiveGroupTMDBAndLaterPageWithoutAnotherSearch
MeowfilmMusicStartupHostTests/testStartupFailureUsesAlertAndPreservesTheOpenSettingsRoute
```

7 项宿主选择覆盖真实 App 仓库、规则 RPC、私有状态、源换代和路由；Film 入口同时验证
novel 默认目标，启动失败验证三项设置分类并保留当前路由。上述宿主不替代 WK 组件验证。

R28 只选择上列发现→详情→阅读／dock／设置导航及以下三项侧栏回归，与前节六项
首页／列表用例合计 10 个唯一必测宿主用例，不代表其余宿主用例重新执行：

```text
MeowfilmRuntimeTests/MeowfilmSidebarTests/testNovelShowsAllAddedSourcesAndUpdatesAvailabilityWithoutNavigationOrRequests
MeowfilmRuntimeTests/MeowfilmSidebarTests/testNovelSourceButtonsSelectDismissObserveSelectionAndStopUpdatingWhenClosed
MeowfilmRuntimeTests/MeowfilmSidebarTests/testCompactLandscapeAndAccessibilityTypeKeepAllDomainsScrollableAndSettingsFixed
```

### 两个真实 App 进程

先正常 `MeowfilmAcceptance build-for-testing`，同一模拟器、同一数据容器，两次独立
`xcodebuild test-without-building`；主会话通过拷贝 xctestrun 注入测试环境：

1. `MEOWFILM_NOVEL_COLD_PHASE=prepare`，只选
   `MeowfilmRuntimeTests/MeowfilmNovelColdStartPrepareTests/testPrepareLibraryAndPrivateStateForNextAppProcess`。
2. 上一测试进程退出后，`MEOWFILM_NOVEL_COLD_PHASE=restore`，只选
   `MeowfilmRuntimeTests/MeowfilmNovelColdStartRestoreTests/testActualAppColdStartRestoresLibraryPrivateValuesMembershipAndAnchor`。

测试自有 Application Support marker `meowfilm-novel-cold-test-process.json` 保存
UUID＋PID；restore 断言两者均不同并清理 marker，日志仅 phase／PID，不输出私有值。
启停、发现开关、group／order、源私有计数正文 1→2、本地 UUID／书架／历史／锚点
均跨此进程边界验证。环境未设置会明确 skip，不算通过；无产品测试开关。

### 截图入口与结果位置

UIKit 用例把截图作为 `.keepAlways` XCTest attachments 保存。旧阶段执行器
`tmp/working/novel-ios-tests.py` 在 Mac 固定副本的 `tmp/test-results/novel/` 下
按 suite 写入 `ios-ui.xcresult`、`ios-host.xcresult` 或 `ios-navigation.xcresult`；
测试所属 target 以上述选择为准。两进程结果为同目录
`ios-cold-prepare.xcresult`／`ios-cold-restore.xcresult`。R28 结果及附件收拢到
`tmp/test-results/reader-page/r28/`（含 `ui-2-attachments/`、`ui-3-attachments/`、`headers-2-attachments/`）。截图入口如下：

| 方法 | attachment 名称 |
| --- | --- |
| `NovelUIKitTests/testDiscoveryEmptyAndLoadedListReturnsFromDetailsWithoutReloading` | `novel-home-empty`、`novel-home-book` |
| `NovelUIKitTests/testDiscoveryChannelAndCurrentKindsExpandOverBooksAndCloseOnSelectionOrOutside` | `novel-home-channels-expanded`、`novel-home-kinds-expanded`、`novel-home-current-category` |
| `NovelUIKitTests/testHomeAndSearchUseSharedMetadataCellsAcrossThemesWidthAndDynamicType` | `novel-home-compact-light`、`novel-home-wide-dark`、`novel-home-compact-large-type`、`novel-search-shared-book-list` |
| `NovelUIKitTests/testReaderFourModesNativeLayoutProgressPurchaseAndBackgroundPause` | `novel-reader-cover`、`novel-reader-slide`、`novel-reader-instant`、`novel-reader-scroll`、`novel-purchase-confirmation` |
| `NovelUIKitTests/testActualReaderKeepsTextAnchorAcrossEveryModeFontAndSafeAreaReflow` | `novel-reader-text-anchor` |
| `NovelUIKitTests/testActualReaderKeepsImageAnchorWhenHighResolutionArrivesFontAndSafeAreaChange` | `novel-reader-late-image-anchor` |
| `MeowfilmNovelNavigationHostTests/testActualNovelDiscoveryDetailReaderDockRestoreAndSettingsRoutes` | `novel-real-app-reader-hidden-dock` |
| `MeowfilmNavigationAcceptanceTests/testPrimaryPagesUseAppOwnedHeaders` | `03-novel`，以及既有原生页面截图 |

小说组件截图要求真实前台窗口且 `drawHierarchy` 成功；所有附件均须实际查看布局，
不能以 attachment 存在或本地静态检查代替视觉通过。

## 验证状态与未覆盖

### R28：发现首页与搜索列表（2026-11-17，已完成）

完整日志、回执与附件目视结论见
[R28 验证报告](../../tmp/test-results/reader-page/verification-report.md#r28)。

| 验证 | 结果 |
| --- | --- |
| `swift-tests-2` | XCTest 30/30：`NovelDiscoveryTests` 17、`NovelBookPresentationTests` 2、`NovelSourceClientTests` 7、`NovelImageLifetimeTests` 4；零失败／跳过 |
| `ios-build-5` | iOS Simulator App＋测试 target 增量 `build-for-testing` exit0；不代表测试执行或真机产物 |
| `source-scope-final`、`simulator-signing-final`、`aether-linkage-final` | 各 exit0 |
| `ui-2.xcresult` | 10 项，8 通过、2 失败、0 skip，exit65；上述六项 NovelUIKit 及两项侧栏来源行为通过 |
| `ui-3.xcresult` | 4/4，exit0、0 skip；实际发现→详情→阅读→dock 返回→设置、侧栏尺寸矩阵、来源状态更新、选源／dismiss／订阅关闭通过 |
| `headers-2.xcresult` | 1/1，exit0、0 skip；复用 `ios-build-5` 有效产物补跑 `MeowfilmNavigationAcceptanceTests/testPrimaryPagesUseAppOwnedHeaders` 通过（`headers-1` 因 `ENOSPC` 中断保留为失败证据） |

`ui-2` 与 `ui-3` 合并覆盖上述 10 个唯一必测宿主用例；两项侧栏来源行为的重复执行
不重复计数，不能称 `ui-2` 自身全部通过。公共头部 XCUITest 在 `headers-2` 独立补验通过。
`ui-2` 的两项失败来自模拟器目标 App 旧音乐展示缓存引用 12 个缺失歌曲；仅在本任务
独占时清理 19185 字节的 `last-success.json`，未清账号、永久歌曲仓库或小说数据，
音乐产品无改动。

`ui-2`（12 张）、`ui-3`（28 张）与 `headers-2`（8 张）导出的原生附件及联系表均已实际目视核对：
去除重复禁用 alpha 后侧栏停用／禁止发现书源清晰可辨且符合系统禁用语义，长名称与 AXXXL 换行不遮挡固定“设置”底栏，
真实阅读页隐藏公共 dock，小说首页保留公共头部与 Regular 空态／列表层次。以上隔离合成夹具验证不代表真实公网
第三方书源、账号、支付或用户真机验收。

### D7／main 冲突适配旧阶段（未执行返修）

D7／当时 main 冲突适配的工程生成及分块静态核对已有历史记录；其后完整终审指出的
四项返修及待选目录跨 revision 修复均已落盘。该阶段只做代码静态收口与累计终审，
没有运行构建、测试、生成器或远端／Mac／模拟器操作，新增／迁移用例不算通过。
当时用户已恢复 Mac／iOS 验证授权，尚未开始最终输入验证，由主会话集中安排，
模拟器并发1并按共享登记、原子锁排队。仅 timeout 的隔离 Mac API 探针此前实际执行，
不替代 iOS 行为验证或受影响目标构建；本小节不描述 R28 当前状态。

### D7 迁移前历史证据

以下均为 D7 迁移前的历史证据；R28 受影响输入不得据此宣称通过，未变范围能否复用
由主会话按最终集成输入确定：

- Swift 相关范围 63 项；最后 Session22／Library6／Anchor2 共30项全部通过，覆盖
  净化恢复、换源失败／取消、display保存、unknown→known与第三来源同ID。
  原始输出为 [ios-swift-final.log](../../tmp/test-results/novel/ios-swift-final.log)，
  其它有效结果见 [ios-swift.log](../../tmp/test-results/novel/ios-swift.log) 与
  [ios-swift-recheck.log](../../tmp/test-results/novel/ios-swift-recheck.log)。
- 真实 App host6、小说 UIKit／WK15、搜索消费者4及导航1均已有有效通过。
  最后精确8项覆盖新增2项恢复、四模式／购章／后台、真实文字及高清图片锚点、
  重复Done／取消、影视详情晚到保留和三目标取消；全部执行、零失败／零skip，见
  [ios-ui.json](../../tmp/test-results/novel/ios-ui.json)。未受影响项的原始输出保留于
  [ios-host-failure.log](../../tmp/test-results/novel/ios-host-failure.log)、
  [ios-ui-failure.log](../../tmp/test-results/novel/ios-ui-failure.log)、
  [ios-host.json](../../tmp/test-results/novel/ios-host.json) 和
  [ios-navigation.json](../../tmp/test-results/novel/ios-navigation.json)；旧失败项均已定向复验通过。
- 两个真实 App 进程的 cold prepare／restore 各1项已通过，exit0、零skip；原始
  [prepare](../../tmp/test-results/novel/ios-cold-prepare.log)／
  [restore](../../tmp/test-results/novel/ios-cold-restore.log) 结果复用。
  存储格式不变；record显式传入原已计算的key不改变该夹具语义，新身份变化另有重开库测试。
- 最终正常 `build-for-testing`、完整模拟器签名 gate 以及普通 arm64 App 增量构建
  均exit0，见 [mac-status.txt](../../tmp/test-results/novel/mac-status.txt)、
  [ios-simulator-signing.log](../../tmp/test-results/novel/ios-simulator-signing.log)、
  [ios-device-native-build.log](../../tmp/test-results/novel/ios-device-native-build.log)。
  arm64为 `CODE_SIGNING_ALLOWED=NO` 编译，不代表真机签名安装。
- 9张真实组件图和8张导航图已实际目视；当时仅会话／身份逻辑变动，布局输入未改，
  复用 [ios-visual-review.md](../../tmp/test-results/novel/ios-visual-review.md)。

SameSite=None 采用用户接受的公开API投影边界；None精确延续、未变零写回、删除、
逐origin、隔离及取消／退休断言仍保留，不扩展其它Cookie属性的例外。

完整审查和证据索引见 [final-review.md](../../tmp/test-results/novel/final-review.md) 与
[verification-notes.md](../../tmp/test-results/novel/verification-notes.md)。
未使用第三方真实书源、账号、支付或用户真机；未做长时间后台恢复与发行安装，
不承诺全部未知第三方／QYSG扩展。

## 原生书源与替换管理

管理与导入行为、当前书源协议字段和净化表单遵循[共同管理契约](../common/novel/reading.md#书源与替换管理)。

`NovelSourcesViewController`、`NovelRulesViewController` 使用 MeowUI 的 `ManagementListView`、`ManagementFormView`、`ManagementDialog` 与 `ManagementImportView`。管理／编辑页使用公共 App 页头，页面背景和表面遵循[设置语义配色](architecture.md#设置语义配色)。来源管理保留原有搜索／圆形工具栏、24 点外距、22 点整表圆角、紧凑行和底部批量栏，仅做主题与必要的动态字体／窄屏适配；同一列表供视频源、音源复用。源字段横向标签与垂直表单分离，键盘不覆盖输入。地址与导入确认以居中浮窗覆盖当前页面，保留后方内容而不另建空白页面；取消等待当前操作实际排空。排版数字／颜色输入也复用该浮窗，无效值留在窗内提示；二维码图片使用系统 CoreImage 解码。
