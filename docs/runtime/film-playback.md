# 本地影视会话 `film.playback.v1`

## 所有权与装配

`packages/film-playback` 是 App 本地的影视业务 owner。它借用已经装配的
CatNode／source／pan／TMDB／豆瓣模块，负责匹配、候选排序、会话、数据缓存、
历史、人工匹配、错误片源屏蔽和偏好。业务不连接 meowfilm-server 的登录、
数据库或播放 API。收藏与识别规则仍由现有客户端 owner 保存，不建立第二份资料。

双端 aggregate entry 显式装配七个模块；通用 `createRuntime` 未显式启用
pan／metadata／film 时保持原默认三个模块。`connectFilmPlayback` 在 provider
变更前后比较真实 generation／revision，再通知数据、会话及媒体 owner 撤权。
失败回执不证明磁盘变更回滚；仅展示名称变化不使有效媒体失效。

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
| `session.open` | `{platform,target,sources,query?,results?,resultHints?,search?,metadata?,cacheEntries?,rules?}` → 当前 snapshot |
| `session.command` | `{sessionId,action,...}` → 受理后的 snapshot，后续由事件推进 |
| `session.snapshot` | `{sessionId}` → 当前 snapshot |
| `session.attempt` | `{sessionId,attemptId}` → 私有媒体 receipt |
| `session.media.release` | `{sessionId,attemptId,resourceId}` → `{released:true}`，重复释放幂等 |
| `session.release` | `{sessionId,keepMedia?}` → `{released:true}` 或 `{retained:true}` |
| `settings.get/set` | 读取／原子保存完整偏好 |
| `library.get` | 读取本地历史、人工匹配、屏蔽、点映选择 |
| `library.remove` | `{kind:'history'\|'blocks',id?,block?}`，省略或 null 清空对应集合，否则准确删除 |
| `manual.update` | `{action:'save',item}` 或 `{action:'remove',id}` |
| `data.search` | `{source,query,page?}` → 完整单页、页号和 `hasMore` |
| `data.detail` | `{source,detailId}` → 所选来源的真实线路／文件 |
| `data.metadata` | `{provider,path,query?,imageSize?}` → 原 provider 数据 |
| `data.cache.entry` | 缓存请求身份 → 带原始时间、完整度的有效记录，未命中为 null |
| `data.invalidate` | `{domain}` → 撤指定来源、账号或 metadata 域并等待排空 |

### 身份与缓存交接

target 包含 `id,kind:'movie'|'tv'|'source'|'group',title`，可有 `year,metadataProvider,
metadataId,season,episode,globalEpisode`。原始单源携带
`source:{key,detailId,lineId?,episodeId?,playId?,candidateId?}`，不按同名扩搜。
已选文件的 `candidateId` 是稳定 opaque 身份，可随快照保留；重开时必须与新详情
重新生成的候选准确匹配，不按名称或数组位置猜文件，也不把私有 playId 放进快照。content id
与当前显示的 metadata provider 身份分离；切换可靠资料关联不改内容 id。
没有可靠元数据身份的聚合使用 `group`，携带
`members:[{sourceKey,detailId}]`，保留全部准确成员及原查询来源范围；
其 `id` 使用共享搜索的稳定 displayKey，不包含临时搜索 session ID，
使同一内容重新搜索后仍能读取原历史、人工匹配和屏蔽。
不伪造 movie／tv，也不降成代表单源。未关联时使用真实源目录与上下集，
iOS 不自动准备，Windows 仍沿正式自动播放入口。

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
full。一个 32 MiB 有界缓存拥有数据、完整度、过期时间和 in-flight 合并；超预算
结果完整返回但不保留，不截断业务页。站点数据、盘列表、stored、短效 URL、
metadata 分别按准确来源／账号／模式／配置隔离；资料持久库不随缓存淘汰。

搜索与播放均使用 `film.data.search/metadata`。TMDB 搜索请求为
`/3/search/multi`、`{query,page:1,language:'zh-CN'}`、`imageSize:'w500'`；
完整详情为 `/3/{movie|tv}/{id}`、
`{language:'zh-CN',append_to_response:'credits,similar,images'}`、
`imageSize:'original'`。查询字段和图片尺寸属于缓存身份，不从展示快照伪造完整记录。
原生不再拥有独立的成功详情缓存或跨请求合并；当前页面仍可持有展示快照。

