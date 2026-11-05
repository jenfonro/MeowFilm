# 搜索观察者与关闭生命周期

## 所有权

- `MusicSearchCoordinator` 是歌曲／歌单搜索状态、分页、融合、取消和观察者发布的唯一 owner，也是替换与 `SEARCH_BUSY` 的唯一裁决者。
- 每个查询身份最多有歌曲和歌单两个会话；每个平台各自持有 page、more、generation、flight 和 error。
- presentation 只订阅快照并发送开始、更多、取消和恢复意图，不直接操作平台请求。

## 请求与状态

- 同一关键词、类型、平台和代次只允许一个有效 flight。
- 旧查询排空期间，同身份 replacement 复用一个准入任务；不同身份仍明确 `SEARCH_BUSY`，不能在排空尾部排队形成隐式第二次替换。
- 应用层策略 lane 只串行搜索设置读取、写入和 start 的准入；`get`、`next`、`cancel` 与旧查询排空不经过该 lane。
- 新搜索和刷新退休旧展示并停止未发送分页，不取消已发出的页；账号变化或平台策略关闭才终止受影响的物理 flight。
- 分页只在 `more=true` 且当前轮未加载时进入；每轮页数和每平台并发来自已保存设置，单次平台请求默认 25 秒。累计结果和页数不再套用隐藏额度。
- 融合在独立当前代次中执行；录音身份不兼容时不合并。旧平台回执或旧融合结果不能进入当前查询。

## 观察者发布

- `Changed` 只发布 coordinator 的不可变快照。一次真实状态变化至多形成一次对应 revision；重复取消不能制造新 revision。
- publisher 与请求排空分离：请求物理结束不能代表已进入的观察者回调已经返回。
- 在当前 owner 的 `Changed` 回调栈内触发关闭时，只排除等待自身回调，仍等待请求、融合和资源真实结算。
- `ExecutionContext` 中遗留的旧回调标记在回调退出后失效，不能跨 owner 或跨生命周期豁免关闭屏障。

## 匹配观察

- 内部 `MatchingObserved` 只在歌曲搜索、未取消且当前数据版本已完成融合时发布，快照另带各平台物理检查时间 `checkedAt`；缓存命中沿用原始检查时间。
- 它复用 `Changed` 的同一 notice 与观察者屏障，不新增请求，也不改变公开快照。原生应用据此记录[匹配状态](music-official-selection.md#21-按需匹配与匹配状态)，回调内不做 I/O。

## 取消、准备关闭与恢复

- `StopLocked` 同步禁止新分页并标记 loading 变化；每个查询唯一的 `CancellationDrain` 等待已发出页的正常完成/超时和真实清理。
- 调用方放弃等待不释放物理请求、平台槽位或查询资源。
- presentation 在发布会触发绑定回调的搜索状态前先登记启动 flight；WPF `SelectionChanged` 或同一搜索重复入口只能加入该 flight，不通过 UI 防抖掩盖重复 owner 请求。
- `PrepareShutdownAsync` 停止准入并等待现有查询及观察者屏障。
- `CancelShutdownPreparationAsync` 只在同一 owner 未终结时重新开放准入，不复活旧 query id、旧请求或旧通知。
- `DisposeAsync` 是终态：取消全部查询，等待排空和观察者结束，再释放 semaphore 与 token。

## 错误与副作用

- 取消、超时、协议、格式、账号和平台失败分别记录在对应平台状态；单个平台失败不触发其他平台或整个页面重新加载。
- coordinator 不持久化搜索结果、不调用脚本、不写账号或曲库，也不通过另一平台回退失败请求。匹配状态只在之后的曲库或播放保存点、按本地成员资格落盘。
- 搜索历史和搜索策略属于 `MusicLibraryOwner` 的独立当前设置，不由观察者生命周期写入。
