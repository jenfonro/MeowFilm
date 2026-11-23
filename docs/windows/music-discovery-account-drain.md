# 搜索发现账号读取结算契约

## 目的

搜索发现读取需要账号状态，但取消返回不能把仍在访问受保护文档的物理操作伪装为已结束。该职责由同一个 `MusicAccountOwner` 提供，不建立第二个账号 owner 或缓存。

## 当前接口

```csharp
internal Task<JsonElement> RestoreForDiscoveryAsync(
    CancellationToken cancellationToken = default);

internal ValueTask<MusicPlatformSession?> AcquireSettledAsync(
    string platform,
    CancellationToken cancellationToken = default);
```

- `RestoreForDiscoveryAsync` 返回当前账号列表投影。
- `AcquireSettledAsync` 返回当前平台会话或 `null`。
- 两者复用 `music.accounts.v1`、磁盘 gate、generation、lease table 和同步 `SessionChanged` 通知。

## 结算语义

- 若本次物理读取已经开始，调用取消后仍等待该读取以及由它触发的同步通知完成，然后才返回 `CANCELLED`／抛出对应取消。
- 若请求尚在 gate 外排队，取消可撤销自己的排队，不等待占槽的无关操作。
- 只等待本次读取及其因果通知，不等待全局 owner 空闲，也不重发列表请求制造 barrier。
- 新读取不能越过仍未结算的同 owner 存储操作；旧会话必须在返回前再次通过 `IsCurrent` 核对。
- 存储、格式和账号错误保持原 code；不转成匿名账号、不使用内存状态兜底，也不写回文档。

## 消费者边界

- 搜索发现 hydrate 使用 `RestoreForDiscoveryAsync`。
- 发现专用平台会话适配使用 `AcquireSettledAsync`；普通账号页面和其他能力按各自已定义的读取语义调用同一 owner。
- 账号变化会使发现缓存和请求代次失效。旧读取完成后不能恢复已退休的发现状态。
- 页面取消、账号取消和 App 关闭分别拥有明确 token；任何一层都不能把调用方超时当作物理读取已经停止。
