# iOS 架构与模块职责

meowfilm 是单一 iPhone App。窗口、导航、呈现、手势及全部页面内容 100% 由原生 UIKit 承载（全仓零 `SwiftUI`、`UIHostingController` 与 `UIViewRepresentable`）。业务、平台、账号、播放、缓存和持久化位于 Swift Package `native/MeowKit`。

App 最低 iOS18；Swift Package 的 macOS15 目标仅用于 `scripts/verify-native.sh` 主机 SwiftPM 快速编译校验，不新增 Mac 客户端。构建输入与工具链验证边界见[发布](release.md)。

共同规则分别见[页面数据请求](../common/pages/data-requests.md)和[音乐平台客户端上下文](../common/music/platform-client-context.md)。本文件说明 Swift Package 与 UIKit 的端侧装配，不把目标契约视为已完成实现。

## 应用工程

| 目录 | 职责 |
| --- | --- |
| `apps/ios/Application` | `UIApplication`／scene 生命周期、唯一窗口、固定产品身份 |
| `apps/ios/Composition` | 资源、原生音乐服务、共享播放控制、影视运行器适配、账号访问与后台同步的装配和关闭 |
| `apps/ios/Debug` | `--debug` 调试控制面、分类日志与底层诊断宿主（按 `Host/`、`Navigation/`、`Inspection/`、`Actions/` 分层） |
| `apps/ios/Navigation` | 根容器、普通页面导航、播放器及其他非导航呈现的路由 |
| `apps/ios/Presentation` | 主页面外壳、页头、底部导航和播放器停靠区 |
| `apps/ios/runtime` | iOS socketpair 启动参数／受限原生设置能力／失败边界与 music/cat/source/pan/tmdb/douban/film/netease/music-platform 九模块薄 entry，复用公共 JS 模块 |

工程文件由 `scripts/generate-ios-project.py` 生成。App 编译上述应用职责目录；NodeMobile.xcframework 链接并嵌入签名，完整 JS 载荷作为 `Runtime` 资源随包签名；构建输入准备见[发布](release.md)。

### 常规文字与字号

普通文字遵守仓库 `AGENTS.md` 的统一常规字体限制：全部 UIKit 普通文字（含标题、按钮、菜单、标签、选中态和歌词）统一使用系统 Regular 字体；默认含粗体的语义字体也明确使用常规字。保留现有字号层级和 Dynamic Type 缩放，歌词的测量与实际绘制使用同一常规字体。阅读器 UI 遵守相同限制，仅可自定义的阅读正文保留用户字体与加粗设置；系统可访问性设置不由应用全局覆盖。

### 设置语义配色

设置内容使用 `PageTheme.settings` 提供的 `SettingsTheme`，仅由浅／深外观决定，不依赖歌曲、命名预设或自定义基准色。所有设置页统一使用中性不透明卡片、淡中性渐变轮廓、行分隔线和独立输入底／边界；正文、辅助文字与字段提示保持可读对比度。操作、选中和开关为稳定蓝色，危险操作为红色，来源状态使用可读的成功／警告／错误语义色；平台标识与真实颜色数据不重染。

设置窗口背景和 App 页头仍使用原 `PageTheme` 渐变；首页、播放器、原始播放目录、FM 选择器和阅读正文的配色契约不变。原始目录复用影视列表布局但显式不采用设置语义色。该投影不读写偏好，不新增主题模式或存储。

App 总设置与音乐／影视／小说全部设置页共用 `MeowUI/Settings/` 统一纯 UIKit 底座：`SettingsVisualMetrics`、`SettingsRowBackgroundView`、`SettingsTableView`、常驻槽位复用的 `SettingsTableCell`（含常驻领先图标槽位，无销毁重建路径，实现 `prepareForReuse()`）、`SettingsListViewController`（基于 `MeowUI/List/` Diffable 底座与 `DisplaySignature` 增量刷新）、`SettingsFormViewController`（支持可选 `formHeader`／`formFooter` 与 `usesFormCardSurface`）、`SettingsSwitch`、`SettingsDividerView`、`SettingsSectionHeaderView` 与 `SettingsSegmentedControl`。全部设置页（含影视设置、规则编辑与管理列表）统一使用 `PageTheme` 语义配色，零冗余设置包装壳；`SettingsListRow` 内置 `LeadingIcon`（`.symbol`／`.asset`／`.badge`）与 `.loading`／`.hint`／`.action` 一等配件，嵌入内容槽位编译期强制要求 `ThemeableView`（`UIView & PageThemeable`）。设置根页及其下级页面通过 `PageChromePolicy` 声明隐藏胶囊播放器并撤销底部占位；导航与播放器均不显示时，Dock 的背景及触摸层也不再覆盖页面。返回设置栈外恢复原播放器状态，不停止播放。

