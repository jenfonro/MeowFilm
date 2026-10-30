# iOS 架构与模块职责

meowfilm 是单一 iPhone App。界面、导航、呈现与手势由 UIKit 持有；业务、平台、账号、播放、缓存和持久化位于 Swift Package `native/MeowKit`。

共同规则分别见[页面数据请求](../common/pages/data-requests.md)和[音乐平台客户端上下文](../common/music/platform-client-context.md)。本文件说明 Swift Package 与 UIKit 的端侧装配，不把目标契约视为已完成实现。

## 应用工程

| 目录 | 职责 |
| --- | --- |
| `apps/ios/Application` | `UIApplication`／scene 生命周期、唯一窗口、固定产品身份 |
| `apps/ios/Composition` | 资源、原生音乐服务、播放控制、账号访问与后台同步的装配和关闭 |
| `apps/ios/Navigation` | 根容器、普通页面导航、播放器及其他非导航呈现的路由 |
| `apps/ios/Presentation` | 主页面外壳、页头、底部导航和播放器停靠区 |
| `apps/ios/UITests` | 共享 UIKit 组件测试，不属于 App 源成员 |

工程文件由 `scripts/generate-ios-project.py` 生成。App 只编译上述四个职责目录；UITests 使用独立测试 target。

## Swift Package

| 模块 | 职责 | 依赖 |
| --- | --- | --- |
| `MeowContracts` | 通用请求、生命周期和错误契约 | 无 |
| `MeowJSON` | 有界 JSON 值与严格解码 | 无 |
| `MeowStorage` | 普通及加密文档存储 | Contracts |
| `MeowAccounts` | 凭据与账号状态基础设施 | Contracts |
| `MeowPlayback` | 系统播放、媒体会话、播放所有权 | Contracts |
| `MeowUI` | 中性主题、窗口渐变、页面生命周期、导航、搜索页与呈现机制 | 无 |
| `MeowMusicFeature` | 音乐界面、交互、公开业务契约与音乐外观策略 | Contracts、Storage、Playback、UI |
| `MeowMusicPlatform` | 网易、QQ、酷狗协议、账号、加密、HTTP 与文件缓存 | Feature、Contracts、JSON、Accounts、Storage |
| `MeowMusicNativeService` | 曲库、队列、搜索、推荐、歌词、缓存编排和持久化 | Contracts、Feature、Storage |

共享模块不依赖音乐模块。业务差异通过显式参数、契约或注入实现表达；模块边界由 `scripts/check-source-scope.py` 检查。

## UI 所有权

- App／scene 持有唯一窗口和产品装配；页面不反向控制窗口或建立第二套导航。
- 普通页面使用同一 `UINavigationController` 和系统交互式返回，滑动返回时露出真实上一页。
- 有未保存修改的歌单编辑器在返回前请求确认，确认结束后恢复系统返回手势。
- 推荐、频道、我的由主页面外壳保留并转发生命周期；隐藏页面不运行订阅或页面任务。
- 胶囊、卡片和完整播放器使用同一个不透明播放器表面，由同一转场所有者负责进入、取消、完成和归还。
- 上层呈现通过视图层级阻挡下层触摸。
- 窗口背景按同一窗口坐标绘制。共享主题只接收解析完成的色板，不读取歌曲或音乐偏好。

## 数据身份

| 身份 | 所有者／用途 |
| --- | --- |
| `dev.meowfilm.aggregate` | 固定产品身份；安装宿主改写安装标识时，内部存储身份不变 |
| Application Support 下 `meowfilm/encrypted-documents-v1` | 加密文档根目录 |
| `music.library.v1` | 曲库、队列、历史和播放记录 |
| `music.accounts.v1` | 三平台账号凭据与固定账号槽 |
| `music.account-url-cache.v1` | 账号授权 URL 缓存 |
| `meowfilm.music.audio-cache.v1` | 实际播放后的完整音频缓存 |
| `music.playback-display.v1`、`music.home.snapshot.v1` | 播放展示和首页快照缓存 |
| `music.ui.appearance.v1`、`music.ui.songColors.v1`、`music.ui.lastChannel` | 音乐外观偏好、歌曲取色缓存和频道恢复 |
| `playback.volume`、播放所有者 `music` | 共享播放音量与当前播放业务所有者 |

歌曲／录音、来源、队列 occurrence、播放 attempt、账号访问代次和文件版本是相互独立的身份，不能用标题、短期 URL 或队列位置替代。
