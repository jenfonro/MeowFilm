# Windows 独立音乐核心

音乐由独立的 `MeowFilm.Audio` 工程内 `NativeAudioPlayback` 管理，直接项目依赖为 `MeowFilm.Core` 与 `MeowFilm.Threading`，托管包直接依赖为 `NAudio.Core`、`NAudio.Wasapi` 和 `FFmpeg.AutoGen`。电影由 `MeowFilm.Media` 中独立的 `FilmPlayback`／`FilmKernel` 管理。中性的 occurrence scope、continuation、read lease 与受控 HTTP 机制归 `MeowFilm.Core`；解码和输出分别归各端内核，不通过视频工程间接复用。

## 管线与依赖

`授权输入 → FFmpeg custom AVIO → 音频解码 → swresample 48 kHz 双声道 float PCM → atempo → 有界 PCM 缓冲 → NAudio EffectChain → WASAPI`

- 托管包精确锁定 `NAudio.Core`／`NAudio.Wasapi` 3.3.0 与 `FFmpeg.AutoGen` 9.0.1.1；`NAudio.Core` 的传递依赖 `System.Numerics.Tensors` 锁定为 9.0.0。这些包均为 MIT 许可。`NAudio.Effects` 在 `NAudio.Core` 内，不是独立 NuGet 包；`FFmpeg.AutoGen` 只提供托管 C API 绑定，不携带 FFmpeg 原生库。
- 音频原生库从签名校验过的 FFmpeg 9.0.1 精确源码构建；版本、来源、树哈希、编译参数、ABI、架构和工具链见 `apps/windows/native/AudioKernel/audio-kernel.lock.json`。电影的独立 FFmpeg 8.1.2 静态闭包不参与此管线。
- 原生构建禁用 GPL、version3、nonfree、网络协议、自带程序及视频 decoder；只启用受控音频解封装（与 `audio-kernel.lock.json` 一致，包含 `iff`）、解码、重采样和必要音频滤镜，内置 URL 协议仅保留 `file`。FFmpeg 日志静默回调在 `AudioNativeLibrary.EnsureLoaded()` 进程级注册一次；解码器白名单使用静态 `FrozenSet`，重采样裁剪在原 32 字节对齐缓冲区内移位以保持 SIMD 对齐。FFmpeg 共享库采用 LGPL 2.1-or-later，而非绑定包的 MIT；来源、许可正文、附加声明及工具链运行库条款统一见[许可来源说明](../../LICENSES/NOTICE.md)，源码与重链接／重构建义务不因哈希锁定而免除。共享 DLL 随 App 放入 `audio-kernel/win-x64`，不启动 ffmpeg.exe，不依赖用户安装 FFmpeg。
- 准备入口为 `scripts/windows/build-audio-kernel.ps1`。完整已验证原生产物按来源、工具链及配置身份共享；源码和可写构建目录属于本副本。运行时从应用随附的 `audio-kernel/win-x64` 目录显式加载锁定 DLL 并校验各库导出主版本号，不从 PATH 回退加载 DLL。

## 输入与安全边界