全部设置子页以设置首页的文字角色为基准：主标签和操作为系统 Body Regular，次要资料和分组标题为 Subheadline Regular，继续响应 Dynamic Type。普通单行由正文行高、上下各 12pt 内边距及 44pt 最小行框决定；菜单、开关和数字附件使用紧凑视觉高度，不额外撑大普通行，触达区与视觉尺寸分离。共享开关保护 44pt 横向占位，填充分配不能移动其共同右边缘。两行账号及真实多行表单可按内容增长。同级主文字字号一致，分组标题不采用系统 increased／加粗样式；首内容外边界以搜索净化页为基准，由 `SettingsVisualMetrics.contentTopSpacing` 统一为 20pt。有首节标题时标题是首内容；没有标题时取首卡，供应商切换和管理搜索同样取实际可见边界。设置表首节 header、手动滚动及整体表单统一消费共同度量，后续组间距、表单内部边距和公共页头高度不变。

输入呈现由字段所有者显式选择 `SettingsInputAppearance`，主题刷新和布局使用同一语义。多项卡片／整体表单采用无框输入并复用字段分隔线；单项输入卡片采用卡内标题、下方输入和底部下划线，设置输入浮窗复用 `SettingsInputSection`。规则字段使用下划线，刮削字段无独立边框且无额外输入下划线。仍需独立外框的单行输入为胶囊、多行输入为方框；管理搜索、音乐数字与账号组合胶囊保留独立轮廓。独立输入浅色底为白色，深色使用对应输入表面；无框／下划线字段背景透明并移除曲率缩进。多行输入保留换行、滚动及编辑空间，主题和尺寸更新不重建字段。离散状态由业务选择语义，`SettingsStatusBadge` 统一呈现小图标、同色浅底／深字及动态胶囊。

### 启动与首页发布

`MeowfilmLaunchController` 只持有用户清除数据的冷启动屏障。屏障成功后才创建 `MeowfilmAssembly`，业务本地恢复不进入清除流程。Coordinator 继续使用已有 `startupPage`，通过原生 `UIActivityIndicatorView` 表示等待，并保留设置入口；不增加品牌画面或最低停留时长。

装配先完成现有脚本会话的本地配置、启动音乐借用者并绑定账号平台会话（`NativeMusicService` 与 `NativeMusicAccountService` 启动即就绪，仅在退出 `close()` 时关闭，不保留锁屏动态访问撤销网关），再通过同一 `prepareMusic` 路径等待账号 `prepareLocalAccounts()`、首页来源 `homeSourceSelection()` 和展示快照 `restoredHome()`。账号准备与私有请求共享本地初始化，读取失败不能当作游客。三项准备成功后才创建并发布可用的 `MusicViewModel`，Coordinator 据此安装首页；快照由原生服务保留供首页复用，无快照也正常结束启动，由首页沿用首载骨架。整库 `model.load()` 和自动同步在发布后执行，启动页不等待远端账号校验、首页 HTTP 或脚本启动中的网络工作。

本地启动失败通过 `PageViewController.enqueueError` 浮窗报告，原启动页停止 loading，已经打开的设置栈保留，不用全页错误说明替代导航，也不新增清除数据入口。关闭会取消启动任务并关闭原生服务；本地等待随后无论成功或失败都不能再发布模型或启动错误。Coordinator 同时拒绝安装已关闭的模型，避免队列中的旧发布切回首页。
启动与全链路运行态通过正常构建产物的 `--debug` 模式核对（见[Debug 模式与调试接口](debug-mode.md)）。

## Swift Package

