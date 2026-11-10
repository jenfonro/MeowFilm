# Windows 影视搜索接线与验证入口

行为以[共同影视搜索契约](../common/film/search.md)为唯一来源；共享 JSON 入口与参考来源见 [`packages/film-search/README.md`](../../packages/film-search/README.md)。本文件只记录 Windows 实现与接通边界。

## 类型与装配

- App 通过 `AppLauncher.createPages` 装配一个 `SearchResultsPage`，实现 Desktop 的中性 `IDesktopSearchPage`；Desktop 不引用影视业务。结果页将原有 `MusicView` 暂时挂入 `MusicHost`，没有第二份音乐请求、搜索协调器或音乐视图实例。
- `MusicCapability.SearchOnlineAsync` 是在线综合入口；独立曲库搜索原入口保留。`SearchResultsPage` 拥有四分类、模式与滚动投影，影视状态仍由 `FilmSearchService`／`FilmSearchSession` 持有。
- `FilmSourceService` 实现 `IFilmSearchSources`，在读取当前 Cat 状态并等待已有 starting 结算后选取可搜索源；`CurrentSearchSources` 是无 I/O 的当前可用集合。实际请求通过既有 `CatScriptTransport` 发出 `search`，携带原 `wd` 和本源 `page`，并检查调用前后的安装／代次。
- Capabilities 将共享文件嵌为 `MeowFilm.FilmSearch.js`，使用 `NativeJavaScriptJsonEngine` 执行；不经 Node、HTTP runner 或另一份 C# 搜索算法。正则来自既有 `FilmRuleService` 已保存快照。MSBuild 继续构建并复制原生 `meowfilm_js.dll`。
- `FilmMetadataService` 实现 `IFilmSearchMetadata`，实际 App 注入同一 runtime 的 `tmdb.data.request`；首次综合搜索与电影／剧集详情使用 `zh-CN`、`w500`。当前平台 entry／manifest／原生 describe 严格装配 music／cat／source／tmdb／netease 五模块。原始 JSON 保留，空凭据仍正常请求显式配置代理；失败不伪造空成功。
- `IRuntimeImageClient` 经当前会话读取 `/v1/tmdb/images/...`，由 AppHost 注入 `ArtworkService` 中性可信字节口；相对引用就是缓存键，保持原生 4 MiB 编码图片与解码／像素预算，普通公网 `ArtworkHttp` 限制不变。
- 生产端点／凭据来自运行器配置；没有读取其他任务私密数据。真实公网联调仍需用户提供可用配置或授权来源，以下合成 HTTP 真实运行器用例不代表公网通过。

## 生命周期与真实播放接收

`FilmSearchService` 是 UI dispatcher owner。每源游标、两个投影及上一已结算轮的位置都保存在会话；切换分类／模式只重投影。来源集合或代次变化撤销当前 lease，同词再次进入先排空旧 Work，再为同一会话建立新 CTS，保留仍有效的成功页；取消关闭准备也按此恢复，只补未确认页和中断的元数据工作。

新词使旧会话退役；旧 Work 结算后移出排空跟踪、取消服务订阅并释放 CTS，不用条数上限或 TTL。提交不同音乐关键词也退休旧影视会话。实际详情仍可持有其会话数据。

TMDB detail flight 使用独立 CTS 与读者归属，成功缓存按类型／ID 索引；TMDB 设置成功保存时取消旧会话／flight 并清空共享详情复用资格，旧 Context 数据仍由原详情持有；最后有效读者离开才取消物理请求。关闭准备取消所有 flight，并等待真实 detail drain 后才释放引擎。已取得的原 search payload、有效查询与原 detail 均保留，不以显示字段替代交接数据。

App 将点击交给 `FilmCapability.ReceiveSearchPlayback`，再通过 Shell 的受控入口进入既有 `film-playback`。`FilmPlaybackPage.FromSearch` 保留 `FilmSearchContext`，不调用 `FilmPlaybackPreview`。Context 区分原点击 `Item`、当前选择 `CurrentItem` 和最终承载 `CarrierItem`；聚合视图按共享输出的完整来源成员身份跟随迟到 TMDB 归并，不按标题重新搜索。原始列表的 `site:` 选择始终只有原单源，`CarrierItem` 仍为该单源，`Metadata` 为空；会话原 payload 仍保留，但不能被播放页冒用为该 raw 条目的 TMDB 简介。来源 generation 改变后，旧选择不跟随替换代次的承载卡。

