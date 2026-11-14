# Windows 小说

共同业务、共享纯规则与排版位置语义见[小说业务与阅读契约](../common/novel/reading.md)。本端使用 `source@0.3.0` / `source.runtime.v2` 的运行器书源仓库，RPC 与受信图片见[运行器书源仓库](../runtime/source-library.md)。

`NovelCapability` 装配在影视之后，拥有小说首页、分类、书籍页与原生阅读路由；统一搜索显式区分音乐、影视和小说会话。顶部媒体“小说”进入 `novel-home`，该首项不在下方侧栏重复展示；下方保留 `novel-categories` 分类入口。独立书架／历史页面已移除，不迁移或删除原生库数据，也不另建替代页面。阅读请求 Shell 隐藏侧栏与播放器控件，保留音乐播放 owner；非沉浸页面继续提供原有 Header 契约。设置沿用壳的小说类别，源编辑稿只在当前窗口内存在，不形成原生书源仓库。

## 首页与分类

首页与分类共享一个活动书源、源返回入口及已加载书籍。首次准备来源投影后，默认选择排序中首个启用、允许发现并同时声明 `exploreKinds`／`explore` 的来源；后续由页面右上角或侧栏显式切换，不跨源聚合，不因失败或来源停用而静默换源。页面只展示运行器返回的书名、作者、简介、封面、`kind`／`wordCount` 等字段及真实来源；没有统一评分、完结筛选、热度排序或综合榜的推断实现。

`exploreKinds` 的返回顺序保持不变，空 URL 作为分组标题而非可请求入口；分类页据此组织分组和入口，首页使用真实可请求入口作为标签。`explore` 使用当前入口 URL 和连续页码，按来源书籍身份去重并保留返回顺序；“更多”保留已加载页，无新增书籍时结束分页。首页保留选读与本地继续阅读、常规宽度六列封面；分类保留三列图文条目、原生分组与入口面板，窄窗减少列数并纵向排列选读／继续阅读。选读是当前结果首本，不推断跨源推荐或排行；无阅读历史时不生成继续阅读书籍。开始阅读、打开详情、收藏与接着读沿用现有规则、Library 和书籍会话；发现页收藏不会切换当前阅读会话，同书跨源的加入操作不会反向取消已存在收藏。

`NovelDiscoveryState` 区分加载、正在取消、成功、空、失败、已取消和不可用。同源同分类刷新暂存入口与首批书籍，仅成功后整体替换；失败／取消保留已展示成功数据、入口、已提交页码及“更多”状态。切源／切分类立即清除旧目标书籍；更多失败保留前页并重试同一页。失败使用统一通知及现有可重试浮层，不把错误说明永久插入页面。取消等待实际来源调用结束才退出加载状态；新代次、来源 revision 或可用性变化隔离旧结果。来源失效时保留所选身份、清除旧书籍与入口；可用来源的新 revision 通过刷新或恢复发现路由重载，停用／删除的来源保持不可用，直到用户显式选择其他来源。呈现更新为实际书源／分类控件保留焦点标识，控件重建加载后恢复对应控件；焦点已移到外层搜索等控件时不抢回。

`NovelCapability` 实现 `IDesktopSidebarSource`，保留同一个 `SidebarView`，只投影真实已启用书源；当前源可切换，无发现能力的行不可请求。`Sources.Changed` 更新来源投影，不形成第二书源库。管理图标发出 `SettingsRequested`，Shell 仅为当前 owner 打开既有设置，不增加设置路由或重挂 SettingsView。侧栏导入及空页导入复用现有网络／内容预览和多选确认；HTTP(S) 地址使用 URL 预览，确认后只导入选择定义，不重复下载，也不强制改写源的 enabled 标记。

来源未返回封面地址时，发现页保留封面比例并显示中性“暂无封面”状态，不生成或补配其他书籍图片；该状态沿用系统高对比颜色。

## 阅读数据与生命周期

