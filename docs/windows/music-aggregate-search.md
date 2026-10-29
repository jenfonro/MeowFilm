# 原生聚合搜索契约

## 所有权

- `MusicSearchCoordinator` 拥有一次关键词下的歌曲和歌单搜索会话、平台分页、请求槽、融合、通知、取消和关闭排空。
- `MusicPlatformCatalog` 执行每个平台的原生物理搜索；Coordinator 不持有凭据、平台 HTTP、持久化或脚本能力。
- `MusicLibraryOwner` 是搜索偏好与搜索历史的唯一 writer。
- `MusicNativeApplication` 在每次物理平台请求内取得精确账号 lease，并在认证失效时只撤销该 lease。
- 页面只投影当前 snapshot，不自行启动第二套平台请求、轮询或结果融合。

## 搜索偏好

`music.library.v1` 的 `native-search-policy` 只接受当前 schema：

```json
{
  "schema": 2,
  "revision": 0,
  "songs": {
    "mode": "aggregate",
    "platform": "wy",
    "order": ["wy", "tx", "kw", "kg", "mg"],
    "enabled": ["wy", "tx", "kw", "kg", "mg"]
  },
  "playlists": {
    "mode": "aggregate",
    "platform": "wy",
    "order": ["wy", "tx", "kw", "kg", "mg"],
    "enabled": ["wy", "tx", "kw", "kg", "mg"]
  }
}
```

- 歌曲和歌单分别选择 `aggregate` 或 `single`。
- `order` 必须是五个平台的完整无重复排列。
- `enabled` 必须是按 `order` 排列的无重复子集，可以为空。
- `single` 只派发当前 `platform`；`aggregate` 只派发当前 `enabled`。
- 保存必须提交准确 `ifRevision`，成功后即时生效。设置提交结果未确认时先封闭并排空两类搜索，不允许继续用旧草稿或影子设置。
- `music.search.start` 的平台数组必须与刚读取的当前偏好完全一致，否则返回 `SEARCH_SETTINGS_CHANGED`。

## 会话命令

Coordinator 接受：

```text
music.search.start
music.search.get
music.search.next
music.search.retry
music.search.cancel
```

### `start`

输入为：

```json
{
  "keyword": "关键词",
  "kind": "songs",
  "platforms": ["wy", "tx", "kw", "kg", "mg"]
}
```

- `kind` 只允许 `songs` 或 `playlists`。
- 同一关键词最多保留一个歌曲会话和一个歌单会话。
- 同词、同 kind、同平台集合重复 start 返回现有会话，不重新请求。
- 同词切换另一 kind 只创建缺失的会话；隐藏 kind 不预取。
- 同词改变某一 kind 的平台集合只退休该 kind。
- 新关键词先取消并真实排空旧关键词的两个 kind，再创建新会话；排空期间同一关键词、kind 与平台集合的重复替换等待同一个 replacement，其他替换请求返回 `SEARCH_BUSY`。
- replacement 的调用方各自等待；某个后加入等待者取消不会取消共享替换。最先准入替换的调用方取消仍按该次既有替换契约使其整体返回 `CANCELLED`，不会在旧查询排空后偷偷启动已放弃查询。

### `get`、`next`、`retry`、`cancel`

- `get` 只返回不可变 snapshot，不执行 I/O。
- `next` 只为仍可继续、没有错误且不在加载的平台取下一页。
- `retry` 只重试指定失败平台的同一未提交页，并设置平台 refresh；不重放成功平台。
- `cancel` 先围住会话，再等待该会话的物理请求、融合和必要通知真实结算。
- 取消调用方等待不等于物理工作已经结束；后台工作不能脱离 owner。

## 请求与分页预算

- 歌曲和歌单两个 kind 共享五个物理请求槽。
- 每个平台每页固定请求 30 项，单次请求截止时间 25 秒。
- 每平台最多 100 页；连续四个没有新增身份的页面停止。
- 每平台候选预算为 `min(2000, floor(5000 / 已选平台数))`。
- 失败页不推进页码、continuation、总量或最近页统计。
- 平台返回不合作取消时，`loading` 和请求槽保持到物理任务真实结束。
- 空平台集合是合法的显式选择，返回 `NO_PLATFORMS`，不发出目录请求。

咪咕歌曲搜索可声明 `totalUnit="groups"`：

