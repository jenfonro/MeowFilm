# 一个 Windows App，按能力组织

共同规则分别见[页面数据请求](../common/pages/data-requests.md)和[音乐平台客户端上下文](../common/music/platform-client-context.md)。本文件说明 WinUI 3／.NET 的具体装配；模块化接入不等于把所有上下文初始化集中在 Accounts 或页面。

## 装配与依赖方向

Windows 全应用的显示投影由业务数据 owner 更新：内容回执、刷新、筛选、账号变化等决定栏目、卡片和列表成员。布局和滚动不重新筛选成员、不扫描整页 XAML 树以决定业务显示，也不触发刷新。到底分页仍按真实滚动意图请求尚未取得的下一页。几何、裁剪、命中区域、动画及绘制更新只处理呈现，不取得业务数据。

回执按稳定身份增量协调：音乐目录显示槽持有最新业务快照并保留栏目和卡片；影视栏目保留内容相同的条目；小说发现、详情和统一搜索局部更新文字、状态及受影响封面。共享 `ManagementList` 保留同 ID 的行状态和控件，选择只更新选择框，列变更只更新列定义；自定义 cells 以显式绑定 owner 身份复用。加载状态、选择及无关属性通知不清空集合或重建整页。真实运行与 `--debug` 模式核对集合通知、控件／像素身份、读取和释放次数，CPU／帧耗时仅作辅助诊断。

歌曲分页与同目标刷新保留存活 `SongRow`，重复歌曲按各自 occurrence 匹配，不去重或清空重填；仓库元数据沿现有 publication fence 更新。播放队列按 `EntryId` 增删移动并保留行对象，当前播放标记只通知前后受影响行。三行歌曲栏让每首歌曲成为原生虚拟列表的数据项，换列不更换资源 owner。

共享 `DesktopViewport` 以 WinUI `EffectiveViewportChanged` 提供首次资源准入，并观察实际祖先可见性、显式裁剪和 Canvas 位置变化；不订阅全局布局轮询。音乐与影视图片、音乐目录可选读取及小说封面复用该机制。当前挂载页面内已准入的资源在滚出后继续完成并复用，不因回滚重取租约、重解码或重建取色。小说正文按当前章节保留已看到的图片，仅更大解码宽度等真实输入变化替换像素。隐藏页面停止新准入、保留成功资源，并按 owner 契约处理未完成工作；虚拟列表以数据项稳定身份持有已准入资源，容器回收只借还视图引用；数据项删除、页面真正卸载、资源身份、owner 变化及关闭按对应生命周期释放并隔离迟到回执。这些边界没有用户开关，行为门禁覆盖首次准入、回滚复用、隐藏、退休和实际排空；具体资源预算见[公共图像](artwork.md)和[小说](novel.md)。

唯一入口是 `apps/windows/app/MeowFilm.Windows.App.csproj`，可执行文件为 `meowfilm.exe`。

```text
Windows App
 ├─ AppHost → Desktop / Platform
 ├─ Capabilities → Desktop / Platform / Audio / Media
 └─ SystemMedia
Audio / Media → Core / Threading
Desktop / SystemMedia → Core / Threading
Platform → Core
```

Core 不认识业务；基础设施不反向引用 App 或能力。一个窗口、一套导航与设置、一个原生播放协调、一个系统媒体会话。原生媒体基础设施由既有音频链和影视媒体共同使用；当前不装配下载能力。`SystemMedia` 只消费本模块的中性 `ISystemPlayback` 与 `WindowsSystemMediaBackend`，项目不引用具体播放工程；音乐／电影 typed 投影由 App 装配。

`scripts/windows/verify-app-structure.mjs` 检查 Windows 源码、公共 `apps/runtime`／`packages`、Windows／共享引擎工具和 `scripts/` 根层的共享 Node `.mjs` 工具，不按文件名登记，也不递归扫描整个 `scripts`。iOS 专属工具放在 `scripts/ios/`，专属模板只在其生成运行时中解析；Windows 源码或工具不能反向导入这些工具或模板。相对导入、链接安全、实际运行时入口与依赖和 WinUI 资源检查保持；EmbeddedResource 的实际路径、逻辑身份及清单也由此检查。公共与 Windows Node 运行器使用 `node:module` 的 `isBuiltin()` 识别内置模块，workspace 导入按包声明的公开 `exports` 和实际目标文件解析，新增公开子路径或被引用的 helper 无需逐文件登记。portable source-runtime 的无 Node／宿主全局边界仍由公共依赖检查负责，第三方脚本权限不因此改变。结构检查不解析 C#／JS 源码中的完整操作表；协议与方法行为由真实应用 `--debug` 模式实跑核对。Windows 构建输入指纹采用相同源码／工具范围及生成目录排除规则，并包含本端配置、共享引擎输入和随包许可；独立 iOS 代码与工具目录不影响该指纹，共享引擎工具仍按整个职责目录计入。

