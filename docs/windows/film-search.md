# Windows 影视搜索接线与验证入口

行为以[共同影视搜索契约](../common/film/search.md)为唯一来源；规则与搜索程序分别见
[`film-rules`](../../packages/film-rules/README.md)、
[`film-search`](../../packages/film-search/README.md)。本文件只记录 Windows 消费者、装配和验证边界，不另定义算法。

原始列表开关置于综合／单曲等分类同行最右侧，TMDB 状态在开关左边；分类区域不足时独立横向滚动，状态和开关不覆盖标签。切换复用同一 Node 会话，不重新搜索。

## 类型与装配

- App 只装配一个 `SearchResultsPage`，复用既有音乐视图与影视卡片。综合、单曲、歌单、影视分类及手动音乐／影视目标保持现行交互；普通数据刷新不覆盖手动目标。
- `FilmSourceService` 实现 `IFilmSearchSources`，只提供来源配置快照及变更事件。搜索范围为启用且开启搜索的猫源与 TMDB，不包含豆瓣。每源传准确安装／站点／generation，以及顶层 `aggregateImages` 布尔和 `order`；不改变已有四个选项、封面单选或列表排序语义。
- `FilmSearchService` 固定借用一个 `RuntimeConnection`，通过 `film.playback.v1` 调用 `film.search.open/snapshot/command/release/handoff`，以 `film` 事件通道订阅 `/v1/film/events/{sessionId}`。同一快照同时提供完整 `cards/rawCards` 与通道状态。
- Node 唯一执行规则、识别、相关度、分页、聚合与排序，持有来源原始响应和规则快照。客户端只显示选定数组并转发动作，不再直接发搜索内容请求、执行 JS、排序或从卡片生成缓存 seed。
- Capabilities 无规则／搜索 JS 内嵌资源；Platform 不再构建或复制独立规则 DLL。实际执行由运行器载荷的公共 script-host addon 提供，缺失运行器或方法时显示真实错误，无原生回退。
- 正式 entry、manifest 与原生 describe 精确装配 `music,cat,source,pan,tmdb,douban,film,netease` 八模块。通用默认三模块不变；加入豆瓣模块不扩展搜索范围。运行器装配与依赖分别见[平台接入](app-runtime.md)、[打包](runtime-packaging.md)。
- TMDB 设置与图片仍走既有 `FilmMetadataService`、`IRuntimeImageClient` 和 `ArtworkService`。配置、内容缓存、完整度与同 key producer 合并归 [Node 数据 owner](../runtime/film-playback.md)；原生图片缓存与解码预算不等于搜索结果条数限制。

## 搜索生命周期

`FilmSearchService` 是 UI dispatcher owner，`FilmSearchSession` 仅保存最后发布的完整展示快照与借用连接。首轮、加载更多和各源失败／空结果等状态均按 Node 回执显示；不在客户端增加自动重试、截条数或重复取数兜底。切换原始／聚合模式只选择 `rawCards/cards`，保留各自滚动位置。

同词分类切换／返回复用同一个 Node session。普通离开立即停止旧 UI 发布并发 `command stop`；停止回执仅更新 loading／cancelled 等状态，不替换 cards/rawCards。停止前已接纳的 open 若迟到，只绑定分配 ID 以便 stop/release，不发布其快照。显式返回通过 snapshot 和 `command resume` 恢复，不再次 open，也不重取成功页；尚未接纳过 open 才在显式返回时首次创建。

新词、来源／顺序／封面配置变化或 TMDB 设置成功保存撤销旧会话发布资格，不自动搜索。退役会话先 stop，最后一个播放页 retainer 释放后再 release；晚到响应不能改写新查询。设置保存失败保留有效查询。

事件连接失败保留当前显示并报告真实错误，同时显式发送 stop 禁止后续新调度；运行器 epoch 撤权后不向替代 owner 重放旧命令。普通 stop、取消等待及 release 不撤销共享 producer；有限在途内容可按原身份完成并入 Node 缓存。真正运行器关闭／来源撤权由对应 owner 取消并排空。

关闭准备停止准入，等待已接纳 open／command／handoff／release 和本端事件订阅结算；取消关闭后可恢复同词。关闭消费者不接管 App 运行器生命周期，物理进程排空仍由 App owner 负责。

## 搜索到真实播放接收

`FilmCapability.ReceiveSearchPlaybackAsync` 以原 opaque `displayKey` 调用 Node handoff，再经 Shell 进入真实 `film-playback`。较旧点击的迟到回执不能覆盖较新导航；同一 session＋selection 再次点击复用现有播放页，来源撤权后创建新选择。

`FilmSearchPlayback` 只解码 Node 返回的 target/query/sources/results/resultHints/search/metadata/cacheEntries/searchReference，并绑定实际 producer：

