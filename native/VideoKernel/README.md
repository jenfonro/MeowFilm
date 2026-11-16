# MeowVideoKernel

电影产品对固定源码 KSPlayer 的薄适配。`native/KSPlayer` 编译实际 `KSMEPlayer`、FFmpeg 解码／过滤、VideoToolbox、原生音频输出、Metal／sample-buffer surface、轨道和字幕；上游播放控件、SwiftUI 页面和 Demo 不进入 target。这里不再保留第二套私有 C decoder、帧队列或音视频输出。音乐不使用此包。

`VideoSession.prepare(url:format:clearKeys:position:headers:)` 为静默候选创建独立核心及 `KSOptions`；必要来源凭据已在 Node 媒体 owner 的 receipt 不重复传回原生，直连媒体则显式传 headers/Cookie。共同输入、隔离和生命周期契约见[电影原生播放](../../docs/common/film/playback.md)。

`VideoOutput.presentationView` 保留核心完整 UIView／NSView 层级。`renderFirstVideoFrame` 等实际 displayed pixel 或 Metal drawable 呈现确认后才允许提交；prepare/open 成功不视为显示。PiP 使用核心实际 display layer，由产品控制器持有，核心不注册独立系统音频会话、Now Playing 或远程控制。

`VideoSession.close()` 加入核心网络、解码队列、轨道、音频与渲染的真实关闭，再由调用方释放 Node 媒体 lease。显式暂停／倍速以及 seek 均由同一媒体代次控制。HDR/DV 保留上游必要处理与原生输出，不将源码存在或模拟器编译当作设备显示验收。

候选的 `validateForCommit()` 为同步健康性门禁。媒体实例采用 8 秒最大前向缓冲；取消尚未确认的 seek 后保留确认位置，只有之后明确恢复才重建失效的 feeding。PiP 始终绑定当前真实 display layer，核心格式变化替换 layer 时由产品 owner 重新绑定。

在 Mac 仓库根目录按所需目标准备：

```sh
python3 -I -B scripts/prepare-ios-video-kernel.py --sources-only
python3 -I -B scripts/prepare-ios-video-kernel.py --target macos-$(uname -m)
python3 -I -B scripts/prepare-ios-video-kernel.py --target ios-simulator-$(uname -m)
python3 -I -B scripts/prepare-ios-video-kernel.py --target ios-arm64
python3 -I -B scripts/prepare-ios-video-kernel.py --verify --target ios-arm64
```

没有默认全量；`--target` 可重复。脚本验证固定源、core-only 源码闭包与本地补丁，再构建对应 FFmpeg 8.1.2 依赖。共用下载与已验证完整产物按[依赖缓存](../../docs/common/dependency-cache.md)复用，工作副本保存私有中间文件。产物及回执位于 `native/KSPlayer/.cache/`；不存在目标 slice 时不得回退假内核或下载预编译播放器。来源与改编记录见[Provenance](Provenance/README.md)，唯一许可正文位于仓库根 `LICENSES`。