`NovelLibrary` 使用现有 `IDocumentStore` 的 `novel.library.v1` CAS 原子文档，保存稳定本地 bookId、书架、历史、阅读位置、用户净化规则与排版。存储回执后才发布快照；损坏文档不会自动覆写。运行器源列表是内存投影；源定义、Cookie、源变量由运行器持有。本地收藏、置顶、历史与锚点的数据 API 保留，不依赖运行器就绪；首页继续阅读与详情加／移书架仍消费原有数据。接着读按最新历史条目的准确 `BookId` 恢复 entry／会话及锚点，不重新按来源或规则身份选择 canonical 条目；相同来源身份或规则身份的已有条目仍可分别保留。打开书籍、显示继续阅读及恢复正文不新增或改写 ReadAt／Anchor，实际阅读进度仍由现有保存路径提交。封面／书籍／正文图片网络调用首次准备来源投影，不要求先进入首页或设置，也不会以旧书籍恢复已停用／删除的来源。章节模型以开放 JSON 保留目录字段和规则上下文，正文／购章返回的变量更新与原字段一并进入后续调用。

保留书籍会话的加／移书架在本地串行写入内读取当前持久成员事实；删除历史后，不从旧会话回填 ReadAt 或 Anchor。即使无书架的历史成员已被删除，再加书架仍保留原 bookId 和当前正文，只有之后实际阅读保存才重新记历史。

`NovelRuleProgram` 通过 `source.runtime.rules.process`（HTTP `/v1/source/rules.process`）发送 `{request:<纯规则输入>}`，消费身份、归并、章节匹配、规则校验、净化／简繁与固定 URL 匹配结果。调用前取得完整输入快照，transform 一次发送全部规则和 conversion；Node 负责启停、order 稳定排序、逐条 timeoutMillisecond（默认 3000ms）及末尾简繁，URL 匹配预算为 2000ms。Windows 不内嵌规则资源或执行引擎，规则计算及校验依赖运行器就绪；本地书架、历史、偏好与净化配置存储仍独立。

规则 RPC 只接受调用方与连接取消，不套用其他 source 方法的 30 秒 HTTP 总截止时间，避免合法多规则被较短的批次计时器截断；实际执行预算由 Node 逐条落实。App 适配器等待该调用实际结束，`NovelRuleProgram` 在取消后拒绝迟到成功结果，暂停／关闭只取消并排空本 owner 接纳的调用，不关闭共享运行器。整章全部成功才提交显示数据。正文签名变化使旧块锚点失效，按当前章比例恢复；通常重排使用 blockIndex 和 UTF-16 行首位置。

保留阅读会话记录原始正文及最后成功处理的净化／简繁输入；恢复路由、继续／开始阅读以及设置普通返回显露阅读视图时，以最新输入从原始正文重处理，不重下正文。仅排版变化或等值规则重存不重复处理。处理中输入再次变化则重新处理原始正文，全部成功才提交；失败／取消保留旧显示内容及本地 bookId、章节和锚点，之后恢复可重试。

换源按候选详情计算共享身份，在正文成功后与新来源、锚点一并提交；未知作者补全后，重开本地库仍能把第三来源的同书匹配到原 bookId，保留书架、置顶与历史关系。

已有正文时，显式目录刷新及来源 revision 变化触发的自动重载先准备候选 Book、identity 和目录，完成章节匹配与正文处理后才整体提交章、正文及锚点；失败或取消保留原活动组合。重排目录按旧目标章节匹配，继续跨章使用新索引；首次仅加载 metadata 不记历史。元数据保存保留持久历史的锚点和对应正文签名，进度保存再同步新签名。

