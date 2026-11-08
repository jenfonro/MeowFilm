# 网盘能力 `pan.runtime.v1`

`@meowfilm/pan-runtime@0.1.0` 是通用运行器的内置 Node 能力，不运行外部脚本，不创建 Worker 或额外进程。它拥有网盘凭据、分享枚举、必要单文件转存、登录及取播放地址；不拥有影视搜索、播放选路、媒体代理、账号页面或客户端缓存。

本轮仅实现运行器。iOS／Windows 默认仍装配 music/cat/source，未启用网盘能力；不能把这里的接口可用描述为平台 App 已接入。

## 受信装配

```js
import { createPanRuntime } from './apps/runtime/pan-entry.mjs';
const runtime = createPanRuntime({ sessionId, requestKey, dataRoot });
await runtime.start();
// 结束时必须等待所有请求与上游连接排空。
await runtime.close();
```

`sessionId/requestKey` 使用 [runtime.http.v1](runtime-bridge.md) 的受信配置，不接受脚本、URL 或 CLI 提供。`dataRoot` 是绝对目录。同进程总装配 `createRuntime({sessionId,requestKey,dataRoot,pan:{}})` 可显式加入 pan；省略 `pan` 时默认三模块不变。pan-only 入口不加载原生脚本引擎。

新模块没有源码上传接口。`describe` 中为 `pan@0.1.0/pan.runtime.v1`；所有业务请求是 `POST /v1/pan/{operation}`，完整 JSON body，不增加摘要、加密、分片或大小分类。Node 调用端用 `client.call('pan.' + operation, params)`。成功与错误信封复用现有 HTTP 契约。

## Provider 与账号

| provider | 配置字段 | 分享 list | 必要 prepare | play 模式 | 登录／刷新 |
| --- | --- | --- | --- | --- | --- |
| `baidu` | `cookie` | 是 | 是 | `streaming` | 二维码 |
| `quark` | `cookie` | 是 | 是 | `streaming`, `download` | 二维码 |
| `uc` | `cookie` | 是 | 是 | `streaming`, `download` | 二维码 |
| `tianyi` | `cookie`, `username`, `password` | 是 | 否 | `streaming`, `download` | Cookie／普通密码刷新 |
| `yidong` | `authorization`, `refreshedAt` | 是 | 否 | `streaming`, `download` | Authorization 刷新 |
| `quark_tv` | `refreshToken`, `deviceId`, `accessToken`, `expiresAt` | 否 | 否 | `streaming`, `download` | token 刷新 |
| `uc_tv` | 同上 | 否 | 否 | `streaming`, `download` | token 刷新 |

`refreshedAt/expiresAt` 为毫秒时间戳（非负安全整数），其余字段为字符串。天翼可只配置 Cookie；保存 username/password 才能在凭据失效后自动普通登录，不绕过验证码。移动 list/streaming 可匿名，download 需要 Authorization。TV 需要 deviceId 与 accessToken 或可刷新的 refreshToken；没有同账号 UID 绑定证明，调用者负责选用与普通盘转存文件对应的 TV 账号。

| operation | 请求 | 成功 value |
| --- | --- | --- |
| `providers` | `{}` | `{providers:[{provider,credentialFields,list,prepare,modes,login,refresh}]}` |
| `settings.get` | `{}` | `{accounts:[{provider,revision,configured,fields}]}` |
| `settings.set` | `{provider,credentials:{...}}` | 对应账号状态 |
| `settings.clear` | `{provider}` | 新 revision 的空账号状态 |
| `refresh` | `{provider}` | `{refreshed:true,account:账号状态}` |

每个 provider 只有一份当前账号。`settings.set` **整份替换**该账号配置，不是合并；clear 删除凭据但保留递增 revision。`configured/fields` 只说明哪些字段非空，不证明账号有效或具备会员资格。普通配置读取、刷新及登录结果不回传原始秘密；不存在“读取 Cookie”接口。不同 provider 不互相读写凭据。

配置明文原子保存在 `<dataRoot>/pan/accounts.json`：

```json
{"version":1,"accounts":{"baidu":{"revision":1,"credentials":{"cookie":"synthetic-example"}}}}
```

目录／文件权限分别为 `0700/0600`（由平台文件系统执行），拒绝符号链接、硬链接文件和路径穿越；损坏文件明确报错，不重置覆盖，不迁移旧客户端数据。**不加密，也不保证同用户进程或恶意脚本无法读取**；没有密钥文件、平台秘密桥或自定义加密壳。单个 dataRoot 应只由一个运行器管理，不支持多进程同时写同一份配置。

