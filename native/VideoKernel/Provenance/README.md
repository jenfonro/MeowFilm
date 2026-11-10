# 固定来源与改编

- FFmpeg n8.1.2：`38b88335f99e76ed89ff3c93f877fdefce736c13`，
  https://github.com/FFmpeg/FFmpeg/tree/38b88335f99e76ed89ff3c93f877fdefce736c13 。
  使用公共 avformat/avcodec/avutil/swresample/swscale API；私有结构只在
  同源受控补丁内修改，不链接旧 FFmpegKit shim。
- KSPlayer：`d0d063ab1f045735129b6617591ad2148d88d5db`，
  https://github.com/kingslay/KSPlayer/tree/d0d063ab1f045735129b6617591ad2148d88d5db 。
  本地解码／重采样／像素输出与阻塞队列机制参考并改编其
  MEPlayer/FFmpegDecode.swift、Resample.swift、CircularBuffer.swift、
  AudioRendererPlayer.swift。版权：kintan/kingslay 及原上游贡献者。
  移植不含 KS UI、AVPlayer 回退、音频会话注册、CADisplayLink、私有 ABI。
  改编部分按项目 GPL-3.0-only 使用；唯一许可正文位于仓库根 `LICENSES`。
  上游 LICENSE SHA256 为 `8ceb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903`；
  prepare 按固定 archive 原文验证，不依赖正文去重后的排版。

实现差异：FFmpeg 8 公共 ABI 薄 C 适配，真实 PTS、统一音视频时钟，
静默解码准备与正式输出分离、显式取消／排空、默认禁止 FFmpeg 原始日志。
10bit 不下转换为 8bit；VideoToolbox 实际 session 属性与软件输出分别记录。
本包不提供商业 CDM 或任意 license HTTP 客户端。

同源补丁仅查询实际 VT session 硬解状态并允许原有软件 fallback，
以及在同轨 sample KID 改变时清除 FFmpeg 原 cipher 缓存；多 KID 查表与
CENC/CBCS 解密仍使用 FFmpeg 原实现，不自造 AES 或商业 DRM 路径。
此补丁不实现 `sgpd/seig` 解析；不能据此声称任意同轨 KID 轮换可用。

HLS／DASH 同源补丁在实际子 AVIO／demux 边界保留终止错误，不跳过失败
分片或把断线转换为正常 EOF；投机预取失败后同资源读取恢复成功不锁存失败。
部分读取同时留下 AVIO error 时先传递真实错误，不让解析器把截断数据当成功；
HLS 重新开始已探测输入／实际 seek 时同步清除旧 error，不污染新读取。
HLS 原有同分片重试仍按上游设置执行，不新增重试配置或错误历史表。
DASH 的实际 libxml2 入口局部使用 NOERROR／NOWARNING，禁止输出原始
媒体地址与 XML 行；不安装全进程 XML handler 或重定向产品 stderr。