`NovelReadingSurface` 由 WPF TextFormatter 排版，提供覆盖、平移、无动画分页和上下滚动。页号不持久化；字号、窗口尺寸和图片尺寸变化重新从正文锚点定位。`ratio` 采用正文 UTF-16 长度、图片固定权重组成的逻辑进度，与 `blockIndex` 配合恢复图片内相对位置，不使用全章布局像素推算长图切片。只保留当前章节显示内容及当前视口的正文图片，图片离开视口取消并释放运行器引用，正文按显示宽度解码，不套用封面 512 像素缩略规则。小说受信二进制图片使用 `IRuntimeImageClient.ReadImageAsync(reference, cancellationToken)` 无额度重载，HTTP读取不含卡片限额机制；原有带 `maximumBytes` 的重载及公共/TMDB消费者保持原限额。原生 byte[] / MemoryStream 表示边界由类型本身处理，不用最大值模拟无限额。自动阅读在手动交互、离页、窗口失焦及末尾暂停。

布局完成通过 `LayoutCompleted` 刷新页码与章内比例标签，不发送 `PositionChanged` 或启动进度保存计时器。首页、分类及搜索封面同样按实际裁剪祖先和滚动视口取得需求；首屏外不获取，滚出清像素并取消、实际排空后释放来源引用，滚回重新获取，退休请求迟到不覆盖新封面。取消关闭后，来源 owner 的明确恢复通知重新接纳仍在视口内的封面，无需滚动或重建控件；通知随已加载且可见的布局观察订退，隐藏／卸载不获取。真实获取失败不因普通布局通知自动重试。

受控 Web 使用锁定版本的 WebView2 SDK 和系统已安装的兼容 Browser Runtime，不静默安装或分发 Browser Runtime。仅承载当前来源的登录／验证码／动态页面，每次交互使用独立临时 profile，禁止弹出其他应用、下载及额外设备权限。动态 request／rule／webview 在加载和异步脚本完成后自动回传；登录／验证码／browser 保留用户确认。Legado browser 的 value 为带最终 URL、状态和正文的响应对象，由共同 helper 适配成 StrResponse；QYSG 不推测额外返回格式。以运行器 `request.cookies` 为权威种子，普通请求头剔除 Cookie；注入后取得 SDK 实际全量 baseline，完成时以全量 Cookie diff 生成逐 origin 的 `cookieChanges`（含删除），保留 hostOnly、Domain、Path、Secure、HttpOnly、SameSite 与到期信息。结果使用共同 `value/url/headers` 封套；不混入原始响应 Set-Cookie 以免使已删除 Cookie 复活。取消／退休不提交 Cookie；返回前等待 Browser 实际退出并删除临时 profile。购章须先确认具体书籍与章节，随后才调用源声明的 payAction，取消不发购章 RPC。

host-only 与同域 Domain Cookie 互换时，先回传旧 scope 删除、再回传新 scope 写入；避免运行器按归一域／路径／名称写入时误删替代值。双向回归经真实运行器 owner 和 WebView2，检查主域的新值及子域可见性，不仅检查 diff 顺序。

验证入口：`scripts/windows/test.ps1 -Group NovelTests,SearchResultsConsumerTests,SettingsNavigationTests,RuntimeHttpClientTests,RuntimeImageTests`；后两组覆盖新增 source 方法清单、受信二进制图片路由及原元数据图片消费者。增量 App 编译使用普通 `dotnet build`，保留项目正常原生目标；不调用会 publish 并替换安装目录的 `build-windows.ps1`。在仓库根目录、正常 Windows 开发环境中执行：

```powershell
dotnet restore apps/windows/app/MeowFilm.Windows.App.csproj --locked-mode --configfile apps/windows/NuGet.Config
dotnet build apps/windows/app/MeowFilm.Windows.App.csproj --configuration Release --no-restore --nologo -nodeReuse:false
```

