# 外部 LX/Cat/Reader 来源动态验收

`scripts/accept-sources.mjs` 是仓库验收工具，不是产品运行入口。它通过公共模块在 QuickJS 中执行原始外部来源，不复制到源码／发布目录，不回退到 Node VM。

| 入口 | 公共契约 | 运行路径 |
| --- | --- | --- |
| `--kind lx`（默认） | `music.script.v3`／`music@0.8.0` | main LX 运行器 |
| `--kind cat` | `cat.script.v1`／`cat@0.1.0` | main Cat Node、私有卷及服务能力 |
| `--kind source` | `source.runtime.v1`／`source@0.1.0` | `scripts/accept-reader-sources.mjs`，Reader 规则与 portable CatVod Spider adapter |

`--kind cat` 与 `--kind source --protocol cat` 是不同输入契约。前者使用真实 Cat Node 路径；后者使用 portable Spider 的 `homeContent/searchContent/detailContent/playerContent` 等接口，不接收 Node `require`／服务脚本，也不自动回退到 LX。

## 输入

```powershell
node scripts/accept-sources.mjs --kind lx --scripts <JS 文件或目录的绝对路径>
node scripts/accept-sources.mjs --kind cat --scripts <绝对路径> --records <JSON 绝对路径> --live --probe-media
node scripts/accept-sources.mjs --kind source --scripts <文件或目录绝对路径> --protocol auto --live --records <JSON 绝对路径>
```

- `--kind` 为 `lx`、`cat` 或 `source`，默认 `lx`；`--protocol` 仅用于 `--kind source`。
- LX 默认只初始化，并注入明确拒绝全部外部请求的阻断 adapter；`--live` 不注入该 adapter，使用产品相同的受控网络路径。offline 与 live 分别记录，不能用合成响应冒充 live。
- Cat 验收必须显式使用 `--live`。Cat 的受限 Node HTTP(S)、DNS 和 socket 能力是产品原生网络路径，不经过 LX 注入式 adapter；工具拒绝无法完整阻断这些能力的 offline 模式，不能把部分阻断报告成离线验收。
- LX `--live` 调用每个 source 声明的 action；`search`／`musicSearch` 分别使用关键字、分页输入，不互相重写为别名。
- LX `--records` 是 site → 原始 `musicInfo` 的 JSON 映射；数组使用第一项。搜索实际返回的第一条记录可供同一 site 后续动作使用。
- Cat 按 runtime 返回的每个 site/actions 建立记录，并优先执行 `init/home/homeVod/homeVideo/search/category/detail/play/proxy/report/watchReport`。home/search/category 的真实返回依次派生分类、详情和播放输入；不能派生的动作标记 `input_required`。未发现 action 时通常标记 `requires_diagnosis`；只有框架已提供完整注册路由清单、且同一脚本确有其他业务动作时，空路由配置项记为 `no_actions`，不虚构动作或算成业务通过。
- Cat `category` 使用 `id/page/filter/filters`，`search` 使用 `wd/quick/page`；运行器按真实注册方法发送 POST 或 GET 等请求。`test/support/filters/thumb` 可使用空对象；未知扩展必须提供 `--records`，不能猜测空参数去执行账号或管理变更。
- Cat `--records` 可按 `文件名或 stem → sites → siteId → actions → operation → payload` 提供实际扩展动作输入，也可直接使用根级 `siteId → operation → payload`。
- 不制造其他平台的 ID，不以网络歌曲替代 `local` 输入，不将缺少输入写成已执行。特殊平台／扩展动作需要调用方提供其实际协议数据。
- `--probe-media` 对 LX `musicUrl` 或 Cat `play` 返回的 HTTP(S) URL 发起有界字节探测；它不下载完整媒体，不证明整首播放或所有画质可用。
- `--output` 必须是仓库 `tmp/` 内的绝对路径；默认报告为 `tmp/test-results/script-sources/acceptance.json`，运行中滚动替换。
- Cat 每份脚本使用独立临时 dataRoot；关闭运行器后递归清理。源码通过 64 KiB 分片安装，声明长度超过 16 MiB 时在暂存入口拒绝；大结果通过 `blob.read` 校验长度/SHA-256 后释放，不把整份源码或结果塞进单个 bridge 帧。

## Reader 与 portable CatVod

Reader 也可直接运行，参数和 `--kind source` 路由一致：

```powershell
node scripts/accept-reader-sources.mjs --scripts <文件或目录绝对路径> --protocol legado --live --records <JSON 绝对路径>
```

