# Windows 影视设置

公共窗口的音乐／影视类别切换到各模块自己的设置视图；影视内容为魔法匹配、猫源管理、视频源管理。设置页隐藏但不销毁公共播放器，返回恢复进入设置前的位置。规则草稿和来源服务由影视能力持有，不以页面可见性代替业务生命周期。

## 魔法匹配

列表净化含剧集净化与集数匹配；另有电影匹配、搜索列表净化。四组规则及对应测试移植自 `meowfilm-server` 设置页的完整读取／初始化／保存／恢复／测试链路，不混用服务端另一条后端识别算法。固定参考提交、输入输出和特殊行为见 [film-rules](../../packages/film-rules/README.md)。

`FilmRuleService.ProcessAsync(request)` 使用已保存快照；`ProcessAsync(request, draft)` 使用明确草稿。两者调用同一程序、返回同一 `FilmRuleResult`，测试不保存草稿。请求包含分区、文本以及搜索分区可空查询词；后续业务可直接消费 `OutputText`、命中、分组键、尾号保护与错误等结果，无需再写净化算法。本轮不接入搜索／播放消费者。

逐行保存、新增、删除提交四组完整草稿；分区恢复默认同样保存完整草稿。非法正则允许保存，在处理时报告；空组保存后保持为空，在下次服务初始化时按服务端首次默认补齐。首次默认与页面恢复默认有意保留参考差异。处理顺序、flags、替换、电影预处理及搜索尾号保护均由共用 JS 程序负责，不使用 .NET Regex 重写。

原生 `NativeJavaScriptJsonEngine` 在一个后台线程串行持有 QuickJS-NG；源码和许可锁定于 `scripts/quickjs-native.json`，C ABI 与资源边界见 [native/JavaScript](../../native/JavaScript/README.md)。规则程序随 App 内嵌，无 Node、WASM、HTTP 或脚本 RPC 依赖；pattern／replace 仅作为 JSON 数据传入。超时、取消、内存失败后释放原生上下文，可重新执行同一固定程序。

`film.magic-rules.v1` 是普通客户端文档，CAS 成功后才发布已保存快照。关闭准备暂停准入并取消／排空操作；已接纳写入等待真实存储回执。取消关闭后复用服务、规则快照和存储指纹；最终关闭才释放引擎。

## 猫源管理

猫源安装权威属于[公共 Cat 管理层](../runtime/cat-script-runtime.md)，客户端仅保存展示快照和映射。只有本地／网络类型，无内置。列为选择框、启用、名称、类型、状态、修改时间、更新状态、操作；名称没有版本副标题。操作为编辑、更新、重启、删除，支持选中条目的批量动作。

本地导入读取 `.js`，网络导入由公共运行器下载；明确重新导入可以选择已有安装身份。本地编辑名称、更新重新选文件；网络编辑名称／URL、更新读取保存 URL。首次成功导入默认启用。修改时间首次为导入时间，成功替换内容后为更新时间；失败、无变化及重启不刷新时间。更新失败保留旧版本，重启只作用于指定安装。

停用回收脚本，保留视频源清单，停用期间不能重启或检测；删除父源同步清除其客户端映射、选项、检测与删除记录。App 装配完成即恢复已启用猫源，启动失败可在猫源管理刷新重试，不阻断纯客户端规则功能。

## 视频源管理

`FilmSourceService` 是本地清单与用户配置的唯一 owner，使用普通文档 `film.video-sources.v1`。稳定身份为 `installationId + siteKey`；端口和 generation 只用于运行期路由／迟到结果隔离，不是持久主键。首页等未来消费者直接读取本地 `Videos`，不需为每次列表访问调用 Node。

四个选项——启用、首页显示、搜索启用、聚合图片——可编辑、保存并重启恢复；聚合图片全表单选。本轮尚无对应首页／搜索消费者，检测不会改写这些开关。列表排序、检测结果和删除意图也持久保存。

用户删除视频源后记录 tombstone。普通刷新、更新、重启和应用重新启动不恢复该条目；明确重新导入成功且返回 ready 的本次清单，才恢复该父源实际包含的条目。失败或 starting 不作为成功空清单，其他父源的相同 siteKey 不受影响。

视频源行内操作只有检测、删除。检测经真实 `cat.script.invoke`，按声明能力调用 home、category、必要 detail／play 和条件 search；按阶段记录 success／unsupported／empty／failed，并形成 valid／search_error／category_error／invalid 汇总。播放地址解析成功只记 `PlaybackResolved`，`MediaPlaybackVerified` 始终 false，不声称媒体可播放。停用、删除、父源更新／generation 变化后迟到检测不发布。

## 界面与验证范围

沿用 WPF 主题、原生键盘焦点、统一通知与确认浮层；表格在自身视口横向滚动。规则测试结果是业务内容，操作错误走通知，不插入教程／解释性占位文案。

长期测试分别归属 `FilmRulesTests`、`NativeJavaScriptTests`、`FilmSourcesTests`、`FilmSettingsUiTests`、`SettingsNavigationTests`；共享规则与 Cat 管理另有定向 Node 用例。替身验证只证明客户端请求与状态契约，真实原生执行、脚本 fixture 路由及 Windows 界面／构建须分别记录。本轮不包含 iOS 实施、用户环境安装、实际媒体播放或所有外部脚本兼容承诺。