来源结果、完成页与来源缓存的交接还须证明来自同一个运行器 owner；Cat 的
generation 数值可能在新进程重新从 1 开始，不是跨进程授权证明。Windows 用仅在进程内
保留的 `DataOwner` 绑定原数据 producer，导航持久化不序列化它。换 owner 或缺少证明时，
原结果通过 `resultHints:[{sourceKey,items}]` 保留准确详情指针与原备注，按需读取新 owner
的 detail，并以真实返回记录重新匹配目标后才生成候选；不预抓全部详情。
旧搜索页完成状态和来源缓存不跨 owner；metadata 记录仍按原配置 revision、时间和完整度验证。
显式重连通过既有来源 owner 恢复 App runtime，并只使用原来源范围与当前 ready 来源的交集；
当前季集及原始单源选择不退回首集，不扩大为全站。普通 SSE 重连仍只取同一会话快照。
重开交集按 engine／installation／site 等授权身份判定，名称变化不撤销来源。
当前 generation／revision 用于新请求，原 producer 证据及旧数据代次不改；
group 仅移除已经不可授权的成员，仍可使用原范围内其余有效来源，
没有有效成员时明确失败，不追加新站点或降成代表 raw。

### 晚到搜索交接

`search.update` 命令携带
`{selectionId,revision,sources,results,resultHints?,search,cacheEntries?,metadataTarget?,metadata?}`。
selectionId 是初始 target.id，revision 为该原生搜索 context 的单调发布代次；
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

- `prepare` 只取地址；`play` 请求正式播放，复用有效已准备地址。Windows 进页
  自动触发正式链；iOS 默认自动准备，允许关闭。初次 metadata 卡片没有选集坐标时，
  iOS 取首个真实正片集，不以历史集数或进度替代；Windows 优先有效历史，否则取首集。
  明确带有季集的重进保留选择（包括季 0），Windows 仅同集历史可承接播放位置，
  不套用别集进度。电影不造集号。iOS 准备不建 decoder、不抢音频 owner、不记观看。
- 默认阶段：可用候选 → 历史 → 人工匹配 → 已得结果与增量搜索。地址进入 pending
  就停止新调度；真正媒体失败才续选。候选／原始文件身份防止重复选同一失败资源。
- `search.more` 只加载尚未完成来源的下一页；菜单打开不产生请求。
  `browse {sourceId,lineId?}` 只读所选 detail／必要分享列表，`sourceId:null` 返回
  纯 metadata。完整线路不重复请求盘列表。
- `browse.metadata {metadataSourceId,keepSource?}` 只能选择已证明关联；
  `keepSource:true` 保留当前站点／线路，只切分季资料。关联查询不阻塞主资料匹配。
  `browse.season {season}` 按需真实分季；映射无证据时不造季或集。
- `select.episode {episode}` 选择真实 metadata 坐标；
  `select.candidate {candidateId}` 选择准确源文件。`next/previous` 使用真实列表。
  同集换源保进度和暂停意图，新集从正确起点，替代失败保留可用旧流。
  旧流结束只负责其历史与释放，不能覆盖后发的选集／换源意图。
  Windows 原始来源续播按已保存的稳定候选身份恢复文件；文件已不存在时不把旧进度套给其他文件。
- `pan {provider}` 固定标准盘族；`quality {quality}` 固定质量并保持另一维偏好。
  `switch` 临时排除准确当前文件；`block {attemptId}` 持久保存目标＋站点详情＋线路后续选，
  电影同样适用。确认框捕获打开时的 current attempt（无 current 时才取 pending）；
  确认期间已换成其他 attempt 则返回 `STALE_SESSION`，不屏蔽未经确认的新片源。
  写失败不得提前改变匹配结果。
  source／group 已选文件在这些显式操作或真实媒体失败后，只有已证明实际同集坐标时
  才临时解开文件／线路锁。raw 仍限原 source／detail，group 仍限原范围；
  无可靠映射则 `NO_MATCH`，不能拿另一集补位。普通重开始终要求保存的准确
  candidateId；替代失败保旧输出与恢复目标，成功后才保存新文件身份。
