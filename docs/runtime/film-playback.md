# 本地影视会话 `film.playback.v1`

## 所有权与装配

`packages/film-playback` 是 App 本地的影视业务 owner。它借用已经装配的
CatNode／source／pan／TMDB／豆瓣模块，负责规则、搜索聚合、匹配、候选排序、会话、数据缓存、
历史、人工匹配、错误片源屏蔽、偏好及稳定内容身份注册。业务不连接 meowfilm-server 的登录、
数据库或播放 API。收藏仍由现有客户端 owner 保存。规则执行与业务会话归 Node；
规则配置及偏好的存储归属依是否注入原生设置区分。

未注入 `nativeSettings` 时，影视规则、偏好、历史、人工匹配和屏蔽由 `packages/film-playback` 的 Node owner 写入唯一 SQLite 数据库中的 typed 关系；规则、偏好、library 各按所属 owner revision/CAS 更新。每条历史与成员关系独立存储，分页读取不要求整库 JSON 物化。schema 不匹配或数据库损坏返回明确存储错误，不写入空库。

注入 `nativeSettings` 后，规则与偏好由原生设置 owner 写入其 typed 关系；Node 仅读取被授权快照，不另存第二份规则或偏好。影视业务资料库仍由 Node owner 持有历史、人工匹配、屏蔽及点映选择关系。
普通原生保存与按块通知见[共同契约](../common/settings-storage.md)，iOS 设置及保护
见[存储](../ios/storage.md#影视设置)，Windows 接线见[原生设置](../windows/native-settings.md)。
HTTP `settings.set` 及规则写入在注入模式返回 `PERMISSION_DENIED`，不形成第二个配置写 owner。

双端 aggregate entry 显式装配九个模块（顺序见[音乐平台运行器](music-platform.md)）；通用 `createRuntime` 未显式启用
pan／metadata／film 时保持原默认三个模块。`connectFilmPlayback` 在 provider
变更前后比较真实 generation／revision，再通知数据、会话及媒体 owner 撤权。
失败回执不证明磁盘变更回滚；仅展示名称变化不使有效媒体失效。

稳定身份与搜索交接的共同语义见[影视契约](../common/film/search.md)，原生展示保留与
恢复边界见[页面契约](../common/pages/data-requests.md#展示与执行生命周期)；
本文件维护 Node 协议字段、数据格式和执行事实，不复制端侧规则。

协议沿用 [runtime.http.v1](runtime-bridge.md) 的请求身份和错误封闭集合；HTTP 入口开放访问，不需要启动或会话凭据。
模块 `film`、版本 `0.1.0`、前缀 `film.`。本文件描述 Node 业务接缝，
不表示原生格式、硬解、HDR 或后台行为已获得运行验证。

Windows 原生候选由 `RuntimeMediaPlaybackIntent` 的同一个实例绑定 prepare、
`TryCommitRuntimeMediaAsync` 和 `DiscardRuntimeMediaAsync`，不能用旧回调提交／丢弃
新候选。每次 attempt 使用新实例；同集继承通过 `PreserveFrom` 携带旧输出的准确
`NativePlaybackScope`，新 attempt 身份不与旧输出共用。原音乐 handoff 不具有
提交／丢弃影视候选的权限；运行器撤权和 App 关闭仍排空其所有实际资源。
Windows 只使用内置原生输出，不提供外部播放器入口或子进程交接。
暂时关闭的业务准入只返回未提交，不丢弃已准备候选。原生对齐的异步边界
恢复后重新检查，实际 swap 与最后一次准入检查在同一 dispatcher turn；Session
释放控制锁后才等屏蔽决定。拒绝后重用同一 receipt 并重新对齐旧输出最新位置／暂停，
成功后才按新快照撤销候选。

## 操作

| operation | 输入与结果 |
| --- | --- |
| `session.open` | `{platform,entrySurface?,entry,searchReference?,restoreOnly?,browsing?}` → 当前纯展示 snapshot；启用站点、顺序与规则均由 Node 内部解析捕获 |
| `session.command` | `{sessionId,action,...}` → 受理后的 snapshot，后续由事件推进；只读 `media.chapters` 返回下述区间结果 |
| `session.snapshot` | `{sessionId}` → 当前 snapshot |
| `session.attempt` | `{sessionId,attemptId}` → 私有媒体 receipt |
| `session.media.release` | `{sessionId,attemptId,resourceId}` → `{released:true}`，重复释放幂等 |
| `session.release` | `{sessionId,keepMedia?}` → `{released:true}` 或 `{retained:true}`（会话保留热态缓存并解绑媒体租约） |
| `settings.get/set` | 读取／原子保存完整偏好 |
| `library.get` | 读取资料库：以统一 `targetId = "work:<sessionKey>"` 归属的历史、人工匹配、错误片源屏蔽、偏好与点映选择 |
| `library.remove` | `{kind:'history'\|'blocks',id?,block?}`，省略或 null 清空对应集合，否则准确删除 |
| `manual.update` | `{action:'save',item}` 或 `{action:'remove',id}` |
| `rules.get` | `{}` → `{rules,revision}` |
| `rules.set` | `{rules}` → 保存并返回新快照 |
| `rules.group.set` | `{group,rules}` → 只归一化指定组，在 owner 写队列中原子合并最新已保存配置，返回 `{rules,revision}` |
| `rules.group.defaults` | `{group}` → 该组默认行数组；不初始化或写入文档 |
| `rules.group.preview` | `{group,rules,request}` → 此组草稿＋关联组最新已保存配置执行预览；不写入 |
| `rules.restore` | `{section:'episode'\|'season'\|'movie'\|'search',rules?}` → 恢复所选段默认值并保存 |
| `rules.preview` | `{request,rules?}` → 规则预览；可用草稿，不写入 |
| `rules.normalize` | `{kind:'match'\|'clean',pattern,replace?}` → 新规则输入标准化结果 |
| `rules.clear` | `{}` → 清除规则行并返回默认快照 |
| `search.open` | `{query}` → 搜索 snapshot，运行器内部读取 `film.video-sources` 启动首轮 |
| `search.snapshot` | `{sessionId}` → 最新完整搜索 snapshot |
| `search.command` | `{sessionId,action:'more'\|'stop'\|'resume'}` → 受理后的 snapshot |
| `search.release` | `{sessionId}` → 释放搜索消费者与会话 |
| `data.catalog` | `{source,action:'home'\|'category',payload?,refresh?}` → 来源目录，条目附轻量 `entry` |
| `data.search` | `{source,query,page?,refresh?}` → 完整单页、页号和 `hasMore` |
| `data.detail` | `{source,detailId,refresh?}` → 所选来源的真实详情／线路／文件 |
| `data.metadata` | `{provider,path,query?,imageSize?,refresh?}` → provider 数据及轻量 `entry` 投影；失败不以旧值冒充成功 |
| `data.metadata.entry` | 同 metadata 输入 → 投影后的 `value`、请求身份、成功时间、完整度及 `cache` 状态；刷新失败可返回同身份旧成功值 |
| `data.invalidate` | `{domain}` → 撤指定来源、账号或 metadata 域并等待排空 |

### 真实媒体章节识别

`session.command` 的只读动作 `media.chapters` 接收
`{sessionId,action:"media.chapters",attemptId,resourceId,chapters:[{title,start,end}]}`，
返回 `{segments:[{kind:"intro"|"outro",start,end}]}`。必须同时匹配此会话仍有效的
current／pending attempt、resource 与媒体租约；其他会话、已替换或已释放的 receipt
返回 `STALE_SESSION`，不存在／已释放的会话沿用 `NOT_FOUND`。没有章节返回空数组。
不创建媒体租约、不写历史、不发布 snapshot，也不触发播放、跳转或 `media.ended`。

章节来自内核读取的真实容器元数据，时间为同一播放时间轴的秒数。章节数量与标题长度不设配额，
标题必须是字符串，`start/end` 必须是有限数，
`start>=0`、`end>start`；不符合时整体返回 `INVALID_REQUEST`。
Node 影视 owner 只匹配完整标准标签：片头／片头曲／片尾／片尾曲、
Opening／Opening Credits／Ending／Ending Credits／Intro／Outro／OP／ED（英文忽略大小写）。
允许空白、最多三位章节编号前缀（如 `Chapter 01: Opening`、`01. 片尾`）
或空白分隔的最多三位编号后缀（如 `Opening 2`）；不匹配任意描述性前后缀。
结果保留原区间，去掉完全相同的重复项并按时间排序，不合并、扩展或猜测章节。
普通首末章节、Credits、序章、预告或没有标记的媒体不因此成为可跳区间。

iOS `FilmPlaybackChapterModel` 借用现有 transport 请求此动作，不在 Swift 复制分类规则。
同一身份及章节快照不重复请求；更换身份／章节、禁用或关闭时立即清空并取消，
迟到回复不能覆盖新媒体。回复仅接受来自该输入快照的有限有效区间；
无效回复／请求错误通过 model 的 `error` 状态提供给统一反馈消费者，不插入页面说明文本。
此 model 不拥有内核章节采集、设置开关或跳过按钮；它们由播放器消费者接线，
跳过操作只 seek 到区间末尾，真实媒体结束仍由内核反馈驱动。

### 规则与搜索

无原生注入时，规则文档由 `createFilmRulesService` 唯一读写，默认初始化、保存、恢复及清除
均通过同一 owner。注入时此服务只消费当前原生快照；`rules.get`、默认值及预览仍可读取，
`rules.set/group.set/restore/clear` 拒绝写入。iOS 的普通读取、分组保存、默认值与纯文本
规范化在原生完成，不经这些 HTTP 写入方法；正则编译／执行仍只在 Node。
分组接口的 `group` 为 `episodeClean`／`episode`／`season`／`movie`／`search`，分别对应
`episodeCleanRegexRules`／`episodeRules`／`seasonRules`／`movieRules`／`aggregateRegexRules`。
净化组 `rules` 是字符串数组，匹配组是 `{pattern,replace,flags}` 数组。空数组表示清空该组。
组预览的 `request.section` 须与组一致（`episodeClean` 对应 `episode`）；关联的另一剧集组
只取 Node 已保存配置，客户端其他组草稿不参与。分组保存仅归一化本组，其他组原样保留；
写入失败不发布新快照。未注入模式的 `rules.set`／`rules.restore` 保留整份提交语义。
Windows 页面整份草稿由原生服务提交，不调用这两个运行器写入方法。
保存仅投影当前定义的规则字段；预览和识别直接消费实际使用的规则组，不在调用前或
每次复用不可变快照时重新校验整棵规则树。未使用的组、查询及普通扩展不阻断本次计算；
实际正则错误、文本表示边界、取消和预算仍按执行结果处理。
iOS 页面新增、编辑、删除和恢复默认只修改本组草稿，唯一页头保存提交本组；失败保留草稿。
保存一旦交给稳定草稿 owner，其 Task 会等待实际落盘回执并更新本组草稿；离页或取消页面
等待者不取消该写入，也不丢弃归一化结果。页内测试随页面隐藏或失活取消，旧结果不再发布；
服务保留取消任务直到调用结束，Node 原生程序先停止并排空实际计算再结束取消。
Node 执行快照的 `revision` 包含共享 QuickJS-NG 0.18.0／Unicode 身份与实际规则内容，不等同于原生文档 CAS 指纹或页面保存 revision。搜索和播放持不可变快照，
保存新规则不改变旧任务、不清原始数据缓存，也不打断媒体。设置预览与播放识别使用独立于第三方
脚本的 Runtime/Context；纯计算可中断并排空，失败后下次调用重建同一内核，不回退到 Node vm。

搜索 snapshot 同时含 `cards/rawCards`、原授权 `sources`、原始 `siteItems/tmdbItems`、TMDB 资料、
查询、页轮次及通道状态。客户端只选视图，不排序、聚合、再跑正则或重取同页。
事件复用 `/v1/film/events/{sessionId}` 的 `snapshot`，`sequence/revision` 单调递增；
重连发送最新完整快照，断流只撤观察者。`stop` 撤发布与后续调度，已接纳的有限网络 producer
仍可完成入缓存；`resume` 仅继续未结算工作，失败页只在显式 `more` 时重试。
具体排序、原始身份、完整页和导航要求见[共同搜索契约](../common/film/search.md)。

搜索卡片已携带 `businessId/playbackTarget`，导航可直接传递固定目标。
`searchReference:{sessionId,selectionId}` 可选；装配层通过 `findHandoff` 复用仍存在的 Node
搜索选择及固定规则，搜索／选择不存在时去掉 reference，按输入固定目标 open。
此降级不把旧游标或展示资料变成新 owner 的事实。
重进时显式 `resumeTarget` 保留用户当前选中的元数据、季集及准确源文件；
只有活跃搜索仍可复用结果与游标，不退回初始卡片目标。
交接顶层的 `selectionMembers:[{sourceKey,detailId}]` 保留该选择的完整 typed 成员，
不随晚到 TMDB 将卡片改为 movie/tv 而丢失；当前 group 仅据此补成员，不重新匹配整页结果。
`sources` 保留完整原搜索授权，`results/resultHints` 仅含所选卡片成员；
已确认游标与合法缓存不因卡片成员数量而收窄。
该事实由装配层直接传给播放 owner，客户端同名字段不作为成员依据。
晚到 `search.update` 同样重读，
只补成员或可靠资料，不替换已准备／正在播放文件。没有搜索 reference 的首页、历史及明确站源
入口仍使用原有 open 契约，由 Node 规则 owner 捕获快照。

### 关键字固定会话、智能列表与四阶段智能播放

- **自包含视频源与映射读取**：运行器通过 `nativeSettings` 的 `film.video-sources` 与 `cat.sources` 块实时读取启用视频源、搜索开关、排序、聚合封面站点与网盘别名映射（`mappings`），客户端不再在 `search.open`／`session.open` 上传 `sources`、`installationId`、`generation`、`cacheEntries` 或爬虫 `raw` 行。
- **统一作品身份与固定关键字会话**：作品持久身份统一为 `targetId = "work:<sessionKey>"`（以净化后标准作品词 `keyword` 锚定），历史、人工匹配、错误片源屏蔽与点映偏好全部绑定该主键。会话在运行器内存中按 `sessionKey` 保持热态（`IDLE_SESSION_TTL_MS = 15 分钟`、`MAX_IDLE_SESSIONS = 24`），离开播放页仅解绑媒体租约；TMDB 与豆瓣作为会话元数据槽位挂载。搜索页与播放会话共用 `data.search(source, query, page)` 缓存与 in-flight 合并；从搜索词 `Q1` 进入关键词为 `Q2` 的卡片时，`Q1` 已缓存结果以 `attachedDisplayOnly` 挂入 `sourceMenu` 供展示切换，而 `search.more` 按 `Q2` 扩搜。
- **未配置免请求与豆瓣游客态边界**：未配置 TMDB 时跳过官方 TMDB 请求；未配置豆瓣 Cookie（游客态）时仅允许对携带豆瓣 ID 的入口卡片调用 `/rexxar/api/v2/{movie|tv}/{id}` 读取简介与 `episodes_count`（生成 `1..N` 剧集列表，不复制主剧简介到单集，单季剧默认归第 1 季，单独第 2 季初始化至首个可用季集），绝不调用 `searchDouban`。当可用视频源与人工匹配源均为 0 时，发布 `noSources: true` 与 `error: "NO_VIDEO_SOURCES"`（双端统一显示 `暂无可用视频源`）。
- **单次识别复用与网盘凭据边界**：每个站点结果按签名经 `ensureRecognized` 执行一次识别并缓存不可变快照，浏览、`智能列表`、切集与智能播放共用同一结果，切集不清空 `session.candidates`。`pan-list` 保持 5 分钟鉴权凭据 TTL 与站点作用域；客户端快照绝不暴露 `fileId`/`stoken`/`fileToken` 等网盘凭据，真正取流时若凭据过期由 `data.js:resolveValue` 经 `shareOrigin` + `stableFileIdentity` 内部透明重拉刷新。
- **无元数据「智能列表」与六级季度优先级**：当会话无 TMDB 且无豆瓣数据，且有效剧集站点结果 `>= 2` 时，`sourceMenu` 首位生成 `智能列表`（`id: "smart"`），按 **1) 文件名季集 > 2) 展示名季集 > 3) 网盘目录唯一季号（`folderStats` 含纯画质子目录向父目录穿透） > 4) 站点原始标题 `seasonRules` 提取季号 > 5) 手动匹配季提示 > 6) 无季标默认第 1 季低优兜底** 聚合分季与集数；选集区提供 `episodes.more`（「加载更多」）增量展开未读站点／分页。
- **四阶段智能播放与语义快照**：智能选源严格按 **Stage 0 已缓存上下文候选（邻集预取 -> 当前同源同线亲和 -> 会话／智能列表已解析候选） -> Stage 1 历史记录（先网盘分享直展 `history_list`，再站点详情 `history_detail`） -> Stage 2 人工匹配源（连续 3 次失败自动停用） -> Stage 3 智能搜索（按已缓存可用、显式季标、`vod_remarks` 更新集数 `>=` 目标集、精确键、相关度与站点顺序预排，并在请求 `detail`/`expand` 前前置过滤 `searchable === false`、`smartSkip`、`siteCleanKeywords` 与 `film_blocked_sources`）** 执行。会话对外统一输出规范化 `work`、`sourceMenu`、`lineMenu`、`directory`、`player`、`noSources` 快照，客户端仅发送 `select.item`、`browse.source`、`browse.line`、`browse.season`、`browse.metadata`、`episodes.more`、`search.more` 等语义命令。