`NovelTests` 包括原子存储失败与第二进程恢复、保留会话删除历史后加书架、冷库直读、真实 App Node 书源仓库重启、真实规则 RPC 超时与恢复、保留多书重处理／失败取消／最新设置与普通设置返回、共享 fixtures、多源搜索、换源／章节上下文、目录显式及自动重载的整体提交／失败／取消、购章确认、真实 WebView2 自动动态获取／隔离／退休及 Cookie scope 双向 owner 回传、封面裁剪视口与迟到隔离／关闭实际排空／同控件恢复／失败布局不重试／隐藏卸载隔离、原生四模式和长图晚到锚点、布局刷新标签且不新增进度保存。发现用例覆盖真实入口与字段保留、分页去重、切源／切分类迟到隔离、取消实际收口、来源 revision／停用／删除、首次／刷新／更多失败与原页重试、刷新原子提交及失败／取消后旧数据和页码保留、应用内重试浮层、跨源同书收藏、侧栏设置事件及网络预览／选择确认、常规六／三列与窄窗布局、高对比及书源／分类焦点恢复和不抢搜索焦点。规则行为用例通过独立测试 owner 借用实际 Node HTTP 运行器；输入快照、RPC 错误、owner 排空及清数据隔离另用显式回执检查。共享 fixtures 只嵌入测试项目，不进入产品。运行器单次预算和排队预算回归归公共 source／NativeJsonProgram 测试所有。这些用例的存在不代表已对当前源码执行通过；实际结果以任务记录为准。

小说阅读流程中仍使用 WPF `Popup` 的操作回执等待真实 `Popup.Closed` 和提交任务实际结束，再一次性解绑及释放取消源；`IsOpen=false` 只是关闭请求，不作为收口回执。设置内多字段编辑、导入和确认使用 Shell 内的 `PlayerDialogHost` 覆盖层，宽度不超过 520 DIP，点击浮层外不会关闭；它保留同一提交排空、失败通知与取消语义。仍打开的手工弹窗在原位显示错误并允许重试；已关闭弹窗的非取消真实写失败交回页面或设置 owner 的现有通知，普通取消不报保存失败。Web交互先排空初始化及资源请求，释放WebView后等待环境级 `BrowserProcessExited` 确认整个进程组和用户目录资源释放，再删除临时profile并返回；不以主browser PID退出、固定延时或文件删除重试代替。`NovelTests` 的 dialog closure/cancellation/isolation、保存失败／owner 通知与 Web lifecycle 用例覆盖这些边界。

`NovelTests` 的 composition、reader、image-anchor、shell、purchase components 和 Web lifecycle 用例自动把 WPF 渲染快照写入测试 DataRoot 的 `novel-previews/`，包含空／带书首页与分类、首页继续阅读、详情、设置、净化、阅读、长图位置、沉浸壳、购章确认和受控Web浮层，文件后缀为 `-96.png/-144.png/-192.png`。WebView原生窗口另由 `CapturePreviewAsync` 写入 `novel-web-lifecycle-browser.png`，不把 WPF 渲染快照当作真实桌面或网页内容截图。可用 `MEOWFILM_SETTINGS_PREVIEW_ROOT` 改为测试 DataRoot 内的其他目录。测试选择、执行结果及截图核对记录保留在本次任务的 `tmp/`，不以历史证据代表当前源码已验证；这些用例不使用第三方真实书源、账号或付费操作，也不发行或安装应用。首页／分类图稿验收另需实际 Windows 可见窗口截图；真实 `exploreKinds`／`explore` 的来源验收也不由测试替身代替。

## 原生书源与替换管理

管理与导入行为、三协议字段和净化表单遵循[共同管理契约](../common/novel/reading.md#书源与替换管理)。

`NovelSettingsView` 的书源与替换标签使用 Desktop 的 `ManagementList`、`ManagementForm`、`ManagementImportSelection`。列表为虚拟化 WPF 控件，编辑器为原生 TabControl／表单，行启用控件使用共享开关样式。设置编辑与网络导入由 `NovelUi.DialogAsync` 挂入 Shell 的 `PlayerDialogHost`，复用桌面浮层的主题边框／表面、恢复输入行分隔、在应用窗口内居中且最大宽度 520 DIP；具体流程保留原提交、取消及 Escape 语义，点击编辑器外不关闭。阅读器内章节选择、换源及 Web 验证交互仍保留各自的 Popup 定位与生命周期。二维码图片由锁定的 ZXing.Net 0.16.11 本地解码，Apache-2.0 许可随 Windows 清单发布。
