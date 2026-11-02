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
version: 0.6.0
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
| `load` | 完整源码校验后安装或更新一个明确实例 |
| `unload` | 按 installation、revision 和可选 generation 卸载 |
| `invoke` | 定向调用实例、site 和声明动作 |
| `status` | 返回当前实例，不访问原生文档 |
| `close` | 封闭新请求并回收本模块拥有的实例和任务 |

## 暂存与加载

- basename 只允许单个 `.js` 文件名，不允许路径。
- 不设置人为整份源码字节或活动暂存 token 数量上限；未完成暂存仅按超时、显式取消和模块关闭回收，实际分配失败按资源错误处理。
- 分片最多 64 KiB，必须连续，长度和小写规范 SHA-256 必须一致。
- 完成后严格 UTF-8 解码，不接受替换字符或非规范字节。
- 暂存从创建或最近一次有效分片起五分钟后由单个主动 timer 回收；timer 重排以 generation 隔离陈旧回调，模块关闭时同步取消并清空全部暂存。失效 token 返回 `STAGE_NOT_FOUND`。
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
- 安装实例最多 32 个；load 只安装启用实例。禁用和删除由原生 owner 调用 unload，公共模块不保存 disabled 影子状态。
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
- 返回数据按 LX 处理器实际值传递，不改成未来 Cat 的业务格式。

控制请求和响应各不超过 512 KiB；命令及反向存储调用不设置全局／实例／site 的 pending 数量额度。音乐同步执行片段上限为 2000 ms，初始化总上限为 5 秒，请求仍受独立截止时间约束。

## 请求绑定的存储

请求处理器第二参数 `context.storage` 的 get/set/delete 是 v2 的特权入口；它绑定调用 ID、site、installation 和 generation，不通过 `AsyncLocalStorage` 推断 guest 异步身份。全局 `meow.storage` 无权限，startup 和 background 不获得隐式权限，保存的旧 context 不能借用新调用。完整边界见 [共用核心](script-runtime.md#v2-特权存储)。

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
- 未结束 storage 任务由所属请求和实例跟踪，不因其他调用数量拒绝。
- guest 存储回调随请求完成、取消和退休释放；物理任务仍保留归属至 adapter 真正结束。

脚本 guest 不获得任意文件、进程、官方账号凭据、应用数据根或通用 document bridge；可信 Node 引擎读取自身锁定的 WASM 不构成 guest 文件权限。

## 网络

注入适配器接收 `validateURL`，只验证 HTTP(S) 语法；没有域名／私网授权名单，也不按源码哈希补 origin。网络能访问已开放服务，不能将文件／进程隔离写成网络隔离。

LX `request` 的回调、返回体和取消语义保留；`follow_max` 在 Node 适配器中受最大重定向次数约束，不等于任意 LX 版本都定义该字段。`showConfigView` 等扩展事件也不等于当前已提供 Web 管理服务，需依脚本实际路径单独验收。

已遇到的扩展选项 `followRedirect` 按布尔值控制自动跳转；false 优先关闭跳转，true 仍遵守重定向上限。`retry` 兼容接受，不额外启动宿主重试或重放 POST；与核对的 LX 桌面 request 封装不消费该字段的行为一致。网络任务在取消后仍由实例跟踪，直至底层确认结束，不设置网络任务总量额度。

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