资料库启动不预检全部控制记录的业务语义；普通更新只校验实际新增或修改的记录，未改的坏兄弟保留原存储行，实际选用坏记录仍明确失败。history、manual、block、group 与偏好输入只投影已有 typed 字段，普通扩展不拒绝操作，也不进入永久存储；人工匹配保存省略的分享口令、季提示和来源字段按本次选择清空。

资料库和元数据缓存采用关系表、typed columns 与有序子关系，单次变更只更新所属实体和关系；metadata owner 保留上游扩展需要的 typed value-tree，不保存完整 JSON 文档。原生配置注入时只使用原生 owner 的偏好事实，缓存与收藏仍按各自 owner 管理。

`entrySurface:'home'|'search'` 标识进入位置，snapshot 的 `entryTarget` 保留原始卡片目标，
`target` 是运行器当前使用的目标；客户端不据显示标题重新构造来源。
首页豆瓣 subject 只是 TMDB 查找种子，不建立独立豆瓣播放会话。Node 按标题执行一次
TMDB SearchMulti 首页查询，按真实媒体类型及年份选择匹配，命中后读取该 TMDB 详情；没有匹配时保留
`kind:'group',members:[]` 的等待态和原卡片信息，不自动调用站点搜索。
`entryResolution` 区分 `loading`、`matched`、`unmatched`、`error`；
其他入口为 `null`。只有 `unmatched` 展示手动搜索动作，请求失败不能当成未匹配。
用户点击“搜索剧集”后才发送 `search.more`；选择已有站点只读取准确详情，
选择网盘／线路再按需读取其目录，均不重新按标题智能匹配。
空等待组仅由 seed 转换创建；调用方传入的空 group（`target` 或 `resumeTarget`）
均以 `SOURCE_NOT_FOUND` 拒绝，携带其他有效恢复目标也不例外。
等待组获得真实成员后，全部成员撤销表示来源失效，不再作为首次 seed 等待态；
刷新不重新查 TMDB，搜索／交接及晚到搜索结果不能补回该组。

