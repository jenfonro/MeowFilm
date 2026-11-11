# iOS 影视设置

设置首页提供音乐设置、影视设置，以及通用分组的偏好设置、缓存管理；底部独立分组为清空本机数据。影视下提供搜索净化设置、智能匹配设置、猫源、视频源、数据源和智能播放设置。智能播放设置收纳偏好、自动准备、Manual 匹配源管理和匹配禁用管理。智能匹配下为剧集列表净化规则、集数匹配规则、电影匹配规则、站点净化设置；后者仍编辑站点名关键词，不是正则。历史继续由 Node 记录，但不在设置显示入口。播放设置及资料遵循[统一播放契约](../runtime/film-playback.md)。

手机一般三级，智能匹配规则和来源详情可到第四级；编辑、测试不另开第五级。当前标题放 App 自有页头，内容只保留实际分组。分组采用淡渐变外轮廓、动态字体与至少 44 pt 点击区。返回保持位置；规则草稿由 App 持有的 `FilmSettingsModel.rules` 保留，不随页面销毁。

## Node 规则与原生草稿

行为唯一来源为 [`packages/film-rules`](../../packages/film-rules/README.md) 的共享 JS，与 Windows 使用同一锁源 QuickJS-NG 0.18.0。唯一规则 owner、文档及执行都在 Node；影视纯 JSON 程序与通用脚本复用 `MeowScriptHost.framework` 原生载体，但使用独立 Runtime／Context／线程，不共享脚本状态。64 MiB 堆、512 KiB JS 栈与两秒截止是执行资源边界，不限制规则条数或文本长度。客户端不再链接独立规则引擎，也不回退到 Swift 正则、JSC、WASM 或 V8。

共享载体保留锁定派生源码的正则编译／执行／全局空替换中断能力，普通构建不现场下载或打补丁；中断不冒充正则语法错误。异常、取消或截止后排空真实资源并恢复同一程序。App 资源分发共享原生引擎 MIT 与 Unicode 表许可。

原生编辑分为搜索净化、剧集列表净化、集数匹配、电影匹配四个独立页面。每页纵向排列规则块，pattern、replace、flags 在同块内；替换字段优先占宽、flags 紧凑，窄屏与大字体在块内堆叠。添加、修改、删除均只改本页草稿；页头唯一保存一次提交本页整组，失败保留编辑。恢复默认只读取当前组默认到草稿，仍需保存才生效。测试在页内展开，使用本页草稿与必要关联组的已保存配置，不混用其他页草稿、不保存。

保存按共享程序执行 JS trim、参考服务端 Go `TrimSpace`、页面 decode；草稿测试不提前套用保存归一化，匹配规则 JSON 内的 pattern／replace 不被后端 trim。空集数匹配组仍返回清理结果与 previews，同时报告“无匹配规则”，不自动回填规则。

`FilmRuleService` 借用同一 App runtime epoch，使用 `film.rules.group.set/defaults/preview` 分别整组保存、只读默认和草稿预览；Node 唯一 owner 在串行写边界按组原子合并，回执更新展示快照，不读写原生规则文档或建立客户端 CAS。`FilmRulesDraft` 仅替换保存成功的那组草稿，其他未保存组原样保留。原整份 `rules.set/restore` 仍服务既有消费者，iOS 页面不借此提交共享草稿。正常空／非法正则的业务结果与运行器不可用明确区分；运行器不可用时不启用旧引擎。

结果中的自由文本（包括 `outputText`、`groupKey`、错误文本和 previews）使用 `FilmRuleText`，`utf16` 保留 JS 原始码元，等价比较也是精确码元比较。无 `u` 的合法正则可以拆开代理对，不能以 Swift `String` 替代业务结果或隐式修复字符；例如 `🎬` 经 `^.` 替换为 `x` 后，结果为 `[0x0078, 0xDFAC]`。UIKit 显式使用 `displayString` 显示（孤立代理项显示为 U+FFFD），不把这个显示值回传业务。

Node 统一处理结果的传输表示：正常文本保持 JSON 字符串，含孤立代理项的文本传为 UInt16 数组，使 Foundation 无损解码。`FilmRuleText.Codable` 保留每个码元并支持无损往返；只有显示使用有损 `displayString`，不把它回传为业务身份。生产装配为 `FilmRuleService(connection:)`。搜索和播放不从客户端携带规则，Node 在接纳业务任务时绑定规则快照。

## 猫源与公共宿主

