# iOS 影视搜索

## 装配与类型

业务规则、分页和数据交接的唯一契约见[共同影视搜索契约](../common/film/search.md)，Node 整合程序见[共享搜索程序](../../packages/film-search/README.md)。本端没有 Swift 清洗、季集识别、评分或聚合算法，也不装载客户端 JS 引擎。

- `MeowfilmAssembly.filmSearch` 装配 `FilmSearchService`，借用既有 `FilmSourceService` 与 App runtime；`FilmSearchTransport` 固定同一个 runtime epoch。`film.search.open` 只传原查询和当前准入来源，包含 `order`／`options.aggregateImages` 的布尔值。来源管理仍由原生来源 owner 负责；稳定源键编码安装／站点，代次单独校验。来源换代、禁用及配置变化撤销旧会话的 UI 发布资格，不擅自重搜。
- `FilmSearchSession` 只持有 Node 身份、完整 `FilmSearchSnapshot`、订阅及消费者生命周期。分页游标、成功页、TMDB 回退、规则快照、两模式顺序和成员关系全由 Node 管理。`snapshot.sequence` 与 `revision` 相同，沿 `/v1/film/events/{sessionId}` 接收完整 `cards`／`rawCards`；不从原始页重算卡片。`search.command more` 分页，切换模式不请求。成功空、通道失败、不可用和取消分别展示；SSE 断开保留已得卡片并显示错误，显式重试用同 session 的 `search.snapshot` 和新订阅，不重发 `open`。
- `cancel()` 同步撤销旧 SSE／在途回执的 UI 发布资格、结束本地 loading，再串行发送 `stop`；不取消 Node 已接纳的内容 producer。stop 回执只接纳序列／轮次、通道、loading、hasMore 等控制状态，保留已有卡片、原始条目及 metadata 展示；即使 open 或 SSE 晚到后 stop 返回了新卡，也要等明确 resume 才重新发布。保留真实 session ID 供停止、恢复和释放。同词按精确 UTF-8 查询复用当前或调用方持有的会话，只发 `resume`，不重取已成功页。新词停止旧会话；Service 弱跟踪仍由导航持有的会话，消费者释放时排空已接纳 open 并向其真实 ID 发送 `release`。搜索关闭不关闭共享 runtime。
- 原始 metadata 搜索响应、有效查询及详情只是当前 snapshot 的展示数据。`FilmSearchContext.metadataDetail` 缺少详情时按明确 TMDB 类型＋数值 ID 读取 snapshot 中的原 search 行；不根据标题猜关联，不把 partial 升级为 full。`FilmTMDBService` 保留既有独立元数据消费者接口，但不再被原生搜索会话逐项调度。缓存、原时间、revision 和完整度由 Node 保管，客户端不生成缓存 seed 或新鲜度。独立接口在主元数据请求成功后导出附属 `data.cache.entry`；明确的缓存存储／读取错误允许返回无 `cacheEntry` 的主结果，取消、关闭、协议及授权错误仍向上传递。
- `MeowfilmSearchComposition` 在应用层协调两个业务，将影视 `FilmSearchResultsContent` 注入既有 `MusicSearchViewController`／结果分页容器。`MeowUI.SearchTargetOption`、`SearchResultCategory`、`SearchResultContent` 只定义中性输入和内容生命周期；音乐算法不导入影视。页头、历史和四分类沿用既有搜索页。影视结果只是无页头的内容控制器，复用 `FilmPosterCell`，搜索补文本角标和来源／聚合／类型标签；首页默认配置和评分语义不变。
- 实际 `MeowfilmCoordinator.showFilmSearchResult` 将 `FilmSearchContext` 交给真实 `FilmPlaybackModel` 和 `FilmDetailViewController`，正式播放进入 `FilmPlayerViewController`，不旁路到裸 URL 播放器。`selectedCard` 保留原 opaque 选择身份；准确 target、可靠 TMDB 关联、全体聚合成员、原始单源及 typed `detailId` 均由 `search.handoff` 返回，不从显示标题重建。回执携带 `searchReference:{sessionId,selectionId}`，`session.open` 通过 reference 重读同一 Node owner 的事实和规则快照；晚到 `search.update` 也只传 reference／revision／当前来源。跨 runtime 的交接明确失败，不给旧事实补证明。仅明确重进的 `retargeted` 请求附 `resumeTarget`，完整保留运行器已确认的 metadata、season／episode／globalEpisode、source／line／candidate（包括数字 0）；普通初进不携带该字段，以跟随 Node 最新 handoff。正在播放时迟到资料不能重启或换集。触发与媒体生命周期见[影视会话](../runtime/film-playback.md)。

