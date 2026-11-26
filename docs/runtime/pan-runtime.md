# 网盘能力 `pan.runtime.v1`

`@meowfilm/pan-runtime@0.1.0` 是通用运行器的内置 Node 能力，不运行外部脚本，不创建 Worker 或额外进程。它负责网盘凭据使用、分享枚举、必要单文件转存、登录及取播放地址；不拥有影视搜索、播放选路、媒体代理、账号页面或客户端缓存。凭据持久化默认归 Node，受信装配可注入唯一原生 store。

本模块仅提供 provider 能力。iOS／Windows 正式 App entry 已在九模块中装配 pan，film 与设置页复用同一份账号事实，不建立两份可写凭据。这不等于账号 UI、真实登录／转存或平台播放已验证，具体业务与边界见[影视播放契约](film-playback.md)。

## 受信装配

```js
import { createAppRuntime, createPanRuntimeModule } from './apps/runtime/entry.mjs';
const runtime = createAppRuntime({
  sessionId,
  modules: [createPanRuntimeModule({ dataRoot })],
});
await runtime.start();
// 结束时必须等待所有请求与上游连接排空。
await runtime.close();
```

`sessionId` 是 [runtime.http.v1](runtime-bridge.md) 的生命周期代次；普通 HTTP 调用无需 session 或启动密钥。`dataRoot` 是由 App 指定的绝对目录，运行器管理其下的 pan 自有数据。通过公共 `apps/runtime/entry.mjs` 单独装配 `createPanRuntimeModule`（`@meowfilm/pan-runtime`）时不加载原生脚本引擎；同进程总装配 `createRuntime({sessionId,dataRoot,pan:{}})` 也可显式加入 pan，省略 `pan` 时默认三模块不变。

新模块没有源码上传接口。`describe` 中为 `pan@0.1.0/pan.runtime.v1`；所有业务请求是 `POST /v1/pan/{operation}`，完整 JSON body，不增加摘要、加密、分片或大小分类。Node 调用端用 `client.call('pan.' + operation, params)`。成功与错误信封复用现有 HTTP 契约。

## Provider 与账号

| provider | 配置字段 | 分享 list | 必要 prepare | play 模式 | 登录／刷新 |
| --- | --- | --- | --- | --- | --- |
| `baidu` | `cookie` | 是 | 是 | `streaming` | 二维码／Cookie 检测 |
| `quark` | `cookie` | 是 | 是 | `streaming`, `download` | 二维码／Cookie 检测 |
| `uc` | `cookie` | 是 | 是 | `streaming`, `download` | 二维码／Cookie 检测 |
| `tianyi` | `cookie`, `username`, `password` | 是 | 否 | `streaming`, `download` | Cookie 检测／普通密码刷新 |
| `yidong` | `authorization`, `refreshedAt` | 是 | 否 | `streaming`, `download` | Authorization 刷新 |

`refreshedAt` 为毫秒时间戳（非负安全整数），其余字段为字符串。天翼可只配置 Cookie；保存 username/password 才能在凭据失效后自动普通登录，不绕过验证码。移动 list/streaming 可匿名，download 需要 Authorization。

| operation | 请求 | 成功 value |
| --- | --- | --- |
| `providers` | `{}` | `{providers:[{provider,credentialFields,list,prepare,modes,login,refresh}]}` |
| `settings.get` | `{}` | `{accounts:[账号状态或{provider,errorCode:"CORRUPT_STORAGE"}]}` |
| `settings.set` | `{provider,credentials:{...}}` | 对应账号状态 |
| `settings.clear` | `{provider}` | 新 revision 的空账号状态 |
| `refresh` | `{provider}` | `{refreshed:true,account:账号状态或单项错误}` |

每个 provider 只有一份当前账号。`settings.set` **整份替换**该账号配置，不是合并；clear 删除凭据但保留递增 revision。`configured/fields` 只说明哪些字段非空，不证明账号有效或具备会员资格。普通配置读取、刷新及登录结果不回传原始秘密；不存在“读取 Cookie”接口。不同 provider 不互相读写凭据，也不接受另一 provider 的文件引用。

