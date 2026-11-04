# Windows 音乐能力实现

## 产品范围

Windows 当前交付一个原生 meowfilm App，并接入音乐相关能力。

- 不是独立 Music 产品。
- 不装配下载管理。
- 不用网页或 Node 代替 WPF、原生业务 owner 和原生播放链。
- 不为其他平台预设兼容层。

## 主要职责

| 目录 | 责任 |
| --- | --- |
| `Composition` | 唯一音乐装配、导航、生命周期和公开 seam |
| `Accounts` | 三平台授权、登录交接和账号详情，旧设备格式只读迁移 |
| `Platforms` | 固定官方平台 HTTP、协议和歌曲 DTO；`ClientContexts` 独立拥有客户端身份、匿名登记和受控更新 |
| `Catalog` | 平台目录、个人内容和歌单读取／提交 |
| `Search` | 歌曲／歌单搜索、策略和请求代次 |
| `Recommendations` | 每日推荐、持续推荐和唯一 queue lane |
| `Library` | 曲库、队列、历史、设置、歌词快照和脚本源元数据 |
| `Sync` | 网易云／QQ／酷狗收藏和歌单同步 |
| `Playback` | 统一音源候选、录音选择、授权、取链、恢复和播放状态 |
| `PlaybackCache` | 可淘汰播放候选、高潮和完整音频文件缓存 |
| `Lyrics` | 原生歌词查询、解析、选择和展示快照 |
| `Scripting` | 原始 LX 音源脚本管理和必要 Node 调用 |
| `Feedback` | 播放上报及其唯一设置 owner |
| `Settings` / `Presentation` | 原生即时设置投影和 WPF 视图 |

依赖方向保持：

```text
Windows App
  -> MusicCapability / MusicNativeApplication
  -> 各能力 owner
  -> Platform / Media / Storage / Core
```

基础层不反向依赖能力或 App。

## 原生与 Node

官方网易云、QQ、酷狗链全部由原生 owner 承担：

```text
账号
搜索
推荐
目录
同步
官方取链
歌词
缓存
播放反馈
持久设置
```

Node 通过平台中性的共用 script-host／LX／QuickJS 核心执行原始 JavaScript；Windows 只负责启动、命名管道与打包，原生脚本入口为：

```text
music.sources.*
music.online.describe
music.online.resolve
music.online.lyrics
music.online.cancel
music.transfer.*   // 仅 kind=script
```

不运行脚本时，官方业务不启动或依赖 Node。Node 没有通用文档访问、任意文件、进程、账号凭据或 App 内部状态能力。

## 脚本源生命周期

原生 `MusicScriptOwner` 拥有安装元数据、存储会话绑定、实例代次、取消和持久事务；公共 Node host 拥有实际 JS 执行会话。当前 `music.script.v2` 的请求处理器以第二参数 `context.storage` 取得绑定调用的权限，全局 `meow.storage`／startup／background 无隐式权限。

- 安装输入来自原生已选择文件的只读字节快照或 HTTPS 下载；两种获取方式进入同一检查、传输与安装流程。
- 设置页允许一次明确选择多份 `.js`；导入只持久当前源元数据并保持停用，不连接音源服务器、不执行脚本。
- transfer 只接受 `.js`、规范 SHA-256、严格 UTF-8 和顺序 Base64 分片。
- 当前 transfer 只允许 `kind=script`。
- 每个 installation、platform 和 generation 都显式指定。
- 启用状态决定实例是否可参与候选；应用启动和列出候选不激活全部脚本，实际需要时才按顺序请求并激活实例。
- 脚本在线请求的内部 25 秒期限届满返回 `TIMEOUT`，统一选择将其作为该候选不可用处理，保留其他已成功的内置候选。普通调用方取消、源撤销或质量取消仍返回 `CANCELLED`，终止旧操作。
- 更新启用中的实例时，先撤销旧 grant，再验证候选；候选 metadata 与脚本 storage 通过同一 library envelope CAS 发布。
- source rollback 只是在当前安装实例的 `current`／`previous` 之间切换当前脚本版本，不解释或转换旧数据格式。
- 迟到脚本回调、旧 grant 和旧 generation 不能覆盖当前实例。
- 音源不再维护域名／私网授权名单；`validateURL` 只检查 HTTP(S) 语法，网络可以访问已开放服务。文件／进程隔离不是网络隔离，官方业务的授权规则保持独立。

## 曲库、队列和播放

