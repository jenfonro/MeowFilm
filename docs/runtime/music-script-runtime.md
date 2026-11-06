# 共用原始 LX 音源契约

## 工厂与当前身份

业务实现位于 `apps/runtime/modules/music-script.mjs`，不依赖 Windows 启动方式：

```js
createMusicScriptModule({
  call,
  hostFactory = createScriptHost,
  network = createNodeNetwork(),
})
```

构造阶段不执行用户脚本、不读取用户文件、不发起网络请求。入口只装配明确业务 owner；安装与调用由原生显式命令触发。

```text
id: music
version: 0.8.0
contract: music.script.v3
```

成功响应为 `{ok:true,contract,value}`；失败只返回固定 `code`，不返回源码、配置、存储 grant、原生错误正文或堆栈。

## HTTP 业务入口

公共 [runtime.http.v1](runtime-bridge.md) 将本地逻辑名 `music.script.*` 映射到以下 POST operation：

| 方法 | value / 作用 |
| --- | --- |
| `load` | `{instance}`，按完整 sourceRevision 安装或更新明确实例 |
| `unload` | `{unloaded:true,cleanupComplete:true}`，installation/revision/可选 generation |
| `invoke` | 脚本业务值或 null，不再额外包 `{value:...}` |
| `status` | `{documentAccess:false,instances}`，不等待其他安装初始化 |
| `close` | `{closed:true,cleanupComplete:true}`，只关闭 music owner |

## 控制数据传输

普通参数一次 JSON POST，结果直接在统一 value 中返回，不设普通 JSON 总字节上限，不按大小选择另一套协议。严格 UTF-8、重复键拒绝、Unicode 键原样保留、业务值深度 64 和安全整数检查保持；业务字段名 `blob` 等只是普通数据。旧 frame、stage/control/blob、Base64、token/TTL/offset 和传输 checksum 均已删除。

## 源码候选与加载

`PUT /v1/music/sources/{installationId}/{sourceRevision}?basename=...` 使用原始 body，一次完整登记 1 至 16 MiB 源码。basename 是 255 字符内无路径 `.js` 文件名。严格 UTF-8 保留原有 BOM 行为，合法 U+FFFD 字符可保留，非法字节不可由替换解码蒙混通过。上传不执行源码、不改变 enabled、不新增磁盘事实源。

同安装只保留一个尚未采用的完整候选；新上传完整成功后才替换，未完成/断线 body 不影响旧候选。仍持有的同键同内容幂等、不同内容报 SCRIPT_INTEGRITY；内容 hash 算一次并复用。没有过期 timer 或上传读取/release 动作。load 必须精确选择 sourceRevision，不存在报 SOURCE_NOT_FOUND；字段/身份/CAS 前置拒绝不消费候选，试运行失败释放，下一次试运行须显式上传。卸载和关闭释放未采用候选，当前/退休执行内容由实际 owner 保留至无引用。

`load` 必须包含：

```text
sourceRevision installationId revision expectedRevision version config permissions grant
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

LX 每实例使用一个原生线程、一个 QuickJS Runtime/Context。普通字节、网络响应、解压与脚本内数据不再受运行器自设内存配额限制，无配额耗尽自动重启；不设全局/实例/site 固定调用数。初始化等待 5 秒、调用与请求截止 60 秒、清理预算 100 ms 保留；原生执行可由主控取消并等待真实线程结束，没有新增单段同步计算时限。

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

脚本 guest 不获得任意文件、进程、官方账号凭据、应用数据根或通用 document bridge；可信 Node 只加载精确受信原生扩展，该加载不构成用户脚本文件权限。

## 网络

注入适配器接收 `validateURL`，只验证 HTTP(S) 语法；没有域名／私网授权名单，也不按源码哈希补 origin。网络能访问已开放服务，不能将文件／进程隔离写成网络隔离。

LX `request` 的回调、返回体和取消语义保留；`follow_max` 在 Node 适配器中受最大重定向次数约束，不等于任意 LX 版本都定义该字段。`showConfigView` 等扩展事件也不等于当前已提供 Web 管理服务，需依脚本实际路径单独验收。

已遇到的扩展选项 `followRedirect` 按布尔值控制自动跳转；false 优先关闭跳转，true 仍遵守重定向上限，`follow_max` 超过上限时按上限截断。`retry` 兼容接受，不额外启动宿主重试或重放 POST；与核对的 LX 桌面 request 封装不消费该字段的行为一致。网络任务在取消后仍保持登记，直至底层确认结束。

LX 继续使用原有网络适配器及请求绑定的原生 storage grant，不套用 source 的 capability 网络包装。Reader/CatVod 的[capability ABI](source-capability-abi.md)属于独立输入契约；LX/source 共用原生绑定机制，Cat 使用独立 Node Worker；三类 module 共用 HTTP 与生命周期基础设施，不互换 site/action/quality 或数据权限。

## 取消与关闭

- HTTP abort 或未完成 response close 取消对应调用；正常 finish 不误取消。取消 fence 结果并退休所属作用域，不假装不合作 adapter 已结束。
- 忽略 signal 的外部任务仍计入实际 drain，不能确认排空时返回 `SCRIPT_CLEANUP`。
- unload、失败更新和 close 退休对应存储 binding。
- close 幂等；成功必须证明 host 中无本模块当前实例且所属任务已排空，不释放其他业务借用的核心。

## 固定错误码

```text
INVALID_REQUEST
LIMIT
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

Windows/iOS 旧调用接入本轮未迁移，新公共 wire 不保留旧协议回退。通用原生构建与定向回归已通过，适用环境及未覆盖项见[通用验证范围](script-runtime.md#构建发布与验证边界)。

测试夹具、引擎打包回归和真实外部脚本验收不能互相冒充。外部验收枚举实际声明动作，按协议传递输入和结果，明确区分宿主能力、资源限额、输入、脚本和上游失败；见 [动态验收](source-acceptance.md)。
