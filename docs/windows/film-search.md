# Windows 影视搜索接线与验证入口

行为以[共同影视搜索契约](../common/film/search.md)为唯一来源；共享 JSON 入口与参考来源见 [`packages/film-search/README.md`](../../packages/film-search/README.md)。本文件只记录 Windows 实现与接通边界。

E49 搜索交接与元数据消费者已完成正式接线并有对应测试用例。下述接线依据当前生产源码；执行结果须对应当前受测输入独立记录，不表示累计整合后的 Windows 构建、原生消费者或真实媒体已经全部通过。

## 类型与装配

- App 通过 `AppLauncher.createPages` 装配一个 `SearchResultsPage`，实现 Desktop 的中性 `IDesktopSearchPage`；Desktop 不引用影视业务。结果页将原有 `MusicView` 暂时挂入 `MusicHost`，没有第二份音乐请求、搜索协调器或音乐视图实例。
- `MusicCapability.SearchOnlineAsync` 是在线综合入口；独立曲库搜索原入口保留。`SearchResultsPage` 拥有四分类、模式与滚动投影，影视状态仍由 `FilmSearchService`／`FilmSearchSession` 持有。
- `FilmSourceService` 实现 `IFilmSearchSources`，在读取当前 Cat 状态并等待已有 starting 结算后选取可搜索源；`CurrentSearchSources` 是无 I/O 的当前可用集合。内容请求经 `FilmSearchDataClient` 调用 `film.data.search`，携带准确 source、原查询和本源 `page`，并检查调用前后的安装／代次；不再由原生适配器直接调用 Cat 的搜索动作。
- Capabilities 将共享文件嵌为 `MeowFilm.FilmSearch.js`，使用 `NativeJavaScriptJsonEngine` 执行投影、聚合和排序，不另写 C# 搜索算法；内容取数另走同一 App runtime。正则来自既有 `FilmRuleService` 已保存快照。MSBuild 继续构建并复制原生 `meowfilm_js.dll`。
- `FilmMetadataService` 实现 `IFilmSearchMetadata`，内容经同一 `FilmSearchDataClient` 调用 `film.data.metadata`。搜索请求为 `/3/search/multi`、`{query:原查询,page:1,language:'zh-CN'}`、`imageSize:'w500'`；电影／剧集完整详情为 `/3/{movie|tv}/{id}`、`{language:'zh-CN',append_to_response:'credits,similar,images'}`、`imageSize:'original'`，与 Node 播放详情同键。保留完整原响应及附加字段，不以卡片显示摘要替代原数据；空凭据仍正常请求显式配置代理，失败不伪造空成功。
- 正式 Windows entry、manifest 与原生 describe 精确装配 `music,cat,source,pan,tmdb,douban,film` 七模块；同进程先建六 provider，再以 `connectFilmPlayback` 接入影视及 provider 撤权 hook。通用 `createRuntime()` 默认三模块不变，精确身份／版本／操作校验不放宽，详见[运行器装配](app-runtime.md)与[打包](runtime-packaging.md)。装配豆瓣不表示搜索范围新增豆瓣。
- 内容缓存、完整度、新鲜度与同 key producer 合并由 [Node film 数据 owner](../runtime/film-playback.md) 唯一持有。原生只保留当前查询／页面展示快照，不保留跨查询的成功详情缓存或跨请求 flight。TMDB 设置仍调用 `tmdb.data.settings.get/update`，配置及凭据只由运行器持久化。
- `IRuntimeImageClient` 经当前会话读取 `/v1/tmdb/images/...`，由 AppHost 注入 `ArtworkService` 中性字节口；相对引用就是缓存键，保持原生 4 MiB 编码图片与解码／像素预算。普通 `ArtworkHttp` 按实际 HTTP(S) 地址读取，不作域名、IP、端口或重定向来源访问准入，见[图像生命周期](artwork.md)。
- 生产端点／凭据来自运行器配置；没有读取其他任务私密数据。真实公网联调仍需用户提供可用配置或授权来源，以下合成 HTTP 真实运行器用例不代表公网通过。

## 生命周期与真实播放接收

`FilmSearchService` 是 UI dispatcher owner。每源游标、两个投影及上一已结算轮的位置都保存在会话；切换分类／模式只重投影。来源集合或代次变化撤销当前查询发布资格，同词再次进入先结算旧原生 Work，再为同一会话建立新 CTS，保留仍有效的成功页；取消关闭准备也按此恢复，只补未确认页和中断的元数据工作。游标的下一请求页与 `ConfirmedPage` 分开，交给 Node 的 `search.page` 是最后确认成功页，失败不推进；无总页数时的完整页／重复页结束规则仍由共同契约约束。