写入先同步暂存文件，再以原子 rename 为提交点；成功后的磁盘与内存配置一致，不承诺断电时目录项的额外耐久性保证。

手动换号／clear 撤销旧代次的进行中网络、登录和文件引用，旧结果不得覆盖新配置。自动刷新只 patch 当前代次，轮换凭据持久化成功后才报告成功。

## 分享与播放

| operation | 请求 | 成功 value |
| --- | --- | --- |
| `list` | `{provider,share:{url,password?}}` | `{share:{id,url,password},files:[{name,path,size?,file}]}` |
| `prepare` | `{provider,file}` | `{file}`，`kind:"stored"` |
| `play` | `{provider,file,mode}` | `{url,headers,mode,file?}` |

`list` 只返回 provider 所支持的视频后缀，不增加字幕匹配、剧集规则或逐文件 M3U8 预请求。按上游分页语义完整消费列表与目录；无固定总数、深度或页数截断。重复目录防环；声明还有下一页却重复／空页时报格式错误，不把失败或部分列表包装成完整空成功。

`name/path` 保留原始名称与目录路径；size 若超过安全整数则为十进制字符串。`file` 是必须完整原样回传的结构化引用，包含 `kind/provider/accountRevision` 及 provider 所需分享 ID、文件 ID、提取码、令牌等；所有数字 ID 用字符串。它不是文件名索引或旧 `vod_play_url`，不使用 `#/$/*`、Base64 播放 ID。引用可能含分享秘密，应由调用方按敏感结果处理，不写日志。

百度／夸克／UC 的 `play` 接受 share 引用，内部按需 `prepare`；其他普通盘直接按分享引用取址。转存只处理所选文件，查找／创建专用 `MeowFilm` 目录，使用转存任务返回的精确目标，不选择目录第一项或按原名猜文件。**没有自动清空、按日删除或覆盖用户文件**；取消不能回滚已完成的远端转存，完成后重复调用可能生成新副本，不提供长期去重缓存。

百度仅使用 `share/transfer` 返回的 `extra.list` 精确映射；若只返回 task ID 而没有所选文件的准确目标，报告 `UPSTREAM_FORMAT`，不增加参考实现没有的任务查询接口，也不猜测目标路径。报错不表示远端转存已经回滚。

TV 只接受明确账号内的 `kind:"stored"` 引用，不 list、不转存、不尝试普通盘回退。可显式传入对应普通盘 `prepare` 返回的完整引用，service 同时校验普通账号 revision；也可传入 `{kind:"stored",provider:"quark_tv",accountRevision:当前TV账号revision,fileId:"准确账号内fid"}`。没有普通 Cookie 与 TV 账号的自动绑定或播放优先级策略。

每次 `play` 只取调用方指定的一种模式，不同时请求 streaming/download，不因失败切换 provider。返回 URL 和必要 headers，调用方负责媒体请求；headers 可能含上游要求的 Cookie／Authorization，不用于其他域名，不写日志。运行器不建立服务器媒体 relay。

### 参考接入与模式含义

网盘通信能力以 meowfilm-server 的独立适配函数为依据，而不是将其播放编排整体移植。普通盘与 TV 的选择、优先级以及失败后是否再次尝试由客户端负责。

- **普通夸克／UC**：两种 `mode` 都调用一次同盘的 `file/download`。按参考 `quarkDirectDownloadWithCookie`／`ucDirectDownload`，在所选文件的同一响应中优先取 `streaming → play_url` 或 `download → download_url`，随后按 `download_url → play_url → url` 读取可用地址。这里的模式是字段偏好，**不保证两种地址不同，也不声明普通盘具有 TV 转码接口**；只有 `download_url` 时仍能提供原文件直链。不会为此请求 TV 或另一种上游取址接口。
- **夸克 TV／UC TV**：明确调用 `/file?method=streaming` 或 `/file?method=download`，分别读取 `video_info` 中首个非空 URL 或 `download_url`。所选结果缺失时报错，不使用另一模式的结果，不补发普通盘请求。
- **百度**：只公开参考实现的 `streaming` 取址；不声明不存在的独立 `download` 能力。
- **天翼**：streaming 使用 `getNewVlcVideoPlayUrl.action`，download 使用 `getFileDownloadUrl.action`，保留各自响应字段及同接口 `FileTooLarge` 处理，不套用普通夸克／UC 的字段规则。
- **移动**：streaming 只使用匿名 `getContentInfoFromOutLink`；download 使用认证签名的 `dlFromOutLinkV3`，`9530` 时仅在已有明确 `contentId` 的条件下按参考换用同端点请求体。不为带密码文件额外重列目录，也不把 list 响应里的 URL 当作播放缓存。

