# 独立音乐核心

歌曲、取址、账号、引用队列、歌词和缓存策略仍归既有原生音乐 owner；共同业务语义见[播放来源与自动续播](../common/music/playback-sessions.md)。系统音频会话、后台声明和唯一 NowPlaying／远程命令 owner 见[系统媒体控制](system-media.md)。

## 依赖与数据流

音乐解码依赖链为 `MeowMusicFeature → MeowPlayback → MeowAudio → CMeowAudioKernel`；音乐目标的依赖闭包不包含 `MeowPlaybackVideo` 或 `MeowVideoKernel`。应用显式注入 `makePlaybackVideoDecoder`，视频经 `MeowPlaybackVideo → MeowVideoKernel → KSPlayer` 使用现行核心，不共享音乐解码器或输出图。

音乐播放链为：

```text
受控本地文件 / PlaybackByteSource / HTTP / scoped HLS
→ custom AVIO → FFmpeg 音频解码与 swresample
→ 48 kHz 双声道 float PCM
→ AVAudioPlayerNode → AVAudioUnitTimePitch → AVAudioEngine mixer / 系统输出
```

`AudioDecoder` 是独立串行 worker；网络与解码不在主线程或实时 render callback 上执行。`AudioDecoder` 每次从 C 读取最多 4096 帧，输出队列至多八块 PCM，另保留一个 native 解码块。TimePitch 在 0.5…3 倍速度内保留音高；媒体进度取实际源 PCM 的渲染 sample time，不以墙钟伪造播放。音量属于原生输出图。

`native/AudioKernel` 按[构建与发布](release.md)锁定 FFmpeg 9.0.1 音频闭包。四个静态库在独立动态 framework 内隐藏上游符号，仅导出项目 C ABI；与现行视频 FFmpeg 8.1.2 共存，不升级视频来规避符号问题。没有 NAudio／WASAPI、AVPlayer 音乐回退或第二套音乐 decoder。

本次不增加 EQ、混响、空间效果或可视化界面，也不新增交叉淡入、无缝切歌或独占／直通输出。原生 PCM 与 AudioUnit 图已属于音乐自身。

## 输入和准备

- 本地文件在 decoder worker 上读取；业务提供的 `PlaybackByteSource` 保持原顺序下载／读取租约语义。seek 只改变本端读取位置，不移动业务下载位置。
- Headerless HTTP 保留 200、未知长度和非 Range 播放支持；只有实际响应提供 Range 能力才允许 seek。HTTP 读取有界回压，重定向、headers、凭据和资源表示一致性由原生政策核对。
- 显式 progressive 或携带请求头的非 HLS 输入使用已有受控分窗 loader。HLS 使用已有改写路由；主清单有界读取，嵌套请求只允许同一 listener／route 根下的规范地址，禁止外部、文件、跨根和重定向旁路。
- 准备只打开输入、解码信息及第一块 PCM；静音候选、暂停准备和缓存探测都不创建 AVAudioEngine 输出。音频准入成功并且当前播放意图仍有效，才创建并启动输出图。
- 定位能力由当前 decoder 的 seekable 与实际 duration 共同确认。准备前可排队；准备后确认不支持定位时，以 `seekFailed` 单次失败并释放当前媒体，不向 decoder 派发 seek。当前生产只在准备完成时取得时长，仍未知的可定位媒体按既有预算超时，不合成时间范围或声称具备后续时长订阅；late-duration 状态机测试只验证策略。
- `PlaybackPreparedReplacement` 保留独立无输出 decoder。定位以真实时间戳裁剪后的首帧为准；交接时长、15 秒预算和 0.25 秒差值政策见[音源管理](music-sources.md)，不是另建策略。
- 输入与 decoder 的失败统一由当前 `PlaybackAudioMedia` 记录；准备／同步／最终提交检查同一错误事实，提交后由该媒体回调当前 controller。

## 取消、恢复与释放

decoder 操作与输入撤权由同一锁核对：取消尚未执行的请求不打断当前执行者，旧取消不能晚到下一次已恢复读取。临时取消实际退出后清除 AVIO 的 error／eof，不丢弃已保留字节。seek 先撤销旧输出 callback、取消并等待 read pump，再定位及准备新 PCM。

输出 callback、准备、seek 和交接均受当前 attempt／generation 围栏保护；旧完成不能发布新曲状态。已消费但取消未交付的 PCM 至多保留一块，普通 read 连续恢复，seek／close 清除。EOF 要求 codec、重采样器及输出队列都排空，自然结束只交付一次，失败不会伪装为自然结束。

输出配置重建从实际渲染位置保留并重排尚未消费的 PCM，不重开非 Range 输入。系统中断、路由与媒体服务恢复仍由共享系统会话决定是否恢复，音乐核心不替业务授予恢复资格。

输出节点的 sample clock 按每块实际调度的 PCM 区间映射回媒体时间；欠载期间的静音空转不计入音乐进度，也不能令恢复后的 PCM 被误认为已经渲染。

Stop 先同步撤销输入及输出；close 等待 decoder worker、已登记读取、HTTP／HLS 连接和输入 owner 的实际关闭。协调器在音乐／视频交接前等待退休 owner 排空，不把 Task 提交或固定等待时间当作释放完成。

## 缓存和验证

缓存检查保持已有源结构、源位深、编码包和预期时长政策，用同一 FFmpeg decoder 解码至实际 EOF；源帧数在重采样前实际计数，不从 48 kHz 输出反向舍入。不创建输出，不将 float PCM 冒充原文件音质。

脚本检查核对精确来源、目标 ABI、隐藏导出、平台、framework 哈希和许可资源；Swift 回归分别覆盖真实合成音频解码、静音准备、取消／seek／EOF、HTTP 表示一致性、业务交接及系统会话。Mac 主机、模拟器、设备 SDK 编译与真实设备输出结果分别记录；替身、编译和模拟器不证明真机听感或真实蓝牙／AirPlay。