## 桌面壳与能力源码

`apps/windows/src/MeowFilm.Desktop` 按职责划分为 `Appearance/`、`Artwork/`、`Contracts/`、`Controls/`、`Dialogs/`、`Management/`、`MediaControls/`、`Navigation/`、`Shell/{Window,Coordinators,Views,Debug}/`、`Theme/`、`Viewport/`（资源入口为 `ms-appx:///MeowFilm.Desktop/Theme/Theme.xaml`，详细接线见[桌面壳](desktop-shell.md)）。

`apps/windows/src/MeowFilm.Capabilities` 根部按垂直领域与共享层收口为 6 个目录：

| 目录 | 责任 |
| --- | --- |
| `Music/` | 唯一音乐能力垂直域：`Composition/`（`MusicCapability`、`MusicNavigation`、`MusicRuntimeContext`、`MusicDialogs`、`MusicPlatformInfo`、`MusicNativeApplication`、`MusicDebugAdapter`）、`Accounts/{Owners,Protocol,Storage,Presentation}/`、`Catalog/`（含 `Presentation/` 及显式 `MusicCatalogContext`、`MusicCatalogSectionView`/`MusicCatalogCardView`）、`Feedback/`、`Library/{Owners,Storage}/`、`Lyrics/`、`Platforms/`、`Playback/`（含 `MusicPlaybackClock` 及声明式 `MusicFmPanelView`/`MusicSourcePickerDialogView`）、`PlaybackCache/`、`Recommendations/`、`Scripting/`（LX 音源脚本管理）、`Search/`、`Settings/`（含 `MusicSettingReceiptTracker` 与 `Presentation/MusicSettingsConverters.cs`）、`Sync/`、`Presentation/`（含 `MusicView`、`MusicContentMenus`、`MusicTheme.xaml` 与声明式 `MusicPlaylistNameDialogView`） |
| `Film/` | 影视垂直域：`Composition/`（`FilmCapability`、`FilmDebugAdapter`、`FilmRuntimeSettings`）、`Home/`、`Playback/`、`Library/`、`Rules/`、`Search/`、`Sources/` 与 `Presentation/{Assets,Controls,Dialogs,Settings,Views}/`（含声明式 `FilmEpisodeDirectoryView`、`FilmManualMatchDialogView`、`FilmDirectoryDialogView` 及全声明式 `FilmSettingsView` 表格单元模板） |
| `Novel/` | 小说垂直域：`Composition/`（`NovelCapability`、`NovelDebugAdapter`、`NovelWork`）、`Domain/`（书源配置、本地书架/历史/净化库、规则与会话）与 `Presentation/{Controls,Dialogs,Reader,Settings,Views}/`（声明式 XAML 页面及 Win2D／DirectWrite 原生阅读器） |
| `Pan/` | 内置网盘账号配置与声明式 `Presentation/` 视图 |
| `Search/` | 跨能力统一搜索页：`SearchResultsPage`、`SearchResultsPage.xaml(.cs)`（全声明式小说/影视/音乐结果投影）与 `SearchResultsDebugAdapter` |
| `Shared/` | 跨能力共享 UI 设施（对具体业务域零反向依赖）：`CapabilityFilePicker`（三能力统一 Windows App SDK 文件选择器）、`Accounts/`（含 `AccountLoginSurface`、`AccountSettingsTheme.xaml`、`QrCodeImage`）、`Controls/`（`CardMotion`、`Shelf`、`AdaptiveCardPanel`、`AspectRatioDecorator`、`SeekBar`、`SkipButton`、`UserScrollIntent`）、`Settings/`（`ISettingsPriorityItem`、`SettingsPriorityList`、`SettingsReorderDrag`/`Shift`、`SettingsLayout`、`SettingsTheme.xaml`）、`Theme/`（`CapabilityTheme.xaml`、`CardBrushes.xaml`） |

