# Windows 原生媒体实现

音乐由独立的 `MeowFilm.Audio` 持有；中性输入与作用域契约归 `MeowFilm.Core`。`MeowFilm.Media` 包含电影专属 `FilmPlayback`／`FilmKernel` 与既有 `NativePlayback` 路径。电影不借用音乐的播放 owner、PCM、时钟或 LibVLC；原生音乐与 Node film 仍分别拥有业务选择、授权资源和恢复状态。本次只接入电影核心，不迁移音乐或清理旧 LibVLC。

## 电影核心：来源、构建与所有权

- App 通过既有 `AppPlaybackCoordinator` 在音乐与电影实际提交之间互斥准入；等待准入不持有电影命令门。音乐实现见[独立音乐核心](music-audio-core.md)。
- `FilmPlayback` 是 App 稳定持有的电影 owner，`FilmNativeMedia` 是影视业务适配。页面只挂载同一 `FilmVideoSurface` 和自定义 WPF 控件；全屏／画中画不创建另一套电影输出。
- `apps/windows/native/FilmKernel` 将本项目 KSPlayer 派生 Apple 核心的解码／flush／有界帧队列机制移植为 Windows C/C++。上游 KSPlayer 固定为 `d0d063ab1f045735129b6617591ad2148d88d5db`，不直接构建官方 Apple Swift SDK，不编译其 UI、Demo 或 FFmpegKit shim。
- [film-kernel.lock.json](../../apps/windows/native/FilmKernel/film-kernel.lock.json) 固定 Windows x86_64 UCRT 的 16 个源码来源、归档／树／许可证哈希和构建输入。FFmpeg 8.1.2、libplacebo 7.387.0、libass 0.17.4 与解码、字体、着色器和压缩依赖构成静态闭包；Jinja／MarkupSafe 是构建依赖，不是随 App 运行的 Python 服务。
- `MeowFilm.Media.csproj` 经 `scripts/windows/build-film-kernel.ps1` 增量构建 `meowfilm_film_kernel.dll`、生成绑定身份及回执，复制到 `film-kernel/`。不手动替换 DLL，不将电影 DLL 放进 Node 载荷；既有 VideoKernel／LibVLC 的构建保留，不再用于电影；音乐继续使用已合入的独立核心。
- 电影 C ABI 只暴露 owner、命令、轨道及快照，不暴露 FFmpeg 或 WPF 私有对象。每个候选有独立输入、解码和输出状态；prepare 静默准备真实帧，只有同一 intent 的有效 commit 可发布当前输出。
- `NativePlaybackScope` 的 `OwnerId = film`、`Identity` 和 generation 围住电影命令与结果。WPF 状态／事件回到所属 STA dispatcher；原生线程只管理内核状态和受控回调，不反向调用父 owner 的命令门。

## 电影输入与网络边界

- `FilmPlayback.PrepareAsync` 接管 `NativeVideoInput` 与可撤销 `INativeMediaResourceLease`；HTTP(S) 地址、typed progressive／HLS／DASH 格式和合法 ClearKey 保持同一 attempt 的身份与授权。头部可从准确 lease 或显式 headers 参数形成单媒体快照。
- receipt 的 URL／`mediaOrigin` 按[共同影视播放契约](../runtime/film-playback.md)消费，不将 receipt 一概视为 film 受控 stream。headerless 且未显式启用短视频缓存时，Windows 原样使用返回 URL；Cat 自有 proxy 仍归 Cat owner，缺省 `mediaOrigin` 不表示另建了 film stream。准确 receipt 的释放不等于撤销共享 Cat 路由。
- FFmpeg 关闭自有网络／协议，根、清单、分片、初始化资源和密钥子 AVIO 经 `FFmpegHttpTransport`／原生受控回调，不转码、remux、整片落盘或创建媒体 HTTP 转发服务。
- 请求头及显式 Cookie 按当前输入快照传递到根、重定向和子请求；不按 Origin 剥离，也不建立全局 Cookie jar 或凭据 owner。响应 Set-Cookie 不自动更新会话。
- 不增加域名、IP 分类、Origin／回环／DNS 或 URL 图准入。实际 URI／HTTP／容器解析、Range 表示一致性、资源预算和取消仍执行；只有已验证的随机访问能力才允许 seek。
- ClearKey 只经私有内存交接并在退休后清理，不写入 URL、公开 options、普通事件或日志。凭据、业务资料和取址仍归原 owner，Media 不选择来源、不读取账号、不运行 Node、不保存业务队列。