| 模块 | 职责 | 依赖 |
| --- | --- | --- |
| `MeowContracts` | 通用请求、生命周期和错误契约 | 无 |
| `MeowJSON` | Codable DTO 值、JSON 语法解析、UTF-8 成员与数字原文转发 | 无 |
| `MeowDebug` | 零第三方依赖的 `--debug` 配置解析、分类日志、Blob 外置、HTTP Control Server、诊断采集与 `DebugDialogControllable`／`DebugAlertActionRegistry` 契约 | 无 |
| `MeowStorage` | 共享 SQLite（`Engine`/`Schema`/`Files`/`Values`/`Debug`，WAL 读写分离与语句 LRU 缓存）及原始资源目录 | Debug、MeowSQLite |
| `MeowAccounts` | 凭据与账号状态基础设施 | Contracts |
| `MeowAudio` | 独立音频解码、有界 PCM、原生保音高变速与 AVAudioEngine 输出 | Debug、CMeowAudioKernel |
| `MeowPlayback` | 受控媒体输入、音乐播放、唯一系统媒体会话与播放所有权 | Contracts、Debug、MeowAudio、Storage |
| `MeowPlaybackVideo` | 显式注入共享协调器的视频解码／原生视图输出、候选内核选择与有界能力回退 | Contracts、Debug、Playback、MeowVideoKernel、MeowAetherKernel |
| `MeowUI` | 纯 UIKit 中性主题、窗口渐变、页面生命周期、自绘页头、导航栈与转场、统一设置底座、三层弹层、搜索页与图片加载基建 | Debug、Storage |
| `MeowRuntime` | runtime.http.v1 客户端、精确 JSON 值、事件观察／取消／关闭与单次内嵌启动机制；fd 仅交接受信启动及生命期 | Debug、JSON、Storage |
| `MeowMusicPlatform` | 音乐领域公开模型／错误契约、网易／QQ／酷狗协议、账号、HTTP 与文件缓存 | Audio、Contracts、Debug、JSON、Accounts、Storage、Runtime、UI |
| `MeowMusicNativeService` | SQLite 强类型音乐持久化（`Persistence/{Core,Songs,Library,Queue,History,Discovery,Home}`）、队列、搜索、推荐、歌词、缓存编排及音乐脚本薄适配 | Contracts、Debug、JSON、Platform、Runtime、Storage |
| `MeowMusicFeature` | 音乐界面、交互、视图模型与音乐外观策略（直接消费 `NativeMusicService`／`NativeMusicAccountService`／`NativeMusicScriptService` 具体类型，仅保留 `MusicUIRouting` 解耦 App 导航） | Contracts、Debug、Storage、Playback、UI、Platform、NativeService |
| `MeowFilmFeature` | 影视页面、播放器偏好、规则／搜索 RPC 消费者及来源 owner，见[影视能力](film.md)与[设置](film-settings.md) | Contracts、Debug、UI、Storage、Runtime |
| `MeowNovelFeature` | 小说发现、搜索、书架／历史、CoreText 原生阅读器、书源与净化管理，见[小说](novel.md) | Debug、UI、Storage、Runtime |
| `native/VideoKernel` | iOS 自有 Swift 主视频内核：FFmpeg 8.1.2 解复用、异步 VideoToolbox、`AVSampleBufferDisplayLayer`、SPSC 实时音频、按需字幕、轨道／章节与诊断 | 锁定静态 FFmpeg 8.1.2 与 Apple 系统媒体框架，无业务或系统媒体会话 owner |
| `native/AetherKernel` | 唯一公开 dynamic `MeowAetherKernel`、Aether 宿主生命周期与输出适配 | AetherEngine 7.68.0、动态 FFmpegBuild 3.15.0、内部静态 Dovi；无业务或系统媒体 owner |
| `native/AudioKernel` | 锁定 FFmpeg 9.0.1 音频闭包、隐藏上游符号的动态框架与最小 C ABI | 无业务或视频依赖 |
| `packages/film-rules`／`packages/film-search` | Node 加载的共享规则／搜索程序，不再作为 SwiftPM 依赖 | 共享 script-host 原生载体 |

共享模块不依赖音乐模块。业务差异通过显式参数、契约或注入实现表达；模块边界由 `scripts/check-source-scope.py` 检查。源码按 target 的实际输入和工程成员核对，未使用的 App 职责目录不必存在或非空，整个 target 仍须有有效源码。系统模块按 SwiftPM 声明类型、接口成员和实际 import 核对依赖；`CSQLite` 保留 SDK sqlite3 头及链接要求，不限制系统模块名称、消费者或接口文件的完整文本形状。

