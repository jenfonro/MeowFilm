# Windows 自适应媒体

本文仅描述 `NativePlayback.OpenAsync`／`AdaptiveMediaSession` 的既有有限自适应路径：将受控 HLS／DASH 输入转换为当前原生播放器可消费的资源图。它不是下载管理器、平台代理或第二个播放器。影视 typed 输入走独立的锁定 FFmpeg 私有 AVIO 解复用桥，见[原生媒体实现](media-playback.md)；下列 parser／格式限制只适用于本文路径，不代表所有影视输入的支持范围。

## 入口和所有权

- 普通网络入口仍为 `NativePlayback.OpenAsync`；manifest 路径或显式请求头进入 `HttpMediaInput` 的受控 HTTP。
- `AdaptiveManifest` 解析根清单和允许的子清单，建立有界资源图；不把任意 playlist 直接交给原生内核继续发现网络资源。
- `AdaptiveMediaSession` 拥有上游传输、解析结果、资源、密钥、临时 capability 服务、取消和实际释放。
- 既有本地表示服务绑定临时 `127.0.0.1` 端口，无 Origin／Host／随机请求头准入；实际资源路由／Range 仍解析，未知路由为 404。
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
- 当前 HTTP 自适应输入不支持的非 HTTP(S) 子资源；
- 表示在读取期间改变、容器或分段关系不一致；
- 非当前、损坏或带额外结构的离线归档。

## 网络和凭据

- 根、子清单、初始化、分段和密钥请求保留真实 URI／HTTP／缓冲字节校验，但没有域名名单、DNS 地址分类、Origin／回环来源或 URL 许可图阻断。
- 显式调用方请求头按输入快照传递给实际重定向／子请求，不按 Origin 剥离；不使用响应 Cookie jar，不将地址、头、密钥写入日志。
- 网络层不设置全局／origin 并发额度，loopback 客户端不按累计或活动 handler 数拒绝；实际 handler 结算后及时移除。representation 一致性仍由对应资源 gate 管理。资源图、清单／分段大小、读取截止和生命周期各按实际资源校验，失败使用稳定错误，不进行无界重试。

## 生命周期

- 新会话预检失败不影响当前播放。
- 接受新会话后，替换、取消、停止、自然结束、失败和关闭都撤销资源图并等待原生请求、上游读取和本地监听器实际退出。
- 旧 generation 的请求和回调不能发布到新媒体。
- 自适应输入失败不得退化为直接把原始 manifest URL 交给 libVLC，也不得删除请求头后重试。

`PlaybackError.AdaptiveInvalid`、`AdaptiveUnsupported`、`AdaptiveDrmUnsupported`、`AdaptiveEncryptionUnsupported`、`AdaptiveBudget` 及 HTTP 固定代码构成当前错误边界。
