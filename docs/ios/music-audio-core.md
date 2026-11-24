# 独立音乐核心

歌曲、取址、账号、引用队列、歌词和缓存策略仍归既有原生音乐 owner；共同业务语义见[播放来源与自动续播](../common/music/playback-sessions.md)。系统音频会话、后台声明和唯一 NowPlaying／远程命令 owner 见[系统媒体控制](system-media.md)。

## 依赖与数据流

音乐解码依赖链为 `MeowMusicFeature → MeowPlayback → MeowAudio → CMeowAudioKernel`；音乐目标的依赖闭包不包含 `MeowPlaybackVideo` 或 `MeowVideoKernel`。应用显式注入 `makePlaybackVideoDecoder`，视频经 `MeowPlaybackVideo → MeowVideoKernel`（或可选 `MeowAetherKernel`）使用现行核心，不共享音乐解码器或输出图。

音乐播放链为：

```text
受控本地文件 / PlaybackByteSource / HTTP / scoped HLS
→ custom AVIO → FFmpeg 音频解码与 swresample
→ 48 kHz 双声道 float PCM
→ AVAudioPlayerNode → AVAudioUnitTimePitch → AVAudioEngine mixer / 系统输出
```

`AudioDecoder` 是独立串行 worker，AVIO 块大小为 64 KiB，网络与解码不在主线程或实时 render callback 上执行；FFmpeg 日志回调经 `pthread_once` 进程级初始化一次。C ABI `meow_audio_decoder_read` 直接输出 48 kHz 非交织双声道 float PCM（planar `left`/`right`，缓存校验可传空指针零分配计数），由后台 `AudioOutputPipeline`（24 块 FIFO、4096 帧块上限）向 `AVAudioPlayerNode` 喂流。TimePitch 在 0.5…3 倍速度内保留音高；媒体进度由 4 Hz 采样器取实际源 PCM 的渲染 sample time，不以墙钟伪造播放。音量属于原生输出图。

`native/AudioKernel` 按[构建与发布](release.md)锁定 FFmpeg 9.0.1 音频闭包。四个静态库在独立动态 framework 内隐藏上游符号，仅导出项目 C ABI；与现行视频 FFmpeg 8.1.2 共存，不升级视频来规避符号问题。没有 NAudio／WASAPI、AVPlayer 音乐回退或第二套音乐 decoder。

本次不增加 EQ、混响、空间效果或可视化界面，也不新增交叉淡入、无缝切歌或独占／直通输出。原生 PCM 与 AudioUnit 图已属于音乐自身。

## 输入和准备

- 本地文件在 decoder worker 上读取；业务提供的 `PlaybackByteSource` 保持原顺序下载／读取租约语义。seek 只改变本端读取位置，不移动业务下载位置。
- 所有非 HLS HTTP(S) 音频（无论是否携带请求头）统一经单一流式 `PlaybackHTTPStreamInput` 读取，不再保留逐块 Range 双轨。支持开放式 Range `206`、CDN 分块子范围 `206`（段尾自动续拉）及 `200 OK` 顺序流，采用 O(1) 分块 FIFO 队列、缓冲命中快速 seek、单缓冲水位回压（256 KiB 暂停 / 128 KiB 恢复）、总长度一致性校验与惰性 `URLSession` 初始化；只有真实 `206` 响应允许 seek。请求头快照（含 `Referer` 等自定义头）与标准 Basic userinfo 透传到根、重定向与子请求，限制最大跳转次数（10 次），不增加跨源剥头、域名白名单、内网 IP 封禁或全局字节配额。HLS 使用本地环回改写路由，将清单中的分片与密钥映射到本地回环并保留请求头，嵌套打开限制在当前路由根下。
- 准备只打开输入、解码信息及第一块 PCM；静音候选、暂停准备和缓存探测都不创建 AVAudioEngine 输出。音频准入成功并且当前播放意图仍有效，才创建并启动输出图。
- 定位能力由当前 decoder 的 seekable 与实际 duration 共同确认。准备前可排队；准备后确认不支持定位时，以 `seekFailed` 单次失败并释放当前媒体，不向 decoder 派发 seek。当前生产只在准备完成时取得时长，仍未知的可定位媒体按既有预算超时，不合成时间范围或声称具备后续时长订阅。
- `PlaybackPreparedReplacement` 保留独立无输出 decoder。定位以真实时间戳裁剪后的首帧为准；交接时长、15 秒预算和 0.25 秒差值政策见[音源管理](music-sources.md)，不是另建策略。
- 输入与 decoder 的失败统一由当前 `PlaybackAudioMedia` 记录；准备／同步／最终提交检查同一错误事实，提交后由该媒体回调当前 controller。

## 取消、恢复与释放

decoder 操作与输入撤权由同一锁核对：取消尚未执行的请求不打断当前执行者，旧取消不能晚到下一次已恢复读取。临时取消实际退出后清除 AVIO 的 error／eof，不丢弃已保留字节。seek 先撤销旧输出 callback、取消并等待 read pump，再定位及准备新 PCM。

输出 callback、准备、seek、配置重建和交接均受单调递增的 `sessionRevision` 与 `attempt` 围栏保护；旧完成不能发布新曲状态，暂停态不会由已排缓冲误报自然结束，失败不再先发伪 buffering。已消费但取消未交付的 PCM 至多保留一块，普通 read 连续恢复，seek／close 清除。EOF 要求主 AVIO 与 HLS 嵌套分片 AVIO 均无传输错误，且 codec、重采样器及输出队列都排空，自然结束只交付一次，失败不会伪装为自然结束。

输出配置重建经 100 ms 去抖，跳过中断态并从实际渲染位置保留并重排尚未消费的 PCM，不重开非 Range 输入；`setPreferredRate` 经 `drivePlayback()` 统一遵守音频激活与输出准入，`cancelSeek` 标记解码器位置脏态以便恢复时重同步。系统中断与媒体服务重置不再调用 `setActive(false)`，由系统会话仅标记失活并决定是否恢复。

输出节点的 sample clock 按每块实际调度的 PCM 区间映射回媒体时间；欠载期间的静音空转不计入音乐进度，也不能令恢复后的 PCM 被误认为已经渲染。

Stop 先同步撤销输入及输出；`AudioDecoder.close()` 严格串行化为 `cancelReads → 串行队列执行 meow_audio_decoder_destroy → input.close()`，消除并发销毁与句柄关闭竞态。播放链路所有对象统一使用 `nonisolated deinit` 与显式主线程生命周期容器，不依赖 `isolated deinit`。协调器在音乐／视频交接前等待退休 owner 排空，已排空的单次释放失败可自动清理而不永久锁死会话。

## 缓存和验证

缓存检查保持已有源结构、源位深、编码包和预期时长政策，用同一 FFmpeg decoder 解码至实际 EOF；源帧数在重采样前实际计数，不从 48 kHz 输出反向舍入。不创建输出，不将 float PCM 冒充原文件音质。

脚本检查核对精确来源、目标 ABI、隐藏导出、平台、framework 哈希和许可资源；解码、静音准备、取消／seek／EOF、HTTP 表示一致性、业务交接及系统会话统一在正常构建产物的 `--debug` 模式中实跑验证。Mac 主机、模拟器、设备 SDK 编译与真实设备输出结果分别记录；编译和模拟器不证明真机听感或真实蓝牙／AirPlay。