## 电影声音、时钟与命令

- 电影音频独立使用 WASAPI。COM、设备和音频滤镜由音频工作线程持有；生产者 write 有界背压，设备错误稳定返回，不能靠回调父 gate 唤醒。
- 位置依据设备实际消耗而非提交量或 UI 墙钟。padding、播放段与 underrun 的时钟围栏防止把未播放样本当成已经播放；snapshot 在内核 gate 下取得一次一致的音频时钟。
- FFmpeg avfilter `atempo` 调整节奏并保持音高，`aformat`／重采样转换到设备格式；不以改变 sample rate 冒充保音高倍速。实际音高、速率、声音和同步仍需真实设备验证。
- pause 冻结设备播放与快照；seek／倍速／选音轨命令可中断当前 queued write，但不取消整媒体。reset／seek 清空旧滤镜、队列与时钟段，设备保持暂停到新首帧准备完成；命令只作用于当前代次。
- 内核 C ABI 提供真实轨道读取／切换，字幕使用 libass／DirectWrite 及原生位图合成；旧 `NativePlayback` 的轨道快照不是电影轨道事实源。ABI 存在不等于电影页面已暴露全部轨道控件。
- EOF 先 drain 解码器、增强层、音频滤镜尾帧及实际设备队列，再发布结束。取消、停止、撤权和析构必须唤醒背压并 join 实际工作线程，不以超时猜测替代退出确认。

## 电影 D3D11 原生 HDR 与 Dolby Vision

- D3D11VA 硬解与 libplacebo 共享 D3D11 设备／受保护 context；软件解码帧也映射到同一原生渲染链路。不经过 WPF 8-bit bitmap，也不以海报、10-bit 纹理或解码成功代替显示验收。
- 逐帧 DOVI RPU／mapping／颜色及扩展元数据参与 reshape 和颜色映射。Profile 7 所需的增强层按同一时间轴提取／解码／配对，非平凡 residual 经 NLQ 与基底合成；不能丢掉必要 EL 或只播 HDR10 基底冒充完整处理。
- NAL-63 extraction 派生自 FFmpeg master `0834db516348d253b2c51f969188501d324c698d` 的 `dovi_split.c`，hvcE/RPU 本地修正适配锁定 8.1.2；不链接 master BSF ABI、mpv 或 libmpv。
- 目标是标准 DXGI HDR 色彩空间与有足够精度的原生 swapchain，包括 PQ/BT.2020 或 scRGB；实际显示能力、当前 Windows HDR 状态及 swapchain 支持必须满足输出要求。不自动开启显示器 HDR，不强制厂商 DV 模式。
- 不发真正 DV／LLDV 传输信号，不使用非生产厂商发射接口；`native_hdr`／`dolby_vision` 快照只描述处理路径，不证明显示器接收了 DV 信号、显示 DV 徽标或取得 Dolby 认证。
- HDR 显示不支持时返回 `FILM_HDR_DISPLAY_UNSUPPORTED`；必需 DOVI 元数据／EL 缺失、畸形或映射不支持时返回 `FILM_DOVI_UNSUPPORTED`。不增加 HDR→SDR 或静默基底 fallback。设备丢失明确失败，需要连同解码器重建 owner，不掩盖为已恢复。

## 电影生命周期、许可与验证边界

