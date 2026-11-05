# 共用原始 LX 音源契约

## 工厂与当前身份

业务实现位于 `apps/runtime/modules/music-script.mjs`，不依赖 Windows 启动方式：

```js
createMusicScriptModule({
  call,
  hostFactory = createScriptHost,
  network = createNodeNetwork(),
  clock = Date.now,
  setTimer = setTimeout,
  clearTimer = clearTimeout,
})
```

构造阶段不执行用户脚本、不读取用户文件、不发起网络请求。入口只装配明确业务 owner；安装与调用由原生显式命令触发。

```text
id: music
version: 0.7.0
contract: music.script.v2
```

响应为 `{ok,contract,...}`；失败只返回固定 `code`，不返回源码、配置、存储 grant、原生错误正文或堆栈。

## RPC

所有方法以 `music.script.` 为前缀：

| 方法 | 作用 |
| --- | --- |
| `stage.begin` | 创建有限期源码暂存，校验 basename、长度和 SHA-256 |
| `stage.chunk` | 按 offset 顺序写入规范 Base64 分片 |
| `stage.abort` | 幂等释放暂存并清零内存 |
| `control.begin/chunk/abort` | 暂存完整控制参数 JSON，与源码暂存分离 |
| `control.call` | 消费控制参数 token，调用明确的原有业务方法 |
| `blob.read/release` | 分片读取并释放完整业务成功信封 |
| `load` | 完整源码校验后安装或更新一个明确实例 |
| `unload` | 按 installation、revision 和可选 generation 卸载 |
| `invoke` | 定向调用实例、site 和声明动作 |
| `status` | 返回当前实例，不访问原生文档 |
| `close` | 封闭新请求并回收本模块拥有的实例和任务 |

## 控制数据传输

控制请求和结果不设独立总字节上限；通用 bridge 的 1 MiB 是物理单帧边界，不是 LX 业务数据上限。小请求和结果保持原有 JSON 形状，大数据由传输适配层透明上传或读回，上层仍使用相同的 `music.script.v2` 业务值。

- 完整参数 JSON 不超过 64 KiB 时直接调用原方法；更大时，先以 `control.begin({length,sha256})` 取得 token，通过 `control.chunk({token,offset,data})` 连续上传，再以 `control.call({operation,token})` 执行。`operation` 仅为 `load`、`unload`、`invoke` 或 `status`；不得递归分派分片方法。
- 完整业务成功信封不超过 64 KiB 时直接返回；更大时返回 `{ok:true,contract:"music.script.v2",blob:{token,length,sha256,chunkBytes}}`。blob 内容是原始完整成功信封的 UTF-8 JSON，不是仅 `value`。业务 `value` 内的 `blob` 字段不会被当作传输描述符。
- `blob.read({token,offset})` 返回普通小信封中的 `data`（规范 Base64）和 `next`。调用方核对每片长度、next、完整长度和 SHA-256，解析原信封并核对契约后恢复业务结果，不向上层暴露分片元数据。
- 上传和读取的字节块均最多 64 KiB；字符串可跨块拆开，整份完成后才严格解码。逻辑 JSON 与直接 bridge JSON 采用相同的 Unicode、重复键、ASCII 属性名、安全整数和深度校验，不借分片绕过数据契约。
- 每个调用在成功、失败、取消或超时后释放其已知上传 token／结果 token。失去收据的未完成暂存、未领取结果按最近有效活动起五分钟主动回收；模块关闭清空全部传输，并继续关闭脚本 host，不以取消调用代替实际资源回收。

源码 `stage.*` 与控制暂存相互独立；物理分片帧和释放回包不再套一层分片。控制分片不改变 generation、grant、定向调用和原生持久事务的校验。

## 暂存与加载

- basename 只允许单个 `.js` 文件名，不允许路径。
- 整份源码最多 16 MiB，在 `stage.begin` 按声明长度提前拒绝；不设置活动暂存 token 数量上限，未完成暂存仅按超时、显式取消和模块关闭回收，实际分配失败按资源错误处理。
- 分片最多 64 KiB，必须连续，长度和小写规范 SHA-256 必须一致。
- 完成后严格 UTF-8 解码，不接受替换字符或非规范字节。
- 暂存从创建或最近一次有效分片起五分钟后由单个主动 timer 回收；任一暂存变更后按最早过期时间重排，陈旧回调只清理已过期项，模块关闭时同步取消并清空全部暂存。失效 token 返回 `STAGE_NOT_FOUND`。
- stage 是一次加载事务的临时字节容器，不是持久记录或文档桥。

`load` 必须包含：

```text
token installationId revision expectedRevision version config permissions grant
```

- `revision` 是正安全整数；`expectedRevision` 精确匹配当前 binding，首次安装为 0。
- `config` 是有界 JSON 对象，最多 32 KiB。
- `permissions` 只有 `storage`，值为不重复的 `read`／`write` 数组；不接受网络 origin、私网或域名授权字段。
- `grant` 是非零 UUID，属于原生存储会话绑定，不是网络权限。
- 相同 revision 仅在源码、版本、config、storage 权限和 grant 全部一致时幂等；新 revision 必须更大并使用未使用过的 grant。
- 不设安装实例数量上限；load 只安装启用实例。禁用和删除由原生 owner 调用 unload，公共模块不保存 disabled 影子状态。
- 更新失败仅在公开 host 保持旧 generation、源码和 ready 状态时保留旧 binding。host 已换代时退休改变后的会话并要求原生明确协调，不通过再次生成代次伪造回滚。