- `--protocol auto|legado|yiciyuan|qysg|cat` 保留协议检测／显式覆盖。该入口没有 `music`／`lx` 协议，LX 使用主入口。
- 目录递归读取 `.js`、`.json`、`.html`／`.htm`，拒绝目录内的符号链接；单文件也可直接输入。`.js` 的 Spider 标记或 `export default` 识别为 portable CatVod，无法识别的 JS 必须明确协议，不猜成 LX 或 Cat Node。
- `.json` 支持 Legado、异次元旧格式、轻悦时光对象／HTML 字符串和 portable Cat 对象；数组按独立来源拆分为 `<file>#<index>`。轻悦时光对象的 `html` 可以是带 `bookSourceName/bookSourceUrl` 的一个或多个空白分隔 HTTP(S) 候选 URL；live 模式按实际响应重试候选，并使用最终 URL 解析相对脚本。
- 默认只进行读取、识别、初始化、卸载和清理，注入阻断网络 adapter；远程 HTML 在离线模式可能初始化失败。`--live` 执行全部声明动作。
- `--scripts`、`--records`、`--payload` 及显式 `--output` 使用绝对路径。Reader 从来源清单排除 records、报告及其 `.tmp` 文件，不把自己的输出重新识别为来源。
- Reader 使用原 `records.source` 形状，按拆分来源身份优先、原文件相对名次之查找动作输入：

```json
{
  "source": {
    "books.json": {
      "actions": { "image": { "url": "https://example.invalid/cover.jpg", "body": "" } }
    },
    "books.json#2": {
      "actions": {
        "content": { "chapterUrl": "https://example.invalid/chapter/1" },
        "image": { "body": "" }
      }
    },
    "spider.js": {
      "actions": { "action": { "action": "login", "value": null } }
    }
  }
}
```

记录按整个来源条目选择，拆分条目存在时不合并原文件的 actions。Reader 从真实搜索／发现结果派生详情，再从详情派生目录、目录派生正文；`exploreKinds/explore/loginStatus/login/image` 等声明动作同样记录。portable CatVod 按 `init/home/homeVideo/category/search/detail/play/proxy/action/destroy` 排序，从真实返回派生播放和虚拟 proxy 输入。无法推导且没有显式输入的动作标记 `not_exercised`，live 模式返回非零，不制造 ID、账号或平台数据。

Reader 的 `--probe-media` 同样要求 `--live`，只对 portable CatVod `play` 返回的 HTTP(S) URL 作有界探测；虚拟 proxy 由声明的 `proxy` 动作验收。

## 发布闭包

```powershell
& .\build\windows\runtime\node.exe scripts/accept-sources.mjs `
  --kind <lx|cat|source> --scripts <绝对路径> --records <JSON 绝对路径> `
  --payload <build/windows/runtime 的绝对路径> --live --probe-media
```

`--payload` 从 complete manifest 中定位公共模块，并拒绝该模块解析到 payload 外的文件依赖。验收工具仍由仓库提供，不随应用发布。Windows 管道／原生回调另由真实管道集成测试覆盖，不能用本工具的进程内调用替代。

Reader 的 manifest 要求以下条目，不要求同时加载 music/Cat Node 模块：

```text
app/modules/source-runtime.mjs
node_modules/@meowfilm/script-host/src/index.js
node_modules/@meowfilm/source-runtime/src/index.js
```