- `preview {enabled}` 只切未播集显示／选择范围并按内容持久；不凭目录最大集号
  推已播。`shortvideo {enabled}` 准备已知相邻地址，预取下一集真实有限媒体字节。
  提升预取项为正式播放复用同 lease，只取消预取任务、不撤正式媒体。
- 人工匹配连续三次 bootstrap detail/list 请求失败可自动停用；取消与解码失败不计。
  修改引用／季线索或停用在保存成功后撤销旧关联、prepared 与邻集预取；旧在途
  不能覆盖新记录的失败状态。已知人工分享列表同样参与相邻取址／媒体缓存。
  设置包含三维规则顺序、盘序、关键词序、净化关键词及 `iosAutoPrepare`；
  净化限制智能匹配与播放页加载更多，不禁普通搜索、不删除来源。
  来源改名和规则修改都重核 prepared 与发现范围，不强停此前已开始的 current 媒体。

## 观察与媒体反馈

GET `/v1/film/events/<sessionId>?after=<sequence>` 返回：

```text
id: sequence
event: snapshot
data: JSON
```

只保留当前状态并随背压合并，不积累无限事件历史。重新连接先交当前快照；
断开观察不关闭业务会话，也不停止实际媒体。snapshot 含 target、stage、metadata、
metadataSources、sources、search、browsing、episodes、candidates、attempt、current、
preferences、error、reportError 和 prefetch；不含 URL、头、分享密码、playId 或 DRM key。

`browsing.episodes` 是第三行：具体站源按可靠季映射过滤，失败时为空并在第三行显示
错误，来源菜单仍可切换。`browsing.originalEpisodes` 保留完整所选线路供原始目录，
目录浏览不转存、不自动播放。纯 metadata 的 `episodes` 仅包含真实资料。
电影候选按质量排序；原生提供固定宽度、截断长文案。

`attempt` 只是媒体意图；私有 `session.attempt` receipt 含
`attemptId,id,url,mediaOrigin?,format?,drm?,title,position,paused,scope`。
只有可信 native owner 消费，不记录日志或放系统 NowPlaying。

原生在真实输出后发送 `media.started {attemptId}`；`media.failed` 统一续选；
`media.progress {attemptId,position,duration,paused}` 为秒；`media.ended` 触发真实
下一集或 ended。历史只在真实播放后写入，进度节流为 12 秒，暂停、停止、结束强制。
观看上报仅在 play 明确返回 `watchReport:true` 时调用正式 report action，准确保留
原 flag/id，拒绝任意回调 URL。错误单独显示在 `reportError`。

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
二进制流。Cat 自有媒体通过同一入口衔接实例生命周期；准备不读取媒体，
也不会因选择本地路由自动启用预取。同一媒体描述明确提供的头沿用于重定向、
HLS／DASH 子清单、key 与分片，不按 origin 注册或剥离必需头。
业务与媒体 HTTP 不限定回环监听，不按域名、Host、Origin 或调用方地址限制访问，
既有数据与媒体接口允许外部客户端、浏览器及预检请求；不要求调用方持有启动密钥。
媒体 ID 和路由表只用于定位资源、缓存复用和准确释放，不是 URL 访问许可。
Range／206／416／HEAD、背压与清单子引用由媒体 owner 管；不经过 JSON／Base64，
不整片下载或转码，不迁远端 GoProxy／relay。ClearKey 交内核处理；
携带许可 URL 不会自动发请求，也不代表具备对应 DRM/CDM 能力。

媒体控制文档、路由表及预取有独立资源预算：registry 16 MiB，控制输入 8 MiB、
重写输出 32 MiB／累计 64 MiB，预取每 lease 32 MiB／pool 64 MiB；普通二进制尾流
不因这些预算截断。实际预取无法完成时明确报错，不以 URL 或清单成功冒充媒体命中。
运行器关闭结果来自真实 drain，失败不返回 `cleanupComplete:true`。
二进制缓存续读优先以强 ETag 作 `If-Range`，否则使用有效 `Last-Modified`；
弱 ETag 不升级为强校验，也不作为缓存206的条件。无可用校验器时仍只读缺失范围，
保留上游响应校验与资源变化错误，不重复下载已缓存前缀。
