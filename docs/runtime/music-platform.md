# 共同音乐平台运行器 music.platform.v1

共同业务规则唯一来源为[平台请求与上下文](../common/music/platform-client-context.md)。本文件说明当前 Node 装配和公共 API；源码接线不代表测试、构建或真实上游已通过。

## 装配与方法

`apps/runtime/modules/music-platform.mjs` 薄适配 `@meowfilm/music-platform` 的 `createMusicPlatform`。descriptor 为 `music-platform@0.1.0`／`music.platform.v1`，方法如下：

| 方法 | 当前输入及返回 |
| --- | --- |
| `music.platform.request` | `{platform,operation,params,candidate?,expectedSession?}`；返回本次操作的规范结果 |
| `music.platform.session.update` | `{platform,session}`，session 为 null 或当前 `{scope,generation,userId,cookie}`；等待实际失效工作结算后返回 `{published:true}` |
| `music.platform.cache.clear` | 可选 `{platform}`；清对应 API 结果缓存，返回 `{cleared:true}` |
| `music.platform.describe` | 返回 `{execution:"node",contract:"music.platform.v1",platforms:["wy","tx","kg","kw","mg"]}` |

以上返回沿现有 HTTP module envelope `{ok,contract,value}`；失败沿现有有限 code。`candidate` 只用于候选账号认证交接，session 材料不进入普通展示 DTO 或日志。operation 以 providers、accounts 和 mutations 的实际公开派发为准，不另建第二份配方表。

`operation:"home"` 返回当前平台的 `entries`、`sections` 及真实 `continuation`，顶部入口无需另行查询目录或在客户端拼固定列表。网易首页入口及 Cube 推荐组统一附带 `requiresAccount`、`display`、`available`；其中账号入口的显示声明取自原生已发布 session。登录／退出沿既有 session 更新失效缓存和旧请求，下一次首页读取返回当前声明。显示隐藏不替代 `daily`、`heartbeat` 等操作自身的实际账号准入。

两端 aggregate 按 `music, cat, source, pan, tmdb, douban, film, netease, music-platform` 顺序装配九模块。`connectMusicPlatform` 从已装配 netease 的真实 `netease.token.get` 能力借用 token；不创建第二个 SDK owner。通用 `createRuntime` 默认仍三模块，仅显式 `musicPlatform` 选项连接此模块。

## 数据与生命周期

默认文档 port 为 `PrivateDocuments(dataRoot, "music-platform")`，用于当前匿名协议状态；原生凭据安全存储、永久歌曲仓库及图片字节不在此处。包使用 Node 内置能力，没有新增 npm 运行依赖。

request 将 HTTP `context.signal` 交给 owner。普通目录读取退订与物理工作分别跟踪；媒体／认证等实际取消、session 失效、已派发写回执和 close 由共同 owner 管理。session 更新等待应退休工作物理结算；close 停止新准入、取消未提交工作并等待真实结束，运行器不把 owner 关闭失败伪报成功。原生设置未知提示见[设置回执](../common/music/settings-receipts.md)。

## 打包与许可

公共打包器从实际静态导入和根 workspace lock 解析 `@meowfilm/music-platform`，复制 `src` 和薄模块；两端 wrapper 仅加入自己的启动文件。中央音乐许可和原始 notices 随公共载荷保存，实际文件及哈希进入 manifest；iOS 另携带现有 Node 兼容许可，Windows App 中央许可清单保持原安全复制门禁。来源路径见 `LICENSES/NOTICE.md` 和[许可说明](../licenses.md)。

直接覆盖入口包括 `runtime-assembly`、`runtime-package`、`runtime-engine-package`、两端 runtime／runtime-engine-package 及 iOS prepare fixture。新增九模块、共享 owner 失败关闭和许可闭包断言仍须在指定机器执行，不能以本说明作为通过证据。
