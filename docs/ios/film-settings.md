# iOS 影视设置

设置先分为音乐／影视；影视下提供魔法规则、猫源、视频源三个页面。沿既有 UIKit 页头、导航、原生表单与动态字体，手机以列表和详情承载管理字段，不使用桌面横表。返回主页面保持位置；规则草稿由 App 持有的 `FilmSettingsModel.rules` 保留，不随页面销毁。

## 原生规则

行为唯一来源为 [`packages/film-rules`](../../packages/film-rules/README.md) 的共享 JS，与 Windows 使用同一锁源 QuickJS-NG 0.18.0。`MeowJavaScript` 是 JSON-only、无 I/O 的 C／Swift 适配，专有后台线程执行；64 MiB 堆、512 KiB JS 栈与两秒截止是执行资源边界，不限制规则条数或文本长度。异常、超时、取消后销毁上下文并按同一程序重建，不切换 Node、WASM、JSC 或 Swift 正则。

三分区四组：列表净化（剧集净化、集数匹配）、电影匹配、搜索列表净化。逐条添加、保存、删除及分区恢复默认提交整个草稿；测试不保存。预处理、flags、replace、顺序、尾号保护及非法正则反馈均由共享程序完成。

`FilmRuleService.process(FilmRuleRequest, draft: FilmRuleSet? = nil)` 是测试和后续业务的同一入口：传 draft 用草稿，不传用保存快照；消费者只解释 `FilmRuleResult`，不重写规则。`initialize`、`save`、`restoreDefaults` 与处理串行，CAS 成功后发布保存快照。规则和本地视频清单不依赖运行器可用。

## 猫源与公共宿主

契约见 [cat.script.v1](../runtime/cat-script-runtime.md)。只有本地／网络，无内置猫源和版本副标题。本地通过系统文件选择及既有 `ImportedFileReader` 读取严格 UTF-8 安全快照（32 MiB 文件读取资源边界，实际字节计数与修改检测），再用分块传输交给公共 Node 管理；网络导入和更新由 Node 下载。

列表显示名称、类型、启停、状态、修改时间及更新状态；详情有启用开关、编辑、更新、单实例重启、删除，列表支持选择及批量操作。本地更新重新选文件，网络更新重新下载。首次成功导入默认启用；停用保留本地清单但禁止重启／检测；父源删除清除关联本地视频、配置与删除记录。时间取更新时间，否则导入时间；失败、无变化、重启不改时间，失败更新保留旧版本。

App 的 `RuntimeBridgeSession` 唯一持有连接，在任何借用方首次请求前冻结音乐反向存储处理表；音乐服务与 `CatScriptTransport` 共享连接但不共享业务状态。App 启动即调用 `FilmSourceService.start()`，恢复启用猫源，不自动检测；真实 starting 状态仅在恢复尚未完成时轮询。启动失败写入可观察状态，规则仍可编辑测试。每进程只启动一次，EOF／不可恢复失败不重放命令或启动第二个 Node。

## 客户端视频清单

`FilmSourceService` 是持久化事实来源，以 `installationId + siteKey` 标识条目；不以运行端口或 generation 作持久身份。保存四选项、排序、检测结果和删除记录，聚合图片全表单选。四选项当前没有首页／搜索消费者，但完整保存；检测不修改它们。列表进入详情可修改开关和排序，行操作只有检测／删除。

普通更新、重启、同步、App 重建不会恢复用户删除项。只有明确且成功的重新导入，按本次 ready 回执中该父源包含的条目恢复；失败、starting、迟到导入都不能抹掉新删除。同步失败不把空清单当成功，未 ready 的猫源保留已知清单。

检测沿参考流程：home → category，必要时 detail／play，条件 search；从真实响应取得后续 ID、线路和 URL，保留 Unicode 与标点参数。逐阶段区分 unsupported、empty、failed，导航循环／重复页明确失败；“有效”仅代表接口与播放地址解析，不代表媒体已播放。generation、停用、删除、页面隐藏取消或 owner 关闭后不发布迟到结果。

## 生命周期与验证边界

页面隐藏取消规则测试／检测及页面订阅，不销毁 App owner。App 关闭先停止接纳并排空本地规则／来源任务，卸载音乐实例，最后关闭并等待共享连接和 Node 线程；数据清除仍按既有下次冷启动删除流程覆盖普通文档和 `script-runtime`，不在原进程重建 Node。

- Swift 定向单测：真实原生 C、共享固定规则场景、持久化／并发／取消、Cat RPC blob 与检测语义；RPC 替身不代表真实 Node。
- `FilmSettingsPlatformTests`：UIKit 草稿、控件、失败／重试、确认、取消、窄／宽视口和大字体截图。
- `MeowfilmFilmSettingsHostTests`：真实 App 资源／静态引擎、NodeMobile Cat HTTP 五阶段参数与客户端持久化，以及前台 App 窗口中的确认／失败弹窗。后台 UI runner 的视图快照不能替代系统弹窗合成截图。`MeowfilmRuntimeHostTests` 使用同一 App 音乐 owner 验证 LX，不能另启一个运行器。
- 冷启动必须分两次独立 `xcodebuild test`／`test-without-building`，同一专用模拟器：先仅运行 `MeowfilmCatColdStartPrepareTests` 并设置 `TEST_RUNNER_MEOWFILM_CAT_COLD_PHASE=prepare`，结束宿主进程后再仅运行 `MeowfilmCatColdStartRestoreTests`，设为 `restore`。脚本私有卷计数证明启用项恢复一次、停用项未执行、没有自动检测且 tombstone 未复活；restore 清理两条测试安装。普通测试运行跳过这两个显式跨进程场景，不能把跳过当作冷启动通过。

不接未来首页／搜索业务，不提供任意第三方脚本兼容保证，不以模拟器结果宣称真机后台或实际媒体播放已验证。
