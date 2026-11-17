# iOS 影视设置

设置首页提供音乐设置、影视设置，以及通用分组的偏好设置、缓存管理；底部独立分组为清空本机数据。影视下提供搜索净化设置、智能匹配设置、猫源、视频源、刮削设置、网盘账号、播放器设置和智能播放设置。智能播放设置收纳偏好、自动准备、Manual 匹配源管理和匹配禁用管理，与本端播放器控制分开。智能匹配下为剧集列表净化规则、集数匹配规则、电影匹配规则、站点净化设置；后者仍编辑站点名关键词，不是正则。历史继续由 Node 记录，但不在设置显示入口。播放设置及资料遵循[统一播放契约](../runtime/film-playback.md)。

普通配置直接读写原生设置文档，不以运行器在线为前提；唯一 owner、离线提交及通知规则见[共同设置契约](../common/settings-storage.md)，实际 namespace、保护和迁移见[存储](storage.md#影视设置)。本文保留 iOS 现有界面，不套用 Windows 列表或弹框调整。

手机一般三级，智能匹配规则和来源详情可到第四级，规则测试从编辑页进入独立页面。当前标题放 App 自有页头，内容只保留实际分组。分组采用公共设置卡片、动态字体与至少 44 pt 点击区；字段、选择与开关遵循[设置语义配色](architecture.md#设置语义配色)，背景／页头继续跟随主题。返回保持位置；规则草稿由 App 持有的 `FilmSettingsModel.rules` 保留，不随页面销毁。

## 播放器设置

「影视设置 → 播放器设置」提供「默认内核」与「播放控制」入口，由 `FilmPlayerSettingsController` 消费 App 共享的 `FilmPlayerPreferencesStore`。未保存时默认 KSPlayer，可选 AetherEngine；只在此设置页选择内核，播放器内没有切核入口。保存后的首选值在下一次创建媒体候选时读取，包括新播放、切集、换源；不热切当前健康实例，也不重新获取当前播放地址。持久化位置、错误及校正写入见[本地存储](storage.md#播放器偏好)。

「播放控制」共17项；下表为完整产品选项，倍率还须与所选内核能力取交集。

| 分组 | 项目 | 可选值 | 默认值 |
| --- | --- | --- | --- |
| 播放控制 | 默认倍速 | 1X、1.25X、1.5X、2X、3X | 1X |
| 播放控制 | 长按倍速 | 2X、3X、4X、6X、8X | 2X |
| 播放控制 | 快进/快退步长 | 5、10、15、20、30、60 秒 | 15 秒 |
| 播放控制 | 控制中心显示 | 开/关 | 开 |
| 播放控制 | 自动识别片头片尾 | 开/关 | 开 |
| 单击手势 | 单击屏幕中间 | 无操作、显示/隐藏播放栏、播放/暂停 | 显示/隐藏播放栏 |
| 单击手势 | 单击屏幕两边 | 无操作、显示/隐藏播放栏、播放/暂停、快进/快退 | 显示/隐藏播放栏 |
| 双击手势 | 双击屏幕中间 | 无操作、播放/暂停 | 播放/暂停 |
| 双击手势 | 双击屏幕两边 | 无操作、播放/暂停、快进/快退 | 播放/暂停 |
| 滑动手势 | 滑动调节音量 | 开/关 | 开 |
| 滑动手势 | 滑动调节亮度 | 开/关 | 开 |
| 滑动手势 | 滑动调节进度 | 开/关 | 开 |
| 滑动手势 | 短剧滑动切集 | 开/关 | 开 |
| 震动反馈 | 长按屏幕 | 开/关 | 开 |
| 震动反馈 | 滑动调节亮度 | 开/关 | 开 |
| 震动反馈 | 滑动调节音量 | 开/关 | 开 |
| 震动反馈 | 滑动调节进度 | 开/关 | 开 |

### 能力过滤与生效

- 设置按首选内核过滤：KSPlayer 显示完整默认／长按倍率；Aether 视频上限2X，默认倍率只显示 1／1.25／1.5／2X，长按只显示2X。应用提供的滑动与触觉能力不因内核名称而隐藏。
- KS 切选 Aether 时，保存边界将默认3X和长按3／4／6／8X降为2X并保存；其余支持值不变。切回 KS 只恢复选项，不恢复旧高倍率，不为每内核保存另一套倍率。
- 播放页菜单按 `FilmMediaState.engine` 的实际内核过滤，保留既有0.5／0.75X低倍速；格式回退后的实际 KS 不改变全局 Aether 首选。命令入口也按实际能力限制，接管 Aether 时超限的会话基础倍率校正为2X，校正反映到当前状态，不因校正恢复播放。
- 默认倍率用于新影视会话的首次候选；同会话切集／换源继承当前基础倍率并按实际内核校正。播放页手动倍率只修改会话基础倍率；长按使用独立临时倍率，不改默认偏好，结束还原基础倍率。取消、设置变化、attempt 变化或页面失活会撤销临时倍率，迟到结束不能恢复旧媒体。
- 步长、点按、滑动及触觉从共享偏好读取；偏好变化取消进行中的手势，新手势使用新值。控制中心开关更新当前影视卡片显隐，章节开关重新更新当前章节识别；二者分别见[系统媒体](system-media.md#影视卡片与内核边界)与[真实章节](film.md#真实章节与跳过)。

KSPlayer 的临时倍率上限8X；偏好持久化、UIKit触摸、取消／换代、实际倍率与音视频时钟分别验收，不以菜单选中值或属性赋值代替实际输出。

## Node 规则与原生草稿

规则执行的唯一实现为 [`packages/film-rules`](../../packages/film-rules/README.md) 的共享 JS，与 Windows 使用同一锁源 QuickJS-NG 0.18.0。配置文档由原生持有；正则编译与执行仍在 Node。影视纯 JSON 程序与通用脚本复用 `MeowScriptHost.framework` 原生载体，但使用独立 Runtime／Context／线程，不共享脚本状态。64 MiB 堆、512 KiB JS 栈与两秒截止是执行资源边界，不限制规则条数或文本长度。客户端不再链接独立规则引擎，也不回退到 Swift 正则、JSC、WASM 或 V8。

共享载体保留锁定派生源码的正则编译／执行／全局空替换中断能力，普通构建不现场下载或打补丁；中断不冒充正则语法错误。异常、取消或截止后排空真实资源并恢复同一程序。App 资源分发共享原生引擎 MIT 与 Unicode 表许可。

原生编辑分为搜索净化、剧集列表净化、集数匹配、电影匹配四个独立页面。每页纵向排列规则块，标题行右侧提供删除；匹配规则和替换内容在同块内纵向编辑。`flags` 不显示，编辑时保留原值，缺省仍由运行器按 `i` 处理。添加、修改、删除均只改本页草稿；页头唯一保存一次提交本页整组，失败保留编辑。恢复默认只读取当前组默认到草稿，仍需保存才生效。独立测试页使用公共菜单行容纳测试文本、测试按钮与最终结果，使用本页草稿与必要关联组的已保存配置，不混用其他页草稿、不保存；隐藏或退出取消测试并拒绝迟到结果。

`FilmRuleSettings` 在原生保存边界完成同语义的纯字符串规范化、默认值及新规则文本解析，不编译正则；草稿测试不提前套用保存归一化，匹配规则 JSON 内的 pattern／replace 保持原文。空集数匹配组仍返回清理结果与 previews，同时报告“无匹配规则”，不自动回填规则。

`FilmRuleService(settingsDocument:connection:)` 的 initialize、保存和默认值直接访问原生文档；分组保存通过 `FilmSettingsDocument.update` 在最新规则上只合并本组，保留其他原始组字段。`FilmRulesDraft` 仅替换保存成功的那组草稿，其他未保存组原样保留。只有 `rules.group.preview`／`rules.preview` 等实际执行借用 App runtime epoch，关联组读取 Node 当前已保存快照；iOS 不通过 HTTP 修改规则。正常空／非法正则的业务结果与运行器不可用明确区分；离线仍可保存，测试执行真实失败，不启用旧引擎。

initialize、两种 save、defaults、restoreDefaults 及两种 normalize 在 actor 的无 await 执行段完成本地读取／CAS／发布，不排在在线预览的 tail 后；只有两个 process 入口进入可取消的串行预览队列。本地操作仍遵守关闭、preparation 及调用者取消门禁；prepareShutdown／close 继续取消并等待真实预览排空，clearData 仍先排空再清理，不因本地保存独立而提前释放在线资源。

结果中的自由文本（包括 `outputText`、`groupKey`、错误文本和 previews）使用 `FilmRuleText`，`utf16` 保留 JS 原始码元，等价比较也是精确码元比较。无 `u` 的合法正则可以拆开代理对，不能以 Swift `String` 替代业务结果或隐式修复字符；例如 `🎬` 经 `^.` 替换为 `x` 后，结果为 `[0x0078, 0xDFAC]`。UIKit 显式使用 `displayString` 显示（孤立代理项显示为 U+FFFD），不把这个显示值回传业务。

Node 统一处理结果的传输表示：正常文本保持 JSON 字符串，含孤立代理项的文本传为 UInt16 数组，使 Foundation 无损解码。`FilmRuleText.Codable` 保留每个码元并支持无损往返；只有显示使用有损 `displayString`，不把它回传为业务身份。搜索和播放不从客户端携带规则，Node 在接纳业务任务时绑定规则快照。

## 猫源与公共宿主

契约见 [cat.script.v2 原生来源意图](../runtime/cat-script-runtime.md#原生来源意图注入)。只有本地／网络，无内置猫源和版本副标题。`FilmSourceService(transport:storeFactory:settingsDocument:runtimeSourceRoot:observeConnection:)` 读取原生来源意图，runtimeSourceRoot 指向同一运行器的 `cat` 根，App 的 observeConnection 注入 `RuntimeSession.connectionIfStarted()`。本地通过系统文件选择及既有 `ImportedFileReader` 读取严格 UTF-8 安全快照（16 MiB 文件读取资源边界，实际字节计数与修改检测），按 SHA-256 排他原子交接至 `cat/sources/<hash>.js`；意图只写 `localHash`，不保存 picker URL、内联源码或另一安装索引。运行器源码上限仍为 16 MiB；网络输入由 Node 下载。

页头右侧的导入菜单提供本地／网络导入，列表工具行仅保留搜索。导入受页面可见性及忙碌状态约束；本地导入进入编辑页，网络导入浮层保留原列表，取消不提交。现有选择目标的界面保留：选择已有项时保存同一 ID 的来源替换，不另建安装。列表显示名称、启停、运行及更新状态，并提供直接编辑；低频动作收进单一更多菜单。未知 `updateState` 按原文字展示，不作为整批 status 失败。类型和修改时间放在详情，详情保留启用开关、编辑、更新和单实例重启；删除通过选择模式勾选后批量确认。本地更新重新选文件，网络更新重新下载。首次来源保存默认启用；停用保留本地清单但禁止重启／检测；父源删除清除关联本地视频、配置与删除记录。时间取实际更新时间，否则导入时间；失败、无变化、重启不改时间，失败更新不冒充新版本就绪。

App 的 `RuntimeSession` 唯一持有共享 HTTP 客户端和宿主生命周期；装配显式启动音乐薄适配器并调用 `FilmSourceService.start()`。普通来源读取、添加、编辑、启停及移除先提交原生意图；保存后只异步通知当前活跃／启动中代次，离线不启动运行器。返回已保存意图不是安装／ready 回执；实际文件、版本、sites、私有卷和更新结果仍属于 Node。启动恢复启用猫源，不执行音乐脚本、不自动检测。运行器不可用时普通设置仍可读取和保存，规则测试、检测、重启及网络更新等业务真实失败。音乐使用 `music.script.v3`，其安装、来源地址、源码和请求私有存储继续由 Node 管理，不迁入影视原生设置。每进程只启动一次，宿主退出／不可恢复失败不重放命令或启动第二个 Node。

`initialize()` 同步读取原生意图与本地目录，随后只调度独立观察，不等待连接或 status；`run` 登记可取消的业务任务，并非把所有操作排在同一在线 tail 后。显式运行器管理及状态合并通过 `managed` 串行，本地配置 CAS 不等待该队列或观察回执。

来源 owner 在 initialize／start／refresh／保存／resume 后可观察已有运行器。它只借用已启动或正在启动的当前 RuntimeSession，等待可被调用者取消；没有已开始的会话时不启动、不重试或积压。取得 client 后捕获该实例读取 actual status，starting／stopping 状态间隔 250ms，稳定状态间隔 1s，用于更新实际状态与站点展示，不使用可能启动运行器的普通 transport。保存先取得原生提交结果再调度观察，不等待实际装载；旧配置代次／输入 revision／启用意图不匹配及 generation 回退的状态不能覆盖新保存。连接关闭或请求错误结束观察；prepareShutdown／close 取消并等待观察及已接纳任务实际排空，不关闭借用 client。

## 客户端视频清单

`FilmSourceService` 是持久化事实来源，以 `installationId + siteKey` 标识条目，按公开协议的原始字符串精确区分，不合并 Unicode 规范等价但码点不同的 key；不以运行端口或 generation 作持久身份。保存四选项、排序、检测结果和删除记录，聚合图片全表单选。[影视搜索](film-search.md)消费启用／允许搜索、来源顺序和聚合图片选项，并观察源代次／禁用；首页可见选项尚无首页消费者。检测不修改这些选项。列表复用书源的 `ManagementListView`，保留整表圆角、搜索和底部批量栏；提供直接启停、编辑和检测状态，检测／取消保留在更多菜单，删除通过勾选批量确认。排序在详情的单项排序行编辑，详情仍可修改四开关，列表按保存后的来源顺序展示。

普通更新、重新保存来源、重启、同步、App 重建均不恢复用户删除项。同步仅采用与当前原生 `inputRevision` 匹配的 ready 状态；失败、starting、迟到旧输入都不能抹掉新删除或发布旧来源目录。同步失败不把空清单当成功，未 ready 的猫源保留已知清单。

检测沿参考流程：home → category，必要时 detail／play，条件 search；从真实响应取得后续 ID、线路和 URL，保留 Unicode 与标点参数。播放候选与导航去重按原始内容精确区分，不合并 Unicode 规范等价但码点不同的线路、ID 或导航参数；`$$$`、`|||`、`#`、`$` 按字面协议分隔，不因相邻组合字符漏切；真正重复的候选、循环和重复页仍按参考流程处理。逐阶段区分 unsupported、empty、failed，导航循环／重复页明确失败；“有效”仅代表接口与播放地址解析，不代表媒体已播放。generation、停用、删除、页面隐藏取消或 owner 关闭后不发布迟到结果。

检测按实际返回分支取列表和播放地址，不把业务 JSON 中的 `statusCode/ok/success/error` 或未采用的 `data` 当成通用失败信封。真实 transport／Spider 调用失败、JSON 解析失败仍记 failed；未取得有效列表或地址仍记 empty，不因删掉预检就视为已解析成功。

完整结果返回后，检测在管理串行边界读取一次宿主权威 status，再检查原检测资格、原 generation 和站点存在性才保存。响应完成与本地落盘之间仍可能发生独立管理换代，因此不能仅使用检测开始前的本地代次；最终确认失败也不发布检测。此确认只属于显式检测，与来源 owner 的只读状态观察分开；页面消费已发布快照，不在每次渲染时自行请求运行器。

## 生命周期与验证边界

### 数据源与智能播放设置

`FilmDataSourceSettingsViewController` 同页切换 TMDB／豆瓣，字段采用上方标签、下方全宽输入。字段及代理含义以[元数据运行器](../runtime/metadata-data.md#设置与凭据)为准：TMDB 单一明文 API TOKEN 支持 v3/v4，附数据／图片代理、语言、地区和成人开关；豆瓣 Cookie 明文回显，数据／图片代理独立选择，默认“无”，自定义只展开普通代理前缀。无服务器代理、密码遮罩、configured 占位或单独清除按钮；空凭据保存即清空。`FilmTMDBService(settingsDocument:connection:)` 和 `FilmDoubanSettingsService(settingsDocument:)` 直接访问各自加密原生文档；失败保留输入与 saved 状态，已提交保存不因离页丢回执，不在启动时经 HTTP 回灌。

偏好关键词和站点净化词用单行逗号分隔，键入不重建正在编辑的行，恢复默认只改选定组。`FilmPlaybackSettingsModel(transport:settingsDocument:)` 在原生文档读取／保存偏好，只规范化实际编辑字段、保留其他原始 JSON；preferencesBusy 与在线业务 busy 分开，挂起的资料库请求不阻塞本地偏好读写。人工匹配按目标分组，分享链接／提取码直接编辑，仍经运行器管理。设置不展示或删除历史／收藏；原业务 owner 不变。

`FilmPlaybackLibrary` 只投影当前 Manual 或屏蔽页面所需的列表；偏好走独立设置入口。Manual 选影片只读取历史的 targetId/title，不校验位置、时长或时间；无法读取这两个字段的辅助历史不进入选项。当前列表可分离坏项不阻断健康兄弟，真实空列表有效，坏根／列表形状错误及非空但全部不可用仍报错。读取投影不回写资料，删除仍提交原始 typed block 身份，保存失败不发布。

偏好通过 `FilmPlaybackSettingsModel.loadPreferences/savePreferences` 独立读取和提交原生文档，成功 CAS 才发布保存值，不经 HTTP `settings.get`／`settings.set` 写回。读取只投影可识别的排序维度／网盘偏好，不执行未知值或替它补默认排序。未编辑某组排序时，保存其他字段保留该组原始数组；显式重排或恢复该组才替换，重排规则维度须完整，编辑网盘顺序时补齐其余已知平台。原生播放快照不复制或解码 `preferences`，媒体准入仍读取真实 `attempt.prepareOnly`。辅助 cache／supplement 不阻断主资料和会话：返回的缓存说明无法识别时保守按 non-fresh 处理，不解锁 stale 限制；现有组件完成证据独立保留。无法消费的补充资料不参与展示，不代替主身份。session、sequence、attempt／resource 及真实执行材料继续按原契约消费。

通用设置的缓存管理提供豆瓣、TMDB、两者三个动作，由 `FilmPlaybackSettingsModel` 调用运行器 `data.invalidate`，不直接清文件、不重启或清用户资料；两项都结算后才解除 busy，部分失败仍报告失败。它不触发配置保存或自动搜索。

两供应商只有原生提交成功才调用 `FilmSearchService.metadataSettingsChanged()`，撤销旧会话和共享详情缓存的复用资格，取消并跟踪旧工作到物理结算；不自动搜索。此回执不等待运行器通知或上游请求。实际旧详情继续持有原快照，下一次显式同词使用新会话。相对图片缓存身份不因设置或运行器端口改变；接线见[影视搜索](film-search.md)。

### 网盘账号

列表保留平台、状态、操作三列，适配窄屏；登录沿用音乐账号浮层、输入和按钮的共享视觉组件，不依赖音乐账号服务或存储。刷新、整份替换、退出与二维码取消以[网盘公共契约](../runtime/pan-runtime.md#设置页)为唯一来源。

`PanAccountsModel.start()` 同步读取原生 providers／accounts，无配置读取进度，不连接运行器或自动刷新账号。`PanAccountService(document:request:)`／`PanAccountService(document:connection:)` 的 replace／clear 直接提交同一加密文档；仅有配置时显示「未验证」，真实在线观察才可显示已登录／失效，且绑定当前 revision、字段及凭据内容身份。

扫码、二维码轮询、显式刷新与真实网盘请求仍由运行器执行；新凭据通过受信 `pan.accounts` CAS 写回同一原生 owner。手动保存完成与扫码 confirmed 分开，不因离线无法验证而撤回已保存配置。关闭、离页及后台停止扫码轮询，已确认账号不因关闭浮层退出；手动表单仍只有当前草稿、不回显旧 Cookie／密码／token，保存则写入上述原生加密文档。

### 既有 owner 与专用验证

页面隐藏取消规则测试／检测及页面订阅，不销毁 App owner。已提交的管理操作取得真实回执，返回后不继续尚未交接的批量更新或改变导航；批量重启重试保留已成功项，只继续剩余项。批量及单项删除都依据已提交本地事实协调重试，删除后的同步失败仍显示原错误，不从已删除项重新执行，也不迟到退出用户随后进入的其他页面。

App 关闭先停止接纳并排空规则执行／来源任务；规则适配的 prepare／clear／close 共享物理排空，关闭不提前越过已接纳 clear，也不关闭借用的 runtime。音乐薄适配器拒绝新工作、取消并排空已接纳的调用与传输，通过 `music.script.close` 的 `closed:true, cleanupComplete:true` 回执确认音乐模块收口，空实例清单或连接断开不等于清理成功。并发／重复关闭共用同一终态，取消等待者不放弃收尾。音乐不关闭共享连接／Cat 模块；App 最后关闭并等待共享连接和 Node 线程。数据清除仍按既有下次冷启动流程覆盖原生文档、专用设置密钥和运行器数据，不在原进程重建 Node。

下列为验证入口及职责，不代表本轮已经执行；编译、传输替身与真实 App 业务须分别记录。

- Swift 规则精确 filter：`FilmRuleLifecycleTests|FilmRulePersistenceTests|FilmRuleReferenceTests|FilmRuleTextTests`，覆盖原生分组 CAS／回执／失败、离线默认及文本规范化、预览取消和物理排空、UTF16 每码元往返；传输替身不代表真实 Node。
- `FilmSettingsDocumentTests|FilmSettingsMigrationTests` 覆盖文档 CAS、按块通知及已知旧配置单向迁移；领域测试 `FilmCatNativeSettingsTests`、`PanAccountServiceTests|PanAccountsModelTests|PanLoginModelTests`、`FilmTMDBServiceTests`、`FilmPlaybackSettingsModelTests` 分别核对原生保存和在线业务边界。
- `MeowfilmRuntimeTests/FilmSettingsPlatformTests`：真实前台 UIKit 草稿、导航生命周期、控件、失败／重试、确认、取消、窄／宽视口和大字体截图。普通规则配置使用独立原生文档，规则执行才走 NodeMobile；来源状态／调用仍有协议夹具，不能替代实际脚本装载验收。
- `MeowfilmFilmSettingsHostTests`：原生规则保存／草稿一致性与真实 Node 预览，固定共享 reference cases 每例两次、Unicode 属性／前后瞻／捕获／g-y／孤立码元，以及正则取消／截止／长编译／全局删除后的恢复；固定 JSON 以内嵌 Swift 常量进入宿主测试，不依赖产品载荷包含 test 目录。Node 宿主的 `native-json` 用例另保留通用无 I/O、内存失败、循环、排队／关闭与真实 C 入口不可捕获中断覆盖。Cat HTTP 五阶段与原生来源／客户端目录持久化、落盘前换代拒旧检测、前台确认／失败弹窗仍保留。显式重启不是实际内存超限实验；后台快照不能替代前台系统弹窗。`MeowfilmRuntimeHostTests` 在同一个 NodeMobile 中并发调用 source 与影视规则，验证共享载体但 Runtime／Context 独立，不另启运行器。

规则迁移的精确 `MeowfilmAcceptance` 选择如下；每项须实际执行，编译或跳过不算规则载体通过：

```text
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testGroupDraftSurvivesNavigationAndRestoreAndSeparateTestNeverSave
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testRulesGroupSaveFailureRetainsDraftAndRetryPreservesOtherSavedGroups
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testModelLoadsNativeRulesAndLocalSourcesAndCloseDoesNotCloseServices
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testNarrowWideLargeTypeAndEmptyScreensProduceRealUIKitAttachments
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testPageSaveNormalizesClearedRowsAndStaleFieldsCannotChangeSuccessor
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testAddAndDefaultsOnlyChangeCurrentGroupDraftAndTestOpensSeparatePage
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testSeparateEpisodeTestsUseThisGroupDraftAndAssociatedSavedGroupOnly
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testSeparateRuleTestCancellationOnHiddenPageDoesNotPublishOrSave
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testNativeGroupSaveWhilePreviewHeldKeepsReceiptAfterPageRemovalAndWaiterCancellation
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testRuleFieldsHideFlagsAndKeepReplacementInTheSameEditor
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testRuleResultDisplayDoesNotReplaceLosslessBusinessText
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSettingsHostTests/testNodeMobileRulesUseSameSavedAndDraftEntryAndKeepIsolatedUTF16
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSettingsHostTests/testNodeMobileGroupSaveAndDraftPreviewPreserveAssociatedSavedGroups
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSettingsHostTests/testNodeMobileEntryMatchesEverySharedReferenceCaseTwice
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSettingsHostTests/testNodeMobileUnicodeAssertionsCapturesAndStatefulRulesRepeatExactly
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSettingsHostTests/testNodeMobileRuleCancellationDeadlineCompilationAndGlobalDeletionDrainThenRecover
-only-testing:MeowfilmRuntimeTests/MeowfilmRuntimeHostTests/testNativeSourceAndNodeRulesUseIndependentRuntimesInTheSameApp
```

- `MeowfilmScriptShutdownHostTests` 仅设 `TEST_RUNNER_MEOWFILM_SCRIPT_SHUTDOWN=1` 后单独运行：真实音乐模块关闭后 Cat 仍可访问，App 最终关闭共享连接并等待 Node 退出；该测试会终止本进程装配，默认跳过，不能与其他宿主业务用例放在同次调用。
- 六块原生设置使用 `MeowfilmNativeSettingsColdStartTests/testRepeatedAppProcessesRestoreNativeSettingsWithoutReinstalling`，配合现场新建的 `TEST_RUNNER_MEOWFILM_NATIVE_SETTINGS_COLD_RUN_ID="$(uuidgen)"`，在单次 `xcodebuild test-without-building` 中设置 `-test-iterations 2 -test-repetition-relaunch-enabled YES` 并仅选择该方法。第一轮真实关闭运行器后离线连续保存六块最终配置，第二轮验证原生文档和运行器冷读，读取不回写、不重放离线通知。检查点只接受相同 runID、App 身份／安装路径／数据容器及不同 PID；恢复完成后清除合成凭据与测试猫源。两轮各保留 `native-settings-cold-<runID>-<phase>.json` 附件，仅含阶段、身份、路径、PID和成功标记，不含设置值或凭据。必须核对恰好 prepare／restore 各一次、均通过且无 skip；两次独立 Xcode 调用可能重新安装并更换容器，不能代替此原容器验收。
- 冷启动必须分两次独立 `xcodebuild test`／`test-without-building`，同一专用模拟器：先仅运行 `MeowfilmCatColdStartPrepareTests` 并设置 `TEST_RUNNER_MEOWFILM_CAT_COLD_PHASE=prepare`，结束宿主进程后再仅运行 `MeowfilmCatColdStartRestoreTests`，设为 `restore`。脚本私有卷计数证明启用项恢复一次、停用项未执行、没有自动检测且 tombstone 未复活；restore 清理两条测试安装。普通测试运行跳过这两个显式跨进程场景，不能把跳过当作冷启动通过。
- TMDB／豆瓣配置冷启动使用同一个专用模拟器，在**单次** `xcodebuild` 中重复两轮，并要求新宿主进程、原 App 数据容器与原安装路径。官方 `man xcodebuild` 的 `-test-repetition-relaunch-enabled YES` 配合 `-test-iterations 2` 要求每轮使用新进程；免重装仍须由本次真实结果核对，不能由参数推定。使用覆盖当前测试源码的 `MeowfilmAcceptance` 构建产物，将 `IOS_TMDB_COLD_XCTESTRUN` 指向对应 `.xctestrun`，`IOS_SIMULATOR_UDID` 指向全程独占的同一设备，`IOS_TMDB_COLD_RESULT` 指向尚不存在的结果包路径：

```sh
TEST_RUNNER_MEOWFILM_TMDB_COLD_RUN_ID="$(uuidgen)" \
xcodebuild test-without-building \
  -xctestrun "$IOS_TMDB_COLD_XCTESTRUN" \
  -destination "platform=iOS Simulator,id=$IOS_SIMULATOR_UDID" \
  -parallel-testing-enabled NO \
  -maximum-concurrent-test-simulator-destinations 1 \
  -test-iterations 2 \
  -test-repetition-relaunch-enabled YES \
  -only-testing:MeowfilmRuntimeTests/MeowfilmTMDBColdStartRepetitionTests/testRepeatedAppProcessesRestoreSettingsWithoutReinstalling \
  -resultBundlePath "$IOS_TMDB_COLD_RESULT"
```

每次调用现场生成全新 UUID；不要复用旧 UUID 或配置自动失败重试。首轮仍只接受默认基址且无凭据的目录，成功保存两供应商合成设置后才在 `Library/Application Support/TMDBColdStartTests/<UUID>.json` 记录 prepare。第二轮读取同一记录，先验证 runID、App 身份、不同 PID、相同数据容器和 App bundle 路径，写入 restoring 防止重复进入，再执行恢复及字段读回断言；原生存储迁移后须同时核对读取未改写原生配置、Node 未另建设置副本，不能以旧“原生目录为空”的断言代替。restore 的 teardown 清除合成凭据、恢复默认基址之后，且本轮没有失败，才记录 restore 完成。无效 UUID、损坏记录、身份或路径不符、同进程重入、restoring／已完成记录都失败；失败不靠删除标记伪造完成。记录按 UUID 保留，旧未清夹具仍受原默认设置 guard 拦截；不读取真实秘密。

每轮保留 `tmdb-cold-<UUID>-<phase>.json` XCTest 附件，字段为 `runID`、`phase`、`processID`、`dataContainerPath`、`appBundlePath`、`appBundleIdentifier`、`succeeded`，不包含设置值或凭据。用 `xcrun xcresulttool get test-results summary --path "$IOS_TMDB_COLD_RESULT"`、`get test-results tests --path "$IOS_TMDB_COLD_RESULT"` 及 `xcrun xcresulttool export attachments --path "$IOS_TMDB_COLD_RESULT" --output-path "$IOS_TMDB_COLD_ATTACHMENTS"` 核对本次真实结果：所选方法恰好执行两轮、均通过且无 skip，附件恰好为同一 runID 的 prepare、restore 各一次且 `succeeded=true`，PID 不同、数据容器及 App bundle 身份／路径一致，并核对日志中阶段间没有安装／重装或清数据。单轮通过、第二轮仍是 prepare、缺少附件、清理失败或任何条件不符均不能算冷启动验收通过，源码编排本身不证明免重装。

原手动入口仍可独立使用：`-only-testing:MeowfilmRuntimeTests/MeowfilmTMDBColdStartPrepareTests/testSaveSyntheticSettingsForNextAppProcess` 配合 `TEST_RUNNER_MEOWFILM_TMDB_COLD_PHASE=prepare`；`-only-testing:MeowfilmRuntimeTests/MeowfilmTMDBColdStartRestoreTests/testNextAppProcessRestoresPlainSettingsWithoutNativeWriteback` 配合 `TEST_RUNNER_MEOWFILM_TMDB_COLD_PHASE=restore`。后者可在完整合成设置 guard 通过后执行原断言及凭据清理，包括重复入口失败后遗留的夹具；不跳过 guard、不仅删除阶段文件，部分写入或 guard 不符时须报告而非冒充清理成功。普通两次 `xcodebuild` 不能称为免重装验证。新入口未设 UUID、旧入口未设对应 phase 均显式跳过，跳过不算通过。真实设置 UI 的保存、保留／替换／清除、失败与截图见 `MeowfilmTMDBHostTests`。

设置不承载首页／搜索业务策略；搜索通过既有 owner 消费其快照。仍不提供任意第三方脚本兼容保证，不以模拟器结果宣称真机后台或实际媒体播放已验证。