原始列表为工具行右侧开关，TMDB 加载／失败／不可用状态在其左边；手机沿现有窄屏工具行布局，不把所有音乐分页标签挤到一行。切换只改同一 session 的展示模式和独立滚动位置；TMDB 失败可用“重试”继续原会话。

## 真实 TMDB 接线

HTTP／设置／图片的唯一协议见[元数据运行器](../runtime/metadata-data.md)，本端不复制其上游路由规则。`apps/ios/runtime/entries/aggregate.mjs` 保留现有 dataRoot 和 Cat 目录布局，显式装配 music／cat／source／pan／tmdb／douban／film／netease 八模块。`RuntimePayload` 和 App launcher 按序精确校验同一清单，精确版本／contract 见[运行底座](runtime-preflight.md)；通用 runtime 默认三模块不变。

`FilmTMDBService.search` 请求 `/3/search/multi`，使用真实原查询、page=1 和 w500；语言／地区／成人选项由 Node 从配置补全；`detail` 按 movie／tv 与数字 ID 请求，使用 original 和 `append_to_response=credits,similar,images`，与智能播放完整详情同键，不请求 configuration。必要查询回退仍由原搜索会话决定。空凭据允许代理端认证，错误按运行器回执保留。设置仍调用 `tmdb.data.settings.*`；只有成功保存使旧查询及 Node 详情缓存失效，下次显式同词创建新会话，旧详情保留原查询／已得展示数据。

`RuntimeHTTPClient.metadataImageURL` 保留合法 `/v1/tmdb/images/...` 或 `/v1/douban/images/...` 相对 URL；`MeowfilmAssembly.filmImages` 以该 URL 作为既有 `ArtworkImages` 的唯一缓存键，读时才通过 `readMetadataImage` 加入当前 endpoint／独立 request ID，不需要会话访问凭据，并按准确 provider 核对回执。逐块执行 4MiB 编码数据预算，完整结束后再交给 ImageIO 生成最长边不超过 1024 的缩略图；这不等同于独立的原图像素或解码峰值内存上限。解码后容器和指定图片状态都必须完整，部分可解码图也不缓存。相对图片引用协议拒绝绝对、网络路径、穿越及 redirect，不限制运行器 endpoint 的域名或地址。502 模块错误、断流、错 ID、取消／关闭不能返回成功图片；取消仅撤销真实 URLSession task，完成以物理 didComplete 为准，owner 关闭等待 session 排空。普通外部图片仍由 `FilmHTTP.readArtwork` 的独立通道读取，上游 metadata API 凭据不会进入该通道。

## 定向验证入口

以下是可执行测试入口，不能代替具体测试运行结果：

