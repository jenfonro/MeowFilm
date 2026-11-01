# Windows 自适应媒体

`MeowFilm.Media` 的自适应路径负责将受控 HLS／DASH 输入转换为当前原生播放器可消费的有限资源图。它不是下载管理器、平台代理或第二个播放器。

## 入口和所有权

- 普通网络入口仍为 `NativePlayback.OpenAsync`；manifest 路径或显式请求头进入 `HttpMediaInput` 的受控 HTTP。
- `AdaptiveManifest` 解析根清单和允许的子清单，建立有界资源图；不把任意 playlist 直接交给原生内核继续发现网络资源。
- `AdaptiveMediaSession` 拥有上游传输、解析结果、资源、密钥、临时 capability 服务、取消和实际释放。
- capability 服务只绑定 literal `127.0.0.1` 的临时端口，每个原生请求必须携带会话随机能力；该能力不持久化、不转发上游。
- App 仍只有一个 `NativePlayback`、一个 LibVLC engine 和一个当前 `MediaPlayer`。

## 当前支持边界

- 支持由当前 parser 完整验证的 HLS VOD、有限 live HLS 和有限 DASH。
- 支持 MPEG-TS 与当前 fragmented MP4 profile；实际资源类型、初始化段、分段关系、byte range 和容器签名必须一致。
- HLS identity AES-128 的当前边界见 `adaptive-encryption.md`。
- live HLS 的刷新、窗口和原生 EOF 恢复规则见 `live-hls.md`。
- `NativeAdaptiveArchive` 可把一个已经验证为有限 VOD 的资源图按顺序生成当前 `MEOWVOD1` 字节流；它不创建下载任务、不保存用户下载记录，也不接受开放中的 live window。
- `OpenReadAsync` 只读取当前 `MEOWVOD1` 归档或受支持的普通媒体字节；离线边界见 `media-offline-input.md`。

以下输入明确拒绝：

- DRM、`SAMPLE-AES`、非 identity key format 和受保护 MP4；
- 动态 DASH、无限或循环清单、超出预算的资源图；
- 不受支持的低延迟／delta／blocking reload live 特性；
- 本地文件子资源、私网或未授权 loopback 子资源；
- 表示在读取期间改变、重定向越权、容器或分段关系不一致；
- 非当前、损坏或带额外结构的离线归档。

## 网络和凭据

- 每个根清单、子清单、初始化段、媒体段和密钥请求都重新通过 URI、DNS、实际连接地址、重定向和实际缓冲字节校验。
- 调用方请求头只在原 credential origin 内保留；一旦跨 origin，后续整条链都不再携带这些头。
- capability token 只用于本地原生请求，绝不发送给上游。
- HTTPS 不得降级到 HTTP；响应 Cookie 不被保存或重放。
- 网络层不设置全局／origin 并发额度，loopback 客户端不按累计或活动 handler 数拒绝；实际 handler 结算后及时移除。representation 一致性仍由对应资源 gate 管理。资源图、清单／分段大小、读取截止和生命周期各按实际资源校验，失败使用稳定错误，不进行无界重试。

## 生命周期

- 新会话预检失败不影响当前播放。
- 接受新会话后，替换、取消、停止、自然结束、失败和关闭都撤销资源图并等待原生请求、上游读取和本地监听器实际退出。
- 旧 generation 的请求和回调不能发布到新媒体。
- 自适应输入失败不得退化为直接把原始 manifest URL 交给 libVLC，也不得删除请求头后重试。

`PlaybackError.AdaptiveInvalid`、`AdaptiveUnsupported`、`AdaptiveDrmUnsupported`、`AdaptiveEncryptionUnsupported`、`AdaptiveBudget` 及 HTTP 固定代码构成当前错误边界。