source 包含 `key,engine:'cat'|'source',installationId,siteId,generation,name`；
portable source 另带 `revision`。原始详情、线路、集数标识保留 string／安全整数
类型，0 合法。未知线路名称只显示、原样调用，不由客户端猜测网盘种类。

既有搜索结果用 `results:[{sourceKey,items}]`、`search:[{sourceKey,page,completed}]`
交接。items 是完整原始行，详情 ID 保留 JSON 类型；page 是最后已确认成功页。
有效 pagecount 优先；缺少时，完整非空页继续，空页或整页没有新增 typed 身份才结束。
失败不推进页码，在途／取消不标成完成。`metadata` 只提供立即展示，
不凭调用方布尔值宣称完整／新鲜。已有成功数据
通过 `cacheEntries` 交接：

```text
kind: search | detail | metadata
value, completeness: partial | complete, fetchedAt, expiresAt?
search: source, query, page
detail: source, detailId
metadata: provider, path, query?, imageSize?, settings:{revision}
```

请求身份、配置代次和原始时间必须来自实际 data owner。`data.cache.entry` 导出同一
记录，不重置 TTL、不请求上游内容。旧代次／过期记录不成为新命中，partial 不冒充
full。可选 handoff 中内容无法消费的记录仅作为缓存 miss，健康记录继续接收并按需
真实加载；请求身份、配置读取及取消错误仍按原语义失败，显式 seed 仍报告坏内容。
一个 32 MiB 有界内存缓存拥有数据、完整度、过期时间和 in-flight 合并；超预算
结果完整返回但不在内存保留，不截断业务页。站点数据、盘列表、stored、短效 URL
随进程，仍分别按真实来源／账号／模式与短效期限失效。TMDB／豆瓣业务元数据另以
共享 SQLite 的 metadata 组件、类型化实体及关系原子持久，复用相同缓存 owner；
请求身份不以明文文件名或持久请求对象泄露凭据。历史／人工匹配／屏蔽属于独立资料库。