- Swift：`bash scripts/test-native.sh test --filter 'FilmSearchSessionTests|FilmSearchHandoffTests|FilmSearchAuthorizationScopeTests|FilmSearchStableIdentityTests|FilmPlaybackSearchContextTests|FilmTMDBServiceTests'`，验证完整 Node 卡片、两模式顺序、错误／空／不可用、SSE、停止即撤发布、同 session 恢复、来源准入、准确 handoff 和关闭排空；播放 context 测试验证晚到 reference 更新和重进坐标。stop 迟到数据回归为 `FilmSearchSessionTests/testStopReceiptKeepsEveryDisplayedFieldAfterRejectedSSEUntilSameSessionResume`、`testCancelledAcceptedOpenBindsIdentityWithoutPublishingStopCardsAndResumesThenReleases`；保留既有 SSE／accepted more 取消断言。搜索算法、逐源游标和缓存行为由 Node 搜索测试覆盖，不再用 Swift 替身实现另一套算法。
- 跨查询详情回归分别验证原生旧 UI 发布被撤销、真实 Node producer 优雅完成、同键缓存复用及配置失效后的物理取消；不能再以原生成功详情字典的调用次数证明统一缓存正确。可控 continuation gate 覆盖晚到旧读者；真实宿主补充 Node 缓存及网络证据。
- scheme `MeowfilmAcceptance` 的 UIKit 精确方法：`MeowfilmAcceptanceUITests/FilmSearchUIKitTests/testExistingPosterAddsSearchBadgeAndSourceLabelAtNarrowAndWideWidths`、`testSearchContentUsesExistingPostersKeepsBothModesAndDeliversLiveContext`、`testRawTypedZeroCardsKeepSeparateNavigationAndRealFavoritesAcrossReload`（后两项使用同一 target/class 前缀）。使用真实 `FilmPosterCell` 断言长标题、窄宽、普通／聚合／电影／剧集文本，并保存附件 `film-search-existing-posters-narrow-three-kinds`；固定 Node wire 回执覆盖模式切换和 live context，不冒充引擎执行。
- 同 scheme：`-only-testing:MeowfilmAcceptanceUITests/SearchPageViewControllerTests`；`MusicUIKitRegressionTests` 下的 `testAdditionalSearchCategoryIsLazyAndTargetSubmissionAlwaysSelectsRequestedCategory`、`testGlobalSearchAddsAllFourCategoriesWhenMusicPlaylistSearchIsDisabled`、`testNewMusicSubmissionDuringPendingAdditionalSwipeDoesNotStartSupersededDomain`。
- 同 scheme 的真实 NodeMobile 精确入口如下。实际 App 协调器验证入口默认目标、真实卡片点击、在途成员交接、详情释放、第二轮后返回再进入及请求次数；临时 Cat HTTP 程序验证完整125条页与第二页；额外验证 Unicode 规则预览与真实搜索输入／聚合结果一致和孤立 UTF16。规则与搜索都经真实 NodeMobile，TMDB 使用 loopback 合成成功空上游并恢复原配置，不访问公网。通道不可用的独立语义仍由消费者协议测试覆盖。

```text
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSearchHostTests/testRealCatSearchReadsCompleteInstalledSourcePagesAndRetainsDetailIdentity
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSearchHostTests/testActualCoordinatorEntryUsesMusicAndFilmPageDefaultsWithoutSubmitting
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSearchHostTests/testActualSearchTapUsesTheDataOwnerAndLiveGroupRawDirectoriesWithoutAnotherSearch
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSearchHostTests/testNodeMobilePreviewAndSearchUseIdenticalUnicodeRulesInputAndSnapshot
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSearchHostTests/testNodeMobileSearchKeepsDerivedLoneUTF16AndOriginalTypedMembers
```

- App 增量构建 scheme 为 `Meowfilm`；上述 UIKit／宿主测试及其构建使用 `MeowfilmAcceptance`。既有海报布局、按压和详情回归可选 `FilmUIKitTests` 的相关方法，不要求全量 UI 测试。

### 真实 TMDB 宿主

新增 Swift 精确 filter（`bash scripts/test-native.sh test --filter '<filter>'`）：

```text
FilmTMDBServiceTests
RuntimeHTTPClientTests/testTMDB
RuntimeHTTPClientTests/testAppDescribeRequiresExactEightModuleOrderVersionsContractsAndMethods
RuntimeEmbeddedLauncherTests/testPayloadRequiresTheCompleteEightModuleHTTPAssembly
RuntimeMetadataImageTests
FilmSearchSessionTests/testNewWordFencesOldPublishingButKeepsRetainedNavigationIdentity
FilmSearchSessionTests/testMetadataChangeNeedsExplicitNewSessionWithoutDiscardingRetainedDisplay
```

适配测试验证完整 JSON 和真实 HTTP 参数；HTTP 测试覆盖无访问凭据与端口变化下稳定身份、502／错 ID／redirect／断流／读取预算、物理取消与 owner 关闭。它们不是 NodeMobile 宿主的替代品。