- 本地 URI 只由原生打开为只读文件租约；缓存与授权文件直接转移 `INativeMediaReadLease`，具体约束见[读取租约](media-playback.md#音乐读取租约)。FFmpeg 不重新解释路径，也不能从容器中打开外部引用。
- HTTP(S) 经受控 transport，保留头快照、重定向、Range、表示一致性、超时及固定错误分类；不形成 Cookie jar，不在 Node 中传输音频字节。
- 撤销和取消先中断读取令牌并关闭 HTTP 请求、解除读取阻塞，再等待解码与输出线程退出，最后在 `AudioDecoderInput.Dispose()` 中释放 `INativeMediaReadLease`（严禁在解码线程仍活跃时并发释放租约）；暂停不豁免授权撤销。拒绝接收的 read lease 也必须释放。
- 所有网络与租约打开／重开／交接准备／暂停操作均纳入 `Track()`／`TrackInput()` 账本；切歌在释放命令门后等待前序在途输入排空，再打开新输入。阻塞式打开、定位、探测和销毁在独立有界工作线程 `AudioWorkerQueue` 执行，不占满默认 `.NET ThreadPool`。释放失败明确返回 `MEDIA_RELEASE_FAILED`，并在所有退休资源实际排空后自动恢复可用，不永久锁死会话。
- queued lease 在实际执行前再次核对授权撤销，不能先退役健康 donor；某个释放失败不会跳过其余已登记输入的排空，也不重试失败的释放。
- 解码不接受视频轨道选择或字幕命令；未知／损坏输入明确失败，不使用旧音乐引擎回退。

## 时钟、命令与生命周期

- 网络／解码工作由专用 `MeowFilm.AudioDecode` 线程完成，WASAPI 回调只读取有界 PCM；环缓冲按 3/4 水位唤醒 producer、合并连续静音欠载区间并只单次唤醒首次渲染等待。缓冲不足时补静音并上报 `IsBuffering` 与 `UnderrunCount`，但不伪造歌曲推进；歌曲位置按设备实际时钟，扣除这些静音并乘以本段播放倍率。
- seek 或倍速变化停止旧输出与 producer，定位和 flush 解码／滤镜，预解首帧返回实际解码时间戳（无已知时长时不再误钳为 0），重新准备 PCM；不把旧块、旧效果状态或旧回调带入新位置。`NativeAudioPlayback` 对外暴露真实 `CanSeek`、`Rate`、`IsBuffering`、`BufferedSeconds`、`UnderrunCount` 与 `ActiveDeviceId`，非 Range 流禁用 seek 与变速。
- HTTP 单次读取取消在可定位流上保留字节位置以便重开，在非 Range 流上避免误毁未中断连接；解码器在重新读取前清理临时取消遗留的 `AVERROR_EXIT`，不因解复用器保守的 `AV_PKT_FLAG_CORRUPT` 标记误杀可解码帧。
- producer 的单次读取可取消；局部重配置不会永久撤销 decoder。取消未完成的重配置不自动启动输出，保留逻辑身份与已接受控制，显式 Resume 重新准备输出；完整 Stop／Dispose 则关闭输入并排空。非 Range 输入可初次播放，不支持需要重定位的 seek／倍速变更。
- 0.5–3.0 倍速中，1 倍完全旁路 `AVFilterGraph`（重采样输出的 48 kHz 双声道 float PCM 直接进入待读缓冲，零滤镜拷贝），其余倍率经单次 `Reconfigure` 拆成两个参数为倍率平方根的 atempo 滤镜，各级保持在 0.5–2.0 内以保持音高。请求／回读倍率与实际输出测量是不同证据，业务 `EffectivePlaybackRateVerified` 不因此变成 true。
- 失败恢复保留逻辑 occurrence、位置、暂停意图和倍率；单次 continuation 绑定原授权和 generation。准备换源仅静默打开并验证解码器，不提前占用 WASAPI；提交换源先冻结 donor 输出并采样最终位置，一次性定位候选并无缝切换，旧输出释放后才启动新输出。
- App 通过显式、一次性输出准入协调音乐与视频，准入等待不持有各引擎自己的命令门；获得准入后再次核对当前输入。暂停不要求取得输出准入。
- 准入保持到 render thread 实际读取 PCM 或自然停止回调解除等待；`PlaybackStopped` 在自然 EOF 与错误路径下均唤醒等待，杜绝 `PlayAsync` 与 `FinishAsync` 争夺命令门死锁。Stop、自然结束与换源退役后的状态／事件再次核验 generation，不以旧异步完成影响新曲。
- 退役期间的调用方取消在发布终态前再次核验，不被转成自然结束或输入失败。
- WASAPI 使用当前默认多媒体输出设备和共享模式，音量只作用于应用会话；会话名称与图标设置失败仅记警告不阻断播放。内置 Win32 COM `AudioEndpointMonitor`（`IMMDeviceEnumerator` + `IMMNotificationClient`）与设备时钟/停止 `COMException` 防护，监听默认设备变更、设备失效与设备恢复/新增，在默认输出热切换或设备拔插时经 120 ms 去抖原地重建 `AudioOutputSession` 保留同一解码器与播放位置（同设备重复通知跳过无意义重建）；全部输出设备移除时保留曲目并暂停上报 `MEDIA_AUDIO_DEVICE_UNAVAILABLE`，设备重新可用后自动续播。`audio.kernel` 调试租约发布不可变线程安全快照。

## DSP 与后续功能边界

输出前经过实际 `EffectChain`，处理的是浮点 PCM，而不是对播放器 URL 设置音效选项。现行默认链不添加音效，不改变原音；新增 EQ、混响、声像或宽度控件时可在此边界接入 Core 的效果，并在音频块边界更新参数。分析／FFT 也应从此 PCM 边界读取，不重新打开媒体。

本次不提供新音效界面、HRTF／对象音频渲染、独占输出设置、相邻曲目无缝或交叉淡入淡出。立体声宽度效果不等于 Atmos／DTS:X 解码。

## 缓存与验证

`MusicAudioFileCache` 的完整文件 producer 使用 Core 的 `NativeHttpMediaTransfer.CopyCompleteAsync`，底层复用 `FFmpegHttpTransport` 私有 Reader 的同一打开、读取、Range 与总长度一致性机制。它不持有原生播放器、解码器或输出；拒绝初始／重定向自适应路径、HLS／DASH／SmoothStreaming Content-Type 及前 512 字节中的 manifest 标记，再按已识别的普通容器前缀顺序复制。服务器返回截短 `206` 分段时继续核对后续范围与总长度，允许重定向（默认上限 10 次）并保留调用方请求头快照（自动跳过传输自管头）；总长度变化按表示变化拒绝，不拼接不同对象。已知长度须完整匹配，未知长度读取到实际 EOF，空内容、截断或超出调用方字节预算明确失败。初始打开与完整探测共用 RequestTimeout，正文的一次 ReadTimeout 同时覆盖续段请求和读取。边下边播的文件读写使用无冗余托管缓冲的异步 `FileStream` 并复用 linked cancellation，打开长度和连续写入进度只作为文件 producer 回执，不表示已完成音频校验。

`NativeAudioCacheProbe` 仅在 `AudioWorkerQueue` 创建 FFmpeg 解码器，全程传递 10 秒超时令牌至 `Open`/`Seek`/`Read`，检查实际时长并定位末尾静音解码至 EOF；不创建 WASAPI 或 SMTC。本地缓存命中打开仅校验 O(1) 文件长度一致性（与 iOS 端保持一致，不再每次播放全文件重算 SHA-256 阻塞元数据门），若本地文件损坏则由 FFmpeg 解码报错触发恢复路径自动清理并重取；文件容器完整性和预期大小检查仍归既有缓存 owner，HTTP 完整复制与实际音频校验不能互相替代。

真实解码、WASAPI 输出与业务流程统一通过正常构建产物的 `--debug` 模式实跑验收（见[调试模式契约](../common/debug-mode.md)）。验收结果与运行证据保留在任务记录，不将编译成功描述为听感验收。
