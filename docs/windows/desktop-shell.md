# Desktop 接线契约

Desktop 只拥有原生 WinUI 3 壳、导航/历史、设置展示、窗口及界面请求协调；不创建账号、存储、Node 或播放器。唯一 App 通过 AppHost 注入当前能力与公共页面。`IDesktopModule` 是既有能力契约名，不代表可启动的领域产品或模式切换。

## 呈现根与窗口接缝

- `DesktopWindow` 继承 `Microsoft.UI.Xaml.Window`；`ShellWindow` 的 `WindowFrame`／`PresentationSurface` 是唯一应用内容根。能力、公共页、页头插槽、侧栏贡献及全局表面均返回 `Microsoft.UI.Xaml.FrameworkElement`。影视页的 `SwapChainPanel`、音乐完整播放器、队列、通知和确认层处于同一 WinUI／Composition 呈现树，遮挡由树内层级决定，不设置视频内容 HWND 或控制条追位窗口。
- 窗口公开 `Handle`、`Root`、`Resources`、`Title`、`Show`／`Hide`／`Activate`／`Resize`、`WindowState`、可取消的 `Closing`、`Closed`、`Deactivated`、`VisibilityChanged` 及 `SessionEnding`。调用方设置 `Content` 后调用幂等的 `AttachRoot()`，绑定树内 owner 和公共字体；`DesktopWindow.GetWindow(view)` 只沿当前树解析所属窗口。
- `NativeCaption` 使用 `AppWindow` 系统标题栏按钮和 `InputNonClientPointerSource` 的非客户区准入；布局坐标仅用于声明标题栏可拖动／交互区域，不搬运应用内容。普通 Win32 顶层窗口系统集成通过 `SetWindowSubclass` 接收消息，不保留 WPF `WindowChrome`／`HwndSource` 可视壳。
- 请求 owner 通过 `MeowFilm.Threading.OwnerDispatcher` 使用同一 Windows App SDK `DispatcherQueue`；调度任务保留完成、异常、排队取消与关闭拒绝语义，计时器由所属队列创建。调度器关闭不等待业务任务；进程内替换由各业务 owner 在关闭队列前完成所需排空。XAML 窗口、控件、资源、输入和动画由 WinUI 持有。

## 导航、页头与全局表面