会话保留已读取资料的线路／季集投影及浏览选择，不拥有第二份 TTL。
每次浏览 detail 仍经过 data owner 的来源有效性和缓存判定；相同不可变条目或实际相同内容
复用规整详情、完整识别目录和所选线路投影，不因取址、失败或进度快照重新识别。
分享扩展从原 detail 的分享行重新交给 data owner 判定盘列表／账号有效性，
不能以已展开的展示行绕过列表期限。真实资料变化更新投影，撤权或会话释放撤销 Node 执行资格；
原生已加载展示的保留由[页面契约](../common/pages/data-requests.md#展示与执行生命周期)约束。
元数据季集 map 按资料引用复用，可见集另随北京时间日期和 preview 改变；
已加载季和关联资料不变时切回沿用原投影。上述引用只用于派生复用，不替代来源／账号／配置验证。

Pan `settings.get` 的坏账号以 `{provider,errorCode:'CORRUPT_STORAGE'}` 表示，不包含
伪造的 `revision/configured`。Film 仅验证本次选中的百度、普通夸克、普通 UC、天翼或移动
账号；其他坏账号不阻塞健康账号的 list／prepare／play，未知 provider 不注册能力。
选中坏账号明确失败，所选身份重复或非法代次返回格式错误；真实账号身份与 revision、
`STALE_ACCOUNT`、取消和失效排空继续核对。

元数据状态是组件级的 `ready/empty/missing/failed`；正常空值和未公布排期是成功数据，
摘要不能冒充详情，季度概要不能冒充逐集响应。`metadata.entry.cache` 包含
`status:'fresh'|'stale'`、可选 `error:{code}` 及 `components`；各成功组件保留各自
`fetchedAt`，读取、搜索摘要或只补演员／图片不会续期整份详情。有效完整响应的空字段和
删除季集覆盖旧值，失败不清已有成功内容，不把旧值标为新鲜。

电影稳定详情十四天；TV先判断所需详情／季度组件，再按 Ended、下一播出日或未知状态
选择十四天、北京时间播出日边界或一天。season结合真实集数、父季度声明及排期，
只对当前请求补全；未来总集数不是已播证据。父详情的相关季集／状态／排期改变后重新判断
该季，常驻页面与全库不轮询。搜索结果六小时，豆瓣热门列表一小时。
显式 `refresh:true` 只绕过准确请求的有效期／当天成功抑制，不清全局；默认请求复用有效数据。
页面停止等待不取消已接纳producer；来源／账号／配置失效和真正关闭则撤代次并排空，
旧在途不能回写内存或持久文件。

搜索与播放均使用 `film.data.search/metadata`。TMDB 搜索请求为
`/3/search/multi`、`{query,page:1}`、`imageSize:'w500'`；
完整详情为 `/3/{movie|tv}/{id}`、
`{append_to_response:'credits,similar,images'}`、
`imageSize:'original'`。运行器按当前设置补充语言、地区和成人选项；有效查询字段和图片尺寸属于缓存身份，不从展示快照伪造完整记录。
原生不再拥有独立的成功详情缓存或跨请求合并；当前页面仍可持有展示快照。
列表中可定位的坏条目不使健康兄弟丢失；真正缺失列表或非空列表全不可用仍报告失败，
不伪装成成功空页。豆瓣搜索只消费 `query`，额外材料不进入实际请求或缓存身份。

来源结果、完成页与来源缓存的交接还须证明来自同一个运行器 owner；Cat 的
generation 数值可能在新进程重新从 1 开始，不是跨进程授权证明。Windows 用仅在进程内
保留的 `DataOwner` 绑定原数据 producer，导航持久化不序列化它。换 owner 或缺少证明时，
原结果通过 `resultHints:[{sourceKey,items}]` 保留准确详情指针与原备注，按需读取新 owner
的 detail，并以真实返回记录重新匹配目标后才生成候选；不预抓全部详情。
元数据智能路径重验标题等匹配证据；站点精确路径仅核对真实来源、typed 详情及准确文件，
不将旧展示标题变成精确取址的匹配门禁。
旧搜索页完成状态和来源缓存不跨 owner；metadata 记录仍按原配置 revision、时间和完整度验证。
新 owner 绑定通过既有来源 owner 取得当前 ready 来源，只使用其与原授权范围的交集；
当前季集及原始单源选择不退回首集，不扩大为全站。普通 SSE 重连仍只取同一会话快照。
重开交集按 engine／installation／site 等授权身份判定，名称变化不撤销来源。
当前 generation／revision 用于新请求，原 producer 证据及旧数据代次不改；
group 仅移除已经不可授权的成员，仍可使用原范围内其余有效来源，
没有有效成员时明确失败，不追加新站点或降成代表 raw。

### 读取恢复

`session.open.restoreOnly:true` 跳过初始化自动 `playSelection`，不按 Windows 历史
另选初始集，也不触发 iOS 自动准备。正常新进入未设置该字段时，保留原平台触发策略。
可选 `browsing` 只允许与 `restoreOnly:true` 一起提交：

```text
browsing: {
  sourceId?: string | null,
  lineId?: string | safe integer | null,
  metadataSourceId?: string | null,
  season?: nonnegative safe integer,
  source?: {key,engine?,installationId?,siteId?,detailId}
}
```

`sourceId` 是来源结果身份，`lineId` 保留 typed 值，`metadataSourceId` 是既有已证明资料选项。
原结果未被新会话带入时，`source` 的真实安装／站点／详情指针补回该来源结果并映射新的
`sourceId`；人工网盘来源仍须找到有效的原资料库关联，不按显示名或剧名恢复。
Node 等待必要的资料来源发现，恢复所选元数据／季度，再浏览真实来源及线路；
`browsing.loading` 在恢复结算前保持 true，失败发布真实错误。该过程只做必要读取，
不要求客户端按猜测延时串联多轮恢复命令。

晚到搜索关联也检查 `restoreOnly`：可以补充已证明的元数据，但不把已恢复的来源目录
切为智能播放目标，不触发自动播放／准备。真实 App 返回、重连及导航顺序仍需实际验收。

### 晚到搜索交接

当前卡片来源范围遵守[共同搜索契约](../common/film/search.md)。播放会话的`results`／
snapshot `sources`是已准入来源，不是完整原始搜索页。
Node在接纳初始结果、`search.update`和后续搜索页时统一核对内容归属；搜索owner已证明的
typed成员、准确原始单源以及同目标历史／人工关联保留各自身份依据，不改为标题猜配。
其他原始结果只留在既有data搜索缓存；搜索游标的前进／完成由原始页身份判断，
不能因本页没有当前作品就提前宣告搜索结束。同内容分季不按当前播放季集从菜单删除，
元数据智能播放仍另行核对该季集资格。客户端不再自行筛选来源。
已准入的当前owner搜索结果可复用稳定卡片／可靠carrier的元数据内容身份，但不因此免除
真实年份、季数、类型及显式provider ID冲突检查；未验证旧hint必须以当前详情重新证明。
仅由历史／人工关联加入的目录不因此获得独立内容证明；编辑或撤销关联后，
不能借用卡片身份继续智能选用旧目录，仍独立成立的搜索／typed成员证明不受影响。
内容准入计算属于页面会话；开始播放另一文件只撤销播放目标计算，
不取消在途的来源更新。停止或关闭页面仍撤销该会话全部计算。

搜索页的 `search.update` 命令携带 `searchReference` 与当前 `sources`；
装配层从 Node 搜索 owner 重读
`{selectionId,revision,selectionMembers,sources,results,resultHints?,search,cacheEntries?,metadataTarget?,metadata?}`，
而非采用客户端上传的同名事实。
交接顶层 `selectionId` 是初始稳定 target.id，而 `searchReference.selectionId`
是搜索投影的 opaque `displayKey`，两者不要求相等；revision 为 Node 搜索会话的单调发布代次；
只接受同一选择、原 scope 和 generation，不能追加授权来源。
跨 owner 仍降 hints，不传旧游标和来源 seed。已停止／释放的会话不产生新请求。

更新只合并已经取得的成功页、成员和已证明的元数据关联，不自动重搜或预读全部详情。
`metadataTarget` 必须来自共享搜索的真实承载卡，包含 movie／tv、provider 和 ID，
完整资料由统一 data owner 读取。原 group 尚未用户选集、且没有 pending／current 时，
可保留 selectionId 升为真实元数据目标，并按平台触发规则继续；已有媒体则仅补充展示资料
及可明确选择的 metadataSources，不能猜当前季集、替换 attempt 或重启播放。
raw 保持准确单源／详情，不接受关联扩大。页面重进保当前已选 target，
包括 raw 的 opaque candidateId，不回初始请求闭包的目标。

## 匹配与页面命令

- 两类业务分派、默认选择及智能净化作用域遵循[共同交接契约](../common/film/search.md#搜索到播放详情的交接)，
  不以入口 displayKey、成员数量或自动触发判断智能播放。
- `prepare` 只取地址；`play` 请求当前正式播放目标，复用同一准确目标的有效已准备地址。
  已有旧 pending／current 不吞掉新的选择；播放器 transport 仍由原生控制当前媒体。Windows 正常新进入
  自动触发正式链；iOS 默认自动准备，允许关闭；`restoreOnly` 初始化不触发。初次 metadata 卡片没有选集坐标时，
  iOS 取首个真实正片集，不以历史集数或进度替代；Windows 优先有效历史，否则取首集。
  明确带有季集的重进保留选择（包括季 0），Windows 仅同集历史可承接播放位置，
  不套用别集进度。电影不造集号。iOS 准备不建 decoder、不抢音频 owner、不记观看。
- 元数据智能阶段：可用候选 → 历史 → 人工匹配 → 已得结果与增量搜索。地址进入 pending
  就停止新调度；真正媒体失败才续选。候选／原始文件身份防止重复选同一失败资源。
  已过期元数据可以继续展示，但不能据此准备／播放新候选；先刷新当前准确资料，
  刷新失败保留展示及真实错误，不把旧资料标为 fresh，不影响已提交的当前输出。
  站点目标先建立准确来源与必要目录，直接沿现有 detail／expand／resolve 取所选文件，
  不进入上述智能阶段；缺失的准确文件返回 `NOT_FOUND`，真实取址错误原样保留，
  不重新匹配、继续全站搜索或替换其他文件。
- `search.more` 只加载尚未完成来源的下一页；菜单打开不产生请求。
  `browse {sourceId,lineId?}` 只读所选 detail／必要分享列表，`sourceId:null` 返回
  纯 metadata。首次读取在初始快照即报告 `browsing.loading:true`；detail 返回后先发布
  线路菜单，只展开当前选中的线路，实际解析完成后才结算目录 loading。成功空目录没有
  虚构候选，失败记录真实 `browsing.error`，不把未解析的分享当作成功文件。
  切回已加载线路沿用上次选择，盘列表有效命中不重复请求上游。两端页面按来源、typed
  线路、资料与季度保留成功展示切片，正常切回不发送 browse RPC，不另建业务 TTL。
  browse、browse.metadata、browse.season、browse.refresh 可携带
  `{sourceId,lineId,metadataSourceId,season}` 完整展示上下文；Node 原子采用并只读所选目录。
  所有浏览仅更新展示，不改变正式 target、播放资料映射、pending/current、预取或自动策略，
  浏览加载／错误也不覆盖播放状态／错误。
- `browse.metadata {metadataSourceId,keepSource?}` 只能选择已证明关联；
  `keepSource` 默认 true，保留当前站点／线路，只切分季资料；显式 false 返回纯资料目录，
  显式 `sourceId`／`lineId` 优先于该默认。站点浏览不选择资料 provider，也不写偏好。
  关联查询不阻塞主资料匹配。
  同一作品的豆瓣 subject 按已确认分季归入一个豆瓣选项，各季保留原 subject 身份。
  可靠豆瓣资料和选项不因季度坐标／集数与 TMDB 相同而删除。可信 `doubanSeasons` 优先于
  豆瓣原始 `seasons` 关联条目。季度划分相同时按季／集映射，否则只按可靠全局
  集号映射；已对应集复用真实 TMDB 集名、剧照、简介、时长及播出日期，未对应集
  保留豆瓣坐标与原生封面回退。映射不替换豆瓣身份／总集数，也不重排原始文件。
  切回 TMDB 按当前真实季度读取 owner 已缓存的完整季资料，不能以集数占位覆盖它。
  TMDB 补充季资料的真实失败通过 `metadataError` 报告，已有旧资料仍可展示；
  不把可用的豆瓣主资料变为空列表，也不以主资料成功掩盖辅助资料刷新失败。
  `browse.season {season}` 按需真实分季；映射无证据时不造季或集。
- `browse.refresh {refresh?}` 默认显式刷新当前元数据及所看季、或准确站点详情／线路目录。
  `refresh:false` 用于普通页面恢复，仅让owner判定是否过期／缺资料，不强刷。
  snapshot 的 `initializationError` 非空时，重跑失败的资料初始化，包括豆瓣入口查找、TMDB 详情和所选季；成功前不允许启动新媒体。初始化失败没有媒体候选，不能用 `media.retry` 恢复。
  保留旧成功展示并报告刷新错误，不更换当前播放文件、目标坐标或媒体lease。
  已加载目录在校验期间以 `browsing.refreshing:true` 保留 rows，不先发布空目录再还原。
  新成功资料已删除的线路／文件不能因随后扩展失败而回滚复活。
  snapshot 的 `metadataCache` 报告资料/当前季刷新状态；`metadataSupplement` 仅在豆瓣
  与唯一TMDB可靠关联时提供原TMDB资料供图文／演职员／相似作品填缺，不改主身份或季集。
- `select.target {target,metadataSourceId?}` 选择运行器给出的完整 `playbackTarget`；
  `metadataSourceId` 是该目录已证明的资料映射身份，不从客户端坐标或 candidateId 猜目标，
  也不先发送浏览同步。Node 核对内容身份、授权来源、真实 detail／line／candidate 及映射；
  已失效或伪造的目标在撤销可用 pending/current 前拒绝。
  元数据页面选择具体站点文件后，`target` 为同一内容 id 的准确 `source` 目标；
  `entryTarget`、元数据来源及已知资料保留，不让展示补全改变文件身份。
  普通 `group` 保留真实成员，用户仍可精确选择 owner 已提供的其他合法目录文件，
  不将其冒充原组成员；伪造候选与失效来源仍拒绝。
  `next/previous` 和预取使用实际播放资料映射与真实列表，不读取正在浏览的另一套资料。
  同集换源保进度和暂停意图，新集从正确起点，替代失败保留可用旧流。
  旧流结束只负责其历史与释放，不能覆盖后发的选集／换源意图。
  当前尾部无下一集或所选季缺失时先让owner检查相关资料／目录，仍无真实记录则结束／报错，
  不凭总集数造下一地址，也不全站重新搜索。
  Windows 原始来源续播按已保存的稳定候选身份恢复文件；文件已不存在时不把旧进度套给其他文件。
- `pan {provider}` 固定标准盘族；`quality {quality}` 固定质量并保持另一维偏好。
  `switch` 临时排除准确当前文件；`block {attemptId}` 持久保存目标＋站点详情＋线路后续选，
  电影同样适用。确认框捕获打开时的 current attempt（无 current 时才取 pending）；
  确认期间已换成其他 attempt 则返回 `STALE_SESSION`，不屏蔽未经确认的新片源。
  写失败不得提前改变匹配结果。
  source／group 已选文件在这些显式操作或真实媒体失败后，只有已证明实际同集坐标时
  才临时解开文件／线路锁；无季集坐标的 HLS variants 仅可在同父 source/detail/line/playId
  且同 fileIdentity 内重排，不将下一原始文件当作替代。raw 仍限原 source／detail，group 仍限原范围；
  无上述证据则 `NO_MATCH`，不能拿另一集补位。普通重开始终要求保存的准确
  candidateId；替代失败保旧输出与恢复目标，成功后才保存新文件身份。
- `media.retry {attemptId?}` 定向撤掉准确 pending/current（或最近失败候选）的短效地址，
  重新取同一文件，不变成换源搜索，保同集进度／暂停意图。传入旧 attemptId 则拒绝。
  客户端按 snapshot 的 `retryMediaAttemptId` 启用重试并原样传回；值由运行器按 pending、current、失败终态的 lastFailed 顺序推导，没有有效候选、初始化失败、来源失效或已停止时为 null，不按通用 error/unavailable 状态猜测。候选耗尽仍可重试有效的最后失败项。
  `media.failed` 同样使失败地址失效，随后仍按既有暂时排除和换源逻辑续选，不永久屏蔽目录。
- `preview {enabled}` 只切未播集显示／选择范围并按内容持久；不凭目录最大集号
  推已播。`shortvideo {enabled}` 准备已知相邻地址，预取下一集真实有限媒体字节。
  提升预取项为正式播放复用同 lease，只取消预取任务、不撤正式媒体。
- 人工匹配连续三次 bootstrap detail/list 请求失败可自动停用；取消与解码失败不计。
  修改引用／季线索或停用在保存成功后撤销旧关联、prepared 与邻集预取；旧在途
  不能覆盖新记录的失败状态。已知人工分享列表同样参与相邻取址／媒体缓存。
  设置包含三维规则顺序、盘序、关键词序、净化关键词、`iosAutoPrepare` 及
  `metadataViewProvider`（只接受 `tmdb`／`douban`，默认 `tmdb`）。
  资料视图偏好由设置 owner 持久化，用户手选视图由原生写入该偏好；浏览命令本身不写设置。
  新页面按偏好选择可用资料，仅有其一时临时使用该 provider，不覆盖偏好；
  已明确选择／恢复的本页资料身份不被迟到关联查询改回默认。顶部作品资料不使用此偏好。
  同值通知不重发状态；只改变显示偏好或 `iosAutoPrepare` 不重启在途取址、
  不替换 pending/current、不重置预取，自动准备开关只用于后续自动入口。
  净化只限制元数据智能选源，不限制普通 `search.more`、来源选择或精确文件。
  实际选路策略变化重核 prepared 与发现范围；来源改名仅在净化资格改变时触发重核，
  其他改名只更新标签，不强停此前已开始的 current 媒体。
  原生设置块通知与直接设置操作共用已观察的 provider 状态：
  `pan.accounts` 仅撤销实际 revision 变化的网盘 provider，`cat.sources` 仅撤销
  实际 generation／revision／执行状态变化或移除的 installation；网盘接管开关或别名
  变化会撤销 Cat 来源的旧目录与媒体，防止继续使用此前接管方式生成的结果。
  显式与主动账号同步收到脚本持久保存回执后，通过 installation／generation 的
  `script-account` 依赖撤销原始脚本目录、搜索、详情、取址和 handoff；脚本没有公开
  每项结果所用账号，因此该依赖覆盖对应实例的脚本结果。已交接的 pan 分享／文件、
  内置目录和当前媒体只消费来源身份及 pan 账号，不因脚本保存而撤销；同账号代次
  Cookie 轮换触发脚本同步时，正在执行的内置列盘／取址继续完成。混合详情撤销
  脚本部分并保留 pan 部分，后续脚本消费重新读取当前详情。
  未变化账号、来源的
  目录、pending/current 和媒体 lease 保留；真实撤权仍同时作用于 data、播放、
  搜索和受控媒体 owner，发布失效并等待资源排空，不以设置成功回执推测实际提交结果。

开启网盘接管后，脚本请求落入尚不支持的端点或无法确定分享身份时，不发送该网盘请求。
本次调用的明确接管失败以 `PAN_INTERCEPTION_UNSUPPORTED` 原样通过 film 返回客户端，
提示关闭对应网盘接管后重试；脚本返回空详情、原始 opaque ID 或另一成功分享都不能
掩盖该失败。无失败且准确交接的分享作为按需入口使用，占位响应不代表内置 pan 已列盘
或账号已验证成功。

## 观察与媒体反馈

GET `/v1/film/events/<sessionId>?after=<sequence>` 返回：

仅消费单值整数 `after`；未知查询项不参与观察位置，也不使有效订阅失败。

```text
id: sequence
event: snapshot
data: JSON
```

只保留当前状态并随背压合并，不积累无限事件历史。重新连接先交当前快照；
断开观察不关闭业务会话，也不停止实际媒体。snapshot 含 entryTarget、entrySurface、target、stage、metadata、workInfo、
metadataSources、sources、search、browsing、episodes、candidates、attempt、current、
preferences、error、initializationError、retryMediaAttemptId、errorStage、reportError 和 prefetch；不含 URL、头、分享密码、playId 或 DRM key。
仍发送完整快照，sequence 语义不变；没有新增目录 revision 或差量协议。

`metadata`／`metadataSourceId` 是当前浏览资料；`workInfo` 是独立作品投影，为 null 或
`{provider,metadataId,kind,title,summary,seasonCount,episodeCount,metadata}`，provider 为 tmdb／douban，
kind 为 movie／tv。已有真实 TMDB 资料时固定优先，仅简介为空时使用可靠关联的豆瓣简介；
无 TMDB 时使用可靠豆瓣资料，不采用站点简介。豆瓣多季总量只汇总已确认且不重复的季，
没有真实总量时计数为 null，不从所选网盘文件数猜测。`workInfo.metadata` 提供所选作品的
完整 catalog 资料（包括图文、评分、类型、演职员、推荐及其稳定业务身份），不是浏览切片；
TMDB 空简介的可靠豆瓣填缺同步投影至该资料。收藏／页面入口身份仍使用 `entryTarget`，
不因关联 provider 变化改成另一作品身份。浏览站点、资料或季度不改变这一规则。

`browsing.episodes` 是具体站源按可靠季映射过滤的选集；首次加载失败或线路真实缺失时为空，
已有有效展示的刷新失败不清空目录。错误由客户端
统一浮层显示，来源菜单仍可切换。`browsing.originalEpisodes` 保留完整所选线路供原始目录，
目录浏览不转存、不自动播放。纯 metadata 的 `episodes` 仅包含真实资料。
电影候选按质量排序；原生提供固定宽度、截断长文案。

`attempt` 只是媒体意图；私有 `session.attempt` receipt 含
`attemptId,id,url,mediaOrigin?,format?,drm?,title,position,paused,scope?`。
只有可信 native owner 消费，不记录日志或放系统 NowPlaying。
内部媒体 scope 保留来源 installation／generation 及实际 pan 账号依赖，供所属 owner
精确失效；原生不从附加 scope 字段推导业务权限。
有上游请求头时一次性生成本地流式资源，由 `mediaOrigin` 标识其本地资源来源；
不把 Cookie 等凭据放进公开候选或快照，不要求播放器先直连失败再换路。
候选和公开投影保留实际存在的 `quality/resolution/fps/codec`；这些字段来自网盘／媒体
规整，不由客户端从站点名、线路名或标题猜造。

实际选中 Cat 父候选后，film 按需使用现成 HLS metadata 增强，消费
`_meow.media[].variants` 的真实 URL／分辨率／帧率。首次指定画质也先发现再过滤，
不因父候选尚无质量而提前拒绝。各 variant 使用不同 opaque candidateId，保留父文件、
来源和季集身份；质量由现有规则规整，缺字段不继承父文件的高画质标签。
父播放描述可共享，variant 地址缓存和失败排除分别按准确子候选隔离；
强刷重取父描述后必须仍找到同一 variant，消失时失败，不回 master 或另一个 variant。
新父描述带来的质量变化同步更新候选，并在准备前重新核对画质约束；
同一 variant URL 的冲突媒体字段返回 `UPSTREAM_FORMAT`，不靠行序选择画质。
同文件 variant 切换承接进度与暂停，失败后允许同父其它真实质量续选。
网盘 prepare 仍按原稳定文件身份复用，普通 provider play 不增加 transcode／quality 参数。

原生在真实输出后发送 `media.started {attemptId}`；
`media.failed {attemptId,code?}` 只接受准确 pending／current attempt，按既有排除／续选规则处理；
`media.progress {attemptId,position,duration,paused}` 为秒；`media.ended` 触发真实
下一集或 ended。历史只在真实播放后写入，播放中进度节流为 12 秒，暂停切换、暂停中位置变化、停止、结束强制。
观看上报仅在 play 明确返回 `watchReport:true` 时调用正式 report action，准确保留
原 flag/id，拒绝任意回调 URL。错误单独显示在 `reportError`。

`code` 只允许双方公开 native 播放失败码及既有 Film／Bridge 安全业务码的有限白名单，
不接受任意文本、URL 或 PiP 控制错误。native 阶段由 pending／current 推导为
`prepare`／`playback`，业务回执码为 `receipt`；最终 `error` 对应该反馈时附带可空
`errorStage`。未传 code 仍走同一入口；通用 `FAILED` 归为 `UPSTREAM_ERROR`／`receipt`，
不伪造原生原因，未登记码拒绝为 `INVALID_REQUEST`。反馈码仅进入快照，不作为 RPC 失败重新抛出。
Windows FilmKernel 的格式、加密、解码、音频、渲染、HDR 显示、DOVI 与设备丢失错误
使用其公开 `FILM_*` 有限码，仍按准确 attempt 归入原生 prepare／playback 阶段。
同一次自动续选保留首次原生原因，后续取址泛错不覆盖；真实输出成功、定向重试或新的播放动作
重置该原因，旧输出的反馈不能污染后发动作。Cat／source 请求失败按其有限公开码映射为
HTTP、格式、脚本、权限、来源状态等类别，未知错误仍为 `UPSTREAM_ERROR`。
选择期间继续发布真实来源／候选失败进展，但不反复发布临时 `session.error`；
最终无可用结果才发布准确失败。不改变准确文件播放的匹配范围。

候选 prepare／commit 与旧输入退役是两阶段。native dispose 通过三身份释放 lease；
已释放 receipt 不能再次获取或 started，但内部当前／候选至多两个身份仍可接受
紧随物理退役的准确终态反馈；新操作替换后旧反馈拒绝。不增加 tombstone 缓存。

## 停止、撤权与本地流

普通停页停止新调度，有限在途数据可进入有效缓存，不回写旧 UI。账号／安装代次
失效及 runtime close 则取消 producer、撤原生发布权并等待实际请求排空。
会话向多步骤 data 调用传入 `canSchedule`，按该动作的代次在下一个上游请求前停止；
不把它接入已开始的缓存 producer。明确重新浏览／播放可开启新动作，最终观看上报仍独立排空。
`keepMedia` 停止业务调度、保留准确媒体，可由真实系统 owner／PiP 或独立外部播放 owner 持有；预准备本身不保活。
iOS 外播请求开始前持有该份额，内置解码／音频排空不提前释放外播本地流。返回前台、明确 stop／新 play、最终 close 或 runtime 退出时释放外部份额；最后份额退出才真实释放 Node 媒体，不自动恢复播放、不伪造外部观看进度。系统实际挂起仍可能暂停本地 Node，不用静音播放或假 PiP 保活。
曾转交外播的准确媒体回执由业务 owner 持有至最终释放确认；旧 native 在新外播之后才排空也不能丢失该回执。Node 释放失败保持可见，由后续明确 stop／close 重试，不重放已完成的 native 释放，也不改写原生排空的不可变失败结果。
原生仍须负责自己的直连 HTTP、解码器、surface、音频和系统生命周期。

无必需头且未显式短视频缓存时直接把 URL 交原生；有头第一次读取即走 Node
二进制流。Cat 自有媒体通过同一入口衔接实例生命周期；准备时仅选中的 `.m3u8`
父描述允许 SDK 读取一次清单以发现 metadata，未选文件、variant 子清单、key、init 与媒体段
不提前读取；DASH 和 extensionless 描述沿原冷取址路径。选择本地路由不会自动启用预取。
同一媒体描述明确提供的头沿用于重定向、
HLS／DASH 子清单、key 与分片，不按 origin 注册或剥离必需头。
业务与媒体 HTTP 不限定回环监听，不按域名、Host、Origin 或调用方地址限制访问，
既有数据与媒体接口允许外部客户端、浏览器及预检请求；不要求调用方持有启动密钥。
媒体 ID 和路由表只用于定位资源、缓存复用和准确释放，不是 URL 访问许可。
Range／206／416／HEAD、背压与清单子引用由媒体 owner 管；不经过 JSON／Base64，
不整片下载或转码，不迁远端 GoProxy／relay。ClearKey 交内核处理；
携带许可 URL 不会自动发请求，也不代表具备对应 DRM/CDM 能力。

媒体控制文档、重写输出与路由表不设人为字节/条目配额；DASH 保留 XML 深度和格式校验。
预取缓存仍为每 lease 32 MiB／pool 64 MiB；普通二进制尾流不因缓存预算截断。实际预取无法完成时明确报错，不以 URL 或清单成功冒充媒体命中。
前台只复用已完成的缓存，不等待同资源的在途预取；缺失字节直接按前台请求读取。
媒体传输合计最多 64 个活动请求，预取准入为前台保留最后一个额度；额度不足时
预取报 `MEDIA_BUSY`，不排队阻塞前台，也不增加总资源上限。
运行器关闭结果来自真实 drain，失败不返回 `cleanupComplete:true`。
二进制缓存续读优先以强 ETag 作 `If-Range`，否则使用有效 `Last-Modified`；
弱 ETag 不升级为强校验，也不作为缓存206的条件。无可用校验器时仍只读缺失范围，
保留上游响应校验与资源变化错误，不重复下载已缓存前缀。
