# Windows 独立音乐核心

音乐由独立的 `MeowFilm.Audio` 工程内 `NativeAudioPlayback` 管理，直接项目依赖只有 `MeowFilm.Core`，托管包直接依赖为 `NAudio.Core`、`NAudio.Wasapi` 和 `FFmpeg.AutoGen`。电影由 `MeowFilm.Media` 中独立的 `FilmPlayback`／`FilmKernel` 管理。中性的 occurrence scope、continuation、read lease 与受控 HTTP 机制归 `MeowFilm.Core`；解码和输出分别归各端内核，不通过视频工程间接复用。

## 管线与依赖

`授权输入 → FFmpeg custom AVIO → 音频解码 → swresample 48 kHz 双声道 float PCM → atempo → 有界 PCM 缓冲 → NAudio EffectChain → WASAPI`

- 托管包精确锁定 `NAudio.Core`／`NAudio.Wasapi` 3.3.0 与 `FFmpeg.AutoGen` 9.0.1.1；`NAudio.Core` 的传递依赖 `System.Numerics.Tensors` 锁定为 9.0.0。这些包均为 MIT 许可。`NAudio.Effects` 在 `NAudio.Core` 内，不是独立 NuGet 包；`FFmpeg.AutoGen` 只提供托管 C API 绑定，不携带 FFmpeg 原生库。
- 音频原生库从签名校验过的 FFmpeg 9.0.1 精确源码构建；版本、来源、树哈希、编译参数、ABI、架构和工具链见 `apps/windows/native/AudioKernel/audio-kernel.lock.json`。电影的独立 FFmpeg 8.1.2 静态闭包不参与此管线。
- 原生构建禁用 GPL、version3、nonfree、网络协议、自带程序及视频 decoder；只启用受控音频解封装、解码、重采样和必要音频滤镜，内置 URL 协议仅保留 `file`。FFmpeg 共享库采用 LGPL 2.1-or-later，而非绑定包的 MIT；来源、许可正文、附加声明及工具链运行库条款统一见[许可来源说明](../../LICENSES/NOTICE.md)，源码与重链接／重构建义务不因哈希锁定而免除。共享 DLL 随 App 放入 `audio-kernel/win-x64`，不启动 ffmpeg.exe，不依赖用户安装 FFmpeg。
- 准备入口为 `scripts/windows/build-audio-kernel.ps1`。完整已验证原生产物按来源、工具链及配置身份共享；源码和可写构建目录属于本副本。加载前核对产物、回执、PE 架构、库版本／构建配置和哈希，不从 PATH 回退加载 DLL。

## 输入与安全边界

