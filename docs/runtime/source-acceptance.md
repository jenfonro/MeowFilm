# 外部来源动态验收

`scripts/accept-sources.mjs` 是仓库验收工具，不是产品入口。它使用公共 music/source module 和 QuickJS 执行用户提供的来源；不会修改、复制到源码或打包进应用，也不会回退到 Node VM。

## 输入

```powershell
node scripts/accept-sources.mjs --scripts <文件或目录绝对路径>
node scripts/accept-sources.mjs --scripts <绝对路径> --live --records <JSON 绝对路径>
node scripts/accept-sources.mjs --scripts <绝对路径> --protocol legado --live
```

支持 `.js`、`.json`、`.html`／`.htm`：

- `.js` 自动区分 LX 与 CatVod JS。
- `.json` 支持 Legado、异次元旧格式、轻悦时光对象／HTML 字符串和 Cat 对象；带 `bookSourceName`、`bookSourceUrl` 的轻悦时光对象可把 `html` 写成一个或多个空白分隔的 HTTP(S) 候选 URL；数组按独立来源拆分。
- `.html` 作为轻悦时光 HTML 源。
- `--protocol auto|music|lx|legado|yiciyuan|qysg|cat` 可显式覆盖识别。

默认模式只验证读取、识别、初始化、卸载和清理，网络请求被阻断，因此远程 HTML 来源会按上游网络失败记录。`--live` 允许初始化所需的远程 HTML／脚本请求并执行声明动作；`--probe-media` 只能与 `--live` 一起使用，并只读取有限媒体字节。

## 动作输入

`--records` 可按来源文件名或拆分后的 `<file>#<index>` 提供动作输入：

```json
{
  "music": {
    "music.js": {
      "sites": {
        "kw": {
          "musicInfo": {},
          "actions": { "customAction": {} }
        }
      }
    }
  },
  "source": {
    "books.json#1": {
      "actions": { "image": { "url": "https://example.invalid/a.jpg", "body": "" } }
    },
    "cat.js": {
      "actions": { "action": { "action": "login", "value": null } }
    }
  }
}
```

LX 的 `search`／`musicSearch` 使用关键字和分页输入，搜索第一条记录可串联到同一 site 的后续动作；也可提供真实 `musicInfo`。Reader 会从搜索结果串联详情、目录和正文；Cat 会从 home/category/search 串联 detail、play 和虚拟 proxy。无法从前序结果推导且未显式提供的动作标记 `not_exercised`，不制造 ID、账号或平台数据。

## 发布闭包

```powershell
& .\build\windows\runtime\node.exe scripts/accept-sources.mjs `
  --scripts <绝对路径> --records <JSON 绝对路径> `
  --payload <build/windows/runtime 的绝对路径> --live
```

`--payload` 要求 complete manifest 同时包含：

```text
app/modules/music-script.mjs
app/modules/source-runtime.mjs
node_modules/@meowfilm/script-host/src/index.js
node_modules/@meowfilm/source-runtime/src/index.js
```

模块解析被限制在 payload 内。验收工具本身由仓库提供，不随应用发布；Windows 命名管道和原生握手仍由独立集成测试覆盖。

## 固定分类

| 状态 | 含义 |
| --- | --- |
| `returned` | 处理器返回；只记录结果种类、数量或长度，不证明内容长期有效 |
| `upstream_failure` | HTTP、网络、供应方业务码或明确上游空/失败 |
| `account_region_configuration_required` | 账号、地区、API key、登录或 Web 交互配置不足 |
| `host_capability_missing` | ABI、权限、编码或加解密能力缺失 |
| `engine_incompatible` | 来源语法、协议、规则或 guest SDK 与当前引擎不兼容 |
| `runtime_limit` | 超时、内存、响应、并发或其他资源边界 |
| `cleanup_failure` | 卸载、profile、虚拟路由或物理任务未完成回收 |
| `not_exercised` | 缺少真实动作输入且无法从前序结果推导 |

报告 schema 为 2。它只保留文件相对身份、SHA-256、协议、声明能力、固定错误码、脱敏诊断、状态码、耗时和结果摘要；不保存源码、完整 URL、Cookie、密钥、配置值、响应正文、异常正文或堆栈。

## 退出条件

以下任一情况返回非零：

- 任一来源初始化失败；
- unload／close／profile／虚拟路由清理失败；
- 来源文件在验收期间变化；
- `host_capability_missing`、`engine_incompatible`、`runtime_limit` 或 `cleanup_failure`；
- live 模式存在 `not_exercised`。

已明确归因的动作级上游失败或账号/地区配置需求会记录但不冒充成功。离线初始化通过、合成夹具通过和真实联网全动作通过是不同证据。

远程 HTML 候选耗尽时，网络错误、非 2xx 和无有效脚本响应分别保留为 `NETWORK_ERROR`、`HTTP_ERROR`、`INVALID_RESPONSE`，并归类为 `upstream_failure`；不会改写成 `engine_incompatible`。