实例响应只含 installationId、revision、generation、state、contentHash、protocol、version 和 capabilities，不泄漏配置、grant 或原生数据路径。当前 protocol 明确为 `lx`。

## 动作与请求语义

`invoke` 必须包含：

```text
installationId revision generation siteId operation payload
```

site/action 来自脚本有效声明，不以旧平台／两三个动作白名单裁剪扩展动作。可选 qualitys 对象 `{type,label}` 归一为 type，字符串音质保持自身身份。声明并不证明处理器存在或业务已成功，实际调用仍需验证；目标不存在、代次过时、实例未 ready、动作未声明或音质不支持时明确失败，不遍历其他实例或静默换源。

LX 请求数据保留 `{source,action,info}`：

- source 为明确 siteId，action 为明确 operation。
- `musicUrl` 将 `payload.type` 或 `payload.quality` 映射为 `info.type`，保留 `musicInfo`；非 local source 仍需匹配声明音质。
- 其他声明动作保留自身数据语义；`search` 与 `musicSearch` 不被强行视作所有脚本通用的别名。
- 返回数据按 LX 处理器实际值传递，不改成 Cat 或 Reader 的业务格式。

脚本内部字节处理、桥接与 `invokeResult` 受单脚本内存上限约束；宿主缓冲网络响应体及解压输出以同一内存上限为上界。不设全局／实例／site 调用数量预算。没有单次同步执行时限；初始化等待上限为 5 秒，调用等待与请求内截止均为 60 秒。调用或初始化等待到期后由主线程探测对应 worker，`cleanupTimeoutMs` 内无回应才终止并判 `TIMEOUT`。

## 请求绑定的存储

请求处理器第二参数 `context.storage` 的 get/set/delete 是 v2 的唯一存储入口；它绑定调用 ID、site、installation 和 generation，不通过 `AsyncLocalStorage` 推断 guest 异步身份。全局 `meow` 只提供 `config`，startup 和 background 不获得隐式存储权限，保存的旧 context 不能借用新调用。完整边界见 [共用核心](script-runtime.md#v2-特权存储)。

宿主对原生只发出：

```text
platform.musicScript.storage.get
platform.musicScript.storage.set
platform.musicScript.storage.delete
```

消息只含：

```text
grant installationId siteId generation key value?
```

- grant 仅在受信适配层与原生存储 owner 之间传递，guest 不获得它。
- 每次回调在调用前后核对实例、generation、site、当前绑定与取消信号。
- 原生保持安装事务与 storage overlay 的发布归属；候选 binding 的登记不赋予 LX startup 特权。
- 旧实例与迟到回调不能借用新 binding 的 grant；结束的请求 context 不能被其他活跃调用接管。
- 未结束 storage 任务不设固定数量预算，按调用和实例生命周期回收；物理任务保留至 adapter 真正结束。
- guest 存储回调随请求完成、取消和退休释放；物理任务仍保留归属至 adapter 真正结束。

脚本 guest 不获得任意文件、进程、官方账号凭据、应用数据根或通用 document bridge；可信 Node 引擎加载自身锁定的 WASM／asmjs 不构成 guest 文件权限。

## 网络

注入适配器接收 `validateURL`，只验证 HTTP(S) 语法；没有域名／私网授权名单，也不按源码哈希补 origin。网络能访问已开放服务，不能将文件／进程隔离写成网络隔离。

LX `request` 的回调、返回体和取消语义保留；`follow_max` 在 Node 适配器中受最大重定向次数约束，不等于任意 LX 版本都定义该字段。`showConfigView` 等扩展事件也不等于当前已提供 Web 管理服务，需依脚本实际路径单独验收。

已遇到的扩展选项 `followRedirect` 按布尔值控制自动跳转；false 优先关闭跳转，true 仍遵守重定向上限，`follow_max` 超过上限时按上限截断。`retry` 兼容接受，不额外启动宿主重试或重放 POST；与核对的 LX 桌面 request 封装不消费该字段的行为一致。网络任务在取消后仍保持登记，直至底层确认结束。

LX 继续使用原有网络适配器及请求绑定的原生 storage grant，不套用 source 的 capability 网络包装。Reader/CatVod 的[capability ABI](source-capability-abi.md)属于独立输入契约；三类 module 只共用 WorkerSession、worker 协议和 QuickJS 机制，不互换 site/action/quality 或数据权限。

## 取消与关闭

- 控制调用消费调用方 signal；取消 fence 当前结果并退休所属作用域，不假装不合作 adapter 已结束。
- 忽略 signal 的外部任务仍计入实际 drain，不能确认排空时返回 `SCRIPT_CLEANUP`。
- unload、失败更新和 close 退休对应存储 binding。
- close 幂等；成功必须证明 host 中无本模块当前实例且所属任务已排空，不释放其他业务借用的核心。

## 固定错误码

```text
INVALID_REQUEST
LIMIT
STAGE_NOT_FOUND
SCRIPT_INTEGRITY
SCRIPT_ENCODING
STALE_SOURCE
SOURCE_NOT_FOUND
SOURCE_DISABLED
PERMISSION_DENIED
CAPABILITY_UNAVAILABLE
NETWORK_ERROR
UPSTREAM_HTTP
UPSTREAM_FORMAT
STORAGE_ERROR
TIMEOUT
CANCELLED
CLOSED
SCRIPT_CLEANUP
SCRIPT_FAILURE
```

测试夹具、引擎打包回归和真实外部脚本验收不能互相冒充。外部验收枚举实际声明动作，按协议传递输入和结果，明确区分宿主能力、资源限额、输入、脚本和上游失败；见 [动态验收](source-acceptance.md)。