契约见 [cat.script.v2](../runtime/cat-script-runtime.md)。只有本地／网络，无内置猫源和版本副标题。本地通过系统文件选择及既有 `ImportedFileReader` 读取严格 UTF-8 安全快照（32 MiB 文件读取资源边界，实际字节计数与修改检测），再以一次 raw PUT 上传并用 sourceRevision 选择完整候选；运行器源码上限仍为 16 MiB。网络导入和更新由 Node 下载。业务参数及结果直接 JSON，不保留分片、传输 token 或字符串化信封；脚本本身返回 JSON 文本时仍由业务解析。

列表显示名称、启停、运行及更新状态，低频动作收进单一更多菜单；类型和修改时间放在详情，详情同时保留启用开关、编辑、更新、单实例重启、删除，批量动作集中在选择模式。本地更新重新选文件，网络更新重新下载。首次成功导入默认启用；停用保留本地清单但禁止重启／检测；父源删除清除关联本地视频、配置与删除记录。时间取更新时间，否则导入时间；失败、无变化、重启不改时间，失败更新保留旧版本。

App 的 `RuntimeSession` 唯一持有共享 HTTP 客户端和宿主生命周期；装配显式启动音乐薄适配器并调用 `FilmSourceService.start()`，没有空反向处理表或反向存储接口。音乐使用 `music.script.v3`，安装、源码和请求私有存储均由 Node 管理。音乐服务与 `CatScriptTransport` 共享客户端但不共享业务状态；启动恢复启用猫源，不执行音乐脚本、不自动检测。真实 starting 状态仅在猫源恢复尚未完成时轮询。来源启动失败写入可观察状态，不阻断独立规则 RPC；整个 runtime 不可用时规则草稿仍可编辑，但保存／测试明确失败。每进程只启动一次，宿主退出／不可恢复失败不重放命令或启动第二个 Node。

## 客户端视频清单

`FilmSourceService` 是持久化事实来源，以 `installationId + siteKey` 标识条目，按公开协议的原始字符串精确区分，不合并 Unicode 规范等价但码点不同的 key；不以运行端口或 generation 作持久身份。保存四选项、排序、检测结果和删除记录，聚合图片全表单选。[影视搜索](film-search.md)消费启用／允许搜索、来源顺序和聚合图片选项，并观察源代次／禁用；首页可见选项尚无首页消费者。检测不修改这些选项。列表保留直接启停与检测状态，检测／取消／删除收进更多菜单，进入详情可修改四开关和排序。

普通更新、重启、同步、App 重建不会恢复用户删除项。只有明确且成功的重新导入，按本次 ready 回执中该父源包含的条目恢复；失败、starting、迟到导入都不能抹掉新删除。同步失败不把空清单当成功，未 ready 的猫源保留已知清单。

检测沿参考流程：home → category，必要时 detail／play，条件 search；从真实响应取得后续 ID、线路和 URL，保留 Unicode 与标点参数。播放候选与导航去重按原始内容精确区分，不合并 Unicode 规范等价但码点不同的线路、ID 或导航参数；`$$$`、`|||`、`#`、`$` 按字面协议分隔，不因相邻组合字符漏切；真正重复的候选、循环和重复页仍按参考流程处理。逐阶段区分 unsupported、empty、failed，导航循环／重复页明确失败；“有效”仅代表接口与播放地址解析，不代表媒体已播放。generation、停用、删除、页面隐藏取消或 owner 关闭后不发布迟到结果。

完整结果返回后，检测在管理串行边界读取一次宿主权威 status，再检查原检测资格、原 generation 和站点存在性才保存。响应完成与本地落盘之间仍可能发生独立管理换代，因此不能仅使用检测开始前的本地代次；最终确认失败也不发布检测。此确认只属于显式检测，不新增后台轮询或让页面展示逐次请求运行器。

## 生命周期与验证边界

### 数据源与智能播放设置