音乐能力彻底拆解为 9 个独立领域控制器（`MusicSettingsController`、`MusicAccountsController`、`MusicLyricsController`、`MusicPlaybackController`、`MusicQueueController`、`MusicDiscoveryController`、`MusicCatalogController`、`MusicSearchController`、`MusicLibraryController`）外加高频 `MusicPlaybackClock`，全部继承纯内核基类 `MusicController` 并通过 `MusicRuntimeContext` 与单向 DAG 显式构造注入协作，不存在 `host.<Controller>` 隐式服务定位器或中间镜像壳层；跨领域联动由 `MusicCapability` 装配根集中接线。`MusicCapability` 仅保留壳级装配与薄导航入口，各音乐视图直接绑定对应领域控制器且不持有 `MusicCapability`。跨多个职责的既有 partial 类型保持原公开 namespace，不为凑目录重新设计业务模型或复制 owner。中性控件包括 `CardMotion`、`Shelf`（通过显式依赖属性 `ItemsSource`/`MinimumCardWidth`/`HorizontalOffset`/`IsBanner` 驱动）、`OverlayScrollChrome`、`SeekBar`、`SkipButton`；主题键为 `Capability.*`，全局唯一可见性转换器为 `MeowFilm.Visible`（`MeowFilm.Desktop.VisibilityConverter`）。全应用共享样式字典统一在 `apps/windows/app/App.xaml` 单次合并（使用带库身份的 `ms-appx:///` URI，不重复合并或靠相对路径搜索回退），业务视图与弹层统一使用编译期 `{x:Bind}`，不使用 `XamlReader.Load` 或运行时业务 `SetBinding`。

Windows 呈现依赖精确锁定的 Windows App SDK，Shell、页面、播放器画面与覆盖内容处于同一 WinUI 合成树。`MeowFilm.Threading` 只封装 Windows App SDK `DispatcherQueue` 的 owner 调度，不依赖 XAML、Desktop 或业务。音视频核心当前使用 Core 与该调度机制，彼此保持独立 owner；阅读器的排版与绘制使用 Win2D／DirectWrite，不引用 WPF framework。工程边界按实际 MSBuild 求值图、App／AppHost／Capabilities 的职责目录、音视频 owner 及依赖项的 `UseWinUI` 声明核对，覆盖传递依赖；新增中性 helper 无需登记项目名全集。循环、重复引用、仓库逃逸、共享层反依赖业务、音视频交叉依赖及调度／播放核心引入 WinUI 呈现依赖仍拒绝。播放器输出及绑定／解绑边界见[原生媒体实现](media-playback.md)；编译、结构边界检查与 `--debug` 实跑分别记录，不互相冒充。

## 当前身份与唯一数据格式

音乐客户端本地初始化不依赖登录；应用明确连接客户端 owner 与账号／目录／播放，构造无 I/O、启动不批量登记。Windows 的文档与关闭次序见[平台客户端上下文](music-platform-client-context.md)。

`AppIdentity` 集中定义产品名称、当前数据/运行时身份和当前原生命名空间：

- 名称：`meowfilm`。
- 数据根为 `%LOCALAPPDATA%/meowfilm/aggregate`；应用内部结构化持久数据与可重建缓存索引物理分离于共享 SQLite 双库 `storage-v1/meowfilm-data.sqlite3` 与 `storage-v1/meowfilm-cache.sqlite3`（均固定 `user_version = 1`），原生客户端与 Node runtime 按 owner 职责连接对应数据库。凭据以永久库所属业务表中的明文 typed fields 保存；图片、音频、脚本源码等原始资源仍由各自 owner 管理文件目录。具体 schema 与 owner 见[本地存储](protected-storage.md)。
- 脚本协议/清单字段 `product` 等于 `aggregate`，是当前 wire 身份。
- 活动产品只读取和写入共享 SQLite schema 与各自原生 owner 定义的唯一当前格式。
- 当前提供可淘汰的播放缓存，未提供用户下载管理。
- 不兼容数据明确失败并保留原始内容。

可执行文件名、程序集名、源文件位置和界面资源键的变化都不等于持久化键变化。

## 主窗口启动门槛

