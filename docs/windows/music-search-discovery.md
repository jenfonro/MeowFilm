# Windows 原生搜索发现

`NativeMusicSearchDiscovery` 是搜索空白页的只读原生 owner。它读取网易云推荐词、热搜和榜单预览，不是逐字输入联想、搜索结果页、账号校验或第二套首页。

展示复用遵循[共同页面契约](../common/pages/data-requests.md)，客户端字段使用[同一 Windows 上下文](music-platform-client-context.md)。

## 数据与入口

`IMusicSearchDiscovery` 只公开：

```text
ReadAsync(refresh)
Rotate(expectedGeneration)
Invalidate(platform?)
CancelAndDrainAsync()
DisposeAsync()
```

一个 `MusicSearchDiscoverySnapshot` 包含：

- 最多 20 个热搜词及可选分数；
- 当前全部去重建议词和本地显示的最多 6 个推荐词；
- 最多 3 个榜单，每榜最多 20 首当前规范歌曲；
- 可隔离的 hot／default／toplist／chart 失败；
- snapshot generation 与抓取时间。

歌曲必须先经过当前 `NativeMusicSong` 校验，不把原始平台响应、Cookie 或 URL 交给页面。

## 请求边界

- 只使用 `MusicPlatformSearchDiscovery` 和固定网易云 HTTP 配方，不启动 Node。
- 一轮最长 25 秒，结果序列化上限 512 KiB；不额外设置物理请求总数或共享并发额度，请求由当前展示流程及实际完成状态管理。
- 首轮并发读取热搜、默认关键词和榜单；随后只为成功榜单读取精确前 20 首。
- 热搜和榜单只解析当前展示的前 20 项和前 3 项；响应仍受实际字节边界约束，不因未展示行的数量拒绝整页。
- 榜单详情请求只要求前 20 行展示成员；优先复用响应已有的合法曲目，批量 song detail 只补当前预览中缺少显示标题的成员。既有完整标题、身份和展示字段不会触发跨平台匹配或音频解析。
- 热搜、榜单、默认词、成员页和缺字段批次分别在读取边界校验内容及身份，成功后才交接该响应的客户端 Cookie；非法内容不更新客户端文档。
- hot 与 toplist 同时失败时整轮失败。单个默认词或单个榜单歌曲失败可记录在 `Failures`，不能把残缺数据伪装成完整成功。
- 合法正文携带已过期的主授权 Cookie 时仍拒绝该响应；整轮或局部失败使用 `AUTH_REQUIRED`，不降级成普通上游错误，也不保存同响应的客户端字段。
- 同一 generation 的非刷新读取共享一个在途请求；最后一个等待者取消时撤销并等待物理请求实际退出。
- `refresh=true` 先退休旧在途请求再建立新 generation，不并行叠加另一轮。

## 会话与缓存

- 发现页可使用匿名网易云或当前网易云账号会话；缓存绑定完整 session scope、generation、userId 和 Cookie 身份，并保存实际读取确认的 post-response 客户端快照引用。
- 读取前、协议结束后和发布前都重新核对会话。
- 客户端快照在发布、缓存复用及本地轮换时核对同一 owner；身份/权威观察变化或未确认回执不能借旧缓存绕过。WY 普通 Cookie 更新在同一观察内合并，不误撤销并行请求；合法响应已推进的快照用于本轮缓存，不重取新代次掩盖真正的旧响应失效。
- 登录、退出、换号或会话失效会调用 `Invalidate("wy")`，同步清除缓存并围住旧请求。
- 只有当前有效 snapshot 可复用。离开页面取消在途请求并等待清理；已完成且仍属当前会话的内存 snapshot 可供同一次页面会话读取。
- `Rotate` 只在现有建议词中本地轮换下一组最多 6 个词，不发网络请求，也不改变账号或搜索历史。

## 页面消费

`MusicCapability` 只在搜索发现页面可见且需要内容时调用读取：

- 首次可见读取一次；布局、模板、集合通知和普通重新绑定不得触发请求。
- 显式刷新保留当前已完成内容直至新结果提交，但同一时刻仍只有一个请求 generation。
- 页面离开、导航、账号变化、关闭和用户取消都会提升页面代次；旧成功和旧失败不能写回当前集合。
- 点击关键词进入当前歌曲／歌单搜索 owner；打开榜单进入当前歌单详情；歌曲播放或追加只调用唯一队列 owner。
- 页面不持有 Cookie、网络 client、曲库 writer 或播放器。

## 当前格式纪律

发现数据只存在内存；不兼容响应明确失败且不写入缓存。
