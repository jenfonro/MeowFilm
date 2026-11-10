# iOS 影视搜索

## 装配与类型

业务规则、分页和数据交接的唯一契约见[共同影视搜索契约](../common/film/search.md)，共享程序输入见 [FilmSearchProgram](../../packages/film-search/README.md)。本端没有另一套 Swift 清洗或聚合算法。

- `MeowfilmAssembly.filmSearch` 装配 `FilmSearchService`，借用既有 `FilmSourceService`／`FilmRuleService`，内容由 `FilmSearchData` 调用同一个 App runtime 的 `film.data.search`。来源管理仍由 Cat owner 负责。`FilmSearchProcessor` 通过现有原生 QuickJS 执行 `FilmSearchProgram.source()`；保存的聚合正则来自原规则 owner，不另建配置。稳定源键编码安装／站点，代次单独校验；接受响应前后核对当前源身份；来源快照观察使在途换代或禁用立即取消旧会话。
- `FilmSearchSession` 持有独立来源游标、当前轮未结算读取、两模式上一结算轮顺序及完整 `FilmSearchSnapshot`。每轮记录成功响应，全部源请求失败标记 `.failed`，与成功空页区分；失败页不进游标，保留显式 more。整体错误只在没有累计可见结果时呈现，不逐源报错。取消后恢复同一个在途轮，只重做未结算读取；已得页与已得元数据不重取。同词按完整 UTF-8 查询复用当前或调用方实际持有的会话，不保存任意关键词历史结果字典。新词撤销旧请求，Service 只跟踪物理排空中的退役工作，结算后移除；关闭搜索内容释放当前 ownership，详情持有的快照仍可读取。
- `FilmSearchMetadataReading` 的生产适配为 `FilmTMDBService`，借用同一个 App `RuntimeSession`，通过 `film.data.metadata` 返回原始 TMDB JSON。内容与 `film.data.cache.entry` 从同一实际 client 取得，保留 producer、原时间、配置 revision 和完整度，不建第二个原生成功缓存或在途合并器。原始搜索响应、有效查询及详情仍可作为当前 snapshot 展示；`FilmSearchContext.metadataDetail` 缺少完整详情时按 TMDB 类型＋数值 ID 读取已保存 search 原行，不能将该 partial 交为 full。普通取消退出本读者并排空原生请求，Node 已受理 producer 可完成入缓存；配置／来源撤权与 runtime close 才物理取消 producer。无 metadata 注入的协议测试仍为 `.unavailable`；生产错误不会转换为空成功。
- `MeowfilmSearchComposition` 在应用层协调两个业务，将影视 `FilmSearchResultsContent` 注入既有 `MusicSearchViewController`／结果分页容器。`MeowUI.SearchTargetOption`、`SearchResultCategory`、`SearchResultContent` 只定义中性输入和内容生命周期；音乐算法不导入影视。页头、历史和四分类沿用既有搜索页。影视结果只是无页头的内容控制器，复用 `FilmPosterCell`，搜索补文本角标和来源／聚合／类型标签；首页默认配置和评分语义不变。
- 实际 `MeowfilmCoordinator.showFilmSearchResult` 将 `FilmSearchContext` 交给真实 `FilmPlaybackModel` 和 `FilmDetailViewController`，正式播放进入 `FilmPlayerViewController`，不旁路到裸 URL 播放器。`selectedCard` 保留原选择身份，`card` 表示当前承载卡；组卡被迟到的 TMDB 吸收时，沿共享程序返回的原成员身份跟随。来源成员用 typed `detailId` 和完整 `raw` 交接；尚无可靠 metadata 的聚合使用 `group`，原始列表始终单源。首次 open 和晚到 `search.update` 使用原查询／范围、成功页及准确 producer；跨 owner 降 hints，不借新 client 给旧事实补证明。页面重进保当前已选 target／文件，不退回初始卡片；正在播放时迟到资料不能重启或换集。触发与媒体生命周期见[影视会话](../runtime/film-playback.md)。

## 真实 TMDB 接线

