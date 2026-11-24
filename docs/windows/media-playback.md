# Windows 原生媒体实现

音乐由独立的 `MeowFilm.Audio` 持有；中性输入与作用域契约归 `MeowFilm.Core`。`MeowFilm.Media` 包含电影专属 `FilmPlayback`／`FilmKernel` 和原生视频输入、资源租约适配。电影不借用音乐的播放 owner、PCM 或时钟；原生音乐与 Node film 仍分别拥有业务选择、授权资源和恢复状态。共同电影输入与生命周期规则见[共同契约](../common/film/playback.md)，本文只说明 Windows 实现。

## Core 输入机制

### 受控 HTTP

- `MeowFilm.Core` 内部的 `FFmpegHttpTransport` 为音乐和电影 custom AVIO 提供 HTTP(S) 输入；其私有 Reader 负责实际打开、读取、Range 和表示身份校验，不创建媒体转发服务。
- `NativeHttpMediaOptions` 管理请求／读取期限和重定向预算，不设显式请求头条数／字节或自定义响应头配额（底层 `SocketsHttpHandler` 保留 .NET 默认 64 KiB 响应头缓冲框架边界）。标准 HTTP(S) URL 的 userinfo 按百分号解码后用于 Basic Authorization，无密码时使用空密码；显式 Authorization 请求头优先。每次重定向／子请求按其实际 URL 处理，不建立额外凭据或 Origin 许可表。
- 只有真实、已校验的 `206` 响应证明随机访问能力，单独的 `Accept-Ranges` 不够；后续读取／seek 继续核对实际范围、总长度及已取得的 validator，表示改变或截断明确失败。非 Range 输入可顺序读取，不虚构 seek 能力。
- Core 的公开 `NativeHttpMediaTransfer.CopyCompleteAsync` 复用同一 Reader，供[音乐完整文件 producer](music-audio-core.md#缓存与验证)使用，不创建原生解码器或输出。`PlaybackException` 只公开固定 `PlaybackError` 代码，不包含输入地址、头或响应正文。

### 音乐读取租约

- Core 的 `INativeMediaReadLease` 表达已授权的只读、可 seek 字节表示，不是路径、URL 或永久文件权限。`Content` 必须可读、可 seek、不可写；`Length` 非负且与流实际长度一致，不另设媒体总长度配额。
- `Revoked` 必须可取消，文件、缓存、账号或能力授权失效时立即触发，暂停不延缓撤权。
- `NativeAudioPlayback.OpenReadAsync` 调用即接管独占所有权，包括参数拒绝、预取消、dispatcher 不可用和打开失败。调用方不得另读、关闭或复用已转移的流。
- 租约的 `DisposeAsync` 必须关闭底层句柄并解除读取阻塞，不依赖 UI dispatcher；音乐 owner 等待实际输入、解码和输出排空后才确认退休，释放失败不能冒充自然结束或成功排空。缓存和文件授权仍归原能力 owner。

## 电影核心：来源、构建与所有权

- App 通过既有 `AppPlaybackCoordinator` 在音乐与电影实际提交之间互斥准入；等待准入不持有电影命令门。音乐实现见[独立音乐核心](music-audio-core.md)。
- `FilmPlayback` 是 App 稳定持有的电影 owner，`FilmPlaybackSession` 直接消费该 owner 与影视输入适配。`FilmVideoSurface` 是 WinUI Grid，画面的 `SwapChainPanel` 与自定义控制内容同属该 Grid；全屏／画中画移动同一 surface，不创建另一套电影输出。挂载事件按当前 `IsLoaded`／`XamlRoot` 协调输出窗口与根订阅，旧窗口迟到的卸载通知不清空已归还页面的输出目标。
- `apps/windows/native/FilmKernel` 是 Windows 独立的电影原生 C/C++ 核心（C ABI v3），采用 `Demux → 有界分组 Packet Queue（未选流统一 AVDISCARD_ALL）→ 独立 Video/Audio 解码 + WASAPI + Present` 管线，不再依赖 Apple Swift/KSPlayer 构建入口。
- [film-kernel.lock.json](../../apps/windows/native/FilmKernel/film-kernel.lock.json) 固定 Windows x86_64 UCRT 的 17 个源码来源、归档／树／许可证哈希和构建输入。FFmpeg 8.1.2、libplacebo 7.387.0、libass 0.17.4（含 libunibreak 6.1）与解码、字体、着色器和压缩依赖构成静态闭包；Jinja／MarkupSafe 是构建依赖，不是随 App 运行的 Python 服务。
- `MeowFilm.Media.csproj` 经 `scripts/windows/build-film-kernel.ps1` 增量构建 `meowfilm_film_kernel.dll`、生成绑定身份及回执，复制到 `film-kernel/`。不手动替换 DLL，不将电影 DLL 放进 Node 载荷；音乐使用独立音频核心及其构建入口。
- 电影 C ABI v3（`MFK_ABI = 3`）暴露 owner、命令、轨道（含视频／音频／字幕轨切换）、章节、静音、快照、原生诊断快照（掉帧、A-V 差值、队列水位、硬解降级、HDR/ToneMap 模式、音频欠载与最近错误码）、脱敏日志回调及 composition surface，不暴露 FFmpeg 或 XAML 私有对象。`mfk_surface` 携带物理尺寸、合成 scale、可见性及仅用于显示能力查询的顶层 HWND；不使用视频 child HWND。每个候选有独立输入、解码和输出状态；prepare 可在未挂载页面时静默准备真实帧，只有同一 intent 的有效 commit 可发布当前输出；所有阻塞原生调用在独立串行执行器运行，不占用 WinUI STA 线程。
- `RuntimeMediaPlaybackIntent` 位于[独立意图文件](../../apps/windows/src/MeowFilm.Media/RuntimeMediaPlaybackIntent.cs)，保持 `MeowFilm.Media` namespace。同一实例绑定 prepare、commit 和 discard，`PreserveFrom` 指向准确同集 donor scope，不以新 attempt 身份推导旧输出。
- `NativePlaybackScope` 的 `OwnerId = film`、`Identity` 和 generation 围住电影命令与结果。状态／事件回到所属 STA UI 线程；非可视 DispatcherQueue 仅保留调度机制。原生线程只管理内核状态和受控回调，不反向调用父 owner 的命令门。

## 电影输入与网络边界

- `FilmPlayback.PrepareAsync` 接管 `NativeVideoInput` 与可撤销 `INativeMediaResourceLease`；HTTP(S) 地址、typed progressive／HLS／DASH 格式和合法 ClearKey 保持同一 attempt 的身份与授权。头部可从准确 lease 或显式 headers 参数形成单媒体快照。
- receipt 的 URL／`mediaOrigin` 按[共同影视播放契约](../runtime/film-playback.md)消费，不将 receipt 一概视为 film 受控 stream。headerless 且未显式启用短视频缓存时，Windows 原样使用返回 URL；Cat 自有 proxy 仍归 Cat owner，缺省 `mediaOrigin` 不表示另建了 film stream。准确 receipt 的释放不等于撤销共享 Cat 路由。
- FFmpeg 关闭自有网络／协议，根、清单、分片、初始化资源和密钥子 AVIO 经 `FFmpegHttpTransport`／原生受控回调，不转码、remux、整片落盘或创建媒体 HTTP 转发服务。
- 请求头及显式 Cookie 按当前输入快照传递到根、重定向和子请求；不按 Origin 剥离，也不建立全局 Cookie jar 或凭据 owner。响应 Set-Cookie 不自动更新会话。
- 不增加域名、IP 分类、Origin／回环／DNS 或 URL 图准入。实际 URI／HTTP／容器解析、Range 表示一致性、资源预算和取消仍执行；只有已验证的随机访问能力才允许 seek。
- ClearKey 只经私有内存交接并在退休后清理，不写入 URL、公开 options、普通事件或日志。凭据、业务资料和取址仍归原 owner，Media 不选择来源、不读取账号、不运行 Node、不保存业务队列。

## 电影声音、时钟与命令

- 电影音频独立使用 WASAPI（启用 `AUTOCONVERTPCM` 兼容 24-bit 等设备格式、MMCSS `"Pro Audio"` 线程优先级与平滑增益斜坡防爆音）。COM、设备和音频滤镜由音频工作线程持有；无音频输出设备时无缝降级为静音单调墙钟播放不拒绝视频，设备热插拔或切换默认设备时通过 `IMMNotificationClient` 自动重连；流内采样率／声道变化动态重建 `AudioFilter`。
- 位置依据设备实际消耗主时钟并按逐包 PTS 平滑校准；音轨早于视频轨结束或无设备时无缝切单调墙钟，缓冲期冻结墙钟防止恢复后快进丢帧；snapshot 在短锁下读取一致时钟，禁止持锁阻塞等待 WASAPI。
- FFmpeg avfilter `atempo` 调整节奏并保持音高，`aformat`／重采样转换到设备格式；调倍速和开关／切换文本字幕免网络 Seek，非致命命令错误不杀死健康播放会话。
- `FilmPlayback.ReadTracksAsync` 从内核读取当前 `FilmMediaTracks`；`FilmMediaTrack.Index` 是原生 stream 身份，不是过滤后列表下标。`SelectVideoTrackAsync`／`SelectAudioTrackAsync`／`SelectSubtitleTrackAsync` 必须携带准确 `NativePlaybackScope`，字幕 index 为 null 表示关闭字幕；取消单次换轨不杀死健康会话。
- 字幕使用 libass／DirectWrite（批量加载字体与 GPU 纹理图集缓存）及原生位图合成；超限或损坏字幕附件局部隔离不杀死整部电影。
- EOF 先 drain 解码器、增强层、音频滤镜尾帧及实际设备队列，再发布结束。取消、停止、撤权和析构必须唤醒背压并 join 实际工作线程，不以超时猜测替代退出确认。

## 电影 D3D11 原生 HDR、SDR 自适应与 Dolby Vision

- D3D11VA 硬解与 libplacebo 共享 D3D11 设备／受保护 context；软件解码帧也映射到同一原生渲染链路。
- 逐帧 DOVI RPU／mapping／颜色及扩展元数据参与 reshape 和颜色映射；普通非 DV HEVC 零侵入旁路 `FilmDovi`。Profile 7 所需的增强层按同一时间轴提取／解码／配对，非平凡 residual 经 NLQ 与基底合成。
- NAL-63 extraction 派生自 FFmpeg master `0834db516348d253b2c51f969188501d324c698d` 的 `dovi_split.c`，hvcE/RPU 本地修正适配锁定 8.1.2；不链接 master BSF ABI、mpv 或 libmpv。
- **只读显示能力检测与自适应 SwapChain（绝不强开系统 HDR）**：仅在窗口挂载、跨屏移动、显示设置变更或显式重建时查询当前显示器真实 Windows HDR 状态（逐帧热路径零 `QueryDisplayConfig`）。HDR／DoVi 片源且显示器已开启 HDR 时，使用 `R16G16B16A16_FLOAT`／scRGB 直出原生 HDR（`native_hdr = true, tone_mapped = false`）；HDR／DoVi 片源但显示器未开启 HDR（或 SDR 屏／拖至未开 HDR 副屏）时，绝不强开系统 HDR、不报错拒播，自动启用 `libplacebo` 动态 Peak Detection 与 HDR／DoVi → SDR Tone-Mapping 输出至 8-bit SDR SwapChain（`native_hdr = false, tone_mapped = true`）；纯 SDR 片源直接使用标准 8-bit SDR SwapChain。
- `mfk_attach` 返回具有独立 AddRef 的 swapchain，跨屏／HDR 变更在原 `IDXGISwapChain` COM 身份上平滑切换格式与色彩空间。UI 线程通过 `ISwapChainPanelNative.SetSwapChain` 绑定后才 start；释放或切换先在 UI 线程解绑，再排空原生输出和释放引用。
- 不发真正 DV／LLDV 传输信号，不使用非生产厂商发射接口；`native_hdr`／`tone_mapped`／`dolby_vision` 快照只描述处理路径，不证明显示器接收了 DV 信号、显示 DV 徽标或取得 Dolby 认证。
- 必需 DOVI 元数据／EL 缺失、畸形或映射不支持时返回 `FILM_DOVI_UNSUPPORTED`。设备丢失明确失败，需要连同解码器重建 owner，不掩盖为已恢复。

## 电影生命周期、许可与验证边界

- prepare 不发布当前输出；commit 同步音量／暂停意图，并要求首帧成功 render／Present 提交（`SubmittedPictures > 0`）后才发布同一 attempt，之后才反馈 `media.started`。URL、prepare 成功或 native playing 状态均不能代替首帧；Present 提交也不等于物理显示验收。同内容按准确 donor scope 承接最新实际位置和暂停／恢复意图，不以准备前的 receipt 覆盖后来控制；不同内容仍使用新 intent。失败的候选不得覆盖现有状态。
- `TryCommitAsync` 的 `false` 仅表示仍有效候选被业务准入暂缓；停止、替换或 donor 失效以 `MEDIA_SUPERSEDED` 终止，不循环重试。业务 owner 对未提交的 supersession 不回报片源失败，但仍等待准确 discard／资源回执释放；真实 `MEDIA_RELEASE_FAILED` 不归为 supersession。迟到的重复提交不退休另一提交已发布的赢家。
- 自然结束／活动失败先置 `HasOutput = false`，退休期间仍保留 current session、`Scope` 和可用时钟；等待真实 retire 后才清 current，并在 `Ended`／`Failed` 回调期间保留该身份和时钟，回调结束后 `ClearOutput`。期间 scoped stop／dispose 仍须等待该 session 真实退休，可先清输出／scope，并抑制旧身份的迟到终止回调。
- 调用方取消、替换、撤权、停止、失败和关闭使旧代次失效并等待真实解码／呈现／音频线程、GPU 引用及 HTTP 回调退出。原生 release 返回前不得释放委托、transport 或租约；read/resource receipt 恰好退休一次。
- `DisposeAsync` 在 UI 线程关闭前等待完成。commit 等待真实已加载且具有有效尺寸的 surface，取消仍使该等待结束；普通覆盖不取消媒体。准备／设备错误有稳定分类；公开错误不包含 URI、路径、请求头、密钥、响应正文或原生日志。
- 固定来源、FFmpeg LGPL、静态依赖的许可选择及 dovi_split 来源见 [NOTICE](../../LICENSES/NOTICE.md) 和 [电影内核补充声明](../../LICENSES/FilmKernel-THIRD-PARTY-NOTICES.txt)。Windows 打包复用既有正文并携带补充声明；静态链接的对应源码／重链接义务不因清单复制免除，`redistributionPermissionVerified` 仍为 false。
- 源码检查、许可证哈希、独立 DLL 构建、完整 App 构建、`--debug` 运行日志／诊断、桌面画面／字幕像素和真实业务 E2E 分别记录，不能互相外推。
- 物理 HDR 输出与 HDR→SDR Tone-Mapping 均须在真实 Windows 桌面环境下分别验证；缺少用于连续播放及 seek 验收的合法长时 FEL 样本时，不声明全 profile 兼容或长时 FEL 播放／seek 已验收。
- 真实验收须覆盖硬解／软件解码、动态 RPU 与必要 EL、实际 HDR 色彩空间／位深和显示器状态、音高／设备消耗／音画同步、暂停／seek／倍速／尾帧、候选切换／取消／撤权／关闭，以及自定义 surface 挂载／裁剪。截图只核对布局／可见结果，不证明 HDR/DV 元数据、信号或完整 profile 兼容。

## 相关契约

- [媒体资源租约](scoped-media-resources.md)
- [系统媒体会话](system-media.md)
- [独立音乐核心](music-audio-core.md)
- [影视播放页](film-playback.md)