普通操作忽略未消费参数，不按字段白名单拒绝请求。`settings.set` 只提取所选 provider 定义的凭据字段，其他字段不会进入持久配置、认证或网络；已定义的字段仍按实际凭据类型及 HTTP 头要求处理。只含未知凭据字段的请求投影为空配置，仍遵循整份替换语义。顶层附加的 `method/url/headers` 等不能改变实际操作或目标，clear 也不会消费附带的 credentials。

加载配置只检查文档根、version 与 accounts 容器，不对全部账号或凭据做预检。provider 只能取得自己声明的字段，并在实际读取该字段时检查类型及头值约束；例如天翼使用健康 Cookie 列分享时，不预检未用的密码。持久凭据中的未知字段不参与 configured/fields，也不会成为 HTTP 头或取得认证权限。选定账号的 revision 必须有效，不能给坏记录补造身份。

健康账号状态仍为 `{provider,revision,configured,fields,status}`。`settings.get` 为每个已知 provider 返回一项；无法投影的账号返回 `{provider,errorCode:"CORRUPT_STORAGE"}`，不附造 revision、fields 或 signedOut，不回填此前成功状态。一个坏账号不会阻断其他账号的读取、请求或变更；它的原始记录继续保留。未知 provider 记录保留在文档中，但不注册能力或授予权限。坏根／存储读取失败仍使配置读取与依赖配置的操作整体失败，不将其报告为空配置。独立数据库中新账号行依当前 schema 初始化；schema 损坏或 revision/凭据关系不符合约束时明确失败，不将错误伪装成空配置。