新词使旧会话退役；旧 Work 结算后移出排空跟踪、取消服务订阅并释放 CTS，不用条数上限或 TTL。提交不同音乐关键词也退休旧影视会话。实际详情仍可持有其会话数据。

普通页面／查询取消只撤销消费者等待、订阅和旧 UI 发布，有限 Node 内容 producer 可以继续并按原身份入缓存，不能因最后原生读者离开就杀上游。来源／配置撤权与运行器 close 才取消失效 producer 并等待实际 I/O 排空。原生 `PrepareShutdownAsync` 等待自身请求／引擎工作结算，不等于 Node 已排空；真实关闭还须等待 App runtime owner。TMDB 设置成功保存才使旧搜索失效、不自动重新搜索，失败保存不撤销当前查询；旧详情继续保有原 snapshot，不取得新缓存资格。

`FilmSearchData` 保存完整响应、实际 `RuntimeConnection` producer 和可选原始 cache receipt。`FilmSearchDataClient` 固定同一连接读取内容及 `film.data.cache.entry`；未命中保持 null，不由 UI 生成 complete、时间或 revision。seed 原样保留 fetchedAt／expiresAt、completeness 和 settings revision，由 Node 检查有效性，partial 不冒充 full。`FilmSearchSession.MetadataData`／每源 `Pages` 保存这些响应，`Details` 只是本会话显示数据，不是另一个新鲜度 owner。交接核对进程内实际 client 与撤权生命周期，JSON 不能携带该授权证明；换 owner 或证明缺失时，旧结果降为 `resultHints`，不继承旧游标／来源 seed，也不因首次消费捕获到新连接就给旧数据盖章。

App 将点击交给 `FilmCapability.ReceiveSearchPlayback`，再通过 Shell 的受控入口进入 `film-playback`。`FilmPlaybackPage.FromSearch` 经 `FilmSearchPlayback.Capture` 构造真实 `FilmPlaybackRequest`，由 `FilmPlaybackSession` 打开 Node 会话，不以预览页代替接收点。Context 区分原点击 `Item`、当前选择 `CurrentItem` 和最终承载 `CarrierItem`；目标与交接规则如下：

- 可靠 TMDB carrier 保留实际 movie／tv 类型及 ID；未可靠关联的站源聚合使用 `group` 与全部 typed 成员，不捏造剧集身份或降成代表单源。原查询、已成功页、原始行和未格式化备注继续交接，不按显示标题重搜。
- `detailId` 保留 JSON string／安全整数，包括数字 `0` 与字符串 `"0"` 的区别；共享投影的 `raw` 保留完整原行，`siteDetail` 只供显示。原始列表的 `site:` 选择只构造准确 `source` target，限制所属来源、结果及游标；不接受聚合／TMDB 扩展，`Metadata` 为空。来源 generation 改变后，旧选择不跟随替换代次的承载卡。
- `FilmPlaybackPage.SearchChanged` 合并原 scope 内的迟到上下文，`FilmPlaybackOpen.SearchUpdateFor` 复核 producer／来源范围，`FilmPlaybackSession` 以单调 revision 合并待发布项，发送同 session 的 `search.update`。更新不主动重搜或预读全部站源详情；已 pending／current 的播放不因迟到关联静默换文件，raw 不扩权，关闭／释放后不重新调度。
- `CaptureRequest` 使用当前 snapshot target，重进／重连保留用户已选目标和准确 raw candidate，而非回退初始卡片闭包目标。没有取得的简介、剧集和地址保持缺失，必要后续详情由 Node 业务按需读取；收藏仍由原生 `FilmLibrary` 持有。

上述 `group`、晚到更新和重进接缝已写入当前生产路径，仍属本轮实现整合范围；不能仅凭静态类型／调用存在宣称端到端已经通过。

结果页与播放接收点均核对同一会话及完整原 item，不以相同展示字段或 displayKey 判定同一选择。新代次／成员内容产生独立播放页和不透明导航身份，旧历史页保持原选择；完全相同的再次点击仍复用当前页。`FilmCapability` 实现中性 `IDesktopNavigationRetention`：保留当前 `Playback` 及 Shell 实际 history／origin 等位置可达的搜索页，其他页面解除会话订阅。Q1 返回结果后再点击 Q2，不保留已经无入口的 Q1；仍在历史或搜索 origin 的 Q1 可按原位置恢复。此处导航页面保留不是业务内容缓存，没有新增条数限制或 TTL。