- prepare 不发布当前输出；commit 同步音量／暂停意图，并要求首帧成功 render／Present 提交（`SubmittedPictures > 0`）后才发布同一 attempt，之后才反馈 `media.started`。URL、prepare 成功或 native playing 状态均不能代替首帧；Present 提交也不等于物理显示验收。准确 donor scope 承接位置，失败的候选不得覆盖现有状态。
- 自然结束／活动失败先置 `HasOutput = false`，退休期间仍保留 current session、`Scope` 和可用时钟；等待真实 retire 后才清 current，并在 `Ended`／`Failed` 回调期间保留该身份和时钟，回调结束后 `ClearOutput`。期间 scoped stop／dispose 仍须等待该 session 真实退休，可先清输出／scope，并抑制旧身份的迟到终止回调。
- 调用方取消、替换、撤权、停止、失败和关闭使旧代次失效并等待真实解码／呈现／音频线程、GPU 引用及 HTTP 回调退出。原生 release 返回前不得释放委托、transport 或租约；read/resource receipt 恰好退休一次。
- `DisposeAsync` 在 WPF dispatcher 关闭前等待完成。准备／设备错误有稳定分类；公开错误不包含 URI、路径、请求头、密钥、响应正文或原生日志。
- 固定来源、KSPlayer GPLv3 派生关系、FFmpeg LGPL、静态依赖的许可选择及 dovi_split 来源见 [NOTICE](../../LICENSES/NOTICE.md) 和 [电影内核补充声明](../../LICENSES/FilmKernel-THIRD-PARTY-NOTICES.txt)。Windows 打包复用既有正文并携带补充声明；静态链接的对应源码／重链接义务不因清单复制免除，`redistributionPermissionVerified` 仍为 false。
- 电影核心已完成正式 Windows 编译；真实核心集成、WASAPI／renderer 的既有验证结果只覆盖各自执行范围。源码检查、许可证哈希、单元替身、独立 DLL、完整 App、桌面画面／字幕像素和真实业务 E2E 分别记录，不能互相外推。
- 物理 HDR 输出尚未验收。Windows HDR 关闭的桌面上，已测真实 HDR/DV 输入 prepare 成功、attach 以 `FILM_HDR_DISPLAY_UNSUPPORTED` 拒绝，只验证准备路径与设备条件门，不证明实际 HDR 呈现。缺少用于连续播放及 seek 验收的合法长时 FEL 样本，不声明全 profile 兼容或长时 FEL 播放／seek 已验收。
- 真实验收须覆盖硬解／软件解码、动态 RPU 与必要 EL、实际 HDR 色彩空间／位深和显示器状态、音高／设备消耗／音画同步、暂停／seek／倍速／尾帧、候选切换／取消／撤权／关闭，以及自定义 surface 挂载／裁剪。截图只核对布局／可见结果，不证明 HDR/DV 元数据、信号或完整 profile 兼容。

## 既有 NativePlayback 路径（本次不迁移）

以下约束只描述既有 `NativePlayback`，不再作为电影 `FilmPlayback` 的 owner 或格式限制：

- 该路径只创建一个共享 `NativePlayback`，候选也由它管理；全部公开状态和事件回到所属 WPF STA dispatcher。
- LibVLC engine 延迟创建并由播放器实例拥有。替换、停止、自然结束、失败和关闭经过同一串行命令门；`OwnerId`、`Identity` 和 generation 阻止旧命令／回调作用于新会话。
- 旧 `apps/windows/native/VideoKernel` access-demux 插件使用锁定 FFmpeg 8.1.2 解复用／合法 ClearKey，将 packet／codec 参数交给 VLC `es_out`；LibVLC 3.0.24 仍负责这条旧路径的解码／渲染。本次不删除或回接为电影核心。

插件与 VLC 使用不同 CRT。轨道的 extradata、语言及标题只借用 FFmpeg 字段至同步深拷贝的 `es_out_Add`；返回后清空这些指针和 `i_extra`，再清理临时 format，不跨 CRT 转移分配／释放所有权。

### 既有输入边界

| 入口 | 输入 | 约束 |
| --- | --- | --- |
| `OpenAsync(..., Uri, ...)` | 本地普通文件或 HTTP(S) | 本地文件按实际输入格式解析；网络输入按 HTTP／媒体格式处理 |
| `OpenAsync(..., Uri, headers, ...)` | 需要受控请求头的 HTTP(S) | 请求头、重定向、地址、Range、超时和表示一致性由 `HttpMediaInput` 验证 |
| `OpenReadAsync` | `INativeMediaReadLease` | 调用即转移独占所有权，只读取已经授权的不可变、只读、可 seek 字节流 |
| `OpenResourceAsync` | `INativeMediaResourceLease` | 接管可撤销 resource receipt；不授予或限制 URL 访问 |
| `PrepareRuntimeMediaAsync`（旧接口） | 同一 `RuntimeMediaPlaybackIntent`、`NativeVideoInput` 及 HTTP(S) 地址或准确 resource lease | typed format／ClearKey 经旧私有桥；不是当前电影消费者的入口 |

