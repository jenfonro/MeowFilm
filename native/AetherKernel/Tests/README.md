# 独立 Aether 真实输出测试夹具

## 输入与路由

- `Fixtures/HLS/rate-audio-video.m3u8`：24 秒 H.264/AAC 有限 VOD。
- `Fixtures/HLS/rate-video-only.m3u8`：24 秒 H.264 无音轨有限 VOD。
- 每份 playlist 有六个 4 秒 MPEG-TS segment，首帧为关键帧，带
  `#EXT-X-PLAYLIST-TYPE:VOD`、`#EXT-X-INDEPENDENT-SEGMENTS` 与 `#EXT-X-ENDLIST`。
  从既有 VideoKernel 公开合成 MP4 直接 remux，不重新编码。
- `Fixtures/software-mpeg4-video.mkv`：24 秒、96×64、10 fps、MPEG-4 Part 2，
  单视频轨。锁定 FFmpegBuild 3.15.0 的 `build.sh` 启用 `mpeg4` 解码器；
  Aether 的正常 native codec allowlist 不包含它，因此走 software。
- `Fixtures/software-video.mkv`：保留的 12 秒、48×32、5 fps FFV1；文件字节未改。
  锁定 build 不含 FFV1 解码器，用于 `.unsupportedFormat` 回归，不再作为成功播放输入。

Native 用 `AetherFixtureHTTPServer` 绑定 `127.0.0.1` 的临时 TCP 端口，仅将指定
playlist 与其六个 segment 的字节放入精确路径白名单。正常 `prepare` 接收实际
HTTP `.m3u8` URL 和 `format: "hls"`，走 `nativeRemoteHLS`；真实首帧之后同时核对
playlist/TS 请求和 native playerLayer。没有将 MP4 伪报为 HLS，没有测试选路 override。
服务器支持 GET/HEAD 与合法单 byte range；不暴露目录、MKV、来源 MP4、任意路径、
URL 解码、重定向或磁盘 fallback。

关闭等待 listener 与所有已接收 NWConnection 的实际 `.cancelled` 回调后才完成，
不以清空引用代替排空。额外回归以实际等待请求字节的 open TCP connection 核对排空。
XCTest teardown 先关闭 session/window，再停 origin；播放请求失败与未授权路径不会静默放行。

## 可复现生成与本地核验

仅使用已有 Linux arm64 FFmpeg 7.0.2-static，不下载或更改 Apple 开发环境：

```sh
python3 -I -B native/AetherKernel/Tests/Tools/make-fixtures.py \
  --ffmpeg ~/Developer/tools/ffmpeg-7.0.2-arm64-static/ffmpeg
python3 -I -B native/AetherKernel/Tests/Tools/verify-fixtures.py \
  --ffprobe ~/Developer/tools/ffmpeg-7.0.2-arm64-static/ffprobe
```

完整逐项命令、命令 cwd、两个原始 MP4 的 SHA-256、工具 SHA-256 和全部 16 个媒体文件
的精确 SHA-256 在 `Fixtures/manifest.json`。
`make-fixtures.py` 不重建或写入 VideoKernel 来源，不重新生成保留的 FFV1。
该旧文件原配方为 FFmpeg 7.0.2 对公开 `rate-video-only.mp4` 使用
`-t 12 -map 0:v:0 -map_metadata -1 -map_chapters -1 -vf fps=5,scale=48:32
-c:v ffv1 -level 3 -threads 1 -pix_fmt yuv420p`；原 MKV 非 bitexact，重新生成不承诺相同 UID/哈希。

`verify-fixtures.py` 核对哈希、TS 包同步字节/包长、容器、编码、有限时长、
十二个 segment 的连续视频时间和首帧关键帧。夹具核验不代替 Apple 实际播放测试。

## Apple 输出回归

全部位于 `MeowAetherKernelTests.VideoSessionTests`：

```text
testNativeAudioVideoHasRealSilentFirstFrame
testNativeVideoOnlyHasRealSilentFirstFrame
testNativeExternalTransportIntentIsDistinctFromHostCommands
testSoftwareVideoOnlyHasRealSilentFirstFrame
testNativeAudioVideoCommitClampsToTwoXAndHonorsSupportedRatesAndPause
testNativeVideoOnlyCommitClampsToTwoXAndHonorsSupportedRatesAndPause
testSoftwareVideoOnlyCommitClampsToTwoXAndPauses
testSeekChangesTheRealPausedPictureAndResumesBothRoutes
testCloseDrainsCommittedOutputAndRejectsLateCallbacksBothRoutes
testCancelStopsCommittedOutputAndCloseDrainsBothRoutes
testCloseDuringPreparationCancelsAndDrainsWithoutLateCallbacks
testTaskCancellationDuringPreparationDrainsOnCloseWithoutLateCallbacks
testClearKeyIsTypedUnsupportedBeforeAnyOutput
testFFV1IsTypedUnsupportedBeforeAnyOutput
testHLSOriginRejectsUnlistedFilesAndDrainsAnOpenConnection
```

需要真实前台图形会话/window 和 `displayedPixelBuffer()`；没有 skip 或假前台事件。
AVPlayerLayer 的该接口只在暂停时提供像素；倍率测试在计时区间之外暂停取图，
恢复并完成实际播放 warmup 后才配对媒体时间与墙钟，计时区间内不暂停。
倍率以实际媒体时间/单调墙钟及真实画面推进核对，seek 继续断言返回位置、实际画面改变、
暂停画面稳定与恢复播放。取消、并发关闭、重复关闭、无迟到回调断言保留。
FFV1 typed 判定使用 `mediaCapabilityUnsupported` /
`VideoDecoderError.unsupportedCodec` 映射。夹具通过 `#filePath` 查找；
Mac 工作副本必须包含同一组输入，不要求修改 Package 或资源清单。
