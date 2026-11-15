# Windows 原生音乐应用边界

## 作用

`MusicNativeApplication` 是单一 Windows App 内的音乐业务装配和调用边界，不是独立 Music 产品，也不是 Node RPC 包装。

它拥有：

```text
MusicLibraryOwner
MusicLibrarySyncService
MusicAccountOwner
MusicPlatformClient
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
IDocumentStore
CacheDocumentStore
IRuntimeClient
播放内核
```

`IRuntimeClient` 同时用于原始 JS 音源和共同 `music-platform` 模块。平台请求、认证、私有响应转换及匿名协议状态归 Node；曲库、永久身份、引用队列、设置、账号安全保存和原生播放归 .NET owner。共同边界见[平台请求](../common/music/platform-client-context.md)。

`InitializeAsync` 依次初始化反馈本地状态、恢复歌曲资料并启动同步调度；不初始化原生设备文档或主动登记全部平台。单个 `MusicPlatformClient` 在构造时绑定账号 sessions，具体边界见[平台客户端](music-platform-client-context.md)。

## 公开 seam

```csharp
public interface IMusicApplication : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> ExecuteAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default);
    Task<JsonElement> ImportSourceAsync(ReadOnlyMemory<byte> source, string basename,
        string? installationId = null, CancellationToken cancellationToken = default);
    Task PrepareShutdownAsync(CancellationToken cancellationToken = default);
    Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default);
    Task AbortAsync(CancellationToken cancellationToken = default);
}
```

`MusicCapability` 拥有 application；App 仍拥有业务 store、独立缓存 store、runtime 和 playback。缓存 store 惰性创建，歌词展示、账号展示、来源候选、自动匹配、首页与音频索引不与业务文档共用根锁或写入队列；成功业务结果只发布缓存内存快照，异步落盘由缓存 owner 排队并在关闭时实际排空。

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
| `music.sources.*`、`music.online.resolve/lyrics/cancel/describe`、完整字节 `ImportSourceAsync` | `MusicScriptOwner` |
| 曲库、队列、历史及当前设置 | 同一个 `MusicLibraryOwner`，并经过同一推荐 queue lane |

## 单一队列 lane

所有最终队列写入共享同一个 `MusicRecommendationQueuePort`：

- 普通队列选择、追加、删除、排序、替换、清空和模式变更。
- 历史歌单恢复。
- catalog 歌单 `action=play` 的真实队列替换。
- 网络歌单收藏使用 `music.catalog.playlist.save`，由同一个 library owner 保存远端引用，不先 prepare／读取成员，也不替换队列或退休当前推荐。

外部调用不能进入 `music.queue.recommendation.*` 私有命令。推荐协调器是该私有事务的唯一调用者。

## 账号代次

- 每个真实平台请求捕获它实际使用的账号 lease。
- 旧请求返回 `AUTH_REQUIRED` 或 `AUTH_EXPIRED` 时只撤销该旧 lease，不撤销并发登录产生的新账号代次。
- 账号未知写入回执会封闭依赖账号的推荐和关闭成功路径。
- 只有显式成功读取当前账号文档，且期间没有更新的未知回执，才解除该 fence。
- 不通过旧 Cookie、其他客户端数据或回退目录恢复账号。
- Node publication 由同一 client 管理实际 `session.update` 及物理排空，不保留独立原生客户端文档／未知设备回执路径。

## 请求准入

- 关闭准备开始后新调用返回 `CLOSED`。
- 调用方取消会取消尚未完成的读取和脚本工作；已经进入持久 CAS 的写入必须取得真实结果。
- 页面、账号、筛选和搜索各自由其 owner 管理 generation；旧结果不能覆盖新状态。

## 两阶段关闭

准备顺序以真实依赖为准：

1. 封闭新请求并取消当前 generation。
2. 同步围住推荐 callback，并启动账号读取退休；排空推荐、VIP 和账号读取。
3. 退休目录／歌词／发现／搜索，等待歌词、发现、匹配、目录和同步工作。
4. 排空已准入 application 请求，再保存可选来源匹配状态。
5. 准备反馈、脚本和唯一 `MusicLibraryOwner`。
6. 等待 `MusicPlatformClient.DrainAsync` 与缓存存储排空；已知账号未知回执仍使关闭准备失败。

准备失败不会释放借用的 App store/runtime/playback。取消关闭会恢复原 owners，但不会自动重放写入、登录、远端同步、脚本调用或播放。

最终 dispose/abort 才释放 application 自己拥有的协调器、账号协议和 platform client；借用的 App runtime/store 由 App 在能力排空后释放。取消关闭准备使用新生命周期，不复活旧 lease、不重放写入或平台请求；未知持久写入不会被描述成回滚。

## 数据格式

- 当前 App 保持现有 `aggregate` 数据身份。
- App 通过 `LocalDocumentStore` 的精确命名空间配置，仅对当前 `music.accounts.v2` 使用 DPAPI；匿名平台状态归 Node 私有文档。
- 曲库、偏好和缓存等普通本地文档直接保存 JSON 字节；存储提供当前文件指纹、原子写入、CAS 与文件隔离。开发阶段不迁移旧普通加密文档，也不自动清理旧数据。
- 脚本自行保存的 Cookie 等运行产物不由运行器特殊加密；运行器继续提供文件与实例隔离。
- 每个原生 owner 只支持唯一当前格式。
- 全新空数据及当前格式正常重载遵循[共同歌曲仓库](../common/music/smart-playback.md)；不读取、识别、迁移或清理历史 namespace。

本轮文档对应静态接线，无运行、构建或关闭竞态通过结论。