本地结构化引用、账号代次、取消与资源排空是调用接口和生命周期要求；它们不增加上游能力。此前明确排除的业务缓存、自动清理、旧播放 ID、媒体代理与分页截断不随参考移植恢复。

## 二维码生命周期

- `login.start({provider})` → `{sessionId,provider,status:"pending",challenge:{type:"url"|"image",url},expiresAt}`。`url` 类型供调用方编码二维码；百度 `image` 类型由运行器使用该登录会话获取上游二维码图片，以 `data:image/...;base64,...` 交付，不要求客户端补带运行器的会话 Cookie，不是网页自动化。
- `login.poll({sessionId})` → `{sessionId,provider?,status:"pending"|"scanned"|"confirmed"|"expired"|"cancelled",account?}`。confirmed 只在凭据真实取得且原子持久化成功后发布，并发 poll 共用同一在途交换，不重复提交账号。
- `login.cancel({sessionId})` 取消未完成会话；已确认的账号不会因此被清除。需要清除账号应使用 `settings.clear`。
- 同 provider 新建登录会撤销旧挑战；换号、关闭与上游有效期结束均清理临时 Cookie／令牌和计时器。未知或已清除的过期 session 报 `LOGIN_EXPIRED`。
- 不后台轮询、不引入 qrencode／浏览器／脚本引擎；页面与轮询交互由后续客户端任务实现。

百度未扫码时 `/channel/unicast` 可能等待超过 30 秒才返回 pending；该请求的上游预算为 40 秒，并取二维码 session 剩余有效期的较小值，仍受调用方取消控制。其他二维码请求保持 30 秒。调用百度 `pan.login.poll` 时使用现有单次调用 deadline 选项留出 45 秒：

```js
await client.call('pan.login.poll', { sessionId }, { timeout: 45000 });
```

45 秒仅覆盖本次调用的等待，不改变通用 client 默认 30 秒，也不是新增配置。实际网络超时仍返回 `TIMEOUT`，不会当作 pending。

## 上游传输、并发与安全边界

原生 `http/https` keep-alive 与 Buffer 聚合供完整响应接口使用；仅执行上游协议明确需要的 JSON/JSONP、解压、Cookie、签名与 AES/RSA。JSONP 不 eval，大整数精确保留。重定向不把凭据自动送往任意域名；协议认证链只允许对应已知端点。错误只使用封闭码，不回传请求 URL、底层异常、上游正文或账号资料。

百度／天翼解析播放重定向时仅需要响应头：保持上游要求的 GET／Range，收到头后停止并排空响应，不下载媒体正文。它不是按响应大小分类，也不改变其他完整响应接口。

TV 刷新**按已确认方案**将长期 refreshToken 发送给第三方明文 HTTP：

- `http://api.extscreen.com/quarkdrive/token`
- `http://api.extscreen.com/ucdrive/token`

实际 file 接口为官方网盘 HTTPS。第三方可获得长期凭据，HTTP 路径存在窃听／篡改风险；本实现不宣称消除这些风险，也不未经验证将协议改成 HTTPS。它与本地凭据不加密是两个独立边界。

各请求可同时等待网络，无全局业务队列、并发额度或每账号线程。解析计算仍在 Node 主线程，不能把 async 等同于 CPU 并行。仅同 provider/账号代次的刷新与相同文件的在途转存合并；取消一个等待者不影响其他等待者，全部取消、换号或关闭时取消上游。普通网络请求默认截止 30 秒；协议任务轮询受自身截止时间和调用方取消控制。`close()` 等待实际工作排空，不以等待者提前取消冒充物理释放。

## 错误与验证

业务错误：`INVALID_REQUEST/UNSUPPORTED_OPERATION/NOT_CONFIGURED/AUTH_REQUIRED/AUTH_FAILED/CHALLENGE_REQUIRED/NOT_FOUND/UPSTREAM_HTTP/UPSTREAM_FORMAT/UPSTREAM_ERROR/NETWORK_ERROR/TIMEOUT/CANCELLED/CLOSED/STALE_ACCOUNT/STORAGE_ERROR/CORRUPT_STORAGE/LOGIN_EXPIRED/PAN_FAILURE`。调用方应按 code 处理，不解析供应方文案。不支持的模式、provider 操作及失效引用在网络副作用前拒绝。

离线 provider 测试使用合成凭据和记录式 HTTP 替身；公共传输用本机真实 HTTP 验证；service 与公共 HTTP 测试覆盖持久化、代次、并发、取消及关闭；通用打包测试验证新闭包与原默认三模块。真实账号挑战、会员／频控、第三方服务可用性、真实转存、Windows/iOS App 接入不由这些测试证明。