- 本地 URI 只由原生打开为只读文件租约；缓存与授权文件直接转移 `INativeMediaReadLease`，具体约束见[读取租约](media-playback.md#音乐读取租约)。FFmpeg 不重新解释路径，也不能从容器中打开外部引用。
- HTTP(S) 经受控 transport，保留头快照、重定向、Range、表示一致性、超时及固定错误分类；不形成 Cookie jar，不在 Node 中传输音频字节。
- 撤销和取消先关闭输入、解除读取阻塞，再等待解码与输出线程退出；暂停不豁免授权撤销。拒绝接收的 read lease 也必须释放。
- read lease 在排入 dispatcher 前登记归属；尚未执行的请求取消后直接释放，Stop／Dispose 等待其物理释放回执。释放失败明确返回 `MEDIA_RELEASE_FAILED`，不作为完成排空或允许重新接收输入。
- queued lease 在实际执行前再次核对授权撤销，不能先退役健康 donor；某个释放失败不会跳过其余已登记输入的排空，也不重试失败的释放。
- 解码不接受视频轨道选择或字幕命令；未知／损坏输入明确失败，不使用旧音乐引擎回退。

## 时钟、命令与生命周期

- 网络／解码工作由 producer 完成，WASAPI 回调只读取有界 PCM。缓冲不足时补静音但不伪造歌曲推进；歌曲位置按设备实际时钟，扣除这些静音并乘以本段播放倍率。
- seek 或倍速变化停止旧输出与 producer，定位和 flush 解码／滤镜，重新准备 PCM；不把旧块、旧效果状态或旧回调带入新位置。
- HTTP 单次读取取消后保留已交给 AVIO 的字节位置；即使 FFmpeg 在自己的缓存内定位而不调用 seek callback，后续读取也在该位置恢复同一表示。
- producer 的单次读取可取消；局部重配置不会永久撤销 decoder。取消未完成的重配置不自动启动输出，保留逻辑身份与已接受控制，显式 Resume 重新准备输出；完整 Stop／Dispose 则关闭输入并排空。非 Range 输入可初次播放，不支持需要重定位的 seek／倍速变更。
- 0.5–3.0 倍速中，1 倍不添加 atempo，其余倍率均拆成两个参数为倍率平方根的 atempo 滤镜，各级保持在 0.5–2.0 内以保持音高。请求／回读倍率与实际输出测量是不同证据，业务 `EffectivePlaybackRateVerified` 不因此变成 true。
- 失败恢复保留逻辑 occurrence、位置、暂停意图和倍率；单次 continuation 绑定原授权和 generation。准备／提交／放弃换源仍归音乐 owner，旧流在候选准备期间继续播放，提交前按 donor 最新位置准备，旧输出释放后才启动新输出。
- App 通过显式、一次性输出准入协调音乐与视频，准入等待不持有各引擎自己的命令门；获得准入后再次核对当前输入。暂停不要求取得输出准入。
- 准入保持到 render thread 实际读取 PCM；Stop、自然结束与换源退役后的状态／事件再次核验 generation，不以旧异步完成影响新曲。
- 退役期间的调用方取消在发布终态前再次核验，不被转成自然结束或输入失败。
- WASAPI 使用当前默认多媒体输出设备和共享模式，音量只作用于应用会话；会话名称与图标显式使用 meowfilm。此名称不是 SMTC 的曲目标题。

## DSP 与后续功能边界

输出前经过实际 `EffectChain`，处理的是浮点 PCM，而不是对播放器 URL 设置音效选项。现行默认链不添加音效，不改变原音；新增 EQ、混响、声像或宽度控件时可在此边界接入 Core 的效果，并在音频块边界更新参数。分析／FFT 也应从此 PCM 边界读取，不重新打开媒体。

本次不提供新音效界面、HRTF／对象音频渲染、独占输出设置、相邻曲目无缝或交叉淡入淡出。立体声宽度效果不等于 Atmos／DTS:X 解码。

## 缓存与验证

`MusicAudioFileCache` 的完整文件 producer 使用 Core 的 `NativeHttpMediaTransfer.CopyCompleteAsync`，底层复用 `FFmpegHttpTransport` 私有 Reader 的同一打开、读取、Range 与表示身份机制。它不持有原生播放器、解码器或输出；拒绝初始／重定向自适应路径、HLS／DASH／SmoothStreaming Content-Type 及前 512 字节中的 manifest 标记，再按已识别的普通容器前缀顺序复制。服务器截短的 `206` 分段须有 strong ETag，后续范围、已绑定的 strong ETag 和总长度必须一致；续段重定向、validator 缺失或变更均按表示变化拒绝，不拼接不同对象。已知长度须完整匹配，未知长度读取到实际 EOF，空内容、截断或超出调用方字节预算明确失败。初始打开与完整探测共用 RequestTimeout，正文的一次 ReadTimeout 同时覆盖续段请求和读取；无法发送的声明 header 在请求前拒绝。打开长度和连续写入进度只作为文件 producer 回执，不表示已完成音频校验。

`NativeAudioCacheProbe` 仅创建 FFmpeg 解码器，检查实际时长并定位末尾静音解码至 EOF；不创建 WASAPI 或 SMTC。文件容器完整性和预期大小检查仍归既有缓存 owner，HTTP 完整复制与实际音频校验不能互相替代。

真实 decoder、生命周期替身、真实 WASAPI 和业务回归分别验证。`NativeAudioOutput.Dummy` 仅用于确定性 no-device 测试，不证明真实扬声器出声或设备时钟。测试结果与本次运行证据保留在任务记录，不将编译成功描述为听感验收。