HTTP／设置／图片的唯一协议见[元数据运行器](../runtime/metadata-data.md)，本端不复制其上游路由规则。`apps/ios/runtime/entries/aggregate.mjs` 保留现有 dataRoot 和 Cat 目录布局，显式装配 music／cat／source／pan／tmdb／douban／film／netease 八模块。`RuntimePayload` 和 App launcher 按序精确校验同一清单，精确版本／contract 见[运行底座](runtime-preflight.md)；通用 runtime 默认三模块不变。

`FilmTMDBService.search` 请求 `/3/search/multi`，使用真实原查询、page=1、zh-CN 和 w500；`detail` 按 movie／tv 与数字 ID 请求，使用 original 和 `append_to_response=credits,similar,images`，与智能播放完整详情同键，不请求 configuration。必要查询回退仍由原搜索会话决定。空凭据允许代理端认证，错误按运行器回执保留。设置仍调用 `tmdb.data.settings.*`；只有成功保存使旧查询及 Node 详情缓存失效，下次显式同词创建新会话，旧详情保留原查询／已得展示数据。

`RuntimeHTTPClient.metadataImageURL` 保留合法 `/v1/tmdb/images/...` 或 `/v1/douban/images/...` 相对 URL；`MeowfilmAssembly.filmImages` 以该 URL 作为既有 `ArtworkImages` 的唯一缓存键，读时才通过 `readMetadataImage` 加入当前 endpoint／独立 request ID，不需要会话访问凭据，并按准确 provider 核对回执。逐块执行 4MiB 编码数据预算，完整结束后再交给 ImageIO 生成最长边不超过 1024 的缩略图；这不等同于独立的原图像素或解码峰值内存上限。解码后容器和指定图片状态都必须完整，部分可解码图也不缓存。相对图片引用协议拒绝绝对、网络路径、穿越及 redirect，不限制运行器 endpoint 的域名或地址。502 模块错误、断流、错 ID、取消／关闭不能返回成功图片；取消仅撤销真实 URLSession task，完成以物理 didComplete 为准，owner 关闭等待 session 排空。普通外部图片仍由 `FilmHTTP.readArtwork` 的独立通道读取，上游 metadata API 凭据不会进入该通道。

## 定向验证入口

以下是可执行测试入口，不能代替具体测试运行结果：

- Swift：`bash scripts/test-native.sh test --filter FilmSearchSessionTests`，覆盖完整页、分页、静默单源失败／全源失败与成功空页区分、两模式轮次、TMDB 回退与缓存、详情失败时精确类型／ID 复用 search 元数据、在途交接、取消恢复、源换代／禁用、关闭排空和快照释放。
- 跨查询详情回归分别验证原生旧 UI 发布被撤销、真实 Node producer 优雅完成、同键缓存复用及配置失效后的物理取消；不能再以原生成功详情字典的调用次数证明统一缓存正确。可控 continuation gate 覆盖晚到旧读者；真实宿主补充 Node 缓存及网络证据。
- scheme `MeowfilmAcceptance`：`-only-testing:MeowfilmAcceptanceUITests/FilmSearchUIKitTests`，使用真实 `FilmPosterCell` 断言长标题、窄宽、普通／聚合／电影／剧集文本，并保存附件 `film-search-existing-posters-narrow-three-kinds`；另覆盖真实 QuickJS 内容、模式切换和 live context。
- 同 scheme：`-only-testing:MeowfilmAcceptanceUITests/SearchPageViewControllerTests`；`MusicUIKitRegressionTests` 下的 `testAdditionalSearchCategoryIsLazyAndTargetSubmissionAlwaysSelectsRequestedCategory`、`testGlobalSearchAddsAllFourCategoriesWhenMusicPlaylistSearchIsDisabled`、`testNewMusicSubmissionDuringPendingAdditionalSwipeDoesNotStartSupersededDomain`。
- 同 scheme：`-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSearchHostTests`，实际 App 协调器验证入口默认目标、真实搜索卡片点击、在途 TMDB 交接、详情释放、第二轮后返回再进入及请求次数；另以临时安装的 Cat HTTP 程序验证真实 transport 完整125条页与第二页。该类的 TMDB 是注入替身，真实 adapter／NodeMobile 覆盖由下列宿主提供。
- App 增量构建 scheme 为 `Meowfilm`；上述 UIKit／宿主测试及其构建使用 `MeowfilmAcceptance`。既有海报布局、按压和详情回归可选 `FilmUIKitTests` 的相关方法，不要求全量 UI 测试。

