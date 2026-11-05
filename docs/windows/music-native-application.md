# Windows 原生音乐应用边界

## 作用

`MusicNativeApplication` 是单一 Windows App 内的音乐业务装配和调用边界，不是独立 Music 产品，也不是 Node RPC 包装。

它拥有：

```text
MusicLibraryOwner
MusicLibrarySyncService
MusicAccountOwner
MusicPlatformClientContextOwner
MusicPlatformHttp
MusicPlatformCatalog
MusicNativeCatalogOwner
MusicRecommendationCoordinator
MusicRecommendationQueuePort
MusicScriptOwner
MusicSongMatcher
MusicLyricApplication
MusicFeedbackController
搜索与发现协调器
```

它借用 App 提供的：

```text
IProtectedDocumentStore
IRuntimeClient
播放内核
```

`IRuntimeClient` 只用于确有必要的原始 JS 音源执行；普通账号、平台、搜索、曲库、推荐、同步、歌词编排和设置都在 .NET 内完成。

`InitializeAsync` 先初始化独立本地客户端状态，再恢复同步调度；不因启动而登记平台或协商全部动态密钥。账号、客户端、目录及播放按显式依赖接入，具体边界见[客户端上下文](music-platform-client-context.md)。

## 公开 seam

```csharp
public interface IMusicApplication : IAsyncDisposable, IScriptPlatformCapabilities
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> ExecuteAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default);
    Task PrepareShutdownAsync(CancellationToken cancellationToken = default);
    Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default);
    Task AbortAsync(CancellationToken cancellationToken = default);
}
```

`MusicCapability` 拥有 application；App 仍拥有 store、runtime 和 playback。

## 当前路由

| 方法族 | owner |
| --- | --- |
| `music.accounts.*` | `MusicAccountOwner` |
| `music.accountDetails.*` | 原生账号详情 |
| `music.catalog.*` | `MusicNativeCatalogOwner` |
| `music.search.*` | 原生搜索 |
| `music.discovery.*` | 原生发现 |
| `music.recommendations.*` | 原生推荐协调器 |
| `music.official.*` | 原生官方候选／授权／取链 |
| `music.librarySync.*` | `MusicLibrarySyncService` |
| `music.lyrics.lookup` | `MusicLyricApplication` |
| `music.sources.*`、`music.online.resolve/lyrics/cancel/describe`、`music.transfer.*` | `MusicScriptOwner` |
| 曲库、队列、历史及当前设置 | 同一个 `MusicLibraryOwner`，并经过同一推荐 queue lane |

## 单一队列 lane

所有最终队列写入共享同一个 `MusicRecommendationQueuePort`：

- 普通队列选择、追加、删除、排序、替换、清空和模式变更。
- 历史歌单恢复。
- catalog 歌单 `action=play` 的真实队列替换。
- catalog 歌单 `action=save` 只保存歌单，但仍与同一个 library owner 串行，避免交叉 CAS。

外部调用不能进入 `music.queue.recommendation.*` 私有命令。推荐协调器是该私有事务的唯一调用者。

## 账号代次

- 每个真实平台请求捕获它实际使用的账号 lease。
- 旧请求返回 `AUTH_REQUIRED` 或 `AUTH_EXPIRED` 时只撤销该旧 lease，不撤销并发登录产生的新账号代次。
- 账号未知写入回执会封闭依赖账号的推荐和关闭成功路径。
- 只有显式成功读取当前账号文档，且期间没有更新的未知回执，才解除该 fence。
- 不通过旧 Cookie、其他客户端数据或回退目录恢复账号。
- 客户端未知回执由客户端 owner 单独封闭；应用显式初始化重试或账号 list 路径可进行权威客户端重读，不重放写入或设备登记。单独账号 owner 的 list 不承担这项协调。

## 请求准入

- application 总准入上限为 32。
- 关闭准备开始后新调用返回 `CLOSED`。
- 调用方取消会取消尚未完成的读取和脚本工作；已经进入持久 CAS 的写入必须取得真实结果。
- 页面、账号、筛选和搜索各自由其 owner 管理 generation；旧结果不能覆盖新状态。

## 两阶段关闭

准备顺序以真实依赖为准：

1. 封闭新请求并取消当前 generation。
2. 排空推荐启动读取、选择 callback 和协调器工作。
3. 取消发现、搜索和目录临时状态，并取消排空跨平台匹配请求。
4. 排空曲库同步、反馈和脚本工作。
5. 排空所有已准入 application 请求，再保存一次本地成员的匹配状态（可选派生数据，失败不阻塞关闭）。
6. 准备唯一 `MusicLibraryOwner`，再由客户端 owner 取消并排空共享匿名登记及客户端写入。

准备失败不会释放借用的 App store/runtime/playback。取消关闭会恢复原 owners，但不会自动重放写入、登录、远端同步、脚本调用或播放。

最终 dispose/abort 才释放 application 自己拥有的协调器；客户端 owner 在共享 HTTP 之前释放，App 的 store 在全部能力排空后释放。取消关闭准备使用新生命周期／客户端快照，不复活旧 lease；未知持久写入不会被描述成回滚。

## 数据格式

- 当前 App 保持现有 `aggregate` 数据身份。
- 每个原生 owner 只支持唯一当前格式。
- 不兼容数据保持原样并明确失败。
