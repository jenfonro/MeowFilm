# 外部音源动态验收

`scripts/accept-sources.mjs` 是仓库验收工具，不是产品运行入口。它通过公共音乐模块在 QuickJS 中执行原始外部脚本，不复制到源码／发布目录，不回退到 Node VM。

## 输入

```powershell
node scripts/accept-sources.mjs --scripts <JS 文件或目录的绝对路径>
node scripts/accept-sources.mjs --scripts <绝对路径> --records <JSON 绝对路径> --live --probe-media
```

- 默认只初始化，拦截实际联网；与联网模式分别记录。
- `--live` 调用每个 source 声明的 action；`search`／`musicSearch` 分别使用关键字、分页输入，不互相重写为别名。
- `--records` 是 site → 原始 `musicInfo` 的 JSON 映射；数组使用第一项。搜索实际返回的第一条记录可供同一 site 后续动作使用。
- 不制造其他平台的 ID，不以网络歌曲替代 `local` 输入，不将缺少输入写成已执行。特殊平台／扩展动作需要调用方提供其实际协议数据。
- `--probe-media` 仅对返回的音乐 URL 发起有界字节探测；它不下载完整音频，不证明整首播放或所有音质可用。
- `--output` 必须是仓库 `tmp/` 内的绝对路径；默认报告为 `tmp/test-results/script-sources/acceptance.json`，运行中滚动替换。

## 发布闭包

```powershell
& .\build\windows\runtime\node.exe scripts/accept-sources.mjs `
  --scripts <绝对路径> --records <JSON 绝对路径> `
  --payload <build/windows/runtime 的绝对路径> --live --probe-media
```

`--payload` 从 complete manifest 中定位公共模块，并拒绝该模块解析到 payload 外的文件依赖。验收工具仍由仓库提供，不随应用发布。Windows 管道／原生回调另由真实管道集成测试覆盖，不能用本工具的进程内调用替代。

## 报告与退出条件

| 状态 | 含义 |
| --- | --- |
| `returned` | 处理器返回；额外记录 URL、歌词长度、数组数量或值类型，不代表内容均有效 |
| `upstream_failure` | HTTP／网络错误、明确供应方错误／空数据，或脚本报告的上游失败 |
| `input_required` | 没有可用原始输入，或处理器明确报告缺少输入 |
| `configuration_required` | API key／配置缺失或失效；不输出秘密值 |
| `script_rejected` | 脚本自己的未定义符号、未实现动作或被拒绝的权限 |
| `host_failure` | 必需的宿主 API／选项／加解密能力未提供 |
| `runtime_limit` | 实际调用超过运行器资源边界 |
| `requires_diagnosis` | 证据不足；必须继续定位，不归咎于上游来刷通过 |

来源分类是依据有限诊断作出的归因，不是对第三方服务或脚本正确性的担保。宿主能力失败优先保留，不因同时出现上游错误而掩盖。

网络记录标明宿主执行回合的 `request`／`session` 归属；初始化快照区分 pending／completed，初始化时未完成的会话请求在清理后的 background 记录中保留最终状态，不能用 Node 的 ambient scope 推断 guest Promise 身份或权限。

初始化、卸载、关闭、源文件字节未变分别记录；统计同时区分声明动作和实际调用。初始化失败、卸载／清理失败、源文件变化、宿主失败、资源超限或尚未归因时，工具返回非零退出码。已明确的输入／配置／脚本／上游失败仍记录，不冒称全功能成功。

报告不保存源码、配置、密钥、Cookie、完整媒体／接口 URL、任意异常正文或堆栈；只保留文件身份与哈希、声明能力、状态码、固定分类、受限符号／成员及数字位置。仅通过初始化、语法或合成夹具不能满足真实脚本业务验收。
