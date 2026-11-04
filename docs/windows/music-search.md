# 原生多平台搜索契约

## 所有权

- `MusicSearchCoordinator` 拥有一次关键词下的歌曲和歌单搜索会话、轮次、平台分页、每平台并发、结果缓存、融合、通知、取消和关闭排空。
- `MusicPlatformCatalog` 执行每个平台的原生物理搜索；Coordinator 不持有凭据、平台 HTTP、持久化或脚本能力。
- `MusicLibraryOwner` 是搜索偏好与搜索历史的唯一 writer。
- `MusicNativeApplication` 在每次物理平台请求内取得精确账号 lease，并在认证失效时只撤销该 lease。
- 页面只投影当前 snapshot，不自行启动第二套平台请求、轮询或结果融合。

## 搜索偏好

`music.library.v1` 的 `native-search-policy` 只接受当前 schema（字段见[搜索设置与历史](music-search-preferences.md)）：

```json
{
  "schema": 3,
  "revision": 0,
  "songs": { "order": ["wy", "tx", "kw", "kg", "mg"], "enabled": ["wy", "tx", "kw", "kg", "mg"],
             "pages": 1, "concurrency": 1, "cacheMinutes": 60 },
  "playlists": { "order": ["wy", "tx", "kw", "kg", "mg"], "enabled": ["wy", "tx", "kw", "kg", "mg"],
                 "pages": 1, "concurrency": 1, "cacheMinutes": 60 }
}
```

- 没有搜索模式。每种搜索固定请求 `enabled` 中的平台；`enabled` 按 `order` 排列，顺序即平台优先级。
- 保存必须提交准确 `ifRevision`，成功后即时生效。保存先封闭并排空两类搜索；保存期间进入的 start 等到保存结束后按最新设置核对，写入未确认时返回 `SEARCH_SETTINGS_UNCONFIRMED`，不会派发旧设置。策略读取与 start 准入在同一策略 lane 内完成，旧查询排空本身不阻塞 `get`、`next`、`cancel`。
- `music.search.start` 的平台数组必须与刚读取的当前 `enabled` 完全一致（含顺序），否则返回 `SEARCH_SETTINGS_CHANGED`。应用随后把该类型的 `pages`、`concurrency`、`cacheMinutes` 一并交给 Coordinator。

## 会话命令

Coordinator 接受：

```text
music.search.start
music.search.get
music.search.next
music.search.cancel
```

### `start`

页面输入为 `{ keyword, kind, platforms, refresh? }`；应用补充当前设置后，Coordinator 接收：

```json
{
  "keyword": "关键词",
  "kind": "songs",
  "platforms": ["wy", "tx", "kw", "kg", "mg"],
  "pages": 1,
  "concurrency": 1,
  "cacheMinutes": 60,
  "refresh": false
}
```

- `kind` 只允许 `songs` 或 `playlists`；平台不重复；`pages` 与 `concurrency` 为 `int` 范围内的正整数，`cacheMinutes` 1–1440。
- 同一关键词最多保留一个歌曲会话和一个歌单会话。
- 同词、同 kind、同平台顺序与设置的重复 start 仅在身份仍有效且第一轮仍执行/融合时共享在途会话；账号失效后的重融合不具备复用资格。已结束会话的显式再次提交新建第一轮，按平台页缓存的实际有效期复用或重新请求；复用缓存不延长其有效期。`refresh=true` 总是新建会话并绕过缓存读取。
- 同词切换另一 kind 只创建缺失的会话。
- 同词改变某一 kind 的平台或设置只退休该 kind。
- 新关键词先取消并真实排空旧关键词的两个 kind，再创建新会话；排空期间同一参数的重复替换等待同一个 replacement，其他替换请求返回 `SEARCH_BUSY`。
- replacement 的调用方各自等待；某个后加入等待者取消不会取消共享替换。最先准入替换的调用方取消使其整体返回 `CANCELLED`，不会在旧查询排空后偷偷启动已放弃查询。

### `get`、`next`、`cancel`