`FilmDataSourceSettingsViewController` 同页切换 TMDB／豆瓣，字段采用上方标签、下方全宽输入。字段及代理含义以[元数据运行器](../runtime/metadata-data.md#设置与凭据)为准：TMDB 单一明文 API TOKEN 支持 v3/v4，附数据／图片代理、语言、地区和成人开关；豆瓣 Cookie 明文回显，数据／图片代理独立选择，默认“无”，自定义只展开普通代理前缀。无服务器代理、密码遮罩、configured 占位或单独清除按钮；空凭据保存即清空。失败保留输入与 saved 状态，已提交保存不因离页丢回执，无原生持久文件或启动回灌。

偏好关键词和站点净化词用单行逗号分隔，键入不重建正在编辑的行，恢复默认只改选定组。人工匹配按目标分组，分享链接／提取码直接编辑。设置不展示或删除历史／收藏；原业务 owner 不变。

通用设置的缓存管理提供豆瓣、TMDB、两者三个动作，由 `FilmPlaybackSettingsModel` 调用运行器 `data.invalidate`，不直接清文件、不重启或清用户资料；两项都结算后才解除 busy，部分失败仍报告失败。它不触发配置保存或自动搜索。

两供应商只有成功回执才调用 `FilmSearchService.metadataSettingsChanged()`，撤销旧会话和共享详情缓存的复用资格，取消并跟踪旧工作到物理结算；不自动搜索。实际旧详情继续持有原快照，下一次显式同词使用新会话。相对图片缓存身份不因设置或运行器端口改变；接线见[影视搜索](film-search.md)。

### 既有 owner 与专用验证

页面隐藏取消规则测试／检测及页面订阅，不销毁 App owner。已提交的管理操作取得真实回执，返回后不继续尚未交接的批量更新或改变导航；批量重启重试保留已成功项，只继续剩余项。批量及单项删除都依据已提交本地事实协调重试，删除后的同步失败仍显示原错误，不从已删除项重新执行，也不迟到退出用户随后进入的其他页面。

App 关闭先停止接纳并排空规则 RPC／来源任务；规则适配的 prepare／clear／close 共享物理排空，关闭不提前越过已接纳 clear，也不关闭借用的 runtime。音乐薄适配器拒绝新工作、取消并排空已接纳的调用与传输，通过 `music.script.close` 的 `closed:true, cleanupComplete:true` 回执确认音乐模块收口，空实例清单或连接断开不等于清理成功。并发／重复关闭共用同一终态，取消等待者不放弃收尾。音乐不关闭共享连接／Cat 模块；App 最后关闭并等待共享连接和 Node 线程。数据清除仍按既有下次冷启动删除流程覆盖普通文档和 `script-runtime/film-rules/rules.json` 等运行器数据，不在原进程重建 Node。

- Swift 规则精确 filter：`FilmRuleLifecycleTests|FilmRulePersistenceTests|FilmRuleReferenceTests|FilmRuleTextTests`，覆盖 RPC 整份底层契约及分组原子保存／回执／失败／取消和物理排空、UTF16 每码元往返；传输替身不代表真实 Node。
- `MeowfilmRuntimeTests/FilmSettingsPlatformTests`：真实前台 UIKit 草稿、导航生命周期、控件、失败／重试、确认、取消、窄／宽视口和大字体截图。规则走 NodeMobile（仅存储失败在 RPC 边界注入），来源 RPC 仍为协议夹具；每个夹具恢复原规则。
- `MeowfilmFilmSettingsHostTests`：真实规则 RPC 保存／草稿一致性、固定共享 reference cases 每例两次、Unicode 属性／前后瞻／捕获／g-y／孤立码元，以及正则取消／截止／长编译／全局删除后的恢复；固定 JSON 以内嵌 Swift 常量进入宿主测试，不依赖产品载荷包含 test 目录。Node 宿主的 `native-json` 用例另保留通用无 I/O、内存失败、循环、排队／关闭与真实 C 入口不可捕获中断覆盖。Cat HTTP 五阶段与客户端来源持久化、落盘前换代拒旧检测、前台确认／失败弹窗仍保留。显式重启不是实际内存超限实验；后台快照不能替代前台系统弹窗。`MeowfilmRuntimeHostTests` 在同一个 NodeMobile 中并发调用 source 与影视规则，验证共享载体但 Runtime／Context 独立，不另启运行器。

规则迁移的精确 `MeowfilmAcceptance` 选择如下；每项须实际执行，编译或跳过不算规则载体通过：

```text
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testGroupDraftSurvivesNavigationAndRestoreAndInlineTestNeverSave
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testRulesGroupSaveFailureRetainsDraftAndRetryPreservesOtherSavedGroups
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testModelLoadsNodeRulesAndLocalSourcesAndCloseDoesNotCloseServices
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testNarrowWideLargeTypeAndEmptyScreensProduceRealUIKitAttachments
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testPageSaveNormalizesClearedRowsAndStaleFieldsCannotChangeSuccessor
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testInlineAddDeleteAndDefaultsOnlyChangeCurrentGroupDraft
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testInlineEpisodeTestsUseThisGroupDraftAndAssociatedSavedGroupOnly
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testInlineRuleTestCancellationOnHiddenPageDoesNotPublishOrSave
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testGroupSaveHandoffReceivesNormalizationAfterPageRemovalAndWaiterCancellation
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testInlineRuleFieldsUseCompactFlagsAndStackOnlyForNarrowOrLargeType
-only-testing:MeowfilmRuntimeTests/FilmSettingsPlatformTests/testRuleResultDisplayDoesNotReplaceLosslessBusinessText
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSettingsHostTests/testNodeMobileRulesUseSameSavedAndDraftEntryAndKeepIsolatedUTF16
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSettingsHostTests/testNodeMobileGroupSaveAndDraftPreviewPreserveAssociatedSavedGroups
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSettingsHostTests/testNodeMobileEntryMatchesEverySharedReferenceCaseTwice
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSettingsHostTests/testNodeMobileUnicodeAssertionsCapturesAndStatefulRulesRepeatExactly
-only-testing:MeowfilmRuntimeTests/MeowfilmFilmSettingsHostTests/testNodeMobileRuleCancellationDeadlineCompilationAndGlobalDeletionDrainThenRecover
-only-testing:MeowfilmRuntimeTests/MeowfilmRuntimeHostTests/testNativeSourceAndNodeRulesUseIndependentRuntimesInTheSameApp
```

- `MeowfilmScriptShutdownHostTests` 仅设 `TEST_RUNNER_MEOWFILM_SCRIPT_SHUTDOWN=1` 后单独运行：真实音乐模块关闭后 Cat 仍可访问，App 最终关闭共享连接并等待 Node 退出；该测试会终止本进程装配，默认跳过，不能与其他宿主业务用例放在同次调用。
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

每次调用现场生成全新 UUID；不要复用旧 UUID 或配置自动失败重试。首轮仍只接受默认基址且无凭据的目录，成功保存两供应商合成设置后才在 `Library/Application Support/TMDBColdStartTests/<UUID>.json` 记录 prepare。第二轮读取同一记录，先验证 runID、App 身份、不同 PID、相同数据容器和 App bundle 路径，写入 restoring 防止重复进入，再完整执行原 restore 的恢复／明文读回／无原生写回断言。restore 的 teardown 清除合成凭据、恢复运行器默认基址之后，且本轮没有失败，才记录 restore 完成。无效 UUID、损坏记录、身份或路径不符、同进程重入、restoring／已完成记录都失败；失败不靠删除标记伪造完成。记录按 UUID 保留，旧未清夹具仍受原默认设置 guard 拦截；不读取真实秘密。

每轮保留 `tmdb-cold-<UUID>-<phase>.json` XCTest 附件，字段为 `runID`、`phase`、`processID`、`dataContainerPath`、`appBundlePath`、`appBundleIdentifier`、`succeeded`，不包含设置值或凭据。用 `xcrun xcresulttool get test-results summary --path "$IOS_TMDB_COLD_RESULT"`、`get test-results tests --path "$IOS_TMDB_COLD_RESULT"` 及 `xcrun xcresulttool export attachments --path "$IOS_TMDB_COLD_RESULT" --output-path "$IOS_TMDB_COLD_ATTACHMENTS"` 核对本次真实结果：所选方法恰好执行两轮、均通过且无 skip，附件恰好为同一 runID 的 prepare、restore 各一次且 `succeeded=true`，PID 不同、数据容器及 App bundle 身份／路径一致，并核对日志中阶段间没有安装／重装或清数据。单轮通过、第二轮仍是 prepare、缺少附件、清理失败或任何条件不符均不能算冷启动验收通过，源码编排本身不证明免重装。

原手动入口仍可独立使用：`-only-testing:MeowfilmRuntimeTests/MeowfilmTMDBColdStartPrepareTests/testSaveSyntheticSettingsForNextAppProcess` 配合 `TEST_RUNNER_MEOWFILM_TMDB_COLD_PHASE=prepare`；`-only-testing:MeowfilmRuntimeTests/MeowfilmTMDBColdStartRestoreTests/testNextAppProcessRestoresPlainSettingsWithoutNativeWriteback` 配合 `TEST_RUNNER_MEOWFILM_TMDB_COLD_PHASE=restore`。后者可在完整合成设置 guard 通过后执行原断言及凭据清理，包括重复入口失败后遗留的夹具；不跳过 guard、不仅删除阶段文件，部分写入或 guard 不符时须报告而非冒充清理成功。普通两次 `xcodebuild` 不能称为免重装验证。新入口未设 UUID、旧入口未设对应 phase 均显式跳过，跳过不算通过。真实设置 UI 的保存、保留／替换／清除、失败与截图见 `MeowfilmTMDBHostTests`。

设置不承载首页／搜索业务策略；搜索通过既有 owner 消费其快照。仍不提供任意第三方脚本兼容保证，不以模拟器结果宣称真机后台或实际媒体播放已验证。
