# 一个 Windows App，按能力组织

共同规则分别见[页面数据请求](../common/pages/data-requests.md)和[音乐平台客户端上下文](../common/music/platform-client-context.md)。本文件说明 WPF／.NET 的具体装配；模块化接入不等于把所有上下文初始化集中在 Accounts 或页面。

## 装配与依赖方向

唯一入口是 `apps/windows/app/MeowFilm.Windows.App.csproj`，可执行文件为 `meowfilm.exe`。

```text
Windows App
 ├─ AppHost → Desktop / Platform
 ├─ Capabilities → Desktop / Platform / Media
 └─ SystemMedia → Media
Platform / Desktop → Core
```

Core 不认识业务；基础设施不反向引用 App 或能力。一个窗口、一套导航与设置、一个原生播放协调、一个系统媒体会话。原生媒体基础设施由既有音频链和影视媒体共同使用；当前不装配下载能力。

`scripts/windows/verify-app-structure.mjs` 检查 Windows 源码、公共 `apps/runtime`／`packages`、Windows 工具及其显式共享 Node 工具，不递归扫描整个 `scripts`。iOS 专属模板只在其生成运行时中解析；Windows 源码或工具也不能反向导入这些模板。相对导入、链接安全、运行时目录闭包和 WPF 资源检查保持；EmbeddedResource 的实际路径、逻辑身份及清单也由此检查。Windows 构建输入指纹采用相同源码／工具范围，并包含本端配置、测试、共享引擎输入和随包许可；iOS 专属代码、脚本及测试不影响该指纹。

## 能力源码

`apps/windows/src/MeowFilm.Capabilities` 按真实职责组织：

| 目录 | 责任 |
| --- | --- |
| Composition | 本期能力注册、原生调度入口、公开 seam、导航与共同模型 |
| Accounts | 原生授权、登录交接和账号资料权益 |
| Platforms | 固定 HTTP／加密协议、来源元数据；`ClientContexts` 独立管理客户端身份、匿名登记、快照及唯一客户端 writer |
| Catalog / Search / Recommendations | 目录、搜索、发现及推荐消费者 |
| Library / Sync | 歌单、队列、历史、当前本地曲库与显式同步 |
| Playback / Lyrics | 实际播放编排、恢复、来源选择与歌词 |
| PlaybackCache | 可淘汰播放缓存、授权复核、容量及读取生命周期 |
| Scripting | 原始 LX 音源管理、CatNode 与来源运行时通信及必要宿主调用，不拥有官方业务 |
| Feedback / Settings | 实际反馈协议与设置消费者 |
| Presentation | 共同视图、主题及中性命名的控件 |
| Film | 影视首页、来源取数、原生搜索会话、播放交接、模块生命周期和影片专属展示 |

具体歌曲、歌词、音乐平台协议保留 `Music` 语义。跨多个职责的既有 partial 类型保持原公开 namespace，不为凑目录重新设计业务模型或复制 owner。中性控件包括 `CardMotion`、`EntryCard`、`Shelf`、`OverlayScrollChrome`、`SeekBar`、`SkipButton`；主题键为 `Capability.*`。WPF 共享资源使用显式程序集 pack URI，不靠移动后目录的相对搜索回退。

## 当前身份与唯一数据格式

音乐客户端本地初始化不依赖登录；应用明确连接客户端 owner 与账号／目录／播放，构造无 I/O、启动不批量登记。Windows 的文档与关闭次序见[平台客户端上下文](music-platform-client-context.md)。

`AppIdentity` 集中定义产品名称、当前数据/运行时身份和当前原生命名空间：

- 名称：`meowfilm`。
- 数据根为 `%LOCALAPPDATA%/meowfilm/aggregate`；仅应用账号与设备会话的敏感文档使用绑定目录身份的 DPAPI；普通文档和脚本源码以明文文件保存，原子写入、CAS 和文件隔离独立保留，见[本地文档存储](protected-storage.md)。
- 脚本协议/清单字段 `product` 等于 `aggregate`，是当前 wire 身份。
- 活动产品只读取和写入 `AppIdentity.NativeNamespaces` 列出的当前格式。
- 当前提供可淘汰的播放缓存，未提供用户下载管理。
- 不兼容数据明确失败并保留原始内容。

可执行文件名、程序集名、源文件位置、测试分组和 WPF 资源键的变化都不等于持久化键变化。

## 主窗口启动门槛