- `get` 只返回不可变 snapshot，不执行 I/O。
- `next` 开始下一轮；上一轮及其融合尚未结束时不做任何事。
- 没有单独的平台重试命令：失败平台保留游标，下一轮自动重新请求同一未提交页。
- `cancel` 先围住会话，再等待该会话的物理请求、融合和必要通知真实结算。
- 取消调用方等待不等于物理工作已经结束；后台工作不能脱离 owner。

## 轮次、分页与并发

- 一次搜索提交为第 1 轮，每次下滑加载为下一轮。每轮对每个仍可继续的平台请求 `pages` 页，每轮条数不固定。
- 搜索页的 `more` 需平台声明还有后续；没有显式游标的平台短页即末页，QQ 按平台 `nextpage`，空页始终结束；即使平台 `total` 更大也如此。每轮按未提交前缀按需生成请求，在途不超过 `concurrency`，页码到 `int` 上限即停止。
- “页”由平台适配器 `MusicSearchPaging` 定义：网易云按偏移量取数，一个请求可覆盖多页但最多 100 条；其余平台每页一个请求。当前页长均为 30，咪咕歌曲按分组计页。
- 并发按“类型 × 平台”限制：同一会话中每个平台最多同时 `concurrency` 个请求，歌曲与歌单互不占用；不存在全局请求槽。单次请求截止时间 25 秒，从请求真正发出时开始计算。
- 一个平台在一轮内只按页序提交连续成功的前缀。失败、结果结束或平台把游标移到别处时，该平台本轮停止，未提交的页留给下一轮。
- 搜索不以累计页数、候选数量或连续重复页数截断；每轮只取得当前设置要求的页，按平台返回的 continuation 和 `more` 继续。重复候选不新增行，但有效前进的游标仍可继续。
- 平台返回不合作取消时，`loading` 和该平台的并发名额保持到物理任务真实结束。
- `UNSUPPORTED` 使该平台在本会话中停止；其他错误不停止平台。
- 空平台集合是合法的显式选择，返回 `NO_PLATFORMS`，不发出目录请求。

咪咕歌曲搜索可声明 `totalUnit="groups"`：

- 仅 `mg + songs` 允许 group 单位；
- 原生 group 页按请求页长限制 group 数量；每个 group 的录音版本完整展开，不另设单组或单页版本数量额度；
- group rank、groupCount、rawItemCount、total 和 continuation 必须完整一致；
- copyright ID 只用于跨页录音版本去重，不能替换平台资源 ID；
- 精简后的完整页直接进入融合，不以持久文档或派生 JSON 的大小限制截断搜索页。

## 缓存

- 已解析的平台页按“类型、平台、关键词、起始页、页数、页长”缓存在内存中，时长取该类型的 `cacheMinutes`；已登录平台同样缓存。
- 缓存总量约 32 MiB，按最近使用淘汰；进程退出即清空，不写入存储。
- `refresh=true` 不读取缓存，成功后覆盖。账号变化（`Invalidate`）丢弃对应平台的全部缓存页，包括设置保存或关闭准备暂停准入期间；其他平台缓存不受影响，迟到的旧代次结果不写入缓存。
- Catalog 不再缓存搜索，结果只缓存一次。
- 页面返回和已加载标签切换仅恢复 snapshot 与滚动位置；显式提交始终交由 Coordinator，不在 UI 维护第二份 TTL 或把旧 snapshot 当成永久缓存。
## 结果融合

