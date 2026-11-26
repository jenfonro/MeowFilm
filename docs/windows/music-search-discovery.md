# Windows 搜索发现

`NativeMusicSearchDiscovery` 是搜索空白页的只读原生 owner，不是逐字输入联想、账号校验或第二套首页。展示生命周期见[共同页面契约](../common/pages/data-requests.md)，共同配方与字段消费见[平台请求](../common/music/platform-client-context.md)。

## 当前入口与数据

`NativeMusicSearchDiscovery` 提供 `ReadAsync(refresh)`、`Rotate(expectedGeneration)`、`Invalidate(platform?)`、`CancelAndDrainAsync()` 和 `DisposeAsync()`。

`MusicSearchDiscoverySnapshot` 保存 hot、suggestions、recommended、charts、failures、generation 和抓取时间。`NativeMusicSearchDiscovery` 经同一 `MusicPlatformClient` 调 `wy/searchDiscovery`，随后采用已取得歌曲到 `MusicLibraryOwner`，再映射规范结果；原生不直接解析上游私有 envelope、验证成员身份或交接响应 Cookie。

## 读取和缓存

- 一次 native flight 沿现有 25 秒 deadline，平台 HTTP 配方和字节预算由 Node 实际传输执行；不以热词、榜单或歌曲条数证明完整性。
- 同代次读取共享在途任务；`refresh=true` 跳过已完成展示缓存，但加入仍有效的 flight，不另派重复读取。
- 等待者取消只退订；即使最后一个离开，owner 仍跟踪物理工作。账号失效和关闭才取消并等待该任务结算，再允许新代次。
- 缓存仅在当前进程保存，关联实际账号 lease。`Invalidate("wy")` 推进 generation、清展示缓存并退休旧读取；不核对原生客户端文档或 post-response 快照。
- `Rotate` 只在已有建议词中本地轮换最多六个词，不发请求、不改账号或搜索历史。
- Node 的局部 failures 直接映射到目标展示；普通内容变化不被原生重新归类为认证或快照错误。

## 页面消费与关闭

`MusicCapability` 只在搜索发现页可见且有真实内容需求时读取；布局、重绑和集合通知不触发请求。显式刷新保留已完成展示，旧页面／账号代次的成功和失败不写回当前集合。

点击关键词交当前搜索 owner，榜单交现有目录详情，播放／追加交唯一队列 owner。页面不持有凭据、平台 HTTP 或曲库 writer。关闭等待 `CancelAndDrainAsync` 的真实物理结算；重复 dispose 返回共同关闭任务。

本轮仅核对静态接线，未执行运行器、原生或上游验证。
