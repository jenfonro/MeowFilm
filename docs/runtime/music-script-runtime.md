# 共用原始 LX 音源契约

## 工厂与身份

`apps/runtime/modules/music-script.mjs` 导出 `createMusicScriptModule({dataRoot, hostFactory, network, download, clock, setTimer, clearTimer})`，身份为 `music@0.8.0` / `music.script.v3`。通用 `apps/runtime` 入口提供本地 HTTP；构造与启动不读取安装目录、不执行脚本、不检测，管理首次使用才打开 `<dataRoot>/music`。

成功响应为 `{ok:true,contract:"music.script.v3",value}`，失败为 `{ok:false,contract:"music.script.v3",code}`。错误只含固定分类，不泄漏源码、凭据或异常正文。音乐无需反向 callback 端点。iOS 薄适配与生命周期见[音源管理](../ios/music.md#音源管理)，Windows 的完整源码与原生消费者见[平台接入](../windows/music-script-runtime.md)；公共测试不代替平台 App 验证。

## 完整 HTTP 输入与业务方法

文件先上传一次完整源码：

```text
PUT /v1/music/sources/{installationId}/{sourceRevision}?basename=fixture.js
Content-Type: application/octet-stream
```

首次文件导入的 `installationId` 由调用方预分配 UUID；`sourceRevision` 是正整数，只标识该安装的完整候选，与 catalog revision 或执行 generation 无关。完整接收、UTF-8 和源码边界通过后才替换待选候选；中断或非法上传不改变安装和既有完整候选。同身份重传必须保持源码及 basename 相同。管理精确选择候选；成功候选可重复选择以得到内容不变的结果，失败试运行释放其输入，后续已上传候选保持独立。

所有业务调用以 `POST /v1/music/<operation>` 发送完整 JSON；Node 调用端的方法前缀为 `music.script.`。

| 方法 | 输入与 `value` |
| --- | --- |
| `sources.list` | `{}` → `{revision,items}`；只读安装事实，不执行脚本 |
| `sources.detail` | `{installationId}` → `{item}`；网络源详情包含完整更新地址 |
| `sources.add` | `{installationId,sourceRevision}` → `{item}`；消费完整文件候选，创建停用安装 |
| `sources.download` | `{url}` → `{item}`；运行器分配 UUID，下载并创建停用网络安装 |
| `sources.edit` | `{installationId,name?,url?}`；只改显示名与网络地址，不改变来源类型 |
| `sources.update` | `{installationId,sourceRevision?}`；文件必须指定候选，网络只从保存地址下载 |
| `sources.rollback` | `{installationId}`；交换 current 与 previous |
| `sources.enable/disable/remove` | `{installationId}`；启用成功才保存 enabled，停用/删除退休实例 |
| `sources.check` | `{installationId}`；独立轻量检测 |
| `resolve` | `{installationId,platform,quality,musicInfo}`；一次具体音质的媒体解析 |
| `invoke` | `{installationId,siteId,operation,payload}`；返回原始业务值 |
| `close` | `{}` → `{closed:true,cleanupComplete:true}`；停止接收并清理本模块 |

`invoke` 的 HTTP `value` 就是脚本结果，包括普通 `blob`、`token`、`value` 字段；不再包一层安装身份。调用选择当前启用内容，执行前捕获 generation，返回前核对未退休。客户端不传外部 CAS、grant、config 或 permissions；旧 load/unload/status 与手工分片接口不提供兼容路径。

`resolve` 的 `value` 包含 installationId、generation、contentHash、platform、quality、url、headers、expiresAt、duration。URL 必须为 HTTP(S)，拒绝凭据 URL、试听/加密标志、禁止请求头、过期时间及相反目标身份。脚本提供的正有限时长以秒返回；候选与已知录音时长均为正数时，绝对差不超过10秒通过时长校验，超过10秒返回 `RECORDING_MISMATCH`。未提供、null 或 0 返回 `duration:null`，任一时长未知时不判录音不匹配；非数字或负时长返回 `INVALID_MEDIA_CANDIDATE`。非有限数值先由原生 `RuntimeValue` 边界拒绝，不转换为未知时长。

时长独立于音质，无法确认实测音质时仍保留已知时长供原生媒体准入检查。`quality` 只回显请求档位，不代表已实测音质。不补搜、不换源、不降级；元数据融合的百分比容差不适用于媒体回执。

公开请求与业务结果遵守64层 JSON 契约，65层拒绝；控制信封和内部 KV 文档包装不占用业务值深度。完整 JSON 不设独立总字节上限，不使用应用层 stage/control/blob、Base64 分片或 TTL 暂存。

## 安装事实与私有 KV

音乐模块是 `<dataRoot>/music` 的唯一 writer。`PrivateDocuments` 限定私有路径、拒绝链接与路径穿越，原子替换完整文件。

- `catalog.json`：schema、revision、UUID、名称、origin、enabled、current/previous、完整声明与检测快照。
- `sources/<sha256>.js`：内容寻址源码；current、previous 和正在试运行的候选保留，完成管理后回收无引用文件。
- `data/<installationId>.json`：按 site 隔离的 guest KV，删除安装时先删除私有数据。
- 文件/网络来源由导入操作决定，不由脚本头推断。网络地址持久保存，列表只显示 host，详情返回完整 URL。
- 客户端可保存常用列表和检测结果等读取快照；运行器仍是安装事实所有者。不读、不迁移、不双写、不删除旧原生数据，不保存脚本执行内存快照。
- 损坏目录/文档明确报错，格式损坏为 `CORRUPT_STORAGE`，I/O 写失败为 `STORAGE_ERROR`，不静默重置目录或发布未落盘状态。
- 源码最多16 MiB、严格 UTF-8、非空；允许前导 BOM，文件名为平面 `.js` basename。脚本头只提供名称、版本、作者、描述和主页元数据。

脚本 `meow.config` 固定为空对象；处理器第二参数的 `context.storage` 提供私有 get/set/delete。请求内读写固定启用，每次操作检查 scope 当前性、installation、generation、声明 site 和取消信号。startup/background 无存储权限，旧 context 不能借用后续请求的权限。

KV 保留持久化数据边界：每值16 KiB，每 site 最多64键/64 KiB，每安装最多32 sites/512 KiB。逐值独立验证 JSON；site/安装文档只做编码字节计量，不把包装层计入64层业务限制。这些是数据契约，与脚本引擎堆配额无关。

## 声明、检测与生命周期

安装的 `capabilities` 返回全部站点及完整声明，包括扩展 site/action。`sites` 仅是五平台 actions/qualitys 的界面投影；执行和验收 CLI 使用完整声明，不据五平台投影截断能力。

轻量检测为五平台各一个固定公开样本，选择声明的基础音质调用 musicUrl，只校验取链契约，不下载音频。停用源临时加载后卸载，enabled 保持不变；检测和播放拥有独立请求取消信号。持久检测仅含内容身份、时间、样本、音质、状态和固定错误码，不保存媒体 URL、请求头或凭据。内容变化使旧结果失效，不后台复检。

同安装生命周期顺序执行，catalog 提交为短同步操作；A 的下载、初始化和退休不阻塞 B 管理、调用或列表查询。有效替换、停用、删除在等待队列前取消被捕获的真实初始化/下载 owner，迟到取消不选择后继实例。LX 更新试运行失败保留原 generation 和状态；候选发布后的清理或持久化失败会退休未确认代次并明确报错。

冷启动只恢复目录，启用来源在首次 invoke/resolve/check 时惰性加载。同进程失败的 owner 或失败恢复记录不会被普通调用静默重建；列表报告 failed，可由显式 enable 或更新重试。正常实例不闲置回收、不自动重启，运行变量自然保留。

取消或请求到期立即封闭权限与结果；异步存储适配器的 `signal/isCurrent` 随真实 capability 撤权失效，不等待调用结算。有限尾链仍沿既有清理时限收尾，网络任务仍登记到实际完成。关闭先终止真实 owner，再排空安装事务与源码下载；清理不能确认时报告 `SCRIPT_CLEANUP`。停用/删除后旧 generation 的结果和 KV 操作不能发布。

## 网络与承载

源码下载默认使用 `createSourceDownload({maxSourceBytes:SCRIPT_LIMITS.maxSourceBytes,maxRedirects:0})`。保留 HTTPS `.js` URL、无凭据/fragment、拒绝重定向、只接受200、30秒期限。源码实际传输及显式解压都受16 MiB边界约束；取消等待 request/response/socket/解压流的真实完成。

普通 guest 网络仍使用 `createNodeNetwork`，只验证 HTTP(S) 语法，不设域名或私网授权名单，不继承源码下载字节边界。LX request 的回调、字节和取消语义保持；followRedirect=false 禁止跳转，true 与 follow_max 遵守既有重定向上限，retry 不启动宿主重放。

LX timer、microtask 和 request 回调的调用、返回值吸收及同步／异步异常分类均受活跃来源的原执行控制；正常回调异常只诊断，退休来源的晚回调保留 session 语义且不能重新取得存储权限。网络 I/O 仍由 session 持有，失败跨线程只传安全分类，进入可信回调后才构造公开 Error；成功响应不额外包装或复制字节。

LX 使用逐实例原生线程及 QuickJS Runtime，脚本宿主来源策略为 [`scripts/script-host-native.json`](../../scripts/script-host-native.json)。客户端影视规则的独立 QuickJS 策略不属于本模块。其他来源的 [capability ABI](source-capability-abi.md) 是独立输入契约，不互换 site/action/quality 或数据权限。生命周期边界见[共用核心](script-runtime.md)，外部脚本验收见[动态验收](source-acceptance.md)。
