# VideoKernel Provenance

`native/VideoKernel` 仅包含 Meowfilm 自有 Swift/C 播放内核与锁定 FFmpeg 8.1.2 静态依赖：

- **FFmpeg 8.1.2**：固定 commit `38b88335f99e76ed89ff3c93f877fdefce736c13`，应用唯一本地补丁 `patches/ffmpeg-native-output.patch`（涵盖 VideoToolbox 硬解启用标志、CENC 多密钥缓存与防优化擦除、HLS/DASH 子段错误传播与空指针防护、以及 HTTP `Set-Cookie` 隔离）。
- **Apple 系统框架**：使用系统 SDK 自带 `libxml2`、`zlib`、`bzip2`、`libiconv`、`AVFoundation`、`CoreMedia`、`CoreVideo`、`VideoToolbox`、`AudioToolbox`、`QuartzCore` 与 `Security`。

所有许可正文统一保存在仓库根 `LICENSES/`，准确校验和与构建选项由 `scripts/ios-video-kernel.json` 锁定。
