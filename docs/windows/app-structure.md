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

Core 不认识业务；基础设施不反向引用 App 或能力。一个窗口、一套导航与设置、一个原生播放协调、一个系统媒体会话。原生媒体内核与自适应流能力由现有音频链直接使用；当前不装配下载能力。

## 能力源码

`apps/windows/src/MeowFilm.Capabilities` 按真实职责组织：

| 目录 | 责任 |
| --- | --- |
| Composition | 本期能力注册、原生调度入口、公开 seam、导航与共同模型 |
| Accounts | 原生授权、登录交接、账号资料权益和只读旧格式迁移 |
| Platforms | 固定 HTTP／加密协议、来源元数据；`ClientContexts` 独立管理客户端身份、匿名登记、快照及唯一客户端 writer |
| Catalog / Search / Recommendations | 目录、搜索、发现及推荐消费者 |
| Library / Sync | 歌单、队列、历史、当前本地曲库与显式同步 |
| Playback / Lyrics | 实际播放编排、恢复、来源选择与歌词 |
| PlaybackCache | 可淘汰播放缓存、授权复核、容量及读取生命周期 |
| Scripting | 原始 LX 音源管理、Cat 运行时通信与必要宿主调用，不拥有官方业务 |
| Feedback / Settings | 实际反馈协议与设置消费者 |
| Presentation | 共同视图、主题及中性命名的控件 |
| Film | 固定影视首页数据、模块生命周期和影片专属展示 |

具体歌曲、歌词、音乐平台协议保留 `Music` 语义。跨多个职责的既有 partial 类型保持原公开 namespace，不为凑目录重新设计业务模型或复制 owner。中性控件包括 `CardMotion`、`EntryCard`、`Shelf`、`OverlayScrollChrome`、`SeekBar`、`SkipButton`；主题键为 `Capability.*`。WPF 共享资源使用显式程序集 pack URI，不靠移动后目录的相对搜索回退。

## 当前身份与唯一数据格式

音乐客户端本地初始化不依赖登录；应用明确连接客户端 owner 与账号／目录／播放，构造无 I/O、启动不批量登记。Windows 的文档与关闭次序见[平台客户端上下文](music-platform-client-context.md)。

`AppIdentity` 集中定义产品名称、当前数据/运行时身份和当前原生命名空间：

- 名称：`meowfilm`。
- 数据根为 `%LOCALAPPDATA%/meowfilm/aggregate`；目录身份参与 DPAPI 绑定，不通过更换身份绕开当前格式约束。
- 脚本协议/清单字段 `product` 等于 `aggregate`，是当前 wire 身份。
- 活动产品只读取和写入 `AppIdentity.NativeNamespaces` 列出的当前格式。
- 当前提供可淘汰的播放缓存，未提供用户下载管理。
- 不兼容数据明确失败并保留原始内容。

可执行文件名、程序集名、源文件位置、测试分组和 WPF 资源键的变化都不等于持久化键变化。

## 原生和脚本边界

网易云、QQ、酷狗官方业务独立于 LX。账号、平台 API、搜索、曲库、缓存和播放在 .NET 内执行；无脚本操作时不启动 Node。

Windows 的唯一 Node 入口是 `apps/windows/runtime/entries/aggregate.mjs`，从公共 `apps/runtime/modules/{music,cat}-script.mjs` 装配 `music@0.6.0`／`music.script.v2` 与 `cat@0.1.0`／`cat.script.v1`。公共层复用一套 script-host／QuickJS 核心；Windows 目录只有启动／命名管道 bootstrap 与薄 entry，没有业务 module 副本。

载荷包含公共 bootstrap、共享 music/Cat 模块、Windows 两个入口文件、runtime-bridge、script-host、有限 Node 兼容依赖、锁定的 QuickJS release-sync 引擎和必要许可，没有普通业务文档桥授权。Cat 设置、源码和私有卷位于 App 固定 `script-runtime` 数据根；公共执行、存储与网络边界见 [共用运行时](../runtime/script-runtime.md)。

App 原生能力装配与 `AppIdentity.ScriptModules` 独立校验：原生能力数量和 `IDesktopModule.Id` 不要求等于脚本模块数量或名称。增加不依赖脚本的原生能力不会被迫增加 Node 模块；脚本清单只决定按需启动后的 JS 载荷与协议版本。

当前 App 先注册音乐能力，再注册不依赖脚本、存储或播放器的 `FilmCapability`，因此启动页保持音乐推荐。影视首页的数据和路由边界见[影视首页](film-home.md)。