### 真实 TMDB 宿主

新增 Swift 精确 filter（`bash scripts/test-native.sh test --filter '<filter>'`）：

```text
FilmTMDBServiceTests
RuntimeHTTPClientTests/testTMDB
RuntimeHTTPClientTests/testAppDescribeRequiresExactEightModuleOrderVersionsContractsAndMethods
RuntimeEmbeddedLauncherTests/testPayloadRequiresTheCompleteEightModuleHTTPAssembly
RuntimeMetadataImageTests
FilmSearchSessionTests/testMetadataSettingsChangeRevokesSameWordAndCacheWithoutDestroyingOldDetailOrAutoSearching
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

1. 从真实影视设置菜单进入 TMDB 页，实际字段／保存动作完成配置；以合成 token／key 验证保留、替换、显式清除、失败不丢输入且不撤销有效会话、修正后重试。实际搜索／电影与剧集详情确认完整 JSON、zh-CN／page1、没有 configuration，FilmPosterCell 的 ArtworkView 通过实际 `assembly.filmImages` 解码图片；换图片基址不改 warm cache key，无凭据也可由代理正常返回。
2. 旧 query 的实际上游 detail 挂起，设置保存成功后观察该连接真实断开；不自动搜索，下次同词取得新详情，旧 FilmDetailViewController 保留已得搜索简介／原查询，不接收新会话结果。不能用该真实取消测试代替“故意忽略取消、返回 late-old”的原生 gate 回归。
3. 普通详情读者取消后，上游仍可完成，后续相同完整请求复用在途／缓存，实际只读上游一次；返回 seed 的原时间、revision、完整度及完整请求身份不变。与第 2 项配置撤权时真实断开分别验证。
4. 实际 ArtworkView／ArtworkImages 经真实运行器处理上游错误、HTTP 断流、HTTP 200 且 Content-Length 准确但 PNG 缺少 IEND、无法解码和最后消费者取消，失败时图片和缓存均为空；同一相对引用随后显式重取完整 PNG 成功。缺少 IEND 的夹具只移除完整 PNG 最后 12 字节，不以 HTTP 断流或无效短数据代替容器不完整。断开以网络回执观察，不用 sleep。API 凭据不发图片，上游不接收 runtime session／request ID。

本类只接受无凭据的专用模拟器；清理恢复本次读取的非敏感基址并清除合成密码。截图附件 `tmdb-settings-saved-no-password-echo`、`tmdb-settings-failed-save-retains-input` 由前台实际窗口生成，裁切并 JPEG 压缩、断言 <700KB。两进程设置恢复 selector 和环境见[影视设置](film-settings.md#既有-owner-与专用验证)。

实际图片 owner 关闭另起进程执行 `-only-testing:MeowfilmRuntimeTests/MeowfilmTMDBShutdownHostTests/testAppShutdownDrainsPendingActualArtwork`，环境 `TEST_RUNNER_MEOWFILM_TMDB_SHUTDOWN=1`；不能与复用 App 的其他业务用例同次运行。用真实上游挂起图片，先恢复合成设置，再关闭 App；观察上游断开、共享 runtime 关闭及 Artwork 无成功缓存。未设环境会跳过，不算关闭通过。

Swift、UIKit、真实宿主、独立生命周期和 App 构建须分别记录实际验证结果，不能互相替代。以上合成 HTTP 上游的真实接线不能冒充公网账号／代理可用性；豆瓣不参与当前搜索入口。智能匹配、正式媒体与后台／PiP 的验证范围另见[影视会话](../runtime/film-playback.md)，本页入口不能代替其通过证据。