- `source` 保原单源 typed ID，数字 `0` 与字符串 `"0"` 不互换；不接受聚合成员或 TMDB 简介扩展。
- 未可靠关联的 `group` 保全部准确成员，不退化为代表项；可靠关联使用 Node 的 movie/tv 与元数据身份。
- 原始行、原查询、已成功页及真实缓存 receipt 原样保留，不从显示标题重建查询、不生成新鲜度证明。换 owner 仍沿既有播放接缝降为 hints，不给旧事实盖新代次。
- 播放 open 与 `search.update` 携带 `searchReference:{sessionId,selectionId}`。Node 重新读取该选择的事实与规则；客户端不上传 rules 或推导 metadataTarget。
- 搜索列表停止后仍可向保留的播放选择通知较新 reference revision，但不发布其 cards/rawCards。播放端只转发 reference，不重搜、不预取全部详情，Node 负责迟到成员／可靠关联及已准备文件保护。会话退役或页面不可达后解除订阅。
- 初次 open 不发送 `resumeTarget`，允许 Node 跟随迟到 handoff。已有确认播放 snapshot 的同 owner 重进才发送当前准确 `resumeTarget`，保留已选元数据、季集、globalEpisode 和 source/candidate；换 owner 不携带失效 searchReference 或 resume marker。目标不由标题猜测。

搜索派生文本可按 `FilmRuleText` 使用 UInt16 数组。展示 helper 接受数组并为 WPF 渲染文本，原 JSON 和 opaque 身份不变，不从显示字符串反推选择。原始来源 `raw` 保持 Cat 契约。

Shell 继续保存原模块位置、公共输入草稿和实际可达的历史／origin。`IDesktopNavigationRetention` 只保可达播放页的 session retainer，无新增条数限制、TTL 或业务缓存。

## 定向测试组

以下为当前测试程序集 `Cases()` 的 group 名；覆盖说明不是执行报告。

| Group | 覆盖 |
| --- | --- |
| `FilmRulesTests` | 六个规则接口、完整草稿、保存失败、opaque revision、UInt16 与关闭排空；业务替身 |
| `FilmSearchTests` | 完整投影与 typed ID、精确顺序／状态、停止／恢复／断线、迟到 SSE/open、来源与 source wire；业务替身 |
| `FilmSearchPlaybackTests` | 原样 handoff、原始单源、迟到 reference、初次与重进、实际 producer、来源交集及取消；业务替身 |
| `SearchResultsConsumerTests` | 真实 Shell/WPF 分类、目标、返回复用、三类导航、迟到更新、封面投影、UInt16 展示与导航先后 |
| `FilmSearchPresentationTests` | 既有卡片／布局、两视图顺序及独立滚动，1110／860 宽度与隐藏 HWND 离屏图 |
| `FilmSettingsUiTests` | 规则草稿／编辑／恢复／失败／取消及受影响设置装配；规则业务替身 |
| `SettingsNavigationTests` | 公共设置导航、App 装配及 Node 不可用时的真实规则错误 |
| `FilmMetadataConsumerTests` | 配置保存成功／失败隔离、迟到结果、关闭写回执及实际内容 producer |
| `FilmRulesRuntimeTests` | 实际 Node/addon 的规则预览、保存／空组重启、恢复／清除、Unicode／flags／UInt16 |
| `FilmSearchRuntimeTests` | 实际 Node 搜索/SSE/Cat Worker、完整页与 typed 成员、选定封面、more/stop/resume/handoff；真实 TMDB HTTP／完整资料／图片和失败回退消费者 |

`FilmSearchRuntimeTests` 接收原 `FilmMetadataRuntimeTests` 中两个受影响搜索／图片用例，语义断言保留；后者剩余设置／缓存／关闭组不因此默认重跑。既有 main 播放、来源和缓存验证仍按各自输入有效性复用，不将此清单扩成全量。

## 真实运行器与执行依赖

业务替身组不启动 Node，但测试进程仍要求真实绝对 `MEOWFILM_TEST_NODE` 和当前编译副本 `tmp/test-results/` 内的 `MEOWFILM_TEST_DATA_ROOT`。STA／Dispatcher、WPF HWND 和正常媒体工程构建依赖仍存在，不手工复制任意 DLL 或绕过正常 target。

两个 Runtime 组使用正常 `prepare-test-bundles.mjs` 生成的当前 Windows 八模块 `aggregate` bundle，以及公共 addon；沿用 `MEOWFILM_TEST_RUNTIME_ROOT` 和默认构建缓存。`FilmSearchRuntimeTests` 的受控 TCP HTTP 上游及 Cat 脚本是合成数据，不等于外部账号、代理或第三方来源在线验收；未取得本轮执行回执前不能宣称实际载体通过。

搜索卡片／元数据图片测试使用隐藏 `HwndSource`（无 `WS_VISIBLE`、有 `WS_EX_NOACTIVATE`），不使用系统输入或前台焦点。离屏图位于 `TestWorkspace.DataPath("film-search", ...)`，实际路径由测试输出；其余设置组沿各自既有窗口要求。

集中验证入口为 `scripts/windows/test.ps1 -Group <上述精确组名>`，不使用 `-All`。脚本按正常流程准备 bundle、锁定恢复、增量测试构建并执行；随后按主会话安排增量构建 App。共享 Node 对照、Roslyn 静态检查、Windows 行为用例与 App 构建应分别报告，不能互相代替。