歌曲候选先由 `NativeMusicSong` 规范化。录音兼容、来源合并与排序由[统一歌曲模型](music-native-library.md#统一歌曲与录音分组)拥有；`MusicRankFusion` 不另外实现时长容差、跨页兼容或冲突身份算法。同一指纹内可以存在多个不兼容的录音子组，新候选必须检查完整证据，寻找可兼容子组，而不是只查询基础 `songId`。

歌单只按 `platform + native id` 分组，同名歌单不合并。

排序使用 Reciprocal Rank Fusion：

```text
score = Σ 1 / (5 + 该平台最佳 rank)
```

同平台多个版本只贡献最佳 rank。平台优先级（设置中的平台顺序）决定合并后的代表项：其显示信息和主录音来自优先级最高的平台；同分时也按代表项的平台优先级排序，再按稳定哈希及身份排序，不依赖平台响应到达顺序或进程随机哈希。

- 第 1 轮：每当一个平台提交新结果，就对全部候选重新融合并发布（热加载）。
- 第 2 轮起：本轮所有平台结束后，把已显示行作为 retained 分组导入同一个融合入口，再逐个处理本轮原始新候选。已显示行固定位置、身份、显示信息和主录音；兼容的新来源加入对应子组，不新增重复行。仅新分组计算本轮 RRF 并追加到末尾，不能先把本轮单独合成代表项再用另一套规则跨页追加。歌单也使用同一入口，按 `platform + native id` 去重。

## Snapshot

当前 snapshot 包含：

```text
ok
id
revision
keyword
kind
items
more
loading
partial
code
cancelled
round
platforms[]
```

每个平台状态包含：

```text
platform
page
count
more
loading
error
```

- `revision` 在同一会话内单调增加。
- `partial=true` 表示至少一个平台或融合失败，不代表其他平台结果无效。
- `round` 是已开始的轮次数。
- `more` 表示至少一个平台仍有后续页；可重试失败的平台同样计入，因为下一轮会重试它。
- `loading` 包括未结束的轮次、平台物理请求及融合计算；已取消会话只在物理工作排空前为 true。
- `page` 是该平台最后提交的原生页，`count` 是累计唯一候选数。
- 平台错误保持独立。`AUTH_REQUIRED`、`AUTH_EXPIRED`、`FORBIDDEN`、`TIMEOUT`、`UNSUPPORTED` 等不能合并成空结果。

`Changed` 通知按 kind 合并到最新 revision。观察者异常不能破坏 owner；一个观察者同步失效账号或关闭 owner 后，后续观察者不能收到已退休 snapshot。页面必须再按 `id + revision + kind` 拒绝旧 Dispatcher 回调。

## 账号与设置失效

- 账号变化同步使受影响平台在两个 kind 中推进 generation、清空候选与游标、丢弃该平台缓存并标记 `STALE_SESSION`；其余平台候选重新完整融合。
- 已在途物理请求仍被追踪；其迟到结果不能提交。下一轮在物理结束后从第一页重新请求该平台。
- 账号失效不退休查询 ID，也不删除其他平台结果，但撤销其“有效首次在途搜索”资格；此时同词显式提交须排空旧查询并请求新身份，不能共享清空后的重融合。
- 搜索设置保存先执行关闭准备并真实排空。成功写入当前偏好后恢复准入，但不自动重放旧查询。
- 关闭准备可取消；恢复后旧查询不会复活。最终 Dispose 排空请求和通知，但不释放借用的 Catalog。

## UI 请求纪律

- 搜索结果默认进入“综合”；综合中的歌曲和歌单栏目都属于当前可见内容，因此各创建一个按轮次请求的会话。
- 综合页在等待首个 start 结算前即登记两类唯一启动 flight；旧关键词排空仍可约束实际准入顺序，但歌单搜索不依赖标签切换才排队。
- 切换到“单曲”或“歌单”时优先复用对应 snapshot；直接进入缺失的单类结果时才创建该 kind。
- “综合”只截取两个 snapshot 的首批结果做栏目投影，“查看更多”切换到对应单类视图，不另发请求。
- 两个栏目分别按自己的启动 flight 与 snapshot `loading` 呈现加载状态；一类结算不隐藏另一类的搜索状态。
- 页面按关键词、kind、当前账号代次和已保存派发平台登记唯一启动 flight，再发布会触发 WPF 绑定回调的状态；SelectionChanged、重复提交或刷新重入只能等待该 flight，不能顺序创建第二个物理 session。
- 原生账号 owner 的首次成功权威观察由 owner 单飞并复用；歌曲／歌单切换及同身份 start 不重复读取整份账号文档。提交未知仍强制重新权威观察，实际平台请求继续取得并核对自己的精确账号 lease。
- 返回搜索页使用内存 snapshot 和各 kind 独立滚动位置，不重新请求。
- “更多”（单曲、歌单视图滚动到底）只调用一次 `next`；失败平台由下一轮自动重试，页面没有单独的平台重试入口。
- 快速换词、离页、取消、账号变化和设置变化都推进页面 epoch，旧回调不能覆盖新页面。
- 页面不按定时器轮询 `get`，不在卡片布局、动画或滚动每帧触发业务请求。
- 页面投影按快照内容增量更新：同一 items 不重复解析卡片或触碰 `Songs`；已显示歌曲行按 `SelectionKey` 保持前缀时原位合并并追加，只有重排才重置集合。加载状态只更新绑定，滚动位置仅在整表重置、切换标签或返回搜索页时捕获和恢复。
- 歌单搜索结果由独立虚拟化列表按列分行展示：每行仍由宽 168 的卡片容器组成，卡片模板内的右距 16 已包含在 168 内，行底距 24；列数按列表实际可用宽除以 168 计算，列数变化才重分行，后续轮次保留已有行对象和滚动位置。离开歌单标签时保留列表、行源、已实现卡片与图像，Collapsed 使其失活；返回继续使用同一容器和实际滚动位置。新关键词或搜索退役释放旧投影；同词刷新保留已有内容直至新结果到达。触底加载只接受向下滚轮、键盘或滚动条意图，布局、标签切换或数据追加不自动请求。

## 搜索历史

`native-search-history` 的当前 schema 为 2，最多 60 个去重关键词，只保存 `text` 和 `kind`。页面可以记录搜索及执行一次带确认的“清空搜索历史”。

搜索历史与活跃搜索会话是不同职责：读取或清空历史不得启动平台请求，取消搜索也不得隐式删除历史。

## 当前格式纪律

- 活动代码只支持当前搜索偏好、历史、平台页和 snapshot 格式。
- 当前格式变化必须同时更新 Library owner、应用准入、Coordinator、平台页解析、UI consumer 和测试夹具。
- 不兼容数据保持原样并明确失败；未经用户授权不得清理或覆盖。

### 页面保留与恢复

推荐、频道和当前搜索综合使用独立的分区集合与滚动宿主；当前搜索歌曲有独立虚拟化表格，歌单使用原虚拟化行列表。缓存恢复不清空再填充分区，保留分区/控件引用；真实数据变化才更新相应分区。其他详情沿现有有界领域缓存恢复，不为每条导航历史建立可视树。

Shell 成功执行后调用 `MusicCapability.CaptureNavigationLocation()` 保存领域位置，用户输入及草稿仍独立保存。位置分别为 `search-local/` 加编码后的本地关键词、`catalog-search/` 加编码后的在线关键词、普通页面 route。`RestoreNavigationAsync(location, token)` 解码并验证关键词；本地通过已保存的 Location 恢复分页、选择及滚动，不发平台搜索。在线当前关键词、账号 epoch 和 session 有效时恢复已选标签及滚动，不提交 `SearchAsync`；已退役关键词由领域启动流程重新加载，不承诺永久保存历史结果。同词本地/在线位置不会冲突，关键词中的斜杠、百分号和路由前缀按数据编码。

频道首次入口由目录 owner 按实际展示的直接标签、更多标签顺序选择第一个可打开项，在同一加载代次内直接读取频道，不向 Shell 发起目录后的补跳。标签展示沿用既有分组排序。再次进入恢复最后选择；目录或默认频道失败保持错误，显式刷新才重试。目录空结果正常结束。

首次无数据加载使用 `MeowFilm.Desktop.LoadingSkeleton` 组成无图片卡片/行占位。已有内容刷新、分页不被整页骨架遮盖，空结果或错误停止。MusicView 按 `IsLoaded && IsVisible` 激活 Discovery/VIP；CatalogView 隐藏时关闭更多频道弹层、停止揭示动画并清除滚动意图。布局和隐藏列表不触发自动翻页。账号失效清理所有保留音乐分区及当前搜索投影，恢复仍执行领域有效性校验。