- `MusicLibraryOwner` 是曲库、队列、历史和设置的唯一 writer。
- `MusicRecommendationQueuePort` 串行所有最终 queue mutation。
- 队列只持久歌曲身份、公开元数据、occurrence 和上下文，不持久短期媒体 URL 或账号凭据。
- 内置源与网络源是类型区别，全部已启用源参与统一选择。关闭内置平台只关闭该平台的官方音频能力，不限制已启用脚本提供同平台录音；禁用脚本不参与。
- 统一选择沿用现有音质顺序、兼容录音、已确认录音偏好及有效缓存。`ResolveAutomaticSources` 通过单次官方 planning 结果回调，将当次录音快照与偏好顺序交给网络源，包含本次发现的兼容录音，不重复发现。同质量先比较现有录音偏好，再按稳定 provider 顺序选择；共享录音信息不共享账号凭据或授予播放权限。
- 官方取链须完成当前账号、质量和媒体授权；LX 每次取链必须指定 installation、platform、generation、song/sourceKey 和 quality。统一选择由能力层协调，脚本按需顺序尝试；已分类的候选不可用可继续到另一类型，取消、存储损坏、未知提交与策略变化必须终止旧操作。
- 手动选择只限定当前歌曲的录音，由符合质量与权限的已启用源提供，不指定全局类型。当前播放仍保留真实来源的 installation、platform、generation 和 sourceKey，队列歌曲身份不变。
- 自动恢复可以双向跨类型，排除键为 `installation + platform + sourceKey`，另排除失败 URL；仍限制最多 4 次来源切换、总计 55 秒。手动指定录音的恢复保持原 20 秒预算，精确刷新失败后可换同一录音的 provider。录音选择与恢复细节见 [统一音源与录音选择](music-official-selection.md) 和 [原生播放续接](playback-recovery.md)。
- 只有真实播放器 `Started` 事件记录播放历史并开始反馈。

## 缓存

缓存与用户下载完全分离：

- 候选缓存保存当前官方候选和授权相关公开信息。
- 高潮缓存保存有限的播放高潮结果。
- 受保护音频文件缓存与离线授权仅适用于官方音频；保存完整已开始播放的字节，读取时仍核对当前录音授权。
- 混合启用及当前由网络源播放都不阻止下一曲可用的官方预加载。预加载只准备官方音频，不给网络源新增离线缓存；内置完整文件与 prepared 缓存候选仍须同脚本候选比较质量，缓存命中不跳过脚本。实际播放经过统一选择及授权核对后，只有匹配结果才可复用预加载文件。
- 缓存 schema、索引、文件身份或保护失败时明确报告；不把损坏文档当 miss，不自动重建旧格式。
- 普通可淘汰缓存不可用可以回到网络读取，但不能回退到旧格式或跳过授权。

## 设置

设置由实际 owner 直接持久化并立即生效：

- 平台账号、播放上报、收藏同步、歌单同步各只有一个控制位置。
- 歌曲搜索和歌单搜索使用独立当前策略。
- 音质、外观、歌词／封面平台顺序和播放设备设置分别由其 owner 保存。
- 音源页直接管理导入、独立启停、更新、回退和删除；管理条目选择不保存全局播放目标，内置／网络可同时启用。
- CAS 失败时 UI 读取当前权威结果用于恢复显示，不重放用户操作。

## 请求生命周期

共同请求规则以[页面数据请求](../common/pages/data-requests.md)为准；下列清单说明 Windows owner 需要明确的实现边界，平台初始化见[客户端上下文](music-platform-client-context.md)：

```text
owner
触发入口
请求身份与参数
generation
在途去重
取消条件
分页终止
缓存语义
失败范围
刷新条件
```

规则：

- 启动只获取恢复当前 App 和首个可见页面所需的数据。
- 页面、栏目、详情、更多面板和分页按进入／可见／操作再获取。
- 布局、绑定和集合变化不得无条件再次触发同一请求。
- 同一身份和参数在同一 generation 内只有一个有效请求。
- 旧请求结果不能覆盖新页面、筛选、账号、歌曲或脚本实例。
- 鉴权、协议、格式和权限错误不得自动重试。

## 数据与协议

数据与协议的持久化行为：

- 保持现有 App 数据身份和命名空间。
- 当前格式变化时同时修改 producer、consumer、契约和全部夹具。
- 不兼容数据保持原样并明确失败；未经用户授权不删除、清空、覆盖或改写。