- 只有一套统一侧栏、内容入口、设置入口和 App 页头。顶部媒体入口按 App 注册顺序显示音乐、影视、小说，进入各 owner 声明的第一条首页；下方导航及“我的”分组仅展示当前 owner，不重复首页入口。能力仍由原实例持有，媒体选择不重建业务服务。`NavigationItem.IconData` 可提供线框图标；未提供时沿用符号字形。`IDesktopSidebarSource` 可贡献 owner 自有的侧栏内容及有界设置请求，切换时仅挂载当前贡献，设置请求只接受当前 owner，窗口关闭退订；小说据此显示真实启用书源及导入入口。
- `ShellWindow` 持有窗口标题、系统按钮、导航／搜索请求及其 owner；`DesktopPageHeader` 是可独立复用的贡献呈现组件，拥有标题、插槽、动作的视觉布局、源订阅及操作生命周期。同一返回／前进控件树进入组件 `Navigation`，同一搜索框进入 `Center`，不复制控件或搜索状态。当前页面通过 `IDesktopHeaderSource` 提供标题、有类型操作和内容插槽，普通能力主视图的搜索准入用于登记全局搜索 owner。
- 普通页头与窗口 caption 共用 64 DIP 高度；侧栏宽 216 DIP，正文顶距 22 DIP。公共组件按导航、标题、贡献动作测量剩余区域，搜索在其中居中，最高宽 430、高 34 DIP，窄窗口缩小而不覆盖窗口按钮或贡献动作。设置左邻铃铛仅呈现图标，当前不接通知业务／未读状态，现有操作通知保持原样。播放队列和通知仍位于页头下方。
- Shell 在首次显示前按主显示器整屏物理像素计算默认窗口：宽为 `screenWidth × 1300/2560`，高为 `screenHeight × 850/1440`，再按该 HWND 的 DPI 换算为 DIP；2560×1440、96 DPI 对应 1300×850 DIP，其他分辨率保持相同整屏占比。现有 860×600 最小尺寸按相同屏幕比例与 DPI 缩放，不能夹大默认值。`Resize` 将 DIP 交回物理窗口尺寸，`DisplayArea.WorkArea` 和 `AppWindow.Move` 完成首次居中；`WM_GETMINMAXINFO` 约束最小跟踪尺寸。
- 页头贡献绑定组件持有的展示代次和取消边界。切页、设置、关闭或换源时先撤销旧操作准入、订阅与插槽；卸载时释放这些资源，重新加载读取当前贡献并建立新代次。已访问能力的内容树隐藏保留不延长其页头生命周期，壳按展示切换显式退休页头，不等待内容树卸载；通过不透明位置恢复历史同样经过请求的页头代次边界。源事件在 UI dispatcher 上核对源和代次，切走再返回同一源也拒绝旧事件；同源刷新保留同一插槽实例及其编辑／焦点状态，替换的动作项不能再执行。动作触发页面切换后由页面请求自己的生命周期接管，旧页头令牌和迟到完成不能覆盖新标题、操作或状态。
- 模块属性通知按待派发的 Dispatcher 批次合并外壳失效，导航与视图替换分别保留；每批读取当前事实并集中呈现，不为每个播放／业务属性重复排队重建外壳。同一展示代次、顺序和动作实例的页头刷新保留动作项与按钮，只更新实际变化的启用态；真正替换／重排动作或退休展示时才更新动作集合，执行时仍检查当前准入。
- 设置页的标题显示在返回按钮右侧，其内容插槽紧随标题，不使用右侧页头操作区；所有普通内容区都直接衔接紧凑页头，不再叠加额外顶距。
- `DesktopHeaderContribution.Slot` 接受页面自有的 WinUI `FrameworkElement`；`SlotPlacement` 支持 `Automatic`／`Leading`／`Trailing`。自动布局在显示标题时把插槽放在标题后，否则放在右侧动作前。页面可自行组合标签、筛选或多行内容并选择插槽位置，组件不读取页面身份。组件调用方以 `headingFallback` 选择显示标题，普通页面的标题继续只更新窗口及内容无障碍名称，不新增可见标题。设置集成通过 `PageHeaderHost` 访问其标题／插槽，不在窗口保留旧布局名称别名。
- 搜索框只存在一份。App 装配 `IDesktopSearchPage` 时，壳通过其 `Targets`／`SubmitAsync` 将不透明目标与关键词送到同一公共结果页；页头 `SearchTargetId`／`SearchContextId` 只提供上下文默认值，不含业务分派。仅页面／结果分类变化自动联动目标，普通刷新不覆盖手选；手选不提交、不改输入。未装配公共搜索页的消费者保留原搜索 owner 入口。设置及从设置进入的设置型公共页隐藏并禁用搜索。模块草稿按 owner 与 route/search 隔离；公共搜索页单独保留未提交草稿与 `Query`，播放返回不会把它写入原模块草稿。
- `IDesktopSearchPage.ContentOwner` 允许应用将 owner 原有视图投影进公共页，进入时从原主宿主解除，`Deactivate` 时归还；没有第二份控件树。该 owner 在公共页内仍可发出其普通内容导航。公共页以 `Destination.PageId` 与模块不透明位置一起进入同一历史序列，返回／前进按历史恢复原页，不另存 origin 或使用返回搜索捷径。具体四分类与影视／音乐编排属于 Capabilities 的[统一结果页](film-search.md)，Desktop 不依赖业务类型。
- 能力 View、SettingsView、PlayerView 仍由其 owner 持有，不因导航重建。壳在首次访问时挂载能力主视图，设置视图在首次打开时挂载；已访问的主／设置树切换时仅隐藏，返回直接显示原树，保留控件、选择和滚动。未访问能力不挂载、不初始化业务。隐藏表面的图片监听、动画、分页及业务读取由对应组件／领域 owner 停用，壳拒绝非当前能力的普通导航意图；播放器仍按独立 owner 准入。普通公共页退出即卸载可视树。需要保留转场或分页状态的表面由能力主视图内部持有，不把公共页留作隐藏 chrome。
- 普通页面只有一个固定底部 `PlayerHost`。壳保留最近一个提供非空 `PlayerView` 的能力作为播放器 owner：切到不提供播放器的影视能力或普通公共页时继续挂载同一实例，切到提供新播放器的能力时才替换 owner；owner 后续替换或撤回 `PlayerView` 时同步刷新。设置及从设置进入的设置型公共页只隐藏播放器和关闭详情／队列，不销毁播放状态，离开设置后恢复同一实例。
- 完整播放器页与播放队列由当前播放器 owner 的 `IDesktopGlobalSurfaceSource` 提供，可在影视等其他普通业务页及普通公共页上打开；播放器发起的已声明音乐路由也可从这些页面返回其 owner。设置展示期间拒绝播放器浮层和播放器导航。普通底栏、详情和队列复用同一播放身份、队列、进度事务和播放引擎，不维护第二套播放状态。
- 播放器页整体挂在 `PlayerDetailHost`，覆盖窗口全部布局行。首次入场直接挂载播放器基座并开始整个表面的滑入；封面、取色和歌词分别由原 owner 异步加载，不是展开或关闭的前置条件。歌词区原位显示一行“加载中…”、歌词或一行“暂无歌词”，真实失败保留错误语义，不将这些内容状态发成全局通知。关闭取消当前读取，迟到回执不能重新展开或改写新身份。详情、自有底部播放条及收起入口属于同一个表面，图像持有至实际卸载；系统最小化、最大化／还原和关闭按钮仍由同一窗口的 `AppWindow.TitleBar` 持有，不复制到播放器控件。详情页底栏不显示重复的小唱片及其占位，普通页封面入口保留。普通页面侧栏、右侧内容和底栏在下方保持挂载、尺寸及样式，退出恢复原页面，不挪用其底部控件。页面退出退休自身拖动和弹层，设置通过收起后普通页面的统一入口进入。
- 频道横向分页和播放器详情可在各自视图内部保留滚动、歌词、筛选、焦点和转场状态，不建立第二套普通页面菜单或页头。
- Navigation 在 UI dispatcher 修改，route 在能力内唯一；内部事件绑定实际订阅实例，隐藏/关闭/旧展示代次的意图拒绝，不依赖全局总线。
- 显式导航／搜索成功且未取消后，壳通过 `IDesktopModule.CaptureNavigationLocation()` 捕获 owner 不透明字符串，与原请求的 Value/IsSearch 一起保存；同 query 的本地／在线搜索由 owner 位置区分。模块历史的返回和前进通过 `RestoreNavigationAsync(location, cancellationToken)` 原样交回位置，不重放显式搜索／导航；公共页历史按 `PageId` 激活原 PageSession。返回成功才保存离开的目标，前进成功才消耗目标；新显式导航成功清除前进分支。设置、队列和全屏播放器暂时禁用但不消耗前进，公共页不再禁止历史前进。历史只保存能力、原请求、位置和可选公共页 ID，不复制控件树，不解析 owner 字符串；领域 owner 检验保留状态、账号及数据有效性。恢复与其他请求共享串行、取消、超时、重试和迟到回执所有权。影视实例保留和读取恢复见[播放页](film-playback.md#页面保留与读取恢复)，Q19 真实导航行为尚需单独验收。
- 可选 `IDesktopNavigationRetention` 接收 UI dispatcher 上的不透明位置集合：实际返回／前进历史、当前模块／设置返回位置，以及在途或可重试的恢复目标；公共页不再另设 origin 保留项。隐藏模块的陈旧 `Current` 不算导航入口。owner 另外保留自己当前选择的资源，并自行释放既非当前选择也无导航入口的状态；壳不解析位置、不决定领域缓存。
- 请求按真实能力串行，30 秒是有界观察而非终止底层任务的证明；取消、超时、替换不能让旧回调覆盖新展示。失败保留重试和必要原生通知，不把异常内容/凭据塞进页面。
- Shell 发起的初始化、导航和搜索若抛出错误，由 Shell 唯一上报；能力通过 `IDesktopModule.OperationErrorText` 只提供固定、脱敏业务错误文本，不同时发布自身通知。能力控件自行处理并结算的操作仍由其原生通知出口拥有；新的 Shell 请求撤销上一模块操作状态及迟到通知准入。存储格式错误明确失败并保留原始数据。
- 当前内容以推荐路线 `catalog-home` 开始；初始化恢复 App 必需的侧栏曲库、设置和当前队列。“我的”分组右侧的创建动作打开能力主视图内的歌单浮层，不创建第二个窗口；收藏和自建歌单详情不显示平台账号或录音身份选择。自建歌单的重命名／删除仍由实际详情页向唯一 Shell 页头提供有类型操作，收藏详情不提供页头操作；歌曲添加目标通过歌曲上下文菜单选择。
- 公共页的受控导航核对实际注册实例、可见状态、route 和操作代次；当前 App 不装配下载管理页面、导航或后台任务。
- 退出窗口立即关闭，没有 5 秒观察门禁、继续等待／强制清理／取消退出状态机。AppHost 停止播放器及所属运行器并让数据库完成已受理事务；不等待所有业务 reader、网络请求和进程内对象析构。`ShutdownCompletion` 只记录回调结束，不冒充完整业务资源释放。
- `PrepareForRender()` 只在共享 UI 线程挂载视图，不显示窗口、不触发 Loaded、不初始化业务；真实渲染和桌面像素检查必须另行加载可见 WinUI 窗口。

## 资源、输入与公共控件

资源入口为 `ms-appx:///MeowFilm.Desktop/Theme.xaml`，在同一 WinUI `Application.Resources` 中合并 `XamlControlsResources` 与 Desktop 资源。普通状态由公共样式和 WinUI 控件模板承载，高对比使用 `ThemeDictionaries` 的系统色。能力可覆盖明确语义色，不复制未知资源或按领域名分支。`NoticeOutlet.SetReceiver(root, receiver)` 登记树内通知接收者，`Publish` 只向 owner 所在 `XamlRoot` 发送；Shell 的 `NoticeStack` 统一展示，不向页面插入解释性文字。`Message` 保留同端点状态替换语义；一次性操作结果使用 `Post`，以独立事件身份发布，不随页面重绘或重新显示重发。

应用菜单使用 WinUI `MenuFlyout`；`MeowFilm.ContextMenu`／`MeowFilm.MenuItem` 分别作用于 `MenuFlyoutPresenter`／`MenuFlyoutItem`。图标、快捷键文字、勾选、子菜单及键盘访问沿用对应原生控件，不重造 WPF 菜单模板。文本控件的编辑菜单由 WinUI 提供，不拦截或重建剪贴板、拼写／IME 操作。窗口级 `PlayerMenuInput` 只协调当前菜单与队列的互斥和释放，不接管菜单的原生指针路由。

底栏空白使用 `PlayerSurfaceInput` 配对同一轮左键 Down/Up，按下时记录活跃窗口及浮层／队列状态；按钮、滑条等不通过此准入。`DesktopWindow.NativeMessage` 观察窗口的 `WM_MOUSEACTIVATE`、原生按下和取消消息，WinUI 根监听按键、指针释放及取消；鼠标激活窗口的那一轮只激活，关闭层不使本轮重新准入，不使用延时冷却。队列事实登记在当前 `XamlRoot`，已活跃窗口中按下底栏空白关闭队列并消费该轮。释放 `PlayerSurfaceInput` 时取消资格并退订窗口／根事件。状态机或命令准入测试不证明真实桌面激活消息实序。

`QueueSurface` 是同一根内的 WinUI `Border`，设置 `CornerRadius=12`、`ThemeShadow` 与 Z 平移，`QueueHost` 挂载 owner 的真实列表。圆角内容、阴影和跨层遮挡由 WinUI 合成，不使用另一个透明窗口。队列业务的行、播放和删除动作由原 owner 持有；圆角与前景像素仍须由真实桌面截图断言验证，不能用仅检查属性替代。

全应用不提供普通文字悬停提示，包括按钮说明、歌名／片名全称、截断简介、音源信息和禁用内容说明；仅保留进度条的时间预览。控件的无障碍名称、菜单、内容预览和真实操作错误通知不受影响。

普通页面由窗口提供应用渐变背景，侧栏对独立的应用背景层做坐标对齐的轻量模糊并保留右分隔线，内容区和底部 `PlayerFrame` 保持透明；播放器页由其根视图提供当前封面派生背景（高对比时为系统窗口色），详情与自有底部播放控件透明。播放控件只通过 `Capability.Player.*`（前景、正文、次要正文、悬停、按下、进度轨道）适配所在底色，播放器页仅为自身底部控件替换这一组；弹层是独立浮层，保留自己的表面色。

`MeowFilm.OverlayScrollViewer` 使用 WinUI `ScrollViewer` 的原生 overlay 滚动条，纵向 `Auto`、横向 `Disabled`；`OverlayScrollChrome.Enabled` 只调整纵向可用性，不承载业务分派。指针、触摸、键盘、自动隐藏、高对比和动画由 WinUI 及系统设置管理，不保留 WPF 输入预处理、私有 thumb 捕获或自建 900ms／160ms 时钟。

全应用字体默认由 Desktop 的 `Typography.xaml`／`Theme.xaml` 拥有，普通文字使用 WinUI 的 `XamlAutoFontFamily` 和常规 `Normal`，正文默认 14 DIP；代码侧权重使用 `Microsoft.UI.Text.FontWeights.Normal`。标题、按钮、选中态与歌词不加粗，层级只通过字号区分；图标使用线框 Path 或专用符号字体，正文不继承图标字族。`DesktopTypography.Install` 在窗口创建时幂等安装资源，`AttachRoot` 应用根控件默认；阅读器正文的用户字体、字号与加粗仍由阅读器负责。字体光栅化、布局取整和透明表面合成使用 WinUI，不保留 WPF `TextOptions` 或强制 ClearType／灰度策略。

侧栏项保留系统焦点轮廓和绑定到实际 owner／目的地的无障碍名称。搜索输入、图标、类别选择与竖向分隔位于同一字段；`Ctrl+K` 和 `Ctrl+F` 聚焦同一输入，普通 Enter 通过唯一壳入口提交。放大镜为同一 `EqualWidthComboBox` 入口内不接收输入的图形，点击图标或类别均由下拉接收，没有独立搜索按钮；类别选择不提交搜索。`EqualWidthComboBox` 约束原生 Popup 内容宽度，选项单行省略；原生弹层与入口的最终左边缘对齐保留真实坐标断言，不以属性相等冒充桌面结果。

## 验证边界

测试进程只创建一个 WinUI `Application`，通过 `TestUiHost.InvokeAsync` 共用 UI 线程，各用例拥有自己的窗口和业务替身。真实控件操作使用 WinUI AutomationPeer 的 Invoke／Selection／ExpandCollapse／Scroll 等语义接口，不使用 WPF `RaiseEvent`、假键盘或系统键鼠注入。搜索／快捷键的内部准入测试不宣称覆盖物理输入实序。

需要像素的测试使用实际加载的 WinUI 窗口。`NativeDesktopCapture` 先调用 `TestDesktopContext.Verify(requireTarget:true)`，再以 `XamlRoot` 对应 HWND、`ClientToScreen` 和 `RasterizationScale` 定位采样，经 Composition rendering／`DwmFlush` 后读取真实桌面像素。`ScreenPoint` 仅用于测试采样，产品没有该追位逻辑。普通 XAML `RenderTargetBitmap` 检查不代表 SwapChainPanel、Composition 特效或桌面遮挡已验收。

本文记录实现和契约，不是通过证明。当前迁移仍需正式 Windows 编译及选定的真实 UI／像素验收；侧栏模糊与前景隔离、缩放、队列圆角／阴影、全局层遮挡、系统高对比和托盘菜单分别以实际结果记录，不由静态检查或编译成功代替。

## 通用外观与主题

- AppHost 注入 `IDesktopAppearance`，壳在模块设置类别之前提供“通用”。通用页不伪装成业务模块；业务通过 `IDesktopGeneralSettingsSource` 分别贡献外观、首页数据源、缓存控制内容，壳仅负责区域切换，设置返回仍恢复最初页面且不重放导航。
- `AppAppearance` 独占原生文档 `app.appearance.v1`，严格字段为 `{schema:1, themeColor:"#RRGGBB"}`。缺失只采用默认、不写盘；损坏或未知版本不覆盖，通知并允许重新读取。保存使用读取指纹 CAS，失败后权威重读、不重放写入；已受理数据库写入在关闭前完成；同进程重置先封闭 owner 再释放存储。该 namespace 不开放给脚本桥。
- `MeowFilm.Brush.AppBackground` 只属于壳的普通页面背景。默认纵向渐变为 `#E4F2FC/#EBF5FC/#F5F8FB/#F7F9FC`，位置 `0/.24/.58/1`，从浅蓝过渡到近白，底部不再变深；自定义色按各层固定权重着色，保留浅底与中性过渡，不铺纯色。存储的 `themeColor` 与默认选择标识 `#E6F3FB` 不变，不重置用户已保存的主题。普通页主操作为绿色，错误、删除、收藏心形及平台标识可保留语义红。
- `SidebarBackdrop` 的 `CompositionVisualSurface.SourceVisual` 只引用独立的整窗 `ApplicationBackground` 背景兄弟层。效果实现依赖 `Microsoft.Graphics.Win2D` 1.4.0；`GaussianBlurEffect`（`BlurAmount=12`、`BorderMode=Hard`、`Optimization=Speed`）编译为 `CompositionEffectBrush`，输入是该专用 surface；先对完整背景模糊，再由侧栏本地 `ContainerVisual` 的 `InsetClip` 限定输出。保留背景原尺寸与树内偏移，不把渐变缩放到侧栏，也不在侧栏边缘截断采样核；不使用会采入前景的 `CompositionBackdropBrush`，不模糊文字、图标或影片。
- 材质覆盖 40% 白色轻层，保留右侧及分组分隔线，主内容侧用 30 DIP 透明渐隐柔化交界；装饰不接收输入，随侧栏退出沉浸画布。背景颜色由同一源 visual 实时更新，尺寸变化更新 surface／sprite／容器，保留效果实例；Source 更换重绑定，卸载退订源事件并释放 Composition 资源。没有逐帧截图、取样自身祖先、桌面 Acrylic 或窗口坐标追位。高对比初始呈现和后续切换均隐藏模糊、白色轻层与交界渐隐，使用系统窗口背景和系统边界色。
- 影片播放页与其他普通页面透明透出同一 AppBackground，不再由海报生成环境色或铺满信息区。全屏音乐播放器与小说阅读器保留独立专用画布及各自的用户配色、夜间和背景设置；音乐底栏背景/进度仍跟随歌曲或固定播放器配色，主播放按钮使用绿色。视频像素区及视频浮窗保留黑底，高对比仍优先系统资源。
- `FloatingSurface`、`ItemHover`、`ItemSelected`、`InputSurface` 从同一 `ThemeColor` 派生浅色弹层、悬停、当前及搜索底色。AppHost 当前只装配一个 Shell 和一个 appearance owner；同一 UI 线程的重复 Theme 字典共享可更新的 `SolidColorBrush`，调色直接更新 `Color`，不复制主题设置或遍历控件树。这不是同一 UI 线程多个独立主题 owner 的契约。高对比由 `ThemeResource`／系统色字典处理，不使用 WPF 资源冻结机制。
- 公共 `MeowFilm.ComboBox` 与设置派生样式共用 `MeowFilm.SettingsComboItem`；弹层沿用 WinUI 原生模板，选项保留字体、悬停、选中、禁用及高对比状态，最大高度由 `MaxDropDownHeight` 控制。需要等宽的选择器使用 `EqualWidthComboBox`，不以长标签撑开弹层。
- `SettingsScrollChrome.xaml` 仅用于设置页和其中列表，纵向滚动条常显且不创建空闲计时器；普通页使用 WinUI 原生自动隐藏滚动条。

## 媒体快捷键设置

- AppHost 先加载 `AppMediaControlSettings`，再通过 `ShellWindow` 构造器末尾的 `mediaControls` 参数注入；上游 `appearance`、`closePreferences` 及内部测试端口 `trayIconFactory` 保留原位置。壳只读 `MediaControls` 暴露同一 `IDesktopMediaControlSettings`，将其交给通用设置的“快捷键”区域和窗口附件；“窗口行为”继续使用同一关闭偏好 owner。Desktop 设置不依赖音乐能力，不创建播放器或全局键注册。
- 原生文档独立为 `app.media-controls.v1`，严格字段为 `{schema:1, globalEnabled:true, systemMediaEnabled:true, bindings:[{action:"TogglePlayback",local:{key:"P",modifiers:1},global:{key:"P",modifiers:3}},…]}`。`action` 使用媒体动作枚举名称，`key` 使用 `Windows.System.VirtualKey` 名称，`modifiers` 使用 `Windows.System.VirtualKeyModifiers` 位值（Control=1、Menu/Alt=2、Shift=4、Windows=8）；`null` 表示清除。八个动作各有一项，翻译的 `global` 必须为 `null`。不迁移或改写外观、音乐文档，不向脚本开放此命名空间。
- 默认两开关开启，彼此独立。应用内键依次为 `Ctrl+P`、`Ctrl+Left`、`Ctrl+Right`、`Ctrl+Up`、`Ctrl+Down`、`Ctrl+L`、`Ctrl+D`、`Ctrl+T`，对应播放／暂停、上一首、下一首、音量加、音量减、喜欢歌曲、打开／关闭桌面歌词、源译文显隐；前七项的全局键在应用内键上增加 `Alt`，翻译全局列不提供编辑。
- 缺失文档只采用默认值，不写盘。读取失败、损坏或未知版本撤销输入及编辑准入，保留原文档并通过 `NoticeOutlet` 通知，允许显式重新读取。保存按上次读取指纹 CAS；失败后权威重读，已接受写入的丢失回执可以恢复为成功，未接受的草稿不发布、不重放。偏好快照不可变；保存成功后才通知输入及系统媒体消费者。
- 通用页沿用原有 32px 左右边距和设置滚动条。“功能说明／应用内快捷键／全局快捷键”展示八行；按钮可通过键盘及自动化访问，录制在当前 WinUI 根的 `PreviewKeyDown`／`PreviewKeyUp` 消费候选按下、长按与释放，并隔断 Shell 固定导航。纯修饰键不提交；Esc 取消、Tab 离开、Delete／Backspace 清除。候选可即时保存并结束视觉编辑，但同一 `IsRecording` 保持至所消费的候选／控制键全部释放，期间不恢复全局注册；候选保存中保留输入按钮的焦点资格，避免控件禁用导致提前失焦。失焦、隐藏、卸载、窗口停用或显式取消输入会话均可立即释放该状态，保存中同样可清理。内部不同动作跨局部／全局重复、`Ctrl+F`、`Alt+Left`、裸 Esc／Space 均拒绝保存，同一动作共享局部／全局组合允许。冲突就地显示并通过通知反馈，键位始终恢复权威值。
- `IsRecording` 和 `GlobalErrors` 仅为运行时状态，不持久化。设置 UI 不探测、不推测占用；运行时附件以 `SetGlobalErrors` 发布真实注册结果，行内保留“热键被占用”或其他具体错误。重新检测只通过 `RequestRegistrationRefresh` 事件请求附件，设置未恢复、全局关闭或正在录制时不发出请求。
- 媒体设置位于 `AppShutdown.sharedParticipants` 首位，其后依次为外观和关闭偏好。同进程重置先同步令 `IsAvailable=false`，撤销编辑、输入及录制准入，封住全部系统媒体请求（包括 Pause／Stop）和自定义快捷键，再等待已接受保存实际结算；外观或关闭偏好保存未结算不能延后封口。退役是单向过程，失败不重新开放旧代输入；只有新装配的 owner 才重新接收工作。关闭询问及隐藏到托盘不进入真正退出准备，继续遵守各自开关。启动失败同样由生命周期所有者排空设置后才释放存储。

`DialogPlacement.ShowAsync(owner, ContentDialog)` 使用 owner 已有的 `XamlRoot`，所属控件卸载或隐藏时关闭；原生 `ContentDialog` 拥有正常关闭时的焦点恢复，公共机制只在关闭交接期间拒绝已隐藏或被业务 `canRestoreFocus` 判据退休的原焦点，不重复恢复或改写滚动位置。窗口移动由正常呈现树处理，不再为表单注册窗口移动追位。播放器内的 `PlayerDialogHost` 是同一根内的模态 Grid，拥有外部点击／Esc 关闭准入及 owner 退场清理。`ColorSelection`、设置下拉框和栏目间距资源由 Desktop 共用，业务设置页只负责提供选项和提交至各自 owner。

## 系统托盘与关闭选择

- Windows App 注入唯一 `IDesktopClosePreferences`。主窗口的标题栏关闭、播放器页关闭和 Alt+F4 使用同一关闭入口；默认询问「最小化到托盘」或「退出」，可勾选「记住我的选择」。取消或 Esc 不保存、不退出；未勾选只执行本次操作。普通最小化按钮仍最小化到任务栏。
- 通用设置的「窗口行为」提供「每次询问」「最小化到托盘」「退出应用」。设置和关闭提示都提交到同一 owner；设置立即保存，关闭提示记住选择时必须收到保存成功后才执行动作。保存失败保留可操作界面及统一错误通知，不把草稿当成已保存偏好。
- `AppClosePreferences` 独占 Windows 原生文档 `app.window.v1`，字段为 `{schema:1, closeBehavior:"ask"|"minimizeToTray"|"exit"}`。缺失采用 `ask` 且不写盘；损坏或未知格式不覆盖。保存使用读取指纹 CAS，失败后权威重读；设置页可重新读取。该 namespace 不开放给脚本桥，已受理数据库事务在 App 退出前实际完成；设置更改在运行期间即提交。
- Shell 拥有托盘实例，在 HWND 可用后注册。Windows App 编译期嵌入 `Desktop/Assets/Meowfilm.ico`（SVG 来源沿用现有绿色 `m` 品牌），托盘复制窗口的原生图标并独立释放，不依赖 `dotnet.exe` 宿主图标。最小化到托盘仅隐藏主窗口，保留播放、系统媒体会话、自定义全局快捷键、运行器和窗口状态；媒体开关仍独立生效，恢复时还原此前正常／最大化状态。隐藏前必须确认图标可用，注册失败保持窗口可见并提示。
- `ShellWindow.IsHiddenToTray` 只读发布这一次托盘隐藏事实，先于子视图可见性变化置位，恢复显示前复位。Film 仅据此保留已有实际输出、attempt 和媒体通道；准备中无输出不保活，恢复不重新开播。普通页面离开停止原生输出，显式 PiP 可继续保留；整进程退出请求停止输出，同进程重置仍确认资源释放。
- 托盘主激活恢复主窗口；「显示主窗口」「退出」使用原生 `HMENU`／`TrackPopupMenuEx`，键盘、外观、高对比、位置和取消由 Windows 系统菜单负责。菜单打开期间创建零尺寸、无 XAML 内容的 Win32 `STATIC` tool-window 作为前台 owner，结束立即销毁；主窗口可保持隐藏，这个系统菜单辅助 HWND 不是第二个应用内容壳。Explorer 重建任务栏后重新注册，恢复失败让主窗口重新可达；真正关闭时移除通知图标并释放句柄、订阅及菜单。
- 托盘显式退出绕过询问和已记住的最小化偏好，直接执行退出。重复请求不重复弹窗或启动收尾；退出不显示清理失败等待界面。`WM_QUERYENDSESSION` 正常交给系统处理，不异步等待或否决；仅收到 `WM_ENDSESSION` 且 `wParam != 0` 时一次发布 `SessionEnding`。Shell 的 `PrepareForSessionEnding()` 幂等，AppHost 可订阅同一事件登记退出；取消的系统注销不退休应用，不因关闭偏好弹出选择。
- 未注入关闭偏好的独立 Shell 消费者继续直接使用其注入的 shutdown 回调，不生成无 owner 的窗口行为设置。实际 App 始终注入偏好。


## 清除全部数据

设置→通用→缓存控制的“本机数据”提供“清除全部数据”，仅在 App 注入清除回调时显示。确认浮层列明不可撤销的本机范围以及云端/外部原文件保留；默认焦点在取消，Esc 取消，打开或取消都不调用清除。确认后禁止并发提交、关闭选择和旧页面导航；不能通过失败后的取消恢复部分清理的旧代。

清理忙碌、失败、重试和退出留在操作浮层，错误使用统一通知组件。失败可重试清除或直接退出，待清除标记由下一次冷启动完成；成功由 AppHost 关闭退役 Shell 并在同一进程打开初始界面，不显示“需重启”。系统注销/关机仍遵守 OS 会话结束，不阻止系统退出。数据范围和恢复规则见[本机数据清除](protected-storage.md#clear-all-local-user-data)。
