# 共同音乐平台运行器 music.platform.v1

共同业务规则唯一来源为[平台请求与上下文](../common/music/platform-client-context.md)。本文件说明当前 Node 装配和公共 API；源码接线不代表测试、构建或真实上游已通过。

## 装配与方法

`apps/runtime/modules/music-platform.mjs` 薄适配 `@meowfilm/music-platform` 的 `createMusicPlatform`。descriptor 为 `music-platform@0.1.0`／`music.platform.v1`，方法如下：

| 方法 | 当前输入及返回 |
| --- | --- |
| `music.platform.request` | `{platform,operation,params,candidate?,expectedSession?}`；返回本次操作的规范结果 |
| `music.platform.session.update` | `{platform,session}`，session 为 null 或当前 `{scope,generation,userId,cookie}`；等待实际失效工作结算后返回 `{published:true}` |
| `music.platform.cache.clear` | 可选 `{platform}`；清对应 API 结果缓存，返回 `{cleared:true}` |
| `music.platform.describe` | 返回 `{execution:"node",contract:"music.platform.v1",platforms:["wy","tx","kg","kw","mg"]}` |

以上返回沿现有 HTTP module envelope `{ok,contract,value}`；失败沿现有有限 code。`candidate` 只用于候选账号认证交接，session 材料不进入普通展示 DTO 或日志。operation 以 providers、accounts 和 mutations 的实际公开派发为准，不另建第二份配方表。