- 仅 `mg + songs` 允许 group 单位；
- 原生 group 页最多 100 group、1000 个原始版本，每 group 最多 32 个版本；
- group rank、groupCount、rawItemCount、total 和 continuation 必须完整一致；
- copyright ID 只用于跨页录音版本去重，不能替换平台资源 ID；
- 完整展开页超过剩余候选预算时整页拒绝并返回 `SEARCH_LIMIT`，不能裁剪后伪装成功。

## 结果融合

歌曲候选先由 `NativeMusicSong` 规范化。合并要求：

- 精确 `sourceKey` 相同，或当前 fingerprint 相同且双方都有有效时长；
- 已知歌手、标题版本和时长必须兼容；
- 时长容差为最多 3 秒或较短时长的 2%；
- 未知时长不能单独作为跨平台身份依据；
- 同名不同歌手、不同版本、不兼容时长及酷狗不同原生 discriminator 必须分开。

歌单只按 `platform + native id` 分组，同名歌单不合并。

排序使用 Reciprocal Rank Fusion：

```text
score = Σ 1 / (5 + 该平台最佳 rank)
```

同平台多个版本只贡献最佳 rank。代表项、tie 和最终顺序使用稳定当前哈希及身份排序，不依赖平台响应到达顺序或进程随机哈希。

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
totalUnit
total
groupCount
rawItemCount
```

- `revision` 在同一会话内单调增加。
- `partial=true` 表示至少一个平台或融合失败，不代表其他平台结果无效。
- `more` 只表示至少一个无错误平台仍满足当前页数、停滞和候选预算。
- `loading` 包括平台物理请求及融合计算。
- `count` 是该平台累计唯一候选数；groupCount 和 rawItemCount 只描述最近成功页。
- 平台错误保持独立。`AUTH_REQUIRED`、`AUTH_EXPIRED`、`FORBIDDEN`、`TIMEOUT`、`UNSUPPORTED`、`SEARCH_LIMIT` 等不能合并成空结果。

`Changed` 通知按 kind 合并到最新 revision。观察者异常不能破坏 owner；一个观察者同步失效账号或关闭 owner 后，后续观察者不能收到已退休 snapshot。页面必须再按 `id + revision + kind` 拒绝旧 Dispatcher 回调。

## 账号与设置失效

- 账号变化同步使受影响平台在两个 kind 中推进 generation、清空候选和分页统计并标记 `STALE_SESSION`。
- 已在途物理请求仍被追踪；其迟到结果不能提交，显式 retry 只能在物理结束后从第一页开始。
- 账号失效不退休查询 ID，也不删除其他平台结果。
- 搜索设置保存先执行关闭准备并真实排空。成功写入当前偏好后恢复准入，但不自动重放旧查询。
- 关闭准备可取消；恢复后旧查询不会复活。最终 Dispose 排空请求和通知，但不释放借用的 Catalog。

## UI 请求纪律

- 进入搜索只启动当前可见的歌曲或歌单 kind。
- 切换 kind 时优先复用其已有 snapshot；没有会话时才创建该 kind。
- 页面按关键词、kind、当前账号代次和已保存派发平台登记唯一启动 flight，再发布会触发 WPF 绑定回调的状态；SelectionChanged、重复提交或刷新重入只能等待该 flight，不能顺序创建第二个物理 session。
- 原生账号 owner 的首次成功权威观察由 owner 单飞并复用；歌曲／歌单切换及同身份 start 不重复读取整份账号文档。提交未知仍强制重新权威观察，实际平台请求继续取得并核对自己的精确账号 lease。
- 返回搜索页使用内存 snapshot 和各 kind 独立滚动位置，不重新请求。
- “更多”只调用一次 `next`；平台重试只调用一次 `retry`。
- 快速换词、离页、取消、账号变化和设置变化都推进页面 epoch，旧回调不能覆盖新页面。
- 页面不按定时器轮询 `get`，不在卡片布局、动画或滚动每帧触发业务请求。

## 搜索历史

`native-search-history` 的当前 schema 为 1，最多 60 个去重关键词，只保存 `text`、`kind` 和 `platform`。页面可以记录搜索及执行一次带确认的“清空搜索历史”。

搜索历史与活跃搜索会话是不同职责：读取或清空历史不得启动平台请求，取消搜索也不得隐式删除历史。

## 当前格式纪律

- 活动代码只支持当前搜索偏好、历史、平台页和 snapshot 格式。
- 当前格式变化必须同时更新 Library owner、应用准入、Coordinator、平台页解析、UI consumer 和测试夹具。
- 不兼容数据保持原样并明确失败；未经用户授权不得清理或覆盖。