scheme `MeowfilmAcceptance` 的新增精确 `only-testing`：

```text
-only-testing:MeowfilmRuntimeTests/MeowfilmTMDBHostTests/testActualTMDBSettingsSearchDetailsAndArtworkPreserveReplaceClearAndFailedSave
-only-testing:MeowfilmRuntimeTests/MeowfilmTMDBHostTests/testSuccessfulSettingsSaveCancelsActualOldDetailAndKeepsItsNavigationSnapshot
-only-testing:MeowfilmRuntimeTests/MeowfilmTMDBHostTests/testCancelledDetailWaiterLeavesActualProducerToPopulateTheSharedCache
-only-testing:MeowfilmRuntimeTests/MeowfilmTMDBHostTests/testActualArtworkRejectsModuleFailureTruncationAndCancellationWithoutCachingThenRetries
```

这些用例在实际 App TEST_HOST 中借用唯一 NodeMobile owner；`NWListener` 仅充当本机合成上游，不替换 runtime 模块、Film adapter 或 Artwork：

1. 从真实影视设置菜单进入 TMDB 页，实际字段／保存动作完成配置；以单个明文输入中的合成 token／key 验证回显、替换、空值清除、失败不丢输入且不撤销有效会话、修正后重试。实际搜索／电影与剧集详情确认完整 JSON、zh-CN／page1、没有 configuration，FilmPosterCell 的 ArtworkView 通过实际 `assembly.filmImages` 解码图片；换图片基址不改 warm cache key，无凭据也可由代理正常返回。
2. 旧 query 的实际上游 detail 挂起，设置保存成功后观察该连接真实断开；不自动搜索，下次同词取得新详情，旧 FilmDetailViewController 保留已得搜索简介／原查询，不接收新会话结果。不能用该真实取消测试代替“故意忽略取消、返回 late-old”的原生 gate 回归。
3. 普通详情读者取消后，上游仍可完成，后续相同完整请求复用在途／缓存，实际只读上游一次；返回 seed 的原时间、revision、完整度及完整请求身份不变。与第 2 项配置撤权时真实断开分别验证。
4. 实际 ArtworkView／ArtworkImages 经真实运行器处理上游错误、HTTP 断流、HTTP 200 且 Content-Length 准确但 PNG 缺少 IEND、无法解码和最后消费者取消，失败时图片和缓存均为空；同一相对引用随后显式重取完整 PNG 成功。缺少 IEND 的夹具只移除完整 PNG 最后 12 字节，不以 HTTP 断流或无效短数据代替容器不完整。断开以网络回执观察，不用 sleep。API 凭据不发图片，上游不接收 runtime session／request ID。

本类只接受无凭据的专用模拟器；清理恢复本次读取的非敏感基址并清除合成密码。截图附件 `tmdb-settings-saved-plain-token`、`tmdb-settings-failed-save-retains-input` 由前台实际窗口生成，裁切并 JPEG 压缩、断言 <700KB。两进程设置恢复 selector 和环境见[影视设置](film-settings.md#既有-owner-与专用验证)。

实际图片 owner 关闭另起进程执行 `-only-testing:MeowfilmRuntimeTests/MeowfilmTMDBShutdownHostTests/testAppShutdownDrainsPendingActualArtwork`，环境 `TEST_RUNNER_MEOWFILM_TMDB_SHUTDOWN=1`；不能与复用 App 的其他业务用例同次运行。用真实上游挂起图片，先恢复合成设置，再关闭 App；观察上游断开、共享 runtime 关闭及 Artwork 无成功缓存。未设环境会跳过，不算关闭通过。

Swift、UIKit、真实宿主、独立生命周期和 App 构建须分别记录实际验证结果，不能互相替代。以上合成 HTTP 上游的真实接线不能冒充公网账号／代理可用性；豆瓣不参与当前搜索入口。智能匹配、正式媒体与后台／PiP 的验证范围另见[影视会话](../runtime/film-playback.md)，本页入口不能代替其通过证据。