身份和标题从不解释为路径或 URL。普通 `Open*` 成功返回只表示内核接受播放请求，不代表已经产生声音、画面或硬件解码；影视 `PrepareRuntimeMediaAsync` 还须等待真实帧准备，但准备成功不等于已提交当前输出、已出声或已验证硬件解码。

### 既有 HTTP 与自适应媒体

- 普通带头 progressive 输入使用受控 `HttpClient` 和 libVLC callback，不经过 Node、Base64、临时文件或产品内转发服务。
- 没有地址分类、域名名单、Origin／回环／DNS 或 URL 图访问阻断；HTTP(S) 重定向与子请求按实际地址访问。URI／HTTP／媒体解析仍真实执行。
- 标准 HTTP(S) URL 的 userinfo 按百分号解码后用于 Basic Authorization，无密码时使用空密码；显式 Authorization 请求头优先。每次重定向／子请求按其实际 URL 处理，不建立额外凭据或 Origin 许可表。URL 不设任意长度准入；HTTP framing、header 与媒体解析资源预算仍保留。
- 显式调用方头按输入快照传递，不按 Origin 剥离；响应 Cookie 不形成 Cookie jar。
- 只有通过强校验的 Range 表示才允许随机访问；表示、长度或 validator 变化会明确失败。
- 既有 `AdaptiveMediaSession` 输入解析 HLS／DASH 并重建播放器可读的实际资源路由；其本地服务不做 Origin／Host／随机请求头访问校验，未知路由仍为 404。电影 typed 输入走上述独立 FilmKernel，而非此资源服务。
- 当前 HLS VOD、受限 live HLS、受限 DASH、AES-128 和离线 `MEOWVOD1` 归档边界分别见 `adaptive-media.md`、`live-hls.md`、`adaptive-encryption.md` 与 `media-offline-input.md`。

### 既有播放状态与命令

- `OpenAsync`／`OpenReadAsync`／`OpenResourceAsync` 先验证新请求，再替换旧会话；无效新请求不能中断当前播放。
- `Started` 只由当前原生 `Playing` 事件触发一次；选择来源或 `Play()` 被接受不等于开始播放。
- `PauseAsync`、`ResumeAsync`、`SeekAsync`、轨道、倍速和字幕命令都绑定提交时的当前会话。
- 暂停和 seek 意图具有独立时序围栏，排队的旧操作不能落到后来切换的媒体。
- `PositionSeconds`、`DurationSeconds`、`IsPlaying` 和轨道快照来自当前原生会话；不以 UI 动画伪造播放状态。
- 受控 continuation 只保存当前逻辑位置和暂停意图，不授予新的 URL、文件或账号权限。
- 旧 runtime 接口也有 prepare／commit 与 donor scope 围栏；它的实现或独立构建证据不作为当前电影核心验收。

### 既有生命周期和错误

- 调用方取消、资源撤权、替换、停止和释放先使当前 generation 失效，再等待原生回调、输入、监听器和租约实际退出。
- read lease 和 resource lease 均恰好释放一次；释放失败使用稳定错误并阻止实例继续打开媒体。
- `DisposeAsync` 必须在 WPF dispatcher 关闭前等待完成。
- `PlaybackException` 只公开 `PlaybackError` 中的固定代码；不暴露 URI、路径、请求头、凭据、响应正文或原生日志。
- 非当前离线归档和损坏输入明确失败且不被改写。

旧私有 DLL 的历史独立构建不代表本轮 Media C#、整 App、新电影核心、ClearKey、seek 或撤权排空通过；旧音频测试或 UI 状态不能替代这些证据。影视业务及端侧接缝分别见[共同运行器契约](../runtime/film-playback.md)和[影视播放页](film-playback.md)。

## 相关契约

- `media-offline-input.md`
- `scoped-media-resources.md`
- `adaptive-media.md`
- `live-hls.md`
- `adaptive-encryption.md`
- `media-tracks.md`
- `system-media.md`
- `music-audio-core.md`