音乐依赖闭包不包含视频目标；应用装配共享一个 `FilmPlayerPreferencesStore` 给设置、播放模型与媒体桥接，候选工厂通过 `makePlaybackVideoDecoder(engine:)` 读取首选内核。出厂默认原生内核（`native`），设置变更只对下一媒体候选生效，当前实例不热切；播放页消费事件里的实际内核，不用首选值代替运行事实。选择、能力过滤及控制生效时机见[播放器设置](film-settings.md#播放器设置)，候选回退见[影视](film.md#内核选择与候选回退)。音乐 PCM／输出与取消排空边界见[独立音乐核心](music-audio-core.md)，系统会话仍由[系统媒体控制](system-media.md)说明。

影视可编辑配置由原生 owner 保存；六块身份、离线读写和按块通知遵循[设置存储共同契约](../common/settings-storage.md)，具体存储归属与保护见[本地存储](storage.md#数据模型与-owner)。规则执行、搜索调度／聚合、智能播放、业务数据缓存及历史／人工匹配／屏蔽仍由 Node film owner 管理，不因配置迁移转移业务 owner。规则与通用脚本使用同一 `MeowScriptHost.framework` 内的 QuickJS-NG 0.18.0 核心，各自独立 Runtime／Context，不链接客户端规则引擎。共用约束见[影视播放契约](../runtime/film-playback.md)，视频内核构建输入见[发布](release.md)。平台验证仍须单独记录，不以九模块装配或内核版本代替实跑证据。

全局搜索的音乐／影视关系由 `MeowfilmSearchComposition` 装配。`MeowUI` 仅提供目标选择和附加结果内容契约；音乐搜索分页容器按契约承载内容，不导入影视、不承载影视取数或整合策略。影视搜索、真实详情导航与关闭 ownership 见[影视搜索](film-search.md)。

原生源码检查（`scripts/check-source-scope.py`）仅覆盖 `apps/ios` 和 `native/MeowKit`，不遍历 Windows 专属源码；Node 依赖锁与包边界检查由 `npm run verify` 执行。

平台响应与普通业务 JSON 使用 `MeowJSON.JSONObject`／`JSONValue` 的现行语义。运行器使用 `MeowRuntime.RuntimeValue`／`RuntimeObject`／`RuntimeJSON`：对象键按原始 UTF-8 区分，保留 Unicode 规范等价但标量不同的成员；语法解析复用 `JSONObject.decodeValue`／`Fields`，同一 UTF-8 键重复时采用末值。`Fields` 按首次插入顺序迭代，同键更新不重排，删除后重插追加到末尾；`JSONObject.encode` 仍按键排序输出。无法无损表示的数字由 `JSONNumber`／`RuntimeValue.exactNumber` 保留原文，不因未消费数字拒绝整棵树。普通数字、身份与资源由实际消费者检查；解码深度 64 保留，私密帧不设业务字节配额。HTTP 只解析一次 envelope，核对 `contract`、`ok` 与 `value`/`code`，不再次全树校验。共同语义见[公共 HTTP 契约](../runtime/runtime-bridge.md)。`JSONObject.decode` 是供既有 DTO 使用的 Foundation 字典投影，不用于原始文档保留；持久原文使用 `decodeValue` 或 `RuntimeJSON`。该边界不增加普通 JSON 总字节配额。

## 纯 UIKit 界面架构与目录规范

iOS 端所有页面、设置表单、浮窗、列表、播放器与阅读器均统一采用纯 UIKit 实现，禁止引入 `SwiftUI`、`UIHostingController` 或 `UIViewRepresentable` 桥接层。

### 模块目录分层

禁止将大量跨子域文件平铺堆塞在单一目录，UI 模块按清晰职责建立子目录归位：

- `MeowUI/`：`Accounts/`、`Appearance/`、`Artwork/`、`Controls/`、`Layout/`、`List/`、`Management/`、`Navigation/`、`Presentation/`、`Search/`、`Settings/`
- `MeowFilmFeature/`：`Home/`、`Detail/`、`Player/`、`Search/UI/`、`Settings/UI/{Core,Sources,Playback,Rules,Pan}/`、`Debug/`（保留 `Catalog/`、`Library/`、`Pan/`、`Playback/`、`Rules/`、`Settings/`、`SettingsSources/`、`Sources/` 非 UI 业务目录）
- `MeowMusicFeature/`：`Appearance/`、`Artwork/`、`Catalog/{Browse,Channels,DailyStyle}/`、`Library/UI/`、`Playback/{Artwork,Cache,FM,FullPlayer,Lyrics,MiniPlayer,Model,Presentation,Queue,Sources}/`、`Presentation/`、`Search/UI/`、`Settings/{State,Pages,Accounts,Sources,Search}/`、`Debug/`
- `MeowNovelFeature/`：`Artwork/`、`Browse/`、`Detail/`、`Reader/`、`Settings/`、`Web/`、`Debug/`（保留 `Library/`、`Reading/`、`Runtime/`）
- `apps/ios/Debug/`：`Host/`、`Navigation/`、`Inspection/`、`Actions/`

### 主题传播、布局与外观策略

- 主题刷新通过显式 `PageThemeable` 协议与类型化控件引用向下分发（`SettingsDividerView`、`SettingsSectionHeaderView`、`SettingsSegmentedControl` 等），禁止递归遍历 `subviews` 猜测叶子视图或分割线刷色。
- 子视图不得反向调用 `superview?.layoutIfNeeded()` 或 `window?.layoutIfNeeded()`，容器布局动画统一由拥有者协调。
- `PageViewController` 通过声明式 `PageChromePolicy` 暴露底部栏／迷你播放器／沉浸态／设置根分支能力，`MeowfilmCoordinator` 与 `MeowfilmRootContainerViewController` 只消费该策略；主页面自定义页头通过中性协议 `PrimaryPageHeaderConfiguring` 注入，`MeowfilmPrimaryShellViewController` 不依赖或强转具体业务控制器类型。

### 三层浮层与唯一反馈通道

1. **`PageAlertPresenter`**：全仓唯一 `UIAlertController` 构造与呈现入口，封装确认、危险确认、输入、选项单与可重试错误弹窗，统一遵守呈现时机并 100% 自动注册 `DebugAlertActionRegistry`。
2. **`FloatingCardDialogController`**：全仓唯一纯 UIKit 居中卡片弹窗基类（统一覆盖 `ManagementDialog`、音乐账号登录／详情、平台排序与网盘登录等），提供统一遮罩、圆角边框、键盘避让、托管异步任务（`runDialogTask(key:)`／`cancelDialogTask(key:)`，随离场与 `deinit` 自动取消并过滤 `CancellationError`）、`dialogWillClose()` 生命周期钩子与 `DebugDialogControllable` 协议。
3. **`FixedSheetTransition` / `VerticalPageTransition` / `PageViewController.presentPageSheet`**：业务 Sheet、队列、音源、FM、频道选择、全屏播放器、阅读器与受控 Web 的呈现入口。

错误与操作提示统一由 `PageViewController` 独立的 `PageFeedbackQueue`（支持同内容去重、转场完成延迟呈现与持久错误源对齐 `syncErrorSource`／`clearErrorSource`）与 `MeowfilmCoordinator` 全局错误出口承接，页面异步任务统一通过 keyed／抛出式 `runPageTask(key:)` 取消并路由错误，各业务域不再自建重复错误弹窗状态机或影子去重字段。

### 图片／取色缓存、虚拟化列表、高频渲染与音频管线解耦边界

- 三域图片存储均由 `MeowfilmAssembly` 显式持有并注入，无全局 `.shared` 单例，共享唯一泛型单飞引擎 `MeowUI/Artwork/SingleFlightImageStore.swift`（`ArtworkImages`、`MusicArtworkImages` 与 `NovelImageStore` 均为薄适配）。图像解码、音乐推荐卡色板采样与全屏播放器背景高斯模糊均在后台执行并按资源身份缓存；滚出视口仅归还视图借用、不释放已准入资源，同页滚回直接命中缓存零重载，页面隐藏停止新准入、卸载／关闭排空租约。
- 全部业务列表启用 `UITableViewCell` / `UICollectionViewCell` 复用与 `prepareForReuse()`，统一使用 `MeowUI/List/`（`DiffableSnapshotBuilder`／`IndexedDiffableProjection`／`applyIfNeeded`）完成保序去重、增量 `reconfigureItems` 与无变化跳过，禁止在外部手写原始 `NSDiffableDataSourceSnapshot`、禁止业务整表 `table.reloadData()` 与非虚拟化 `UIStackView` 承载长选集／列表（影视详情选集／演职／相似列表由 `UICollectionView` 虚拟化承载）。
- 高频播放状态与低频浏览／详情状态物理隔离（如 `FilmMediaStateStore` 与 `FilmPresentationStateStore`），宽 `ObservableObject`（>5 `@Published`）在 UI 层全部按消费字段切片观察；`PageViewController` 在页面隐藏期间自动暂挂主题刷新与 `whenVisible: true` 观察回调并于回前台时合并生效，避免后台页面被播放进度或无关状态唤醒重绘或触发 `applyTheme()`；逐字歌词由连续单调时钟驱动，仅在播放态／倍速／seek／活跃行／尺寸变化或漂移超阈值时重新校准动画。
- 音频实时解码与 PCM 喂流（`AudioOutputPipeline`）运行在独立 `.high` 优先级任务并脱离 `@MainActor`，配合 `.userInitiated` 解码队列、24 块（~2s）无缝 FIFO 缓冲、`maximumFramesToRender = 4096` 与 1.0x `timePitch.bypass` 抵御前台转场与退后台 CoreAudio 切片切换；`PlaybackSystemSession` 对 `MPNowPlayingInfoCenter` 做状态差分与时间推算防抖（杜绝高频 XPC 洪泛）；全屏播放器将 `AVRoutePickerView` 延后至转场落稳后挂载，并按尺寸缓存唱片径向遮罩、玻璃底盘与默认封面位图。

## UI 所有权

- App／scene 持有唯一窗口和产品装配；页面不反向控制窗口或建立第二套导航。
- 普通页面使用同一 `PageNavigationController` 作为页面栈所有者，系统导航栏隐藏且保持为空；页面不再写入 `navigationItem` 的标题、标题视图、左右按钮或搜索框。
- 每个页面由 `PageViewController.pageHeader` 绘制可见页头，背景与页面对齐；返回符号、`×`、文字按钮和菜单按钮均为自绘，无胶囊底、无系统按压高亮。
- 页面标题使用 `pageTitle`，不设置 UIKit 的 `UIViewController.title`；系统导航栈保留 UIKit 自身唯一的空 `navigationItem`，不覆盖为另一套替身元数据。
- 页头内部统一使用 Auto Layout；背景铺满页面，按钮与标题行遵守水平安全区，横屏避开刘海。页面内容从 `contentTopAnchor`（自身页头底部）开始，不重复增加顶部安全区。窗口根容器与主页面外壳不安装页头；推荐、频道、影视、小说、我的各自持有页头，嵌套的领域页面共用所在主页面的页头；模态页使用自身安全区而非窗口状态栏高度。
- 书源、音源、视频源和猫源管理共用纯 UIKit `ManagementListView`（Diffable 数据源与统一 `SettingsSwitch`）；列表在实际底部安全区内留 4pt 收口并显示完整边框／圆角，批量操作栏位于卡片内、靠近其底边，滚动范围避让操作栏。键盘升起时卡片及操作栏共同避让，不靠把白色列表表面铺到屏幕底边减少空白；没有附加工具时搜索框占据工具行可用宽度。
- 窗口根容器和 `PageNavigationController` 把状态栏样式交给栈顶页面；页面未覆盖时保持系统默认，捕获状态栏的模态页自行决定。
- 推入和返回使用应用自有的横向转场。返回时上一页静止，不变暗、不加阴影；当前页连同页头跟手划开。
- 左边缘与内容区右滑都由自有手势驱动，复用同一距离／速度判定；可与横向分页协调，在分页到达首屏前不触发返回。系统返回手势保持禁用。
- 有未保存修改的歌单编辑器在返回前请求确认，确认后仍走同一条返回路径。
- 推荐、频道、影视、小说、我的由主页面外壳保留，首次切换到时创建并加载。
- 推荐／频道／影视／小说／我的是五个独立页面；切换时各页连同自己的页头按页面顺序左右滑动，页头不在页面间移动或共用，底部栏选中态在切换开始时同步；减少动态效果时直接切换。底部入口为「首页／影视／小说／我的」，「首页」进入推荐页；频道页也高亮「首页」，但仍保持真实频道页面身份和不透明 dock 背景。
- 推荐页头依次为菜单、搜索胶囊与原功能按钮；频道页头依次为菜单、频道滚动条、频道选择及圆形搜索按钮，不再提供「推荐／频道」页面切换控件。频道切换条在频道数据到达后显示，搜索进入既有搜索页。频道通过现有内容入口或侧边栏「频道」进入；底部「首页」、音乐侧边栏「首页」及屏幕左边缘右滑均返回推荐。边缘返回沿用统一距离／速度规则，短滑或取消保留当前频道；频道内容区横滑切换频道，分页、栏目、轮播和列表滚动都等待边缘手势失败后才接管触摸。
- 影视页头为菜单与占据其余标题区域的搜索胶囊；菜单打开影视侧边栏，胶囊进入统一搜索页（默认选中影视目标），返回保留原影视页面与滚动位置。影视侧边栏「首页」返回影视首页。播放器与底部栏固定在屏幕底部，不随键盘升起，键盘只增加内容的底部避让。进入／返回非主页面时底部栏只做上下收放，按钮行保持固定高度与横向位置，不随页面左右滑动；这是既定效果。
- 音乐、影视与小说共用侧边栏框架：保留原窗口底色和顶部居中 App 图标，菜单按内容高度排列，图标槽与文字列统一对齐，选中项使用圆角底色与主题强调色；普通文字保持系统 Regular。菜单区域可独立滚动，带图标、左对齐文字和尾部箭头的“设置”固定在底部安全区上方，不随列表滚动。音乐主菜单为首页／频道；“我的”沿用音乐内容。影视主菜单为首页／视频源（视频源行当前仅展示，不接入首页切源）。音乐与影视侧边栏均不提供数据源快捷切换。小说主菜单为首页／已添加书源（可发现书源切换同一发现会话，详见[小说](novel.md)）；二级页仍使用返回入口。
- 页面跳转由路由显式调用，不通过业务状态标志触发。
- 上层呈现通过视图层级阻挡下层触摸。
- 资源活跃、页面可见及浮层呈现时机分别管理；待处理的错误在页面稳定出现或导航／呈现转场完成后处理，不因此重新加载业务。
- `PageViewController.enqueueError` / `enqueueNotice` / `syncErrorSource` 作为页面反馈唯一队列入口（对当前展示中与队列中的同内容反馈统一去重，转场与浮层结算后自动消费；需要显式恢复的页面通过 `retry:` 在同一错误浮层内提供“重试”；持久状态错误通过 `syncErrorSource` 按源去重并随成功或重试自动复位）。页面根据实际呈现关系和呈现完成回执记录已接受的浮层身份，不只检查同步的 `presentingViewController`；`dismissPageModal` 以实际退出回执结算该次转场后调用业务动作，系统文件选择器等浮层正在自动退出时复用已有转场，不再次发起退出；已有转场被取消时保留浮层及 picker 身份，继续接收选择／取消，不执行业务动作；UIKit 已自动退出才触发的 action 仍可消费一次，已失效页面不再重试；影视设置的成功反馈在暂不可呈现时保留，待页面出现或原浮层退出后显示，不重新提交保存。

## 页面加载与刷新

- 页面在导航栈中叠加；被覆盖的页面保留内容、滚动位置和状态，滑动返回时直接露出上一页。被覆盖不取消页面已经开始的加载。
- 页面从导航栈移除后释放，并取消其未完成的请求；搜索历史等持久数据不受影响。
- 页面内容在创建时构建并加载一次，返回页面不重新构建、不重新请求。主题、深浅色和字体大小变化仍更新样式与布局。
- 推荐、频道、我的和歌单页提供下拉刷新，失败时提供重试。搜索结果不刷新，获取不同结果需要重新搜索。
- 本地数据变化（收藏、新建／重命名／删除歌单、当前播放等）同步到已显示的页面，包括插入和删除列表项，只更新受影响的部分，不重载整页；这是本地状态同步，不等同于网络刷新。
- 默认不自动重新拉取网络数据，账号／数据源变化及按日期内容失效除外：
  - 登录、退出或切换账号后，旧账号的数据与结果立即失效，依赖账号的页面清空并按新账号及页面需要加载。已确认无本地账号的普通业务准入只返回鉴权结果，不重建账号范围或触发首页刷新；显式验证仍核对凭据并保留原失效边界。
  - 音乐数据源由原生业务服务独立持久化，设置在网易云／酷狗间切换；首页、频道与标签随来源失效，隐藏页不读取，恢复可见后按需读取一次。网易云账号变化不影响酷狗匿名目录；资源详情和已交接播放仍按各自平台与生命周期运行。
  - 按日期生效的内容（如每日推荐）跨天后失效，下次显示时重新加载。
- 搜索发现的摘要、热词及各榜成员状态分别持有；真实可见需求只补未加载成员，失败目标仅显式重试。页面隐藏期间范围失效立即清空，但不为隐藏页重读；详细数据范围与缓存见[音乐能力](music.md#搜索发现)。

## 播放器

- 胶囊与卡片是同一个停靠元素的两种形态，由该元素自身形变切换。
- 首页显式播放成功后展开卡片；展开后 5 秒内未操作播放／暂停、上一首／下一首或进度，则收回胶囊。上述控制操作使卡片常驻，之后操作卡片外区域时收回，不拦截该操作。
- 胶囊和卡片的非控制区域直接打开完整播放器；卡片同时收回胶囊。完整播放器呈现时，底部栏和胶囊保持在底页原位，不下移或隐藏。
- 完整播放器是独立页面，从底部向上滑入并叠加在当前页面之上，不由停靠元素形变而来。
- 完整播放器通过自绘收起按钮向下收起；屏幕左边缘右滑仍作为返回输入，但手势进度映射为同一条向下收起轨迹，不改变普通页面的横向滑动返回。
- 播放器的私人漫游选择页是直接呈现的独立单页，使用 `VerticalPageTransition(edge: .top)` 从顶部向下进入，关闭向上退出，左边缘返回只驱动相同轨迹（全屏播放器使用 `.bottom`），不使用没有可返回栈项的导航容器。关闭只返回播放器，不退出 FM；短滑、取消、旋转或后台中断保留页面与选择能力，账号／FM 失效则在转场结束后关闭。
- 播放器不安装公共页头；独立收起／音频输出控件按自身水平安全区避让，背景继续贯穿页面。
- 停靠元素与完整播放器各有控件实例，但复用同一个播放控件组件和同一份播放状态。
- 播放器视图只负责布局和动画；播放状态、歌词、副歌、封面读取等业务任务由对应状态所有者负责。
- 收藏状态由稳定的音乐业务所有者持有，停靠播放器、队列和系统元数据共同消费；只按收藏自身身份与版本更新，不由消费者的出现、消失或普通歌单变化重读。
- 首页 FM 入口订阅实际会影响当前会话封面的状态，按有效封面去重，只更新图片；播放进度、歌词与其他播放设置不重新配置卡片。
- 已结算的副歌结果（包括正常无标记）按歌曲、播放 attempt、来源与时长保留；展示开关只控制尚未开始的读取，不替代业务失效。取消立即撤销旧请求的发布权，实际执行仍追踪到完成，不以旧请求数量阻塞新有效读取。

### 播放队列

- 队列、已播歌曲和已播歌单来自本地曲库服务；打开面板只读本地历史，不发起平台推荐或账号请求。已经启用的推荐续播仍由原业务所有者管理。
- `FixedSheetTransition` 持有固定尺寸呈现与交互关闭；面板整体按相对位移下移，取消回原位，下层页面保持原视图和布局。遮罩点击、遮罩下拖及顶部横线下拖共用此生命周期。
- 三个分类共用尺寸稳定的左侧头栏，当前播放的数量显示为标题右上角角标；底层滑块、选中文字的轻微缩放及 `ink`／`muted` 文字色共用分页位置连续更新，不等待分页结束，也不因选择重排容器。右侧清空操作对应当前分类。
- 已播歌曲与已播歌单分别在原历史文档中事务清空，不停止播放、不修改队列或另一类历史；本地回读按分类区分请求代次，旧读取不能覆盖已完成的删除。
- 当前队列与已播歌曲使用原生可复用文本行；历史歌单在同一横向 strip 中以窄卡片展示，仅可见卡片读取本地详情，离开分类不丢弃已读详情和滚动位置。
- 排序柄及行长按进入同一 `UICollectionView` 交互式移动，由 UIKit 处理提升、邻行让位和边缘滚动。原生移动结束后按 occurrence 顺序提交一次，取消或队列／访问代次改变时废弃预览；已接受的落点由共享模型完成，关闭面板不撤销该事务。收藏、删除和播放不复用排序手势。
- 历史详情行只用于展示；删除和继续播放使用服务签发的完整详情令牌，不把历史 occurrence ID 伪装成播放引用。

## 背景与配色

- 当前只适配浅色外观，App 固定使用浅色；深色外观需要整套颜色另行适配。
- App 启用 `UIDesignRequiresCompatibility`，以旧版系统界面样式运行。这是消除 iOS 26 第三方键盘顶部玻璃色条的既定方案：该区域由系统绘制，App 不为此另写布局或适配代码；系统控件随之使用旧版样式属预期结果，不视为待处理问题。
- 主文字与次级信息分别使用共享主题 `PageTheme.ink` 和 `PageTheme.muted`，当前浅色默认值为 `#39343E` 和 `#7F7A7D`。播放器标题、歌手及页面内容复用这组语义色，强调与状态保留各自颜色。
- 元素容器默认透明，不设底色。页头不透明。底部播放器与底部栏区域铺按窗口坐标绘制的页面渐变，不使用模糊：推荐页内容可滚到胶囊（卡片）两侧，自其顶部起渐隐、到其底部完全遮住，底部栏处不透出内容；其他页面的列表延伸到屏幕底部，由这层不透明背景遮住，使内容止于胶囊上方 8 pt，避免胶囊与页面元素连在一起，胶囊下方（含胶囊收起时）不透出内容。这层背景是实现该边界的手段，不是多余遮挡。
- 搜索热搜／音乐榜是明确分层的局部卡片，使用 `PageTheme.surface` 的不透明表面与 `border` 细描边，不加静态阴影；标题播放能力与成员行语义分离，热搜可批量播放但成员行仍执行搜索。历史／猜你喜欢／曲名以 14 pt、标题 16 pt、播放标签 12 pt 为动态字体基准，视觉尺寸与至少 44 pt 的控件热区分开。
- 搜索结果的“综合／单曲／歌单”分类由横向分页承载，分类文字与指示条复用播放队列头部的分页插值，随页面位移连续变化。单曲行只显示歌曲文本及收藏、加入播放队列、更多三个操作，不显示封面或序号；“单曲”标题与圆形播放全部按钮作为列表内容随纵向滚动。歌单结果不显示播放控件，歌单分类不重复显示分类标题。
- 搜索结果按歌曲／歌单内容分别更新行投影，加载状态只更新对应状态行；标题可用性或请求代次变化才重新配置标题，未变列表不提交快照。补入来源会更新已显示行的菜单和操作输入。三个分类保留各自的 UITableView 复用与滚动位置，切分类不重算行源、不自动分页；切换后须有新的纵向拖动或显式加载操作才继续读取，旧请求代次的操作仍拒绝执行。
- 页面背景按同一窗口坐标绘制主题渐变。
- 底部栏图标与文字使用独立定义的固定颜色；底部栏不再单独铺白底，背景随上一条规则。
- 胶囊使用白色不透明底色；卡片使用由当前歌曲封面取色的浅色渐变。停靠元素通过自身的圆角与阴影同底页分层，不改变底部栏配色。
- 完整播放器页面背景取自当前歌曲封面颜色。
- 由封面取得的背景色调整到浅色外观的取值范围内：深色或过亮的封面不能产生近黑或近白的背景。适配只调整背景色，不为个别封面改动文字和控件颜色。
- 共享主题只接收 `PagePalette`（命名预设或自定义 RGB 基准色），不读取歌曲或音乐偏好。自定义颜色在原生调色面板内只预览，确认后由既有偏好 owner 持久化；歌曲采样与缓存仍归音乐 owner。设置分组按需求增加很淡的渐变外轮廓，不改变窗口背景坐标。

## 数据身份

| 身份 | 所有者／用途 |
| --- | --- |
| `dev.meowfilm.aggregate` | 固定产品身份；安装宿主改写安装标识时，内部存储身份不变 |
| Application Support 下 `meowfilm/storage-v1/meowfilm-data.sqlite3` | 原生客户端与 Node runtime 共用的永久数据 SQLite 数据库（`user_version = 1`） |
| Application Support 下 `meowfilm/storage-v1/meowfilm-cache.sqlite3` | 原生客户端与 Node runtime 共用的可重建缓存 SQLite 数据库（`user_version = 1`） |
| Application Support 下 `meowfilm/resources-v1` | 原始图片、音频等资源 owner 的文件目录 |
| Application Support 下 `meowfilm/script-runtime/music-platform` | Node 唯一维护的匿名协议状态 |

| `music_*` / `ios_music_*` / `cached_*` | 音乐曲库、队列、历史、账号、反馈设置（`data` 库）与来源发现、首页快照、展示媒体缓存（`cache` 库）的 SQLite owner tables |
| `music.platform.audio-locators.v1` | 账号作用域下的官方音频定位缓存 |
| `meowfilm.music.audio-resources.v1` | 实际播放后的完整音频缓存 |
| `music.presentation-resources.v1` | 共享图片／歌词展示缓存（索引与结构化歌词存于缓存库 `cached_images` / `cached_music_lyrics*`） |
| `music.home.display.v1`、`music.source-checks.v1` | 首页展示快照和来源发现观察（缓存库） |
| `music.ui.appearance.v1`、`music.ui.songColors.v1`、`music.ui.lastChannel` | 音乐外观偏好、歌曲取色缓存和频道恢复 |
| `playback.volume`、播放所有者 `music`／`film` | 共享播放音量与当前播放业务所有者 |
| `meowfilm.film.library.v1` | 影视收藏身份，保留原来源／影片字段及可选 Node `businessID/playbackTarget`，见[影视收藏](film.md#详情和收藏) |

歌曲／录音、来源、队列 occurrence、播放 attempt、账号访问代次和文件版本是相互独立的身份，不能用标题、短期 URL 或队列位置替代。