可投影账号状态中的 `status` 为 `signedIn/expired/signedOut`。无凭据为 `signedOut`；已有凭据且本进程未收到明确账号认证失败时为 `signedIn`，不是永久有效性保证。`refresh` 成功后为 `signedIn`，明确账号认证失败后为 `expired`；网络、超时、上游格式等错误不改变此前状态，也不删除凭据。刷新只证明实际使用的凭据路径；未用字段仍损坏时，刷新可以成功，但 account 仍返回上述显式单项错误，不把坏材料投影成健康配置。此观察状态不另建持久库，配置读取本身不执行在线验证；原生离线配置投影与显式验证见[设置页](#设置页)。分享密码错误不作为账号失效依据。

五个 provider 均支持 `refresh`。Cookie-only 账号使用只读账号接口检测，不列分享、不转存；支持密码或 token 的账号按其协议刷新。天翼只有 Cookie 时也可检测；用户名与密码仍用于普通登录，不绕过验证码。本地保存凭据不等于验证成功；需要验证时显式刷新，只有真实在线回执才报告已验证。

运行器 standalone owner 和 App 注入模式均使用同一 SQLite schema 的 `pan_accounts` 行（配合 `owner_revisions` 的 `pan.accounts` owner CAS）；App 模式原生 UI 直接写 `pan_accounts` 对应 provider 行，Node 扫码／刷新只能经受信 provider revision CAS 回写当前账号。凭据为所属关系字段内的纯明文值，不使用 DPAPI、Keychain、应用层加密或第二凭据库。每个 provider 的 account revision、row revision 和凭据在同一事务提交。RPC、上游响应和显式用户账号导入／导出仍可使用 JSON，但内部不生成 `accounts.json`，不把整份账号 JSON 存入数据库。

手动换号／clear 撤销旧代次的进行中网络、登录和文件引用，旧结果不得覆盖新配置。自动刷新只 patch 当前代次，轮换凭据持久化成功后才报告成功。

aggregate 装配还将该 owner 的账号变更连接到 Cat 管理同步：保存、扫码确认、刷新／轮换及清除后主动安排；原生 owner 重读及脚本新 generation 同样同步当前事实。此同步与五盘接管开关独立，脚本管理保存失败不回滚本端账号提交。只有脚本实际支持的管理协议、字段和端点才会接收凭据，成功必须得到私有卷持久回执；不支持与失败分别报告，未配置过的空账号不擦除脚本自行管理的账号。同账号代次的凭据轮换不会因脚本同步而取消内置 pan 的请求或媒体；真实换号／退出仍按账号 revision 撤销旧权限。同步完成状态及缓存／关闭边界见 [Cat 管理同步](cat-script-runtime.md#管理同步与生命周期)。

### 原生凭据 store

`createPanService`／`createPanRuntimeModule` 可接收受信 typed `store`，提供 `read/write/close` 和可选 `reload`；注入后不会建立第二份账号存储。`settingsChanged()` 通过串行变更边界重读同一原生 owner，撤销旧账号代次的请求、登录及引用。

重读时仅撤销 revision 或凭据材料发生变化的账号，不因坏兄弟中止健康账号；坏根／读取失败则撤销依赖该文档的进行中操作，并等待后续有效通知恢复。CAS 冲突仍重读最新文档、向原请求返回 `STALE_ACCOUNT`，不自动重试覆盖。原生 capability 传递当前业务值和 revision；数据库使用 typed fields，不经 JSON 文档 clone 作为持久边界。

`settings.set/clear` HTTP 接口仍保留，但实际写入也必须经过 supplied store；它们不绕过原生 CAS。iOS／Windows 普通页面直接调用原生 owner，运行器通过私密 `pan.accounts` 单 provider CAS 写回扫码及刷新所得凭据。新凭据持久化成功才发布 confirmed／refreshed；手动换号、清除或其他块内容变化导致provider 的 account／row revision 冲突时，旧请求不能覆盖新值。共同规则见[原生设置契约](../common/settings-storage.md#owner-与并发写入)，iOS 私密能力见[存储](../ios/storage.md)，Windows 接线见[原生设置](../windows/native-settings.md#网盘凭据回写)。

## 分享与播放

| operation | 请求 | 成功 value |
| --- | --- | --- |
| `list` | `{provider,share:{url,password?}}` | `{share:{id,url,password},files:[{name,path,size?,file}]}` |
| `prepare` | `{provider,file}` | `{file}`，`kind:"stored"` |
| `play` | `{provider,file,mode}` | `{url,headers,mode,file?}` |

`list` 只返回 provider 所支持的视频后缀，不增加字幕匹配、剧集规则或逐文件 M3U8 预请求。按上游分页语义完整消费列表与目录；无固定总数、深度或页数截断。重复目录防环；声明还有下一页却重复／空页时报格式错误，不把失败或部分列表包装成完整空成功。夸克／UC 的总数统计只用于提示继续读取，不等同于明确的续页信号；当前页为空且未明确声明续页时正常结束，`has_more: false` 同样结束，不因统计总数多于实际可见条目而丢弃已读取的目录。

分享请求仅将 `share.url/password` 交给 provider；嵌套扩展和顶层额外参数不透传为网络选项、HTTP 头或账号凭据。`file` 仍是原样回传的 provider 结构化引用，不因普通 extra 放宽其身份、账号代次或操作模式要求。

`name/path` 保留原始名称与目录路径；size 若超过安全整数则为十进制字符串。`file` 是必须完整原样回传的结构化引用，包含 `kind/provider/accountRevision` 及 provider 所需分享 ID、文件 ID、提取码、令牌等；所有数字 ID 用字符串。它不是文件名索引或旧 `vod_play_url`，不使用 `#/$/*`、Base64 播放 ID。引用可能含分享秘密，应由调用方按敏感结果处理，不写日志。

百度／夸克／UC 的 `play` 接受 share 引用，内部按需 `prepare`；其他普通盘直接按分享引用取址。转存只处理所选文件，查找／创建专用 `MeowFilm` 目录，使用转存任务返回的精确目标，不选择目录第一项或按原名猜文件。**没有自动清空、按日删除或覆盖用户文件**；取消不能回滚已完成的远端转存，完成后重复调用可能生成新副本，不提供长期去重缓存。

百度仅使用 `share/transfer` 返回的 `extra.list` 精确映射；若只返回 task ID 而没有所选文件的准确目标，报告 `UPSTREAM_FORMAT`，不增加参考实现没有的任务查询接口，也不猜测目标路径。报错不表示远端转存已经回滚。

每次 `play` 只取调用方指定的一种模式，不同时请求 streaming/download，不因失败切换 provider。返回 URL 和必要 headers，调用方负责媒体请求；headers 可能含上游要求的 Cookie／Authorization，不用于其他域名，不写日志。pan provider 自身不建立媒体 relay；film 的独立受控媒体入口不属于此 provider 的取址接口。

### 参考接入与模式含义

网盘通信能力以 meowfilm-server 的独立适配函数为依据，而不是将其播放编排整体移植。网盘的选择、优先级以及失败后是否再次尝试由调用方负责；正式 App 中由 Node film 智能会话编排，不由 pan provider 自行选择。

- **普通夸克／UC**：两种 `mode` 都调用一次同盘的 `file/download`。按参考 `quarkDirectDownloadWithCookie`／`ucDirectDownload`，在所选文件的同一响应中优先取 `streaming → play_url` 或 `download → download_url`，随后按 `download_url → play_url → url` 读取可用地址。这里的模式是字段偏好，**不保证两种地址不同，也不声明普通盘具有独立转码接口**；只有 `download_url` 时仍能提供原文件直链。不会为此请求另一种上游取址接口。
- **百度**：只公开参考实现的 `streaming` 取址；不声明不存在的独立 `download` 能力。
- **天翼**：streaming 使用 `getNewVlcVideoPlayUrl.action`，download 使用 `getFileDownloadUrl.action`，保留各自响应字段及同接口 `FileTooLarge` 处理，不套用普通夸克／UC 的字段规则。
- **移动**：streaming 只使用匿名 `getContentInfoFromOutLink`；download 使用认证签名的 `dlFromOutLinkV3`，`9530` 时仅在已有明确 `contentId` 的条件下按参考换用同端点请求体。不为带密码文件额外重列目录，也不把 list 响应里的 URL 当作播放缓存。

本地结构化引用、账号代次、取消与资源排空是调用接口和生命周期要求；它们不增加上游能力。此前明确排除的业务缓存、自动清理、旧播放 ID、媒体代理与分页截断不随参考移植恢复。

## 二维码生命周期

- `login.start({provider})` → `{sessionId,provider,status:"pending",challenge:{type:"url"|"image",url},expiresAt}`。`url` 类型供调用方编码二维码；百度 `image` 类型由运行器使用该登录会话获取上游二维码图片，以 `data:image/...;base64,...` 交付，不要求客户端补带运行器的会话 Cookie，不是网页自动化。
- `login.poll({sessionId})` → `{sessionId,provider?,status:"pending"|"scanned"|"confirmed"|"expired"|"cancelled",account?}`。confirmed 只在凭据真实取得且原子持久化成功后发布，并发 poll 共用同一在途交换，不重复提交账号。
- `login.cancel({sessionId})` 取消未完成会话；已确认的账号不会因此被清除。需要清除账号应使用 `settings.clear`。
- 同 provider 新建登录会撤销旧挑战；换号、关闭与上游有效期结束均清理临时 Cookie／令牌和计时器。未知或已清除的过期 session 报 `LOGIN_EXPIRED`。
- 运行器不后台轮询、不引入 qrencode／浏览器／脚本引擎；客户端登录浮层可见时驱动轮询，离开、关闭或切换方式时取消会话，包括清理迟到的 `login.start` 回执。已经 confirmed 的账号不因关闭浮层退出登录。

## 设置页

影视设置中的「网盘账号」保留平台、状态、操作三列及现有主题、登录浮层。未登录显示登录；已登录显示刷新与退出登录；失效显示重新登录与退出登录。操作失败走应用统一通知，在线操作可显示自身进度，不为原生配置读取增加进度条。

百度、夸克和 UC 默认扫码，可切换手动 Cookie；其余 provider 按其凭据字段手动登录。手动输入只持有当前浮层草稿，不读取或回显已有秘密，提交采用账号配置的整份替换语义。二维码 `url` 使用客户端二维码编码能力，`image` 解码运行器交付的 data URI，不套用音乐平台的域名约束。

Windows 表格直接读取原生设置，不自动请求 refresh，手动提交与退出等待原生 CAS 回执；二维码及显式验证仍调用运行器，配置读取不显示顶部进度条。页面及单行操作代次隔离迟到结果，关闭浮层不清除已保存账号；保护与回写见[原生设置](../windows/native-settings.md#网盘凭据回写)。

iOS 的 `PanAccountService(document:request:)`／`PanAccountService(document:connection:)` 直接读取原生配置；`providers/accounts/replace/clear` 不请求运行器，页面进入不自动 refresh。已有凭据但无匹配当前内容的在线观察显示「未验证」，不把 configured 当作 signedIn。手动保存及退出在原生事务完成后结算，不等待验证或旧在线工作排空；扫码、显式刷新仍请求运行器，离线真实失败。关闭浮层不清除已保存账号，手动保存完成不冒充扫码 confirmed。文件保护与状态接线见[iOS 设置](../ios/film-settings.md#网盘账号)。

未采用原生存储的消费者继续使用 `providers/settings.get` 与 `settings.set/clear`，不能把 Node HTTP 写入当作本地同步事务。页面及单行操作都须隔离迟到结果，已经接纳的持久化按真实回执结算；在线登录关闭、离页及后台取消轮询，但已 confirmed 的账号不因关闭浮层退出。端侧实际接线分别以平台文档为准，不将一端验证结果视为另一端通过。

百度未扫码时 `/channel/unicast` 可能等待超过 30 秒才返回 pending；该请求的上游预算为 40 秒，并取二维码 session 剩余有效期的较小值，仍受调用方取消控制。其他二维码请求保持 30 秒。调用百度 `pan.login.poll` 时使用现有单次调用 deadline 选项留出 45 秒：

```js
await client.call('pan.login.poll', { sessionId }, { timeout: 45000 });
```

45 秒仅覆盖本次调用的等待，不改变通用 client 默认 30 秒，也不是新增配置。实际网络超时仍返回 `TIMEOUT`，不会当作 pending。

## 上游传输、并发与安全边界

原生 `http/https` keep-alive 与 Buffer 聚合供完整响应接口使用；仅执行上游协议明确需要的 JSON/JSONP、解压、Cookie、签名与 AES/RSA。JSONP 不 eval，大整数精确保留。HTTP 重定向不设域名／Origin／降级访问门禁，显式请求头与 307/308 的 body 保留；301/302 POST 和 303 依协议转 GET 并去掉正文头。百度二维码、天翼登录和播放取址可跟随来源提供的 HTTP(S) 地址；登录 Cookie jar 仍按 Domain/Path/Secure/expiry 选取，不混入其它账号或目标的 Cookie。分享 URL 的 provider 识别属于分享协议解析，不是网站访问限制。错误只使用封闭码，不回传请求 URL、底层异常、上游正文或账号资料。

百度／天翼解析播放重定向时仅需要响应头：保持上游要求的 GET／Range，收到头后停止并排空响应，不下载媒体正文。它不是按响应大小分类，也不改变其他完整响应接口。

各请求可同时等待网络，无全局业务队列、并发额度或每账号线程。解析计算仍在 Node 主线程，不能把 async 等同于 CPU 并行。仅同 provider/账号代次的刷新与相同文件的在途转存合并；取消一个等待者不影响其他等待者，全部取消、换号或关闭时取消上游。普通网络请求默认截止 30 秒；协议任务轮询受自身截止时间和调用方取消控制。`close()` 等待实际工作排空，不以等待者提前取消冒充物理释放。

## 错误与验证

业务错误：`INVALID_REQUEST/UNSUPPORTED_OPERATION/NOT_CONFIGURED/AUTH_REQUIRED/AUTH_FAILED/CHALLENGE_REQUIRED/NOT_FOUND/UPSTREAM_HTTP/UPSTREAM_FORMAT/UPSTREAM_ERROR/NETWORK_ERROR/TIMEOUT/CANCELLED/CLOSED/STALE_ACCOUNT/STORAGE_ERROR/CORRUPT_STORAGE/LOGIN_EXPIRED/PAN_FAILURE`。调用方应按 code 处理，不解析供应方文案。不支持的模式、provider 操作及失效引用在网络副作用前拒绝。

离线 provider 测试使用合成凭据和记录式 HTTP 替身；公共传输用本机真实 HTTP 验证；service 与公共 HTTP 测试覆盖持久化、代次、并发、取消及关闭；通用打包测试验证新闭包与原默认三模块。真实账号挑战、会员／频控、真实转存、Windows/iOS App 接入不由这些测试证明。
