# 共用原始 LX 音源契约

## 工厂与当前身份

`apps/runtime/modules/music-script.mjs` 的 `createMusicScriptModule({dataRoot, hostFactory, network, clock, setTimer, clearTimer})`
由两端薄 entry 装配，身份为 `music@0.8.0` / `music.script.v3`。构造与 App 启动不读取安装目录、不执行脚本、不检测。
管理首次使用才打开 `<dataRoot>/music`。响应为 `{ok,contract,...}`，失败仅返回固定 `code`，不泄漏源码、凭据或错误正文。

## RPC

所有方法以 `music.script.` 为前缀：

| 方法 | 输入与结果 |
| --- | --- |
| `sources.list` | `{}` → `{revision,items}`；只读安装事实，不执行脚本 |
| `sources.detail` | `{installationId}` → `{item}`；网络源详情包含完整更新地址 |
| `sources.add` | `{token}` → `{item}`；消费已选文件的 stage，创建停用的本地安装 |
| `sources.download` | `{url}` → `{item}`；HTTPS 下载、校验并创建停用网络安装 |
| `sources.edit` | `{installationId,name?,url?}`；只改显示名和网络地址，不能编辑源码或改变来源类型 |
| `sources.update` | `{installationId,token?}`；本地必须传新 stage，网络从已保存地址下载 |
| `sources.rollback` | `{installationId}`；交换当前与上一版源码引用 |
| `sources.enable/disable/remove` | `{installationId}`；启用成功才保存启用状态，停用/删除退休实例 |
| `sources.check` | `{installationId}`；独立轻量检测，不进入自动播放调度 |
| `resolve` | `{installationId,platform,quality,musicInfo}`；一次具体音质的 `musicUrl` |
| `invoke` | `{installationId,siteId,operation,payload}`；其他已声明动作，保留原始 `value` |
| `stage.begin/chunk/abort` | 源码暂存、顺序分片及幂等释放 |
| `control.begin/chunk/call/abort` | 透明控制参数传输 |
| `blob.read/release` | 透明业务结果传输 |
| `close` | 封闭请求，排空任务并释放本模块实例 |

`resolve` 返回 installationId、generation、contentHash、platform、quality、url、headers、expiresAt。
URL 必须是合法 HTTP(S)，拒绝凭据 URL、试听/加密标志、禁止请求头、过期时间及相反的目标身份。
脚本提供的正时长与已知录音时长相差超过 `max(3s,较短×2%)` 时返回 `RECORDING_MISMATCH`；未知时长不判错。
该接口不补搜、不选其他脚本、不降级；原生选源会话拥有这些策略。请求音质不是实测音质。

## 持久化与隔离

`PrivateDocuments` 是 script-host 提供的通用私有目录原语：固定 area、平面文件名、拒绝链接与路径穿越、原子文件替换。
音乐模块是唯一 writer；Windows/iOS 不再保存安装目录、下载源码或处理反向 storage RPC。

- `catalog.json`：当前安装格式、revision、UUID、名称、origin、启用、current/previous、声明及检测快照。
- `sources/<sha256>.js`：内容寻址源码；当前与上一版引用仍在时保留，模块回收新目录内无引用内容。
- `data/<installationId>.json`：guest 私有 KV，按 site 分开，与管理目录完全分离。
- 本地/网络来源由导入操作决定，不由 homepage 或脚本头推断。网络源保存完整 HTTPS 更新地址，列表只暴露 host。
- 不读取、迁移、双写或删除旧原生脚本数据；新格式损坏明确报 `CORRUPT_STORAGE`，写失败报 `STORAGE_ERROR`，不静默重置。
- 源码最多 16 MiB，严格 UTF-8、无 BOM、非空；stage basename 只能为平面 `.js` 文件名。
- 网络导入/更新拒绝重定向、凭据 URL 与非 200，30 秒期限及 16 MiB 响应资源边界。
- guest 只获得处理器第二参数 `context.storage`；每次操作检查活跃请求、installation、generation、声明 site 和取消信号。
  startup/background 没有存储权限，旧 context 不能借用新调用。全局 `meow` 只提供 config。
- KV 资源边界：每值 16 KiB、每 site 64 个键/64 KiB、每安装 32 sites/512 KiB；不向 guest 暴露管理数据或应用目录。

