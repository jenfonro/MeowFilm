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
NativeDatabase
MusicLibraryDatabase
IRuntimeClient
播放内核
```

`IRuntimeClient` 同时用于原始 JS 音源和共同 `music-platform` 模块。平台请求、认证、私有响应转换及匿名协议状态归 Node；曲库、永久身份、引用队列、设置、账号安全保存和原生播放归 .NET owner。共同边界见[平台请求](../common/music/platform-client-context.md)。

`InitializeAsync` 依次初始化反馈本地状态、恢复歌曲资料并启动同步调度；不初始化原生设备文档或主动登记全部平台。单个 `MusicPlatformClient` 在构造时绑定账号 sessions，具体边界见[平台客户端](music-platform-client-context.md)。

## 公开边界

```csharp
public sealed partial class MusicNativeApplication : IAsyncDisposable
{
    public Task InitializeAsync(CancellationToken cancellationToken = default);
    public Task<JsonElement> ExecuteAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default);
    public Task<JsonElement> ImportSourceAsync(ReadOnlyMemory<byte> source, string basename,
        string? installationId = null, CancellationToken cancellationToken = default);
    public Task PrepareShutdownAsync(CancellationToken cancellationToken = default);
    public Task AbortAsync(CancellationToken cancellationToken = default);
}
```

`MusicCapability` 拥有 application；App 持有唯一 `NativeDatabase`、runtime 和 playback。音乐歌曲、账号、缓存索引与业务数据按 owner 分表/分关系存储，不建立第二文档数据库或独立缓存根；异步资源 I/O 由对应资源 owner 管理，关闭时等待已受理操作排空。

## 当前路由

| 方法族 | owner |
| --- | --- |
| `music.accounts.*` | `MusicAccountOwner` |
| `ReadDetailsAsync`／`RefreshDetailsAsync`（typed 方法） | 原生账号详情（`MusicAccountOwner`） |
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
- 账号未知写入回执会封闭依赖账号的推荐并使保存准备报告失败；不等于资源未释放。
- 只有显式成功读取当前账号文档，且期间没有更新的未知回执，才解除该 fence。
- 不通过旧 Cookie、其他客户端数据或回退目录恢复账号。
- Node publication 由同一 client 管理实际 `session.update` 及物理排空，不保留独立原生客户端文档／未知设备回执路径。

## 请求准入

- 关闭准备开始后新调用返回 `CLOSED`。
- 调用方取消会取消尚未完成的读取和脚本工作；已经进入持久 CAS 的写入必须取得真实结果。
- 页面、账号、筛选和搜索各自由其 owner 管理 generation；旧结果不能覆盖新状态。

## 进程内退役

入口同步封闭新请求；准备工作随后切到不捕获 UI 上下文的后台执行，与普通命令使用同一线程边界。即使前序排空任务已完成，搜索退休时的歌曲资料投影也不会回到 UI 线程读取仓库。

1. 封闭新请求并取消当前 generation，同步围住推荐 callback。
2. 同时停止推荐、账号读取、目录、歌词、发现、搜索、同步、反馈和脚本工作；App 同时停止所拥有的执行端，避免读者等不到生产者结束。
3. 排空已准入 application 请求、真实本地事务和资源工作，再释放 application 自己拥有的协调器、账号协议与 platform client。
4. App 最后释放被借用的数据库和播放资源，确认旧代停止后才能清空数据或重新装配。

退役不可取消，不复活旧 lease、callback 或播放意图。保存准备的失败仍保留在原回执；Dispose／Abort 报告实际释放失败，不重复把已结算的未知业务回执当作资源仍被占用。未知写不会被重放或描述成回滚。

正常整进程退出采用[App生命周期](app-runtime.md)的独立边界，不将全部进程内退役步骤作为退出门禁。

## 数据格式

- 当前 App 保持现有 `aggregate` 数据身份。
- 音乐账号、匿名协议身份、歌曲库、队列与缓存索引使用共享 SQLite 中各自 owner 的 typed relations；凭据明文保存，不使用 DPAPI。
- 曲库、偏好和结构化缓存保存为 typed rows 与有序子关系，事务及 revision CAS 保护并发提交；不保存内部 JSON 文档，不探测、迁移或清理旧数据。
- 脚本自行保存的 Cookie 等运行产物不由运行器特殊加密；运行器继续提供文件与实例隔离。
- 每个原生 owner 只支持唯一当前格式。
- 全新空数据及当前格式正常重载遵循[共同歌曲仓库](../common/music/smart-playback.md)；不读取、识别、迁移或清理历史 namespace。

本轮文档对应静态接线，无运行、构建或关闭竞态通过结论。
