# 固定来源与适配边界

完整播放器源码、来源映射及可审核补丁由 `native/KSPlayer` 持有，固定 kingslay/KSPlayer `d0d063ab1f045735129b6617591ad2148d88d5db`。本目录的 `MeowVideoKernel` 只拥有电影产品的静默候选、输出确认、播放命令和资源排空适配，不再拥有独立 FFmpeg C decoder／sample-buffer engine。

FFmpeg 8.1.2 固定 `38b88335f99e76ed89ff3c93f877fdefce736c13`，使用 `patches/ffmpeg-native-output.patch` 对合法 ClearKey、响应 Cookie 隔离与 HLS／DASH 子请求错误传播的同源修正。许可正文统一见 `LICENSES`，准确来源、构建输入和校验由 `scripts/ios-video-kernel.json` 及 KSPlayer 来源清单记录。

本包不提供商业 CDM／license HTTP 客户端，不将任意 profile／增强层或外部显示模式未经实测写成已验证能力。