`AppLauncher` 通过 WinUI `Application.Start` 建立唯一 UI 线程及其同步上下文，在创建首个启动窗口前安装 Desktop 的唯一排版默认，再完成模块装配、外观和窗口关闭偏好读取，依次等待实现 `IDesktopStartupParticipant` 的模块完成 `PrepareStartupAsync`，随后才构造 `ShellWindow` 并调用 `Activate()`。现有启动窗口承载等待、失败和重试；模块完整的 `InitializeAsync` 仍由桌面壳的选中模块生命周期负责。外观与关闭偏好作为 App 共享参与者加入同进程退役；主窗口拥有原生托盘，隐藏到托盘不进入 App 关闭流程，具体行为见[桌面壳](desktop-shell.md#系统托盘与关闭选择)。

音乐参与者只完成最小本地准备：通过 `music.accounts.list` 恢复账号状态，读取 `music.catalogSource.settings.get` 和 `music.home.v1`。账号与来源恢复失败不能冒充游客或默认来源；不等待网络登录验证、账号资料或首页 HTTP。快照缺失、不可读取或不能解码为可用展示时均视为未命中，允许正常首载；不把缓存错误变成启动门槛，也不把真实首页请求失败改写为成功空结果。

`music.home.v1` 是全应用唯一的最近成功首页展示（存于缓存库 `meowfilm-cache.sqlite3`），带平台身份，不按账号分仓、无 TTL，也不携带执行权限；显示相符快照后的网络更新由首页 owner 负责。持久化、失败保留与认证失效恢复见[首页启动与展示快照](music-catalog.md#首页启动与展示快照)。

## 原生和脚本边界

五平台官方联网配方独立于 LX 音源脚本，由共同 Node `music-platform` owner 执行账号认证、平台目录、搜索、歌词、官方音频、同步和反馈协议。原生 .NET 保留安全账号保存／授权发布、永久歌曲仓库、引用队列、共享图片、播放器和真实本地事务／journal；凭据只在当前授权交接中借给 Node，不建第二账号库。网易订阅／退订的 token 由该模块按需借用既有 token owner，失败保留原生本地意图。单个音源脚本失败不使本地功能或官方入口失效；共同运行器不可用时联网操作报告真实失败，不回退原生 HTTP。公共宿主随 App 启动，影视随后恢复启用猫源，不因此执行音乐或停用的猫源脚本，也不加载 Watchman SDK。

Windows 的唯一 Node 入口是 `apps/windows/runtime/entries/aggregate.mjs`，从公共 `apps/runtime/entry.mjs` 按序装配 `music@0.8.0`／`music.script.v3`、`cat@0.2.0`／`cat.script.v2`、`source@0.3.0`／`source.runtime.v2`、`pan@0.1.0`／`pan.runtime.v1`、`tmdb@0.1.0`／`tmdb.data.v1`、`douban@0.1.0`／`douban.data.v1`、`film@0.1.0`／`film.playback.v1`、`netease@0.1.0`／`netease.token.v1`、`music-platform@0.1.0`／`music.platform.v1` 九模块。公共层管理 LX/source 原生 QuickJS 线程、Cat Node Worker 和按需网易 token Worker；Windows 目录保留私密启动／存活管道 bootstrap、薄 entry 与设置专用 adapter，业务使用完整 HTTP，没有业务 module 副本或第二套 runner。

载荷包含公共 entry/bootstrap/client/contracts、共享九模块及 `source-rules.mjs`、Windows 启动／entry／设置适配文件、runtime-bridge、script-host、source-runtime／novel-rules、pan-runtime、film-playback／film-rules／film-search、netease-token／锁定 jsdom、music-platform、Node 兼容及规则解析依赖、锁定的原生 QuickJS-NG addon/source SDK 和必要许可，没有通用业务文档桥授权。CatNode 实际源码、安装事实和私有卷位于 App 固定 `script-runtime` 数据根，来源意图由原生唯一保存，source 的 Reader/CatVod 不继承该私有卷或服务能力。film 复用同一批 provider，独占影视规则执行、搜索投影、元数据与业务缓存、新增历史／人工匹配／屏蔽及预览选择；普通规则与偏好改由原生唯一保存，运行器消费快照。网盘、TMDB／豆瓣敏感设置同样归原生，所需读取与网盘生成凭据回写仅经[私密设置能力](native-settings.md)，不是脚本 namespace 授权；原生 `FilmLibrary` 收藏仍为 `film.library.v1`。小说纯规则统一由 source 的 Node owner 执行，书架／历史／阅读偏好／净化配置仍由原生 `novel.library.v1` 唯一持有。公共执行、存储与网络边界见[共用运行时](../runtime/script-runtime.md)；source 平台能力以[capability ABI](../runtime/source-capability-abi.md)为唯一来源。Node 无关仅指 source-runtime 规则包，不表示 Node 兼容层无需 Node；Windows 原生小说页面及阅读器见[小说接入](novel.md)。

App 原生能力装配与 `AppIdentity.ScriptModules` 独立校验：原生能力数量和 `IDesktopModule.Id` 不要求等于脚本模块数量或名称。增加不依赖脚本的原生能力不会被迫增加 Node 模块；脚本清单只决定 App 启动时的 JS 载荷与协议版本。

当前 App 依次注册音乐、`FilmCapability` 和 `NovelCapability`，因此启动页保持音乐推荐。影视能力拥有规则设置草稿及 Node 回执快照、视频源、元数据设置、搜索与播放会话消费者；首页通过元数据适配读取豆瓣，正式播放实现通过 `FilmPlaybackSession` 消费 Node film，而不是由首页本地 preview 提供片源。电影由独立 FilmPlayback／FilmKernel 持有解码、WASAPI 设备时钟及 D3D11 原生 HDR／DOVI 输出；经 App 既有仲裁器与音乐 NativeAudioPlayback 互斥准入，不借用音乐 PCM／时钟。Core 的 `NativeHttpMediaTransfer.CopyCompleteAsync` 供音乐缓存完整文件 producer 使用，缓存不依赖电影解码／输出。固定来源、许可及构建与真实输出的验证边界见[原生媒体实现](media-playback.md)。App 另通过 `AppLauncher.createPages` 装配 `SearchResultsPage`，Desktop 只消费中性 `IDesktopSearchPage`；影视规则及搜索 JS 只在 Node 载荷执行，Capabilities 不嵌入规则／搜索程序，也不保留客户端引擎回退。小说规则消费者借用同一运行器，原生 UI 和数据 owner 不迁移。原生导航／搜索已接入正式播放会话与小说详情；实现接通、构建与 `--debug` 真实业务／媒体验证分别记录，不代表整 App 已全部通过。搜索交接只见[共同契约](../common/film/search.md)。影视首页见[影视首页](film-home.md)，设置见[影视设置](film-settings.md)，端侧搜索接通边界见[影视搜索](film-search.md)。


## 同进程清除本机数据

通用设置的清除操作由 `AppLauncher` 注入，不由音乐或影视模块互相清理数据。`UserDataDirectory` 的一次待清理请求同时覆盖 `AppDataPaths.Documents`（`aggregate`）与 `AppDataPaths.Caches`（`aggregate-cache`）；前者包含全部 Node 元数据／媒体／脚本缓存，后者包含全部原生缓存文档、账号展示快照及音频文件，不按命名空间清单筛选。两根仍保留独立存储队列和根锁；普通启动只取得 App 准入，不打开或创建可选缓存根。

`AppUserDataReset` 串行持有一次已确认操作的阶段：记录待清理请求、真实释放旧代、删除两根自有数据、重新装配；失败显式重试只继续未成功的阶段。旧窗口取消导航并锁定交互，媒体设置先封住系统输入，随后使用单向 `AppShutdown.CloseAsync` 同时停止业务所有者和运行器，包含共享 SQLite 与可选资源目录的 owner。内存缓存随旧代释放；缓存落盘尚未结算、任一根删除失败或实际释放失败，都不能放行新代启动。删除前两根均取得排他删除句柄，任一仍有存储占用则不开始删另一根。

共享运行器在业务停止开始时立即退役；原生消费者通过[运行器关闭回执](app-runtime.md#所有权与关闭)完成本地排空，不向退役宿主再次请求会话重置或图片释放，真实关闭失败仍阻断删除与替代代次。清除失败后的退出直接走进程退出路径，不重试整套释放；持久重置标记留待冷启动完成。清除进行中不接纳竞争动作。

两根全部清除后才移除 pending，关闭旧 Shell，但不退出 WinUI Application 及其 UI 线程；复用启动/失败重试窗口创建新的 `AppStartup`、store、cache owner、runtime、业务模块、偏好、播放和窗口附件。小说原生写入、规则 RPC 与 Web 交互随旧 owner 取消并排空，搜索页及导航委托一并释放并重新装配，不借用旧 `NovelCapability`。入口装配闭包在资源成功释放后清空，旧 Shell 的 Closed 不会终止替代代次。程序 PID 不变，无需手动重启。中断后的进程启动先继续同一请求，部分删除失败仍不准入业务；完成请求的重试不再删除新代业务或缓存。范围及文件排他边界见[本机数据清除](protected-storage.md#clear-all-local-user-data)。

正常退出与上述同进程重置分别处理：Shell 立即关窗，AppHost 停止播放器和 Node、封闭数据库准入并完成已经接纳的事务，然后结束应用消息循环。正常退出不等待全部业务 owner 的取消、网络 ACK 或资源释放证明。启动窗口关闭也直接取消启动并结束应用，不保留清理超时／重试清理循环。
