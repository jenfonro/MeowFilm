# 文档

- [第三方许可](licenses.md)

## 共同契约

- [共同契约索引](common/README.md) — 按页面、设置存储、音乐平台和验证职责维护，端侧 API、存储与实现差异仍在各端文档。
- [跨端 Debug 模式与调试接口](common/debug-mode.md) — Windows、iOS 与共用 Node 运行时的运行期 `--debug` 开关、分类日志、本地 Control API、底层诊断与跨端 CLI 共同契约。

## 共用脚本运行时

- [公共核心与平台边界](runtime/script-runtime.md)
- [原始 LX 音源契约 music.script.v3](runtime/music-script-runtime.md)
- [CatNode 脚本契约 cat.script.v2](runtime/cat-script-runtime.md)
- [Reader 书源契约 source.runtime.v2](runtime/reader-source-runtime.md)
- [小说书源仓库、图片与交互](runtime/source-library.md)
- [source CatVod Spider 输入契约](runtime/cat-source-runtime.md)
- [来源 capability ABI（共同契约唯一来源）](runtime/source-capability-abi.md)
- [Runtime bridge](runtime/runtime-bridge.md)
- [TMDB／豆瓣只读数据、持久设置与图片流](runtime/metadata-data.md)
- [网易订阅实时 token](runtime/netease-token.md)
- [共同音乐平台请求 music.platform.v1](runtime/music-platform.md)
- [内置网盘能力 pan.runtime.v1](runtime/pan-runtime.md)
- [本地影视会话、智能匹配与媒体流 film.playback.v1](runtime/film-playback.md)
- [外部 LX、CatNode 与 Reader／CatVod 来源动态验收](runtime/source-acceptance.md)

## iOS

- [架构与模块职责](ios/architecture.md)
- [音乐能力与统一音源管理](ios/music.md)
- [音源管理与选源](ios/music-sources.md)
- [独立音乐核心](ios/music-audio-core.md)
- [系统媒体、锁屏歌词与耳机控制](ios/system-media.md)
- [网易云漫游、心动与队列推荐](ios/music-recommendations.md)
- [影视首页、详情与原生播放](ios/film.md)
- [影视设置、播放器控制与来源管理](ios/film-settings.md)
- [影视搜索](ios/film-search.md)
- [小说、书架与原生阅读器](ios/novel.md)
- [本地存储](ios/storage.md)
- [Debug 模式与调试接口](ios/debug-mode.md)
- [构建、发布与安装](ios/release.md)

## Windows

### 架构

- [App 结构与能力划分](windows/app-structure.md)
- [Debug 模式实现说明](windows/debug-mode.md)
- [桌面壳](windows/desktop-shell.md)
- [影视首页](windows/film-home.md)
- [影视播放页](windows/film-playback.md)
- [影视设置、客户端规则与来源管理](windows/film-settings.md)
- [影视搜索](windows/film-search.md)
- [小说、书架与原生阅读器](windows/novel.md)
- [本地 SQLite 与资源存储](windows/protected-storage.md)
- [原生设置与运行器消费](windows/native-settings.md)
- [公共图像](windows/artwork.md)
- [系统媒体会话](windows/system-media.md)

### 媒体

- [原生媒体播放](windows/media-playback.md)
- [独立音乐核心](windows/music-audio-core.md)
- [媒体资源租约](windows/scoped-media-resources.md)
- [本地媒体文件授权](windows/granted-media-files.md)
- [选择文件快照](windows/selected-files.md)
- [播放续接](windows/playback-recovery.md)

### 音乐

- [音乐能力实现](windows/music-implementation.md)
- [原生音乐应用边界](windows/music-native-application.md)
- [账号](windows/music-native-accounts.md)、[账号存储](windows/music-account-storage.md)
- [平台客户端上下文](windows/music-platform-client-context.md)
- [设置页呈现与音源演示边界](windows/music-settings-presentation.md)
- [平台目录](windows/music-native-platforms.md)、[目录与频道](windows/music-catalog.md)、[个人目录页面](windows/music-catalog-personal-ui.md)
- [多平台搜索](windows/music-search.md)、[搜索发现](windows/music-search-discovery.md)、[搜索设置与历史](windows/music-search-preferences.md)、[搜索观察者](windows/music-search-observers.md)、[发现账号读取](windows/music-discovery-account-drain.md)
- [曲库状态](windows/music-native-library.md)、[曲库同步](windows/music-library-sync.md)
- [推荐](windows/music-recommendations.md)、[推荐消费](windows/music-native-recommendations.md)
- [歌词](windows/music-native-lyrics.md)、[歌词应用链](windows/music-lyric-application.md)、[元数据偏好](windows/music-metadata-policy.md)
- [官方录音选择](windows/music-official-selection.md)、[音质策略](windows/music-quality-policy.md)、[播放反馈](windows/music-playback-feedback.md)

### 脚本运行时

- [启动与命名管道](windows/app-runtime.md)
- [原始 JS 音源接入](windows/music-script-runtime.md)
- [脚本连接恢复](windows/runtime-recovery.md)

### 打包

- [应用构建、安装与卸载](windows/runtime-packaging.md)