## 轻量检测

五平台固定样本，按脚本声明的基础音质各调用一次 musicUrl，只校验取链契约，不下载音频。
不支持平台为 unsupported；停用脚本临时加载后卸载，原启停不变；检测与播放解析各自持有取消信号。
持久事实只含时间、内容身份、样本、音质及各平台状态/固定错误码，不保存媒体 URL、请求头或凭据。
内容变化使旧结果失效，不后台复检。状态由有效检测推导为未检测、正常、部分可用或异常，加载失败亦为异常；启用不代表健康。

## 控制数据传输

控制请求和结果不设独立总字节上限；通用 bridge 的 1 MiB 是物理单帧边界，不是 LX 业务数据上限。小请求和结果保持原有 JSON 形状，大数据由传输适配层透明上传或读回，上层仍使用相同的 `music.script.v3` 业务值。

- 完整参数 JSON 不超过 64 KiB 时直接调用原方法；更大时，先以 `control.begin({length,sha256})` 取得 token，通过 `control.chunk({token,offset,data})` 连续上传，再以 `control.call({operation,token})` 执行。`operation` 仅为管理命令、`resolve` 或 `invoke`；不得递归分派分片方法。
- 完整业务成功信封不超过 64 KiB 时直接返回；更大时返回 `{ok:true,contract:"music.script.v3",blob:{token,length,sha256,chunkBytes}}`。blob 内容是原始完整成功信封的 UTF-8 JSON，不是仅 `value`。业务 `value` 内的 `blob` 字段不会被当作传输描述符。
- `blob.read({token,offset})` 返回普通小信封中的 `data`（规范 Base64）和 `next`。调用方核对每片长度、next、完整长度和 SHA-256，解析原信封并核对契约后恢复业务结果，不向上层暴露分片元数据。
- 上传和读取的字节块均最多 64 KiB；字符串可跨块拆开，整份完成后才严格解码。逻辑 JSON 与直接 bridge JSON 采用相同的 Unicode、重复键、ASCII 属性名、安全整数和深度校验，不借分片绕过数据契约。
- 每个调用在成功、失败、取消或超时后释放其已知上传 token／结果 token。失去收据的未完成暂存、未领取结果按最近有效活动起五分钟主动回收；模块关闭清空全部传输，并继续关闭脚本 host，不以取消调用代替实际资源回收。

源码 `stage.*` 与控制暂存相互独立；物理分片帧和释放回包不再套一层分片。控制分片不改变定向调用、当前安装内容与请求取消的校验。


## 网络

注入适配器接收 `validateURL`，只验证 HTTP(S) 语法；没有域名／私网授权名单，也不按源码哈希补 origin。网络能访问已开放服务，不能将文件／进程隔离写成网络隔离。

LX `request` 的回调、返回体和取消语义保留；`follow_max` 在 Node 适配器中受最大重定向次数约束，不等于任意 LX 版本都定义该字段。`showConfigView` 等扩展事件也不等于当前已提供 Web 管理服务，需依脚本实际路径单独验收。

已遇到的扩展选项 `followRedirect` 按布尔值控制自动跳转；false 优先关闭跳转，true 仍遵守重定向上限，`follow_max` 超过上限时按上限截断。`retry` 兼容接受，不额外启动宿主重试或重放 POST；与核对的 LX 桌面 request 封装不消费该字段的行为一致。网络任务在取消后仍保持登记，直至底层确认结束。

LX 继续使用原有网络适配器及请求绑定的模块私有 storage，不套用 source 的 capability 网络包装。Reader/CatVod 的[capability ABI](source-capability-abi.md)属于独立输入契约；三类 module 只共用 WorkerSession、worker 协议和 QuickJS 机制，不互换 site/action/quality 或数据权限。


## 取消与关闭

调用方取消封闭结果，真实外部任务仍须排空；无法确认清理时报告 `SCRIPT_CLEANUP`。
同安装生命周期变更串行，实际调用可并发，返回前核对仍是同一 generation；停用/删除后旧结果不能发布。
未知回执不会触发原生自动重放写入，调用者重新读取共享管理事实。
初始化与调用的 worker 资源、请求 context 边界见[共用核心](script-runtime.md)。外部脚本验收见[动态验收](source-acceptance.md)。