首页／频道等浏览操作由五平台 provider 直接输出[固定内容协议](../common/music/platform-client-context.md#固定内容协议)的规范 `sections` 与真实 `continuation`；`content.mjs` 为纯结果构造辅助（`browsePage`／`browseSection`／`browseCard`／`songCard`／`channelDescriptor`），非后置翻译垫片，且已移除无消费的 `browseCard.recommendationReason` 与 `previewSongs`。顶部入口是最终 `layout:"feature-rail"` 栏目，不额外返回 `entries` 或要求客户端拼固定列表。网易首页入口及 Cube 推荐组的 `requiresAccount/display/available` 取自当前已发布 session；显示声明不替代动作自身的账号准入。

`operation:"playlists", params:{ids:[...]}` 是客户端显式请求的网易集合摘要，内部按上游每批 100 个 ID 执行，返回规范 Collection 列表。首页／频道提取不主动调用该配方补图或标题；适配器记录的结构统计仅以可选 `diagnostics` 返回，不阻断内容。

FM 封面需求使用既有 `operation:"fm", params:{preview:true,...}`：规则 FM 的 `ruleId/tabTagId` 选择封面配方，普通 FM 选择已有单曲预览配方。运行器不在首页／频道中触发它；客户端仅消费返回的 `cover`，按需与生命周期见[共同展示资源](../common/music/presentation-resources.md#缺少图片引用的按需读取)。规则 FM（`/api/tag/tab/fm/recommends`）与稀疏 radio/similar 返回仅含歌曲定位 ID 时，运行器统一按详情配方补齐歌曲资料，保留外层 `alg/source/sourceId`，未返回详情的无效 ID 不伪造为空歌曲。FM 首次进入与预览（`continuation:false` 且无 `unplaySongIds`）及首页、频道目录、频道页、榜单在当前会话作用域下缓存 1 小时（`3600000` ms，降级错误页不缓存）；FM 续播批次（`continuation:true` 或带 `unplaySongIds`）绕过缓存实时获取随机歌曲。

`operation:"lyrics"` 保持 `{text,translation,romanization}`；QQ／咪咕附属轨失败时额外返回 `errors`，以 `translation`／`romanization` 为键、有限错误码为值。失败轨为空字符串，健康主轨及兄弟轨继续返回；没有任何可用轨的解析失败仍抛错。取消、超时、会话失效及资源超限不降级为轨道错误。原生可继续消费原三个字段，不能把 `errors` 中的附属失败解释成主轨失败。

网易 `playlist` 先选择当前成员再消费详情；`tracks` 中只有定位字段、没有歌曲字段的成员引用不作为已取得的歌曲记录，仍按选中成员执行既有每批 100 个 ID 的详情读取。已包含歌曲字段的稀疏记录保持上游实际值，不因歌手、专辑、封面或时长等可选字段为空补读；详情未返回的成员不伪造为空歌曲。`memberIds`／`consumed` 保持真实成员顺序和重复 occurrence；`sourcePositions` 只对已关联成员给出位置，canonical 别名返回的未知关联仍为 null。`playlist.complete` 消费全集，不借分页清理省略完整成员。

两端 aggregate 按 `music, cat, source, pan, tmdb, douban, film, netease, music-platform` 顺序装配九模块。`connectMusicPlatform` 从已装配 netease 的真实 `netease.token.get` 能力借用 token；不创建第二个 SDK owner。通用 `createRuntime` 默认仍三模块，仅显式 `musicPlatform` 选项连接此模块。

## 数据与生命周期

匿名协议状态保存在同一 App 数据根的 `music_anonymous_*` SQLite 类型表；原生凭据安全存储、永久歌曲仓库及图片字节不在此处。读取快照不预检未选平台：所选身份在实际消费前检查，缺失／损坏报 `CORRUPT_STORAGE`，不重新生成安装身份。普通扩展不阻断请求，写入只投影匿名协议字段；网易账号凭据不进入匿名 Cookie 表。CAS、未知提交回执后的重读与关闭排空保持原职责。包复用 Node 内置能力与公共 JSON 模块，没有新增第三方 npm 运行依赖。

QQ 匿名 prepare 只保存实际请求使用的 q36 与保存时间，不要求或保存 q16；UID／SID、时效、保存确认和关闭排空保持原职责。

网易 VIP 页并发读取推荐与榜单，并等待两支全部结算后发布。任一分支的认证、权限、限流、验证或生命周期错误仍使请求失败；可隔离的内容读取错误以对应 section 的 `errorCode` 和空 `items` 返回，健康栏保持可用，两支均失败则报告错误，不发布成功空页。

API 缓存复制使用公共 `@meowfilm/runtime-bridge/json`；HTTP 请求中的精确数字定位在音乐实际参数消费处转为字符串或协议整数，不对 rawJSON 使用 `structuredClone`。网易根业务状态仍要求数字整数；配方显式消费 `data` 状态层时检查该层，搜索等读取 `result` 的配方不检查未消费兄弟 `data.code`。已有根／嵌套字段选择的配方跟随实际使用层：仅使用根结果时忽略兄弟状态，使用嵌套字段或没有根结果时保留嵌套失败。真实认证、限流、业务拒绝及写入子回执继续按原错误分类返回，不能用空内容代替失败。

request 将 HTTP `context.signal` 交给 owner。普通目录读取退订与物理工作分别跟踪；媒体／认证等实际取消、session 失效、已派发写回执和 close 由共同 owner 管理。session 更新等待应退休工作物理结算；整个 owner 的 close 停止新准入并取消所有实际 transport，包括已向上游派发的写请求。派发不等于确认成功，这类中断保留 `unknown` 结果，不谎报回滚；close 等待实际 I/O 结束，不等待远端 ACK 或普通请求超时。原生设置未知提示见[设置回执](../common/music/settings-receipts.md)。

## 打包与许可

公共打包器从实际静态导入和根 workspace lock 解析 `@meowfilm/music-platform`，复制 `src` 和薄模块；两端 wrapper 仅加入自己的启动文件。中央音乐许可和原始 notices 随公共载荷保存，实际文件及哈希进入 manifest；iOS 另携带现有 Node 兼容许可，Windows App 中央许可清单保持原安全复制门禁。来源路径见 `LICENSES/NOTICE.md` 和[许可说明](../licenses.md)。

直接覆盖入口包括 `runtime-assembly`、`runtime-package`、`runtime-engine-package`、两端 runtime／runtime-engine-package 及 iOS prepare fixture。新增九模块、共享 owner 失败关闭和许可闭包断言仍须在指定机器执行，不能以本说明作为通过证据。

普通 HTTP 响应、歌词解压、歌单同步和播放反馈上传不设人为字节配额；API 缓存采用 LRU（128 项 / 4 MiB 上限），按平台与当前会话代次隔离并在 `session.update` 时失效。网易 NCBL 播放日志按 Zstandard 128 KiB raw block 和 NCBL 32 KiB frame 分块，保留 UInt16/UInt32 字段的真实可表达范围，不以旧单条/总日志字节门槛截断业务内容。