`AppLauncher` 完成模块装配、外观和窗口关闭偏好读取后，依次等待实现 `IDesktopStartupParticipant` 的模块完成 `PrepareStartupAsync`，随后才构造 `ShellWindow` 并调用 `Show()`。现有启动窗口承载等待、失败和重试；模块完整的 `InitializeAsync` 仍由桌面壳的选中模块生命周期负责。外观与关闭偏好作为 App 共享参与者加入两阶段关闭；主窗口拥有原生托盘，隐藏到托盘不进入 App 关闭流程，具体行为见[桌面壳](desktop-shell.md#系统托盘与关闭选择)。

音乐参与者只完成最小本地准备：通过 `music.accounts.list` 恢复账号状态，读取 `music.catalogSource.settings.get` 和 `music.home.v1`。账号与来源恢复失败不能冒充游客或默认来源；不等待网络登录验证、账号资料或首页 HTTP。快照缺失、不可读取或不能解码为可用展示时均视为未命中，允许正常首载；不把缓存错误变成启动门槛，也不把真实首页请求失败改写为成功空结果。

`music.home.v1` 是全应用唯一的最近成功首页展示，带平台身份，不按账号分仓、无 TTL，也不携带执行权限；显示相符快照后的网络更新由首页 owner 负责。持久化、失败保留与认证失效恢复见[首页启动与展示快照](music-catalog.md#首页启动与展示快照)。

## 原生和脚本边界

网易云、QQ、酷狗官方业务独立于 LX。账号、平台目录、搜索、曲库、缓存和官方播放在 .NET 内执行，不以 Node 连接为前置条件。网易订阅／退订的 token 准备按需借用公共 Node，失败保留本地意图，订阅请求及 Cookie 仍归原生 owner。公共宿主随 App 启动，影视随后恢复启用猫源，不因此执行音乐或停用的猫源脚本，也不加载 Watchman SDK。

Windows 的唯一 Node 入口是 `apps/windows/runtime/entries/aggregate.mjs`，从公共 `apps/runtime/entry.mjs` 按序装配 `music@0.8.0`／`music.script.v3`、`cat@0.2.0`／`cat.script.v2`、`source@0.2.0`／`source.runtime.v2`、`pan@0.1.0`／`pan.runtime.v1`、`tmdb@0.1.0`／`tmdb.data.v1`、`douban@0.1.0`／`douban.data.v1`、`film@0.1.0`／`film.playback.v1`、`netease@0.1.0`／`netease.token.v1` 八模块。公共层管理 LX/source 原生 QuickJS 线程、Cat Node Worker 和按需网易 token Worker；Windows 目录只有私密启动／存活管道 bootstrap 与薄 entry，业务使用完整 HTTP，没有业务 module 副本或第二套 runner。

载荷包含公共 entry/bootstrap/client/contracts、共享八模块、Windows 两个入口文件、runtime-bridge、script-host、source-runtime、pan-runtime、film-playback／film-rules、netease-token／锁定 jsdom、Node 兼容及规则解析依赖、锁定的原生 QuickJS-NG addon/source SDK 和必要许可，没有普通业务文档桥授权。CatNode 设置、源码和私有卷位于 App 固定 `script-runtime` 数据根，source 的 Reader/CatVod 不继承该私有卷或服务能力。film 复用同一批 provider，独占影视业务缓存、新增历史／人工匹配／屏蔽／偏好；原生 `FilmLibrary` 收藏仍为 `film.library.v1`，规则与图片缓存不迁入 Node。公共执行、存储与网络边界见[共用运行时](../runtime/script-runtime.md)；source 平台能力以[capability ABI](../runtime/source-capability-abi.md)为唯一来源。Node 无关仅指 source-runtime 规则包，不表示 Node 兼容层无需 Node，也不表示已提供阅读 UI 或其他平台客户端。

App 原生能力装配与 `AppIdentity.ScriptModules` 独立校验：原生能力数量和 `IDesktopModule.Id` 不要求等于脚本模块数量或名称。增加不依赖脚本的原生能力不会被迫增加 Node 模块；脚本清单只决定 App 启动时的 JS 载荷与协议版本。

当前 App 先注册音乐能力，再注册 `FilmCapability`，因此启动页保持音乐推荐。影视能力拥有客户端规则、视频源、元数据适配／设置、搜索与播放会话消费者；首页仍独立读取豆瓣，正式播放实现通过 `FilmPlaybackSession` 消费 Node film，而不是由首页本地 preview 提供片源。共享 NativePlayback／libVLC 与私有锁定 FFmpeg 解复用桥的边界见[原生媒体实现](media-playback.md)。App 另通过 `AppLauncher.createPages` 装配 `SearchResultsPage`，Desktop 只消费中性 `IDesktopSearchPage`；共享搜索 JS 以 `MeowFilm.FilmSearch.js` 嵌入 Capabilities，原生 QuickJS 不依赖 Node addon。原生导航／搜索已接入正式播放会话；实现接通、构建、原生测试与真实媒体验证分别记录，不代表整 App 已全部通过。搜索交接只见[共同契约](../common/film/search.md)。影视首页见[影视首页](film-home.md)，设置见[影视设置](film-settings.md)，端侧搜索接通状态和原生测试清单见[影视搜索](film-search.md)。


## 同进程清除本机数据

通用设置的清除操作由 `AppLauncher` 注入，不由音乐或影视模块互相清理数据。`UserDataDirectory` 的一次待清理请求同时覆盖 `AppDataPaths.Documents`（`aggregate`）与 `AppDataPaths.Caches`（`aggregate-cache`）；前者包含全部 Node 元数据／媒体／脚本缓存，后者包含全部原生缓存文档、账号展示快照及音频文件，不按命名空间清单筛选。两根仍保留独立存储队列和根锁；普通启动只取得 App 准入，不打开或创建可选缓存根。

`AppUserDataReset` 串行持有一次已确认操作的阶段：记录待清理请求、真实释放旧代、删除两根自有数据、重新装配；失败显式重试只继续未成功的阶段。旧窗口取消导航并锁定交互，媒体设置先封住系统输入，随后使用 `AppShutdown.ForceReleaseAsync` 退役全部所有者，包含实际排空的 `CacheDocumentStore` 与其 backing store。内存缓存随旧代释放；缓存落盘尚未结算、任一根删除失败或释放超时，都不能放行新代启动。删除前两根均取得排他删除句柄，任一仍有存储占用则不开始删另一根。

两根全部清除后才移除 pending，关闭旧 Shell，但不关闭 WPF dispatcher；复用启动/失败重试窗口创建新的 `AppStartup`、store、cache owner、runtime、业务模块、偏好、播放和窗口附件。入口装配闭包在资源成功释放后清空，旧 Shell 的 Closed 不会终止替代代次。程序 PID 不变，无需手动重启。中断后的进程启动先继续同一请求，部分删除失败仍不准入业务；完成请求的重试不再删除新代业务或缓存。范围及文件排他边界见[本机数据清除](protected-storage.md#clear-all-local-user-data)。