结果页与播放接收点均核对同一会话及完整原 item，不以相同展示字段或 displayKey 判定同一选择。新代次／成员内容产生独立播放页和不透明导航身份，旧历史页保持原选择；完全相同的再次点击仍复用当前页。`FilmCapability` 实现中性 `IDesktopNavigationRetention`：保留当前 `Playback` 及 Shell 实际 history／origin 等位置可达的搜索页，其他搜索页从缓存删除并解除会话订阅。Q1 返回结果后再点击 Q2，不保留已经无入口的 Q1；仍在历史或搜索 origin 的 Q1 可按原位置恢复。没有新增条数限制或 TTL，原首页预览缓存不变。未知剧集、推荐和地址保持空／不可用。

公共页的输入草稿独立于原模块草稿；播放返回恢复未提交输入和原结果查询，重复点击不重新搜索。Shell 保存原模块不透明位置，退出公共结果页恢复原业务页，而非音乐内部的三分类投影。

实际结果页无卡且全源失败时显示“搜索不可用”，包括 TMDB 已成功返回空的情况；显式加载更多仍可重试失败原页，单源失败不增设提示。

## 定向测试清单

原搜索三组共 **28 cases**，由测试程序集自动发现，无需手动注册。

### `FilmSearchTests`（15）

Case 精确名称统一为 `film-search: ` 加下列文本：

```text
complete pages exceed twelve and one hundred with independent cursors
missing page counts repeated identities and silent explicit same-page retry
first-round late ranking and later-round aggregate upgrades freeze both orders
TMDB raw fallback boundaries preserve original payload and effective query
TMDB details cache by type and ID and more never repeats metadata
new-query cancellation fences noncooperative old results
retired physical work releases owner tracking while playback retains its snapshot
all source failures remain retryable and distinct from successful empty results
source generation replacement recovers the same query without refetching retained pages
prepare resume retains completed pages and metadata with a fresh lease
prepare resume recovers only interrupted source pages and missing metadata details
hanging detail receives cancellation and physically drains before engine close
cross-query detail flights cannot inherit another query cancellation or cache late data
in-flight playback follows original members into a late TMDB carrier
real Cat transport selects enabled searchable sources and sends original query and page
```

真实执行内嵌搜索 JS／原生 QuickJS，来源及 TMDB 使用替身；最后一项复用 `FilmSourcesTests.SearchWire`，经过真实 Cat transport 编解码及来源服务，但没有真实 Node 宿主。

### `SearchResultsConsumerTests`（11）

Case 精确名称统一为 `music-ui: ` 加下列文本：

```text
unified search music submissions from library and same-word tabs always open online overview
unified search shell target defaults manual draft refresh and result tabs remain synchronized
unified search real playback keeps second-round sources and returns without another query
unified search in-flight navigation keeps late TMDB members and full metadata
unified search raw playback keeps one source and no TMDB overview
unified search unchanged display after generation replacement opens the new identity
unified search page distinguishes all-source failure from successful empty results
unified search unreachable playback releases its cache and session observer
unified search playback restores retained history origin and current identities
unified search music card navigation leaves the projected view and back restores it
unified search rapid tab changes and page exit cannot remount stale results
```

使用真实 Shell、Music／Film capability、导航接收点与 WPF 路由事件；音乐 HTTP 和脚本接口为既有替身。generation 用例覆盖普通卡与 TMDB 承载卡的相同展示字段替换，并恢复仍在真实历史的旧页；释放用例同时检查 owner 缓存数及实际 `Session.Changed` 订阅数。执行时输出 `EVIDENCE film-search unreachable playback ...` 和 `EVIDENCE film-search navigation retention ...`，尚无本轮原生执行回执。

### `FilmSearchPresentationTests`（2）

Case 精确名称统一为 `music-ui: ` 加下列文本：

```text
film search existing cards render ordinary aggregate TMDB and long titles at narrow widths
film search raw aggregate round trips preserve order scroll and request counts
```

在真实搜索页和既有 `FilmCard` 上检查 1110／860 宽度、长标题、三种标签、文本角标、首页原评分／激活／294 高度，以及两个模式的顺序／独立滚动。使用隐藏 `HwndSource`（无 `WS_VISIBLE`，有 `WS_EX_NOACTIVATE`），不显示或激活窗口、不使用系统输入。第一项保存实际产品离屏渲染至 `TestWorkspace.DataPath("film-search", "cards.png")`，输出 `EVIDENCE film-search cards <absolute-path>`；查看前裁切或压缩至小于 700KB。