LX 沿用 `music@0.8.0` 的透明分片：大控制参数与结果按 64 KiB 字节块传输，原生桥接 1 MiB 物理帧边界不变，见 [LX 传输契约](music-script-runtime.md#控制数据传输)。

## 报告与退出条件

LX/Cat Node 保持 main 报告和以下分类：

| 状态 | 含义 |
| --- | --- |
| `returned` | 处理器返回；额外记录 URL、歌词长度、数组数量或值类型，不代表内容均有效 |
| `upstream_failure` | HTTP／网络错误、明确供应方错误／空数据，或脚本报告的上游失败 |
| `input_required` | 没有可用原始输入，或处理器明确报告缺少输入 |
| `configuration_required` | API key／配置缺失或失效；不输出秘密值 |
| `script_rejected` | 脚本自己的未定义符号、未实现动作或被拒绝的权限 |
| `host_failure` | 必需的宿主 API／选项／加解密能力未提供 |
| `no_actions` | 完整框架清单确认该配置项没有发布业务动作；单独计入 `sitesWithoutActions` |
| `runtime_limit` | 实际调用超过运行器资源边界 |
| `requires_diagnosis` | 证据不足；必须继续定位，不归咎于上游来刷通过 |

来源分类是依据有限诊断作出的归因，不是对第三方服务或脚本正确性的担保。宿主能力失败优先保留，不因同时出现上游错误而掩盖。

产品 `onDiagnostic` 只提供稳定错误码／结构字段。验收工具显式启用 `onScriptError`，在回调内按本工具的规则分类原始消息，随即丢弃正文；普通产品运行不启用这个原文观察通道。初始化无论由 RPC 错误还是返回实例的 failed 状态失败，都保留原始 `code`，另记 `initializationFailure` 分类；不重试或改写成一次成功。没有因果网络证据的 `TIMEOUT` 保留 `requires_diagnosis`，不能自动归为上游失败。

Cat 已声明路由的 HTTP 404 不属于 `host_failure`；HTTP 500 也不自动证明上游失效。结合请求作用域网络记录及脚本脱敏诊断分类；服务 JSON 的 `message/msg/err/error` 等明确错误字段只贡献固定分类，包括 Axios 的 `ECONNABORTED` 超时，异常原文不进入报告。缺少证据时保留 `requires_diagnosis`。无正文成功响应记录为 `returned/empty`，不等同有有效搜索或播放数据。

固定分类还覆盖未配置 Cookie／需要登录／尚未添加频道、无直播源、缺失或无效媒体标识，以及明确的 JSON 解析失败。JSON 解析错误只在有本请求上游响应时归上游数据；空输入的 JSON 解析错误才归输入，无响应证据的非法 JSON 不自动归因。Cat 的网络目标不按公网、私网、回环或局域网域名分类过滤；未登记的本地目标本身不是宿主拒绝证据，实际失败时应按真实网络错误或响应归因。已登记 listener 的端点映射属于路由处理，不改变这一网络规则。

网络记录标明宿主执行回合的 `request`／`session` 归属；初始化快照区分 pending／completed，初始化时未完成的会话请求在清理后的 background 记录中保留最终状态，不能用 Node 的 ambient scope 推断 guest Promise 身份或权限。

初始化、卸载、关闭、源文件字节未变分别记录；统计同时区分声明动作和实际调用。初始化失败、卸载／清理失败、源文件变化、脚本被宿主拒绝、宿主失败、资源超限或尚未归因时，工具返回非零退出码。已明确的输入／配置／上游失败仍记录，不冒称全功能成功。

报告不保存源码、配置、密钥、Cookie、完整媒体／接口 URL、任意异常正文或堆栈；只保留文件身份与哈希、声明能力、状态码、固定分类、受限符号／成员及数字位置。仅通过初始化、语法或合成夹具不能满足真实脚本业务验收。

Reader 保留原 schema 2、`runtime`、`protocolOverride`、`rows` 和汇总字段，另以 `kind: "source"` 标明入口。它的动作和失败分类保持如下命名：

| 状态 | 含义 |
| --- | --- |
| `returned` | 处理器返回，仅保留结果类型和数量 |
| `upstream_failure` | 网络、HTTP、供应方业务错误或明确上游失败 |
| `account_region_configuration_required` | 账号、地区、API key、登录或 Web 交互配置不足 |
| `host_capability_missing` | 必需宿主能力／权限缺失，优先于同时出现的上游错误 |
| `engine_incompatible` | 来源语法、规则、guest SDK 不兼容或尚不能归因的错误；必须继续定位 |
| `runtime_limit` | 实际超时、内存或其他资源边界 |
| `cleanup_failure` | 卸载、关闭、profile、虚拟路由或物理任务回收失败 |
| `not_exercised` | 无真实动作输入且无法从前序结果推导 |

Reader 分别记录初始化、unload、close、文件字节是否变化、声明动作数和实际调用数。初始化失败、任一宿主／引擎／资源／清理失败、文件变化或 live 中有 `not_exercised` 均返回非零；初始化成功后的抛错仍计入失败，后续 close 成功不能抹去 unload 失败。live 中没有业务动作（空动作或只有 `init/destroy`）标记 `NO_BUSINESS_ACTIONS`／`engine_incompatible` 并返回非零；离线初始化不增加此门禁。

网络证据只用于已明确的 HTTP／网络失败归因；同一动作曾收到失败响应，不能把随后未知函数、任意脚本抛错或非法 guest 结果改判为上游失败。已归因的动作级上游或账号／地区配置失败会保留，但不算 `returned`。`NETWORK_ERROR/HTTP_ERROR/INVALID_RESPONSE` 的远程 HTML 初始化失败保留原始码并归为上游失败，仍返回非零。

Reader 与 LX/Cat Node 采用相同的隐私要求：报告和标准输出只保留结构及固定分类，不保留正文、配置或完整 URL。离线初始化、合成夹具和真实联网业务动作是不同证据。