公共页的输入草稿独立于原模块草稿；播放返回恢复未提交输入和原结果查询，重复点击不重新搜索。Shell 保存原模块不透明位置，退出公共结果页恢复原业务页，而非音乐内部的三分类投影。

实际结果页无卡且全源失败时显示“搜索不可用”，包括 TMDB 已成功返回空的情况；显式加载更多仍可重试失败原页，单源失败不增设提示。

## 定向测试清单

以下 group 均由测试程序集自动发现，文件位于 `apps/windows/tests/MeowFilm.Windows.Tests/`；覆盖说明是源码入口，不是执行报告。精确 case 以最终冻结的 `Cases()` 声明为准，不固定活动用例数量。

### `FilmSearchTests`

`FilmSearchTests.cs` 覆盖完整页、独立游标、无总页数／重复页、显式失败页重试、流式排序／后续追加、原 payload／有效查询、详情新鲜度委托给 Node、跨查询取消隔离、来源代次恢复及迟到 carrier。真实执行内嵌搜索 JS／原生 QuickJS，来源及 TMDB 为替身。

其中 `FilmSourcesTests.Search.cs` 的 `SearchWire` 经过真实来源服务和可控 runtime 连接，检查 `film.data.search`／`film.data.cache.entry`、准确 source／原查询／独立页码、完整 raw 与空 receipt，不直接发 Cat search；不启动真实 Node。

### `SearchResultsConsumerTests`

`SearchResultsConsumerTests.cs` 使用真实 Shell、Music／Film capability、导航接收点与 WPF 路由事件；音乐 HTTP 和 runtime 为可控替身。覆盖分类／草稿／返回位置、普通 group 与 raw／TMDB 交接、后续成功页和迟到 `search.update`、相同展示的新 generation、全源失败与成功空的区分、可达页面及订阅释放。

释放／历史恢复用例检查页面保留数和实际 `Session.Changed` 订阅数，输出 `EVIDENCE film-search unreachable playback ...`／`EVIDENCE film-search navigation retention ...`；未取得本轮原生执行回执。

### `FilmSearchPresentationTests`

`FilmSearchPresentationTests.cs` 在真实搜索页和既有 `FilmCard` 上检查 1110／860 宽度、长标题、三种标签、文本角标、首页原评分／激活／294 高度，以及两个模式的顺序／独立滚动。使用隐藏 `HwndSource`（无 `WS_VISIBLE`，有 `WS_EX_NOACTIVATE`），不显示或激活窗口、不使用系统输入。卡片用例保存实际产品离屏渲染至 `TestWorkspace.DataPath("film-search", "cards.png")`，输出 `EVIDENCE film-search cards <absolute-path>`；查看前裁切或压缩至小于 700KB。

### 必要既有消费者回归

保留播放接收／页头必要回归，不默认运行全部 UI 组：

| Group | 精确 Case |
| --- | --- |
| `FilmPlaybackTests` | `film-playback: unseeded route is empty and explicit wire cards preserve movie or series identity` |
| `FilmPresentationTests` | `film-presentation: more opens data while all cards open the playback page` |

两项使用 `Window.Show()` 的既有屏幕外、`ShowActivated=false` 场景，需要可创建窗口的 Windows 会话，但没有真实输入／桌面激活依赖；不是搜索三组的纯隐藏 HWND 路径。未选入的旧悬停／焦点用例不能据此宣称无桌面依赖。

E49 还需覆盖 `FilmPlaybackContractTests.cs` 的原始缓存时间／完整度／settings revision、typed ID，以及 `FilmPlaybackRecoveryTests.cs` 的同 producer 证明、换 owner 降 hints、准确来源交集和 raw 恢复。它们使用可控连接／媒体替身，不等于真实媒体验证；与搜索三组和下节 metadata 组一起纳入最终受影响范围，不能只跑旧 UI case 就声明交接完成。

## 执行依赖与当前证据边界

