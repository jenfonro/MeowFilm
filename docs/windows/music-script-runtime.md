# Windows 原始 JS 音源运行时

## 当前边界

Windows App 的 Node 运行时只执行原始 LX 音源 JavaScript。它不拥有官方平台账号、搜索编排、曲库、队列、缓存、播放、设置或通用文档访问。

唯一入口：

```text
apps/windows/runtime/entries/aggregate.mjs
```

入口同步装配 `createMusicScriptModule({call})`。构造阶段不执行脚本、不读取文件、不启动网络请求；只有原生 `MusicScriptOwner` 发起明确命令时才暂存、加载或调用脚本。

## 工厂与契约

```js
createMusicScriptModule({
  call,
  hostFactory = createScriptHost,
  network = createNodeNetwork(),
  clock = Date.now,
})
```

模块 descriptor 固定为：

```text
id: music
version: 0.4.0
contract: music.script.v1
```

所有响应使用 `{ok,contract,...}`；失败只返回固定 `code`，不返回源码、配置、grant、原生错误正文或堆栈。

## 当前 RPC

所有方法以 `music.script.` 为前缀：

| 方法 | 作用 |
| --- | --- |
| `stage.begin` | 创建一次有限期源码暂存，校验 basename、长度和 SHA-256 |
| `stage.chunk` | 按 offset 顺序写入规范 Base64 分片 |
| `stage.abort` | 幂等释放暂存并清零内存 |
| `load` | 校验完整源码后安装或更新一个明确实例 |
| `unload` | 按 installation、revision 和可选 generation 卸载实例 |
| `invoke` | 定向调用一个实例、站点和操作 |
| `status` | 返回当前实例，不访问原生文档 |
| `close` | 封闭新请求并回收当前模块拥有的全部实例和任务 |

## 暂存格式

- basename 只允许单个 `.js` 文件名，不允许路径。
- 单源码最多 512 KiB。
- 最多两份暂存，总计最多 1 MiB。
- 分片上限 64 KiB，必须严格连续。
- SHA-256 必须是小写规范十六进制。
- 完成后使用严格 UTF-8 解码；不接受替换字符或非规范字节。
- 暂存五分钟后在下一次暂存访问时惰性失效，不启动 timer 或 worker。
- 不存在或已过期的 token 返回 `STAGE_NOT_FOUND`。

stage 只是当前加载事务的临时字节容器，不是导入记录、持久文档或兼容层。

## 实例身份与更新

每个实例由以下当前身份确定：

```text
installationId
revision
generation
contentHash
protocol=lx
version
permissions
grant
```

规则：

- `revision` 为正安全整数，`expectedRevision` 精确匹配当前 binding。
- 相同 revision 只有源码、版本、权限和 grant 全部一致才幂等；当前原生源格式不持久用户脚本配置 JSON。
- 新 revision 必须大于当前 revision，并使用未使用过的 UUID grant。
- 安装实例最多 32 个。
- load 只安装启用实例；禁用和删除由原生 owner 调用 unload，不在 Node 中保留 disabled 影子状态。
- 更新失败时只有公开 host 仍保持原 generation、源码和 ready 状态才保留当前实例；若 host 已换代，撤销旧授权并要求原生根据 status 明确协调，不能伪造回滚。
- 音乐 LX 会话的单次同步片段上限为 2000 ms；初始化总上限仍为 5 秒，调用仍受独立请求截止时间约束。

`instance` 只返回 installation、revision、generation、state、contentHash、protocol、version 和声明能力。能力仍按 LX 平台、actions 和 qualitys 与宿主允许集合求交。

## 定向调用

`invoke` 必须指定：

```text
installationId revision generation siteId operation payload
```

当前操作只有 `musicUrl` 和 `lyric`；站点只有 `wy/tx/kg/kw/mg/local`。目标不存在、代次过时、实例未 ready、操作未声明或音质不支持时明确失败，不尝试其他实例或其他平台。

控制请求和完整响应各不超过 512 KiB。宿主调用预算为全局 16、实例 8、站点 4；超限不扩散请求。

## 网络权限

- 每个安装实例最多保存 128 个 canonical HTTP(S) origin。
- 导入只读取用户明确选择的脚本字节；原生审查器提取字面 origin，并按内容 SHA-256 补入已经离线审查确认的动态 origin。
- `localhost`、`.localhost`、`.local`、回环、链路本地、私网、共享地址空间和显式 IPv6 本地地址均拒绝；`localServices=false` 不能通过填写网络 origin 绕过。
- Node 对每次初始请求和重定向重新核对精确 origin。未授权地址明确失败，不自动扩大权限或改用其他脚本。
- 导入和列表不启动 Node；只有明确启用或实际调用选中脚本时创建会话。

## grant 与原生 storage

唯一反向方法：

```text
platform.musicScript.storage.get
platform.musicScript.storage.set
platform.musicScript.storage.delete
```

消息只包含：

```text
grant installationId siteId generation key value?
```

- grant 只在受信适配层和原生脚本 storage owner 之间传递。
- 每次回调在调用前后核对 installation、generation、site、取消信号和当前授权。
- 候选更新在执行 host install／update 前登记，但候选 storage 只写入原生事务 overlay；成功后与 metadata 原子发布，失败则撤销。
- 旧实例和迟到回调不能借用新 binding 的 grant。
- LX 启动 root scope 没有 site，当前公开 host 会拒绝启动期 storage；正式 invoke 使用明确 site scope。
- reverse storage 同样使用全局 16、实例 8、站点 4 的未结束任务预算。

Node 不获得任意文件、进程、账号凭据、应用数据根或通用 document bridge。

## 取消与关闭

- 每个控制调用都消费调用方 signal。
- 取消只 fence 当前结果并撤销对应授权，不假装不合作 adapter 已停止。
- 忽略 signal 的外部任务仍计入物理 drain；无法确认清理时返回 `SCRIPT_CLEANUP`。
- unload、失败更新和 close 都撤销对应 grant。
- close 幂等；成功必须证明 host 中无当前实例且已排空模块拥有的任务。

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

网络和原生失败只映射固定码，不返回上游正文、凭据或本机路径。
