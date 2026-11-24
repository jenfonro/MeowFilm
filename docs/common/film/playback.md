# 电影原生播放

电影业务会话和来源凭据由 [Node 影视 owner](../../runtime/film-playback.md) 持有；此处定义电影原生核心的共同输入和生命周期契约。实现与验证状态分别见各端说明。

## 原生媒体请求上下文

原生直连媒体的可信 owner 可显式提供本媒体实例的 HTTP headers／Cookie 请求上下文，
不可变上下文随该实例持有，不使用全局 Cookie jar，不保存凭据，也不自动消费
`Set-Cookie` 或更新 Cookie。没有显式上下文时使用空值，不能继承上一媒体的请求头。

头名称按 HTTP token 语法验证后统一为小写；值原样保留，允许水平制表符和普通文本，
拒绝 CR、LF、NUL、其他非法控制字符及 DEL。大小写不同的重复头一律拒绝，即使值相同；
独立 Cookie 参数合入 `cookie` 头，与 headers 内任意大小写的 Cookie 同时提供也拒绝，
不静默覆盖或拼接。空头值保留，缺失 Cookie 与显式空 Cookie 区分。
允许 `Referer`、`User-Agent`、`Authorization`、`Cookie` 及其他语法有效的头，
不增加头白名单、域名或 IP 准入限制。

现有 `session.attempt` 媒体 receipt 保持空原生请求上下文；来源请求头和凭据仍由
[Node 媒体 owner](../../runtime/film-playback.md#停止撤权与本地流) 持有并用于受控二进制流，
不新增 receipt 凭据字段，不把来源 Cookie 复制到原生或公开投影。
上下文和包含它的媒体输入不能通过日志、description、debug description 或 Mirror
暴露头名称、值及 Cookie；校验错误也不能附带原输入。规范化后的 headers 仅供可信核心适配器读取。
ClearKey 仍使用独立、被动的原生密钥契约，不因请求上下文产生许可请求或额外 DRM 能力。

构造及脱敏测试不代表核心已正确传递上下文；根／子清单、初始化段、分片、密钥、
重定向、Range／seek 和取消／释放须由各端在真实 HTTP 与播放生命周期测试中确认。

## 核心与原生界面

两端主视频内核均基于锁定的 FFmpeg 8.1.2 源码与自有原生解码／轨道／字幕／缓冲／主时钟／音视频管线（iOS 为 `native/VideoKernel` 自有 Swift 核心并保留可选 `native/AetherKernel`；Windows 为 `apps/windows/native/FilmKernel` C/C++ C ABI v3 核心）；平台保留原生业务会话、系统媒体 owner 和自定义界面。原生 HDR／Dolby Vision 在开启 HDR 的显示器上直出原生 HDR 与动态元数据；当 Windows 显示器未开启系统 HDR 或位于 SDR 屏时，严禁强开系统 HDR，按只读显示检测自动启用 `libplacebo` 动态 Tone-Mapping 输出至 SDR SwapChain（并在快照与 `--debug` 明确区分 `native_hdr` 与 `tone_mapped`）。具体可用 profile、解码和输出分别以端侧证据为准。

候选媒体准备与提交分离：候选静默准备，真实画面输出并通过业务与系统音频准入后才改变当前 owner。失败候选不应停止健康旧输出。暂停、倍速、seek 按代次确定完成资格；旧结果不能覆盖较新目标或恢复旧速率。

首帧凭据不能覆盖随后发生的核心失败；提交前最后一次健康性检查与 owner 切换之间不能挂起。取消 seek 保持暂停及最后确认位置，不发布未完成目标，也不立即发起回滚 seek；之后明确恢复播放时，必要时从确认位置重新建立真实解码和输出，更新的用户 seek 优先于该恢复。

取消先停止本实例继续产生输出的资格；关闭须等真实网络／解码／渲染／音频工作退出，再释放媒体授权。仅设置取消标记或 shutdown 返回不能视为排空。原始 URL、headers、Cookie、解密材料与外部错误内容不进入事件、UI 状态或日志。