- 搜索三组、上表两项及可控连接的 contract／recovery 组执行时不启动 Node，不需要 runtime 测试 bundle 或 Node addon；真实 metadata runtime 组另见下节。测试进程入口仍校验真实绝对 `MEOWFILM_TEST_NODE` 路径和已存在的 `MEOWFILM_TEST_DATA_ROOT`，后者须位于当前编译副本的 `tmp/test-results/` 内，不代表这些用例启动 Node。
- 不能把“用例不启动 Node／媒体”理解成测试工程无原生构建依赖：`MeowFilm.Platform` 正常构建原生 QuickJS，`MeowFilm.Capabilities` 引用 `MeowFilm.Media`，后者正常增量生成并复制锁定私有 VideoKernel DLL／回执。沿用 [Windows 构建依赖](app-structure.md)及[媒体内核](media-playback.md)准备工具链和默认缓存，不手工复制任意 DLL 或跳过正常 target。
- 搜索三组不要求活动交互桌面：STA／Dispatcher 与 WPF／原生 QuickJS 环境必需，但不 Show 产品窗口、不抢焦点、不使用 `SendInput`。截图来自隐藏离屏 HWND；上表两项的窗口要求不能与其混同。
- 保持正常增量缓存；不要调用默认会准备 bundle 并清理其测试目录的 `scripts/windows/test.ps1` 来替代上述依赖判断。主会话在最终集成终审、解除 C# 冻结后可直接按锁恢复、增量构建、执行 DLL；保留 App 增量构建作为最终集成步骤，本轮文档工作不执行：

```powershell
# 在当前同步副本根目录，沿用既有测试数据目录；不清理或另造运行时载荷。
$root = (Get-Location).Path
$env:MEOWFILM_TEST_NODE = (Get-Command node.exe).Source
$env:MEOWFILM_TEST_DATA_ROOT = Join-Path $root 'tmp\test-results\windows-native\data'
New-Item -ItemType Directory -Force $env:MEOWFILM_TEST_DATA_ROOT | Out-Null
Push-Location (Join-Path $root 'apps\windows')
try {
  dotnet restore tests\MeowFilm.Windows.Tests --locked-mode --configfile NuGet.Config
  if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
  dotnet build tests\MeowFilm.Windows.Tests -c Release --no-restore --nologo -nodeReuse:false
  if ($LASTEXITCODE -ne 0) { throw 'Incremental test build failed' }
  $dll = 'tests\MeowFilm.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\MeowFilm.Windows.Tests.dll'
  dotnet $dll --group FilmSearchTests SearchResultsConsumerTests FilmSearchPresentationTests
  if ($LASTEXITCODE -ne 0) { throw 'Search tests failed' }
  dotnet $dll --group FilmPlaybackContractTests FilmPlaybackRecoveryTests
  if ($LASTEXITCODE -ne 0) { throw 'Playback handoff tests failed' }
  dotnet $dll --case 'film-playback: unseeded route is empty and explicit wire cards preserve movie or series identity'
  if ($LASTEXITCODE -ne 0) { throw 'Playback receiver regression failed' }
  dotnet $dll --case 'film-presentation: more opens data while all cards open the playback page'
  if ($LASTEXITCODE -ne 0) { throw 'Presentation regression failed' }
  dotnet restore app\MeowFilm.Windows.App.csproj --locked-mode --configfile NuGet.Config
  if ($LASTEXITCODE -ne 0) { throw 'App locked restore failed' }
  dotnet build app\MeowFilm.Windows.App.csproj -c Release --no-restore --nologo -nodeReuse:false
  if ($LASTEXITCODE -ne 0) { throw 'Incremental App build failed' }
} finally { Pop-Location }
```

本阶段未执行 Windows 构建、原生测试或远端操作；以上是验证入口，不是通过报告。共享算法 Node 对照不能替代 Windows 原生证据；旧搜索交付的静态回执、hash 和 case 数量也不证明当前整合输入。工程资源检查入口仍为 `node scripts/windows/verify-app-structure.mjs`，包含 EmbeddedResource 清单；本次纯文档收口未运行该检查。

## TMDB 真实接通定向测试

`FilmMetadataRuntimeTests.cs`／`FilmMetadataConsumerTests.cs` 正由对应 owner 整合，以下保留 group、依赖和必要覆盖，不固定精确 cases 或数量。当前未取得最终输入的执行结论。

真实 runtime 组必须使用当前 Windows 七模块 entry 的 bundle（根下的 `aggregate`），经正常 bootstrap／ensure-runtime-native 与 `scripts/windows/prepare-test-bundles.mjs` 准备；需要 Node addon、原生 QuickJS 及测试工程上述原生构建闭包，不能以过时模块 bundle 或 DTO 替身替代。沿用 `MEOWFILM_TEST_NODE`、`MEOWFILM_TEST_DATA_ROOT`、`MEOWFILM_TEST_RUNTIME_ROOT`；既有 bundle 根为 `tmp/test-results/film-search/bundles`，应复用当前副本而不是另造轮次目录。冻结解除后，在前节同一环境和构建输出上：

