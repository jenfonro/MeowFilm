# AudioKernel

独立音频 C decoder，不拥有输出、DSP、账号、HTTP 上游策略或系统媒体会话。Swift 输入和 AVAudioEngine owner 在 MeowKit 中由应用装配。

## 构建与验证

```sh
python3 -I -B scripts/prepare-ios-audio-kernel.py --sources-only
# 以下仅在具备对应 SDK 的 Mac 上执行，目标必须显式选择；可重复 --target。
python3 -I -B scripts/prepare-ios-audio-kernel.py --target ios-arm64
python3 -I -B scripts/prepare-ios-audio-kernel.py --target macos-arm64 --target ios-simulator-arm64
python3 -I -B scripts/prepare-ios-audio-kernel.py --verify --target ios-arm64
```

可选 slice：`macos-arm64`、`macos-x86_64`、`ios-simulator-arm64`、`ios-simulator-x86_64`、`ios-arm64`。同 SDK 的已有、匹配当前配方的架构合并为一个 framework slice。不自动下载 SDK 或安装工具。

- 未修改来源与四库完整构建输入存放于 `~/Developer/cache/ffmpeg`，以来源、锁、准备脚本、SDK、架构和工具链区分；下载、来源提取和产物发布均有进程间原子锁。
- 本副本构建/日志位于仓库 `.cache/ios-audio-kernel`。共享产物不原地写入，临时目录验证后原子发布；修改的共享缓存不当作命中。
- 本 Package 只依赖 `.cache/Artifacts/CMeowAudioKernel.xcframework`。准备脚本将 FFmpeg 四个静态库及原创 C adapter 链接为动态 `CMeowAudioKernel.framework`，使用 two-level namespace 与精确导出列表，只导出七个 `meow_audio_decoder_*` 函数。不将 FFmpeg 9 静态库暴露给 App 的视频 FFmpeg 8 符号空间。
- `--verify` 不下载、不构建；重新核对源树、当前 C/header/Package、锁/准备脚本、当前 SDK 设置/版本、编译工具身份和环境、四库/头文件、framework 文件哈希、Mach-O 架构/平台/minimum/SDK/动态依赖/外部符号以及 XCFramework 组成。
- framework 经 MeowAudio 的 SwiftPM 依赖进入 App，由 Xcode 嵌入并随 App 签名。准备回执描述未签名构建输入，不代替安装后签名验收；端侧装配和许可资源见[构建与发布](../../docs/ios/release.md)。

## C 输入与生命周期

ABI 见 `Sources/CMeowAudioKernel/include/MeowAudioKernel.h`。回调存储保持到 `destroy` 完成，C 不释放 `opaque`。除 `cancel` 外，操作由同一串行 worker 排序；所有使用结束后才能销毁。`cancel` 原子设置 interrupt，输入 owner 同时负责实际唤醒阻塞回调。取消读取实际结束后才允许 worker 调用 `resume`；owner 须先撤销其临时输入取消状态。恢复仅清除临时取消状态及 AVIO error/eof，不重置 `buf_ptr`、`buf_end`、`pos` 或丢弃缓存字节。

`read` 返回实际输出帧数，可以小于请求；0 只代表 codec 与 swr 均已 EOF 排空。输出为请求 sample rate 的双声道 interleaved float，`position` 为第一帧实际媒体时间。输出 sample rate 限于 1…384000；单个解码/转换块不超过 262144 帧，输入最多 64 声道。裸 interleaved PCM 保留被中断输入拆开的不足一个采样帧的尾字节，避免取消变成 PCM 对齐错误。负数仅为固定分类，不输出 FFmpeg 错误正文：-1 cancelled、-2 invalid media、-3 input failed、-4 unsupported、-5 out of memory。

`format_hint` 是 FFmpeg demuxer 名称（如 `mp3`、`hls`）或 NULL，不是 URL。主输入始终 custom AVIO。未知时长为 NaN；seekable 来自实际 AVIO seek 能力探测，不仅取决于有无 seek 回调。seek 先失效旧 PCM，再真实调用 demuxer seek、flush codec、清空重采样及尾字节；以实际时间戳（保留分数输出采样时间）裁剪目标前样本，缺少必要时间戳则返回 unsupported。

## 受控 HLS

默认拒绝二次打开。允许 HLS 时，输入 owner 提供改写后的主播放列表字节和 `http://127.0.0.1:<port>/<route-root>/`，注意它是以 `/` 结尾的路由根，不是主播放列表 URL。嵌套普通 URL 或 `crypto+http:`/`crypto:http:` 必须精确位于同一根下；直接 tcp、file、外部主机、其他端口、根外路径、dot traversal、转义路径、query、fragment 均拒绝。路由使用规范 ASCII opaque 路径，与现有 HLSLoopbackRoute 的改写形式对应。

可选 `primary_url` 必须位于同一路由根下，作为 custom AVIO 主清单的逻辑地址供 HLS 重载使用；它不绕过 custom AVIO 打开任意输入。普通字节输入不提供路由根或主地址。

仅编译 http/tcp/crypto，TCP 仅为已核对 HTTP URL 的内部 transport；AES128 使用 FFmpeg 自带实现。`io_open` 强制统一 interrupt、15 秒网络读超时、禁重定向/环境代理/自动重连/持久与并行请求，避免 HTTP 内部跳转绕过根边界。上游重定向、headers 与授权仍归 Swift 路由。opaque 路径不依赖媒体后缀授权。

## 验证边界

实际 SDK 编译、Mach-O／导出、App 嵌入／签名及两版 FFmpeg 共存通过 `scripts/prepare-ios-audio-kernel.py --verify` 与 `scripts/verify-native.sh` 核对；实际音频播放、路由与听感通过 `--debug` 模式在真实设备上验收，详见[独立音乐核心](../../docs/ios/music-audio-core.md)。

许可来源和适用发行义务见[来源与许可](Provenance/README.md)。
