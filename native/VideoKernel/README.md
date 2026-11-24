# iOS 原生视频内核 (`VideoKernel`)

`MeowVideoKernel` 是 iOS 侧自有精简视频播放内核，直接构建于锁定源码的静态 FFmpeg 8.1.2 与 Apple 原生媒体管线之上，不依赖第三方播放器封装，也不拥有业务会话或系统媒体控制权。

## 核心架构

- **管线结构**：`DemuxThread → 有界 PacketQueue（未选流统一 AVDISCARD_ALL）→ VideoDecodeThread（异步 VideoToolbox + B 帧重排 + EOF 排空 + FFmpeg 软件 NV12/P010 回退）& AudioDecodeThread（FFmpeg + swresample → 无锁 SPSC 环形缓冲 → AVAudioEngine + AVAudioUnitTimePitch）& 按需单轨 SubtitleEngine → PresentLoop（MasterClock）`。
- **显示管线**：统一使用 `AVSampleBufferDisplayLayer` 输出 8-bit SDR 与 10-bit HDR10 / HLG / Dolby Vision 画面，硬件旋转走图层变换，无 Metal 主线程阻塞与后台 GPU SIGKILL 风险。
- **无 Seek 命令与容错**：倍速调节（0.25x–8x）与文本字幕开关/切换均免 Seek；禁用 `AV_EF_EXPLODE`，不注入默认 User-Agent，打开完成后立即擦除 ClearKey 与敏感请求头；支持轨道、字幕、章节、缓冲进度、显示分辨率及脱敏诊断快照。

## 锁定准备

```sh
python3 -I -B scripts/prepare-ios-video-kernel.py --target ios-arm64
python3 -I -B scripts/prepare-ios-video-kernel.py --target ios-simulator-arm64
python3 -I -B scripts/prepare-ios-video-kernel.py --target macos-arm64
python3 -I -B scripts/prepare-ios-video-kernel.py --target ios-arm64 --verify
```

`--target` 可重复指定。脚本验证固定 FFmpeg 8.1.2 源码与 `patches/ffmpeg-native-output.patch`，构建对应目标的静态 XCFrameworks 并输出到 `native/VideoKernel/.cache/Artifacts/`。共用下载与已验证产物按[依赖缓存](../../docs/common/dependency-cache.md)复用。来源与许可记录见 [Provenance](Provenance/README.md) 与仓库根 `LICENSES/`。