```powershell
# 在副本根目录准备真实 bundle；随后从 apps\windows 使用前节 $dll。
$env:MEOWFILM_TEST_RUNTIME_ROOT = Join-Path $root 'tmp\test-results\film-search\bundles'
& $env:MEOWFILM_TEST_NODE (Join-Path $root 'scripts\windows\prepare-test-bundles.mjs') $env:MEOWFILM_TEST_RUNTIME_ROOT
if ($LASTEXITCODE -ne 0) { throw 'Runtime bundle preparation failed' }
```

```text
--group FilmMetadataRuntimeTests RuntimeImageTests FilmMetadataConsumerTests
```

### `FilmMetadataRuntimeTests`

测试经过实际 Windows entry、严格 manifest／describe、`NodeRuntime`、App epoch owner、`FilmMetadataService`、共享搜索 JS、`ArtworkService`／`ArtworkImage` 和真实 `FilmSettingsView`。本机可控 TCP HTTP 上游只返回合成 JSON／PNG 和假凭据；必要覆盖包括：

- 原 query／page1／zh-CN、movie／tv 完整 append／original 请求、完整原 payload、未知字段、真实错误／回退及无隐式重试。
- 同 key 并发完整详情在 Node 合并，类型／query／append／imageSize 不同仍保持请求身份隔离；普通取消后迟到完整响应可入 Node 缓存，不能误写成应立即断开上游。
- 设置保留／替换／明确清除、空字符串／空格草稿与失败重试，重启真实 Node 验证持久恢复；配置撤权等待旧 producer 物理排空，关闭等待上游断连及实际 Node 进程退出。
- 图片的原生完整解码、代理变更后的稳定键、尺寸独立、重启后读取与独立取消／关闭排空。

设置／图片通过隐藏 `HwndSource`（无 `WS_VISIBLE`，有 `WS_EX_NOACTIVATE`）呈现，不使用 `Window.Show`、系统输入或前台焦点，**不要求活动交互桌面**，仍需能创建 WPF HWND 的 Windows 会话。保存实际原生渲染至：

```text
TestWorkspace.DataPath("film-search", "tmdb-artwork.png")
TestWorkspace.DataPath("film-search", "tmdb-settings-wide.png")
TestWorkspace.DataPath("film-search", "tmdb-settings-narrow.png")
```

每张输出须小于 700KB，实际绝对位置由 `TMDB_PREVIEW` 输出；未执行前不能声称已有截图或 Windows 通过。

### `RuntimeImageTests`

`RuntimeImageTests.cs` 不启动 Node、不依赖 bundle；真实 `RuntimeHttpClient` 对本机 TCP HTTP 交换，实际 Artwork 消费失败／成功流。覆盖无认证凭据与准确请求 ID、非法引用不连接、重定向／响应 ID／MIME 拒绝、4 MiB 边界、截断失败、不污染缓存、稳定相对键、显式重试及取消／关闭物理释放。普通 JSON 的 HTTP 502 拒绝为 `INVALID_RESPONSE`，仅图片错误分支接受 502 并解包安全错误码。

缺尾用例发送准确 `Content-Length` 的完整 PNG 去掉整个 IEND，不能由 HTTP 截断检查代替；也覆盖 JPEG／GIF 结束标记。真实 Artwork 必须返回 `ARTWORK_FORMAT`、不缓存／不自动重试；同一相对引用显式重试完整图片才成功，并随后命中有效缓存。上述真实 Node 错误恢复场景同样覆盖 PNG 去 IEND 后的失败与恢复。

### `FilmMetadataConsumerTests`

不启动 Node、不依赖 bundle；真实统一搜索页、Shell 与 FilmCapability，metadata runtime 使用可控连接替身。覆盖失败保存不失效、成功保存不自动取数、同词新查询向 Node 重新确认新鲜度、旧详情快照／迟到发布隔离，以及内容和 cache receipt 来自准确 producer、不伪造原生缓存凭证。

consumer 沿用无激活离屏 Shell 流程，无真实输入／活动桌面要求。关闭还须等待已受理设置写入的真实回执；强制释放先撤销 owner 的保存观察者，搜索已关闭后到达的写回执正常结算，不触碰已释放 CTS。此消费者结算与 Node producer 物理排空分别验证。