### 必要既有消费者回归

仅选择受本次空剧集兼容／页头改动影响的入口，不默认运行全部 UI 组：

| Group | 精确 Case |
| --- | --- |
| `FilmPlaybackTests` | `film-playback: direct preview and movie or series cards need no detail requests` |
| `FilmPresentationTests` | `film-presentation: more opens data while all cards open the playback page` |

两项使用 `Window.Show()` 的既有屏幕外、`ShowActivated=false` 场景，需要可创建窗口的 Windows 会话，但没有真实输入／桌面激活依赖；不是新增三组的纯隐藏 HWND 路径。未选入的旧悬停／焦点用例不能据此宣称无桌面依赖。

## 执行依赖与当前证据边界

- 新三组以及上表两项均**不需要 runtime 测试 bundle 或 Node addon（仅指此处原搜索三组，不包括下节新增真实 TMDB 组）**。测试进程入口仍要求一个真实绝对 Node 路径，这是 `TestWorkspace.Initialize` 的统一前置校验，不代表这些用例启动 Node。新消费者显式注入 `QualityFixture.ScriptRuntime`，不会使用 `SearchScene.Runtime` 的延迟宿主。
- 新三组**不要求活动交互桌面**：STA／Dispatcher 与 WPF／原生 QuickJS 环境必需，但不 Show 产品窗口、不抢焦点、不使用 `SendInput`。截图也来自隐藏离屏 HWND。
- 保持正常增量缓存；不要调用默认会准备完整 bundle 的 `scripts/windows/test.ps1` 来替代上述依赖判断。主会话在最终集成终审后可直接按锁恢复、增量构建、执行 DLL：

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
  dotnet $dll --case 'film-playback: direct preview and movie or series cards need no detail requests'
  if ($LASTEXITCODE -ne 0) { throw 'Preview regression failed' }
  dotnet $dll --case 'film-presentation: more opens data while all cards open the playback page'
  if ($LASTEXITCODE -ne 0) { throw 'Presentation regression failed' }
  dotnet restore app\MeowFilm.Windows.App.csproj --locked-mode --configfile NuGet.Config
  if ($LASTEXITCODE -ne 0) { throw 'App locked restore failed' }
  dotnet build app\MeowFilm.Windows.App.csproj -c Release --no-restore --nologo -nodeReuse:false
  if ($LASTEXITCODE -ne 0) { throw 'Incremental App build failed' }
} finally { Pop-Location }
```

本阶段未执行 Windows 构建、原生测试或远端操作；以上是已编写的验证入口，不是通过报告。共享算法 Node 对照由主会话独立持有，不能替代 Windows 原生证据。工程资源静态检查入口为 `node scripts/windows/verify-app-structure.mjs`，现在也输出 EmbeddedResource 清单。

## 本次变更索引与交付边界

以下路径均相对仓库根目录，列出原搜索块生产／测试／文档／脚本文件；真实 TMDB 增量索引见本文末，工作记录另为 `tmp/working/film-search/windows-progress.md`。

| 验收 | 目录与文件 | 实际影响 |
| --- | --- | --- |
| 1、7、8 | `apps/windows/app/Program.cs`；`apps/windows/src/MeowFilm.AppHost/AppLauncher.cs` | 公共页装配、实际播放导航、App 资源释放 |
| 1、8 | `apps/windows/src/MeowFilm.Desktop/` 下 `DesktopHeader.cs`、`IDesktopModule.cs`、`IDesktopPage.cs`、`IDesktopSearchPage.cs`、`ShellWindow.Header.cs`、`ShellWindow.Navigation.cs`、`ShellWindow.Pages.cs`、`ShellWindow.xaml`、`ShellWindow.xaml.cs` | 中性目标、视图投影、草稿、请求准入、返回位置与导航保留通知；没有影视业务依赖 |
| 1、8 | `apps/windows/src/MeowFilm.Capabilities/Search/` 下 `SearchResultsPage.cs`、`SearchResultsPage.xaml`、`SearchResultsPage.xaml.cs` | 四分类结果页和影视投影；音乐复用原 owner |
| 1 | `apps/windows/src/MeowFilm.Capabilities/Composition/MusicNavigation.cs`；`apps/windows/src/MeowFilm.Capabilities/Presentation/` 下 `MusicView.xaml`、`MusicView.xaml.cs` | 公开在线综合入口、复用时隐藏旧标题及三分类、原视图归还 |
| 3–8 | `apps/windows/src/MeowFilm.Capabilities/Film/Search/` 下 `FilmSearchContracts.cs`、`FilmSearchSession.cs`、`FilmSearchService.cs`；`apps/windows/src/MeowFilm.Capabilities/Film/Sources/FilmSourceService.cs` | 真实 Cat 调用、原生共享规则、分页／批次、TMDB 注入、会话恢复及物理排空、完整交接 |
| 2、7、8 | `apps/windows/src/MeowFilm.Capabilities/Film/` 下 `FilmCapability.cs`、`FilmHomeModels.cs`、`FilmPlaybackPreview.cs`；其 `Presentation/` 下 `FilmCard.xaml`、`FilmPlaybackView.xaml`、`FilmPlaybackView.xaml.cs`、`FilmView.xaml.cs` | 既有卡片补字段、真实搜索接收与空剧集安全、影视默认目标；首页预览入口保留 |
| 5、10 | `apps/windows/src/MeowFilm.Capabilities/MeowFilm.Capabilities.csproj`；`scripts/windows/verify-app-structure.mjs` | 共享搜索资源装配、实际资源路径与清单门禁 |
| 1–8 | `apps/windows/tests/MeowFilm.Windows.Tests/` 下 `FilmSearchTests.cs`、`FilmSourcesTests.Search.cs`、`SearchResultsConsumerTests.cs`、`FilmSearchPresentationTests.cs` | 上述 28 项原生定向用例；尚未执行 |
| 1、2、7 | 同测试目录 `FilmPlaybackTests.cs`、`FilmPresentationTests.cs`、`FilmSourcesTests.cs` | 页头契约预期、可空类型适配、复用既有来源替身 |
| 9、10 | `docs/windows/` 下 `app-structure.md`、`desktop-shell.md`、`film-home.md`、`film-playback.md`、`film-search.md`、`music-search.md` | 本端装配事实、共同契约引用、精确测试清单与未覆盖边界 |

首版交付的本地静态输出（不包含本轮四项返修）：`git diff --check` 通过；工程结构检查列出 182 个相对导入、38 个资源引用及两个内嵌 JS 资源；6 个变更 XML 均可解析；当时 23 项新增 case 与文档清单一致，变更文档链接存在。新搜索链的 12／100 条截断、后续固定 `page:1` 与假预览引用检索为零；页码只在游标初始化为 1，排序仍交给共享资源。共享文件只读核对 SHA-256 为 `fc9c156af9845625cade5b6fcfc8ba52def5941ff87d2f053058287effdaa389`。原始输出在首版交付会话的静态检查工具回执 `1a1111`，不冒充本轮或 Windows 执行证据。

没有删除用例。既有预期变更逐项为：播放页页头、数据页页头的 `SearchVisible/SearchEnabled` 从关闭改为开启，并新增 `SearchTargetId:film`；原预览的三处可空调用适配仍保留原断言，其中一处明确缺集即抛错。`FilmSourcesTests` 仅改为 partial 以复用真实传输替身。本轮按主会话明确待修纠正 `LateCarrier` 的错误理解：raw `Sources.Count` 从 2 改为 1，`CarrierItem.sourceKind` 从 `tmdb` 改为 `site`，新增 `Metadata == null`；保留原 sourceKind／installation／detail 身份断言，并补 generation／siteKey／displayKey／原 Item 保持的断言。没有其他预期修改或测试削弱。

### 累计终审四项返修增量

| 待修／验收 | 本轮文件（沿用上表目录） | 可观察结果／覆盖 |
| --- | --- | --- |
| 1／7 | `FilmSearchContracts.cs`；`FilmSearchTests.cs`；`SearchResultsConsumerTests.cs` | raw 永远单源无 TMDB 简介；迟到聚合跟随继续保留；真实播放空简介断言 |
| 2／7、8 | `FilmSearchContracts.cs`、`FilmSearchSession.cs`、`SearchResultsPage.cs`、`FilmCapability.cs`、`FilmPlaybackPreview.cs`；`SearchResultsConsumerTests.cs` | 完整 item 变化替换卡片与新页；相同展示的新 generation 进入新 Context；旧普通／TMDB 详情不串代，实际历史可恢复，无额外元数据请求 |
| 3／3、6 | `SearchResultsPage.cs`；`SearchResultsConsumerTests.cs` | 实际页面全源失败＋TMDB 成功空显示不可用；单源静默显式 more、成功空另有断言 |
| 4／8 | `IDesktopModule.cs`、`ShellWindow.xaml.cs`、`FilmCapability.cs`、`FilmSearchSession.cs`；`SearchResultsConsumerTests.cs` | 中性导航位置集合；Q1/Q2 不可达页移除缓存和观察者；当前／history／origin 身份恢复 |
| 必要说明 | `docs/windows/film-search.md`、`desktop-shell.md`、`film-playback.md`；Windows 进度文件 | 本端类型与接通事实、精确用例及预期纠正；不改共同契约 |

本轮完整增量静态终审的源码／真实交互链回执为 `46e2ae`、`9cf3fb`，文本核对回执为 `62a0ce`：12 个生产／测试／文档增量文件无行尾空白；15＋11＋2 个用例名称与文档逐项一致；3 篇文档本地链接存在；旧 session＋displayKey 缓存键、raw 扩大为 2 的预期、被 TMDB 状态掩盖的全源失败条件均检索为 0；Desktop 中性边界无影视标识；共享 JS hash 未变。不运行构建、测试、远端或 Git；有效原生证据仍由主会话冻结最终输入后统一取得。

需供另一端关联核对的边界：已取消同词会话必须更新 lease 而非无条件返回；在途详情必须取消并物理排空，不能继承别的查询取消归属；普通组被迟到 TMDB 吸收后 Context 必须沿原成员跟随；新启用来源需使捕获的来源集合失效；已退役会话需移出服务跟踪。Windows 的 WPF 视图归还、草稿隔离及隐藏 HWND 渲染适配属于本端实现，没有跨写 iOS 或改共享规则。

## TMDB 真实接通增量测试

以下为已编写、**本块尚未执行**的准确入口。`FilmMetadataRuntimeTests` 必须使用当前 Windows 五模块 entry 的真实 bundle（目录下的 `aggregate`），由主会话正常 bootstrap／ensure-runtime-native 后准备；需要 Node addon 与原生 QuickJS DLL，不能以旧三／四模块 bundle 或 DTO 替身替代。沿用 `MEOWFILM_TEST_NODE`、`MEOWFILM_TEST_DATA_ROOT`、`MEOWFILM_TEST_RUNTIME_ROOT`；固定 bundle 根由主会话使用 `tmp/test-results/film-search/bundles`。

```text
--group FilmMetadataRuntimeTests RuntimeImageTests FilmMetadataConsumerTests
```

### `FilmMetadataRuntimeTests`（4）

完整 case 为 `film-tmdb-runtime: ` 加以下文本：

```text
real App adapter search detail artwork and restart preserve stable identities
source failures stay errors and failed detail preserves complete original search metadata
native settings preserve replace clear failed drafts and restore after restart
cancelled metadata and artwork shutdown drain the real Node upstream
```

测试经过实际 Windows entry、严格 manifest／describe、`NodeRuntime`、App epoch owner、`FilmMetadataService`、共享搜索 JS、`ArtworkService`／`ArtworkImage` 和真实 `FilmSettingsView`。本机可控 TCP HTTP 上游只返回合成 JSON／PNG 和假凭据，断言原 query／page1／zh-CN、movie/tv 详情路径、w500、完整 payload、真实错误／回退及无隐式重试；检查设置的空字符串及全空格草稿保留原凭据／替换／明确清除／失败草稿重试，并重新启动真实 Node 验证持久恢复。图片检查原生解码、代理变更后的稳定键、尺寸独立和重启后仍可读取；取消／关闭检查上游断连及实际 Node 进程退出。不会连接真实公网或读取用户数据。

设置／图片通过隐藏 `HwndSource`（无 `WS_VISIBLE`，有 `WS_EX_NOACTIVATE`）呈现，不使用 `Window.Show`、系统输入或前台焦点，**不要求活动交互桌面**，仍需能创建 WPF HWND 的 Windows 会话。保存实际原生渲染至：

```text
TestWorkspace.DataPath("film-search", "tmdb-artwork.png")
TestWorkspace.DataPath("film-search", "tmdb-settings-wide.png")
TestWorkspace.DataPath("film-search", "tmdb-settings-narrow.png")
```

每张输出须小于 700KB，实际绝对位置由 `TMDB_PREVIEW` 输出；未执行前不能声称已有截图或 Windows 通过。

### `RuntimeImageTests`（11）

不启动 Node、不依赖 bundle；真实 `RuntimeHttpClient` 对本机 TCP HTTP 交换，实际 Artwork 消费失败／成功流。普通 JSON 的 HTTP 502 拒绝为 `INVALID_RESPONSE`，仅图片错误分支接受 502 并解包安全错误码。完整 case 为 `runtime-image: ` 加以下文本：

```text
relative reference sends exact session auth and unique request IDs
absolute external network traversal and query references never connect
ordinary JSON rejects 502 while image failures preserve safe codes
redirect wrong response ID and wrong MIME cannot become bytes
content length and chunked reads enforce the existing four MiB boundary
truncated bytes fail without retry or successful artwork caching
artwork rejects invalid response IDs and nonimages without poisoning cache
complete HTTP images missing format terminators fail artwork and explicit same-reference retry succeeds
stable artwork cache survives endpoint changes without authenticating public URLs
caller cancel and transport close physically dispose unfinished streams
artwork close cancels its trusted reader and waits for physical settlement
```

缺尾用例发送准确 `Content-Length` 的完整 PNG 去掉整个 IEND，不能由 HTTP 截断检查代替；也覆盖 JPEG／GIF 结束标记。真实 Artwork 必须返回 `ARTWORK_FORMAT`、不缓存／不自动重试；同一相对引用显式重试完整图片才成功，并随后命中有效缓存。上述真实 Node failures case 同样覆盖 PNG 去 IEND 后的失败与恢复。

### `FilmMetadataConsumerTests`（3）

不启动 Node、不依赖 bundle；真实统一搜索页、Shell 与 FilmCapability，metadata runtime 使用可控替身。完整 case 为 `music-ui: ` 加以下文本：

```text
TMDB save retires search only after success and same-word playback uses a fresh session
TMDB save cancels old details without allowing late data to seed a new query cache
TMDB admitted settings save drains before close and never restores a revoked search
```

新 consumer 仍使用原有无激活离屏 Shell 流程，无真实输入／活动桌面要求。它们核对失败保存不失效、成功保存不自动取数、同词新会话／新详情、旧播放快照不串数据、迟到 detail 无共享缓存资格、关闭等待已受理写入的真实回执；强制释放先撤销 owner 的保存观察者，搜索已物理关闭后到达的写回执仍正常结算、不触碰已释放 CTS。

### 必要旧回归与传输增量

```text
--group RuntimeHttpClientTests AppRuntimeClientTests RuntimeRecoveryTests ArtworkTests ArtworkFailureTests
--group FilmSearchTests SearchResultsConsumerTests FilmSearchPresentationTests
--case "App uses its single current storage identity without product selection"
--case "native-runtime: App real Node/ACL/PID handshake preserves aggregate identity"
--case "native-film-runtime: cold start restores enabled cats and four options without executing disabled scripts"
--case "film-playback: direct preview and movie or series cards need no detail requests"
--case "film-presentation: more opens data while all cards open the playback page"
```

其中 real Node 两项依赖同一当前五模块 bundle／addon，其余依赖判断见各组原说明。`AppRuntimeClientTests` 新增准确 case：

```text
app-runtime: cancelled image waits for physical bytes and clears rejected pixels
app-runtime: shutdown epoch fences a late image and clears its returned bytes
```

TMDB 阶段的 `RuntimeHttpClientTests` 新增 `runtime-http: description rejects ` 前缀下的 `missing TMDB module`、`TMDB old version`、`TMDB wrong contract`、`TMDB missing operation`，成功 describe 方法数从 32 更新为 35。App 身份与所有实际 runtime 测试夹具当时由三模块更新为四模块，这是验收 11 的必要预期调整；以下文件／验收映射仍记录该阶段。当前网易 token 接入后精确五模块、36 个方法，见[App 运行器](app-runtime.md)；既有失败、取消、资源、来源、音乐和 UI 断言不删除、不放宽。

纯传输／Artwork 无产品窗口或焦点要求；上列两项既有播放／呈现回归继续使用 `ShowActivated=false` 的屏幕外窗口。以上定向范围不是全量矩阵，不重跑无关音乐真实网络测试。App 需正常增量构建，包装／共享门禁由对应 owner 统一安排。本块没有执行构建、测试或远端操作；真实公网配置与账号权限仍未覆盖。

### TMDB 增量文件／验收映射

目录均相对仓库根；未改公共运行器默认 entry、共享规则或包装脚本。

| 验收 | 文件 | 必要影响 |
| --- | --- | --- |
| 11 | `apps/windows/runtime/entries/aggregate.mjs`；`apps/windows/src/MeowFilm.AppHost/AppIdentity.cs` | 同一 App runtime 显式四模块，原 dataRoot 与 source 平台保持 |
| 11、15 | `apps/windows/src/MeowFilm.Core/Runtime/` 下 `RuntimeHttpProtocol.cs`、`RuntimeHttpClient.cs`、`IRuntimeClient.cs`、`AppRuntimeClient.cs`；`apps/windows/src/MeowFilm.Platform/Runtime/NodeRuntime.cs` | 精确 describe／契约；受信图片验证、预算、认证、失败、取消／epoch／排空 |
| 15 | `apps/windows/src/MeowFilm.Desktop/Artwork/` 下 `ArtworkService.cs`、`ArtworkRaster.cs`；`apps/windows/src/MeowFilm.AppHost/AppLauncher.cs` | 中性字节注入、稳定键及原生完整容器／解码／资源 owner；公网限制不变 |
| 12–14 | `apps/windows/app/Program.cs`；`apps/windows/src/MeowFilm.Capabilities/Film/Search/FilmMetadataService.cs`、`FilmSearchService.cs`；同能力 `FilmCapability.cs`；`apps/windows/src/MeowFilm.Capabilities/Search/SearchResultsPage.cs` | 真实 adapter、最小设置 owner、成功保存失效、旧详情保留／迟到隔离／关闭观察者释放 |
| 13 | `apps/windows/src/MeowFilm.Capabilities/Film/Presentation/` 下 `FilmSettingsView.Metadata.cs`、`FilmSettingsView.xaml`、`FilmSettingsView.xaml.cs`、`FilmSettingsTheme.xaml` | 现有原生设置新增 TMDB，无密码回显／原生持久化，保留／替换／明确清除、失败重试 |
| 11–16 | `apps/windows/tests/MeowFilm.Windows.Tests/` 下 `FilmMetadataRuntimeTests.cs`、`RuntimeImageTests.cs`、`FilmMetadataConsumerTests.cs`、`RuntimeHttpClientTests.cs`、`AppRuntimeClientTests.cs`、`AppCompositionTests.cs` | 本节准确清单的新增测试，真实 HTTP／Node／Artwork／设置截图和消费者边界 |
| 11、15 | 同测试目录 `FilmSourcesTests.cs`、`MusicNativeRecommendationTests.cs`、`MusicNativeScriptTests.cs`、`MusicQqFeedbackIntegrationFixture.cs`、`RuntimeRecoveryTests.cs` | `IRuntimeSession` 图片接口的必要替身适配，未使用路径明确拒绝 |
| 15 | 同测试目录 `ArtworkTests.cs` | 原 WIC 合成图片 fixture 公开给新增真实 HTTP 消费测试，不改旧断言 |
| 11、14 | 同测试目录 `RuntimeTests.cs`、`FilmSourceRuntimeTests.cs`、`MusicOnlineTests.cs`、`MusicScriptIntegrationTests.cs`、`FilmSearchTests.cs`、`SearchResultsConsumerTests.cs` | 实际 bundle 夹具四模块；搜索夹具可注入真实 metadata owner |
| 16 | `docs/windows/` 下 `app-runtime.md`、`runtime-packaging.md`、`runtime-recovery.md`、`music-script-runtime.md`、`app-structure.md`、`film-settings.md`、`film-search.md`、`artwork.md` | 精确四模块装配、端侧事实、测试依赖与未覆盖边界，不重复共同规则 |

本增量没有删除用例或放宽有效断言；旧预期修改仅 App／fixture 模块集合三→四和成功 describe 方法数 32→35。既有 `IFilmSearchMetadata` 夹具参数泛化及新增图片接口不改变原失败／取消预期。