### 必要旧回归与传输增量

```text
--group RuntimeHttpClientTests AppRuntimeClientTests RuntimeRecoveryTests ArtworkTests ArtworkFailureTests
--group FilmSearchTests SearchResultsConsumerTests FilmSearchPresentationTests
--group FilmPlaybackContractTests FilmPlaybackRecoveryTests
--case "App uses its single current storage identity without product selection"
--case "native-runtime: App real Node/ACL/PID handshake preserves aggregate identity"
--case "native-film-runtime: cold start restores enabled cats and four options without executing disabled scripts"
--case "film-playback: unseeded route is empty and explicit wire cards preserve movie or series identity"
--case "film-presentation: more opens data while all cards open the playback page"
```

其中 real Node 两项依赖同一七模块 bundle／addon，其余依赖判断见各组说明。`RuntimeHttpClientTests` 保留精确模块／版本／contract／operation 的拒绝校验；`AppRuntimeClientTests` 覆盖取消图片等待物理字节结束、epoch 关闭隔离迟到图片和清空返回字节，不以放宽握手或固定旧方法数量消除失败。

纯传输／Artwork 无产品窗口或焦点要求；上列两项既有播放／呈现回归继续使用 `ShowActivated=false` 的屏幕外窗口。以上定向范围不是全量矩阵，不重跑无关音乐真实网络测试。App 需正常增量构建，包装／共享门禁由对应 owner 统一安排。本块没有执行构建、测试或远端操作；真实公网配置与账号权限仍未覆盖。

## 实现位置与交付边界

目录相对仓库根；共同规则仍只以共同文档为准，下面是当前职责索引，不是历史提交／通过报告。

| 职责 | 文件 | 必要影响 |
| --- | --- | --- |
| 装配 | `apps/windows/runtime/entries/aggregate.mjs`；`apps/windows/src/MeowFilm.AppHost/AppIdentity.cs` | 正式七模块、精确 descriptor；公共默认三模块不变 |
| 页面／导航 | `apps/windows/app/Program.cs`；`apps/windows/src/MeowFilm.AppHost/AppLauncher.cs`；`apps/windows/src/MeowFilm.Capabilities/Search/SearchResultsPage.cs`；同 Capabilities 的 `Film/FilmCapability.cs`；`apps/windows/src/MeowFilm.Desktop/` 导航文件 | 共用搜索页、原音乐 owner、真实播放接收、草稿／历史位置与可达页面保留 |
| 原生搜索 | `apps/windows/src/MeowFilm.Capabilities/Film/Search/` 下 `FilmSearchContracts.cs`、`FilmSearchSession.cs`、`FilmSearchService.cs`、`FilmSearchDataClient.cs`、`FilmSearchPlayback.cs`、`FilmMetadataService.cs`；同能力 `Sources/FilmSourceService.cs` | 共享原生规则、分页／投影、统一 Node 取数、producer receipt、typed 交接与设置适配 |
| 播放交接 | 同 Film 目录 `FilmPlaybackPage.cs`、`FilmPlaybackOpen.cs`、`FilmPlaybackSession.cs`、`FilmPlaybackModels.cs` | 准确来源范围、跨 owner hints、group／search.update、当前 target 重进；实际媒体边界见[播放页](film-playback.md) |
| 图片／设置 | `apps/windows/src/MeowFilm.Core/Runtime/`；`apps/windows/src/MeowFilm.Platform/Runtime/NodeRuntime.cs`；`apps/windows/src/MeowFilm.Desktop/Artwork/`；同 Film 的 `Presentation/FilmSettingsView.Metadata.cs` | 图片请求相关性／预算／稳定键与关闭，设置脱敏、保留／替换／清除；详见[图片](artwork.md) |
| 构建／资源 | `apps/windows/src/MeowFilm.Capabilities/MeowFilm.Capabilities.csproj`；`apps/windows/src/MeowFilm.Platform/MeowFilm.Platform.csproj`；`apps/windows/src/MeowFilm.Media/MeowFilm.Media.csproj`；`scripts/windows/verify-app-structure.mjs` | 搜索内嵌资源、原生 QuickJS 与锁定 VideoKernel 正常构建闭包，不降低源码／打包门禁 |

本次只同步本文说明及工作记录，未改源码、测试或断言，也未运行上述入口。当前生产／测试整合、Windows 增量构建、原生消费者和真实媒体结果由对应 owner 最终交付；真实公网配置与账号权限仍未覆盖。
