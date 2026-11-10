# TMDB／豆瓣数据运行器

本模块负责只读元数据获取、来源协议适配、设置持久化和图片流转发。不做跨源匹配、智能搜索、账号收藏／评分写入或元数据／图片持久缓存。TMDB 和豆瓣保留各自数据结构，只有原有图片字段改为稳定的运行器相对路径。

## 装配与会话

从 `apps/runtime/entry.mjs` 导入：

```js
import {
  createAppRuntime, createTMDBDataModule, createDoubanDataModule,
} from './apps/runtime/entry.mjs';

const runtime = createAppRuntime({
  sessionId, requestKey, // 受信宿主生成；不是上游 API 凭据
  modules: [
    createTMDBDataModule({ dataRoot }),
    createDoubanDataModule({ dataRoot }),
  ],
});
await runtime.start();
// 宿主结束会话时：await runtime.close();
```

也可与既有业务模块一并传给 `createAppRuntime`，每个模块只能出现一次。通用默认 `createRuntime`／`startRuntime` 仍装配 music、cat、source。iOS／Windows App entry 显式增加 tmdb 和 [netease token](netease-token.md)，在同一会话中精确校验五模块；没有装配 douban 或 pan。模块源文件已纳入公共运行器打包闭包，端侧搜索与设置接线分别见 [iOS 影视搜索](../ios/film-search.md)和 [Windows 影视搜索](../windows/film-search.md)。

| 模块 | 版本 | contract | 逻辑方法前缀 |
| --- | --- | --- | --- |
| tmdb | 0.1.0 | `tmdb.data.v1` | `tmdb.data.` |
| douban | 0.1.0 | `douban.data.v1` | `douban.data.` |

复用 [runtime.http.v1](runtime-bridge.md) 的 `Authorization: Bearer <requestKey>`、`X-Meow-Session` 和每次请求独立的 `X-Meow-Request-Id`。业务路由 `POST /v1/{module}/{operation}`，正文 `application/json`。两模块没有源码上传能力。

成功为 `{ok:true,contract,value}`，value 是直接 JSON，不是字符串。失败为 `{ok:false,contract,code}`，不返回异常堆栈、供应方响应正文或认证地址。

## 设置与凭据

两模块均提供：

- `settings.get {}`：读取非敏感设置和凭据是否已配置。
- `settings.update {…patch}`：原子更新指定字段；省略字段不变，凭据空字符串表示清除。成功返回与 get 同形的脱敏设置。

运行器独占 `<dataRoot>/tmdb/settings.json` 和 `<dataRoot>/douban/settings.json`，文档格式为 `{version:1,settings:{…}}`。首次访问恢复配置，之后使用内存快照；更新通过运行器完成，不支持绕过运行器并发外部编辑。首次未配置读取默认值，不写空文件。损坏文件明确失败，不当成缺失、不静默重置。

恢复时先严格校验 UTF-8 字节，再解码并解析 JSON；保留 UTF-8 BOM 处理，不依赖内嵌 Node 的 ICU。非法字节返回 `CORRUPT_STORAGE`，不改写原文件。

凭据由运行器保存和使用。客户端仅负责设置编辑和提交，不另建持久化事实来源，不在每次启动回灌。不向外部脚本实例授予此目录。

**文件本身不额外加密**。复用 `PrivateDocuments` 的安全路径和原子写入；POSIX 新文件权限为 `0600`。宿主必须提供应用专用 dataRoot 并负责系统沙箱／目录权限，尤其 Windows 的 ACL。能够读取此目录的用户可以读取凭据。日志、调试导出及备份不得公开此目录；这不是 Keychain／DPAPI 保险库。

### TMDB

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| `apiBase` | `https://api.themoviedb.org` | `/3/...` 之前的完整基址 |
| `imageBase` | `https://image.tmdb.org/t/p` | 图片 size 之前的完整基址 |
| `apiKey` | 空 | API key，仅发 API 请求 |
| `accessToken` | 空 | Bearer token，仅发 API 请求；两者均有时优先 token |

get 不包含 `apiKey`／`accessToken` 原值，改为 `apiKeyConfigured`／`accessTokenConfigured` 布尔值。地址只能使用 HTTP(S)，不允许 URL userinfo、query 或 fragment；不猜测站点是哪种代理项目，不自动添加或剥离代理前缀。

例如使用 Vercel API 与 GoProxy 图片：

```json
{
  "apiBase": "https://metadata.example.invalid",
  "imageBase": "https://images.example.invalid/tmdb-img/t/p"
}
```

API 请求将到 `https://metadata.example.invalid/3/movie/42`；图片到 `https://images.example.invalid/tmdb-img/t/p/w500/example.jpg`。GoProxy 的图片路由透传到 `image.tmdb.org` 根，因此必须保留 `/t/p`，不是只填 `/tmdb-img`。

支持二级及多级部署目录。例如 GoProxy 挂载在 `/pan` 时：

```json
{
  "apiBase": "https://proxy.example.invalid/pan/tmdb",
  "imageBase": "https://proxy.example.invalid/pan/tmdb-img/t/p"
}
```

对应请求为 `/pan/tmdb/3/movie/42` 和 `/pan/tmdb-img/t/p/w500/example.jpg`；再嵌套一层目录也按完整前缀拼接，不会回到站点根。`apiBase` 不包含 `/3`，因为 `request.path` 已包含它。运行器不按代理项目名自动增删目录，也不会从 API 地址推导图片地址；更换这些前缀不改变客户端持有的 `/v1/tmdb/images/...`。

### 豆瓣

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| `apiBase` | `https://m.douban.com` | `/rexxar/api/v2/...` 之前的基址 |
| `searchBase` | `https://search.douban.com` | `/movie/subject_search` 之前的基址 |
| `imageBase` | `https://img3.doubanio.com` | `/view/...` 等来源图片路径之前的基址 |
| `apiProxy` | 空 | 可选 URL 包装型数据中转模板 |
| `imageProxy` | 空 | 可选 URL 包装型图片中转模板 |
| `cookie` | 空 | 豆瓣请求凭据，不发给图片源 |

get 用 `cookieConfigured` 替代原值。域名替换／路径前缀型代理直接配置 base；URL 包装型代理必须显式配置包含**一个** `{url}` 或 `{encodedUrl}` 的 HTTP(S) 模板。例如 `https://relay.example.invalid/?url={encodedUrl}`。`apiProxy` 用于数据与搜索，`imageProxy` 用于图片；模板不参与稳定图片身份。将凭据发送到显式配置的数据代理意味着信任该代理，运行器不会替第三方代理保证保密。

## 数据请求

### TMDB `request`

```json
{
  "path": "/3/search/multi",
  "query": {"query": "示例", "page": 1, "language": "zh-CN"},
  "imageSize": "w500"
}
```

仅支持显式只读元数据路由，使用上游 GET。`path` 不是任意 URL，不含 query、fragment 或编码穿越；参数单独放 query。不允许调用者用 query 覆盖凭据。`imageSize` 缺省为 `original`，支持 TMDB 的 `original`／`w<正整数>`／`h<正整数>` 格式；具体尺寸是否由上游支持仍由来源决定，configuration 返回的尺寸数组可供选择。

覆盖电影／剧集／人物／综合搜索、discover、trending、genre、configuration、电影与剧集列表／详情、季集、人物与合集详情及关联的演职员／图片等只读子资源；不开放 account、authentication 或任意 API 转发。

精确路径如下，`[]` 为可选，`id` 是正整数字串，`n` 是无多余前导零的非负整数字串，`key` 是字母／数字／下划线／连字符：

```text
/3/configuration[/{countries|languages|primary_translations|timezones}]
/3/genre/{movie|tv}/list
/3/search/{movie|tv|multi|person}
/3/discover/{movie|tv}
/3/trending/{all|movie|tv|person}/{day|week}
/3/movie/{latest|now_playing|popular|top_rated|upcoming}
/3/movie/{id}[/{alternative_titles|credits|external_ids|images|keywords|lists|recommendations|release_dates|similar|translations|videos|watch/providers}]
/3/tv/{latest|airing_today|on_the_air|popular|top_rated}
/3/tv/{id}[/{aggregate_credits|alternative_titles|content_ratings|credits|episode_groups|external_ids|images|keywords|recommendations|screened_theatrically|similar|translations|videos|watch/providers}]
/3/tv/{id}/season/{n}[/{aggregate_credits|credits|external_ids|images|translations|videos|watch/providers}]
/3/tv/{id}/season/{n}/episode/{n}[/{credits|external_ids|images|translations|videos}]
/3/tv/episode_group/{key}
/3/person/{latest|popular}
/3/person/{id}[/{combined_credits|external_ids|images|movie_credits|tagged_images|translations|tv_credits}]
/3/collection/{id}[/{images|translations}]
/3/credit/{key}
/3/watch/providers/{regions|movie|tv}
```

`append_to_response` 的每个子资源同样必须落入这些路径；不通过 append 绕过只读范围。query 的值是字符串、数字或布尔值，语言列表／筛选组合按 TMDB 原有字符串语法填写。TMDB 允许的图片字段为 `poster_path`、`backdrop_path`、`profile_path`、`logo_path`、`still_path`、`file_path`；依赖外部头像的评论接口不在本轮。

例如成功 value：

```json
{
  "page": 1,
  "results": [
    {
      "id": 42,
      "media_type": "movie",
      "title": "示例",
      "poster_path": "/v1/tmdb/images/w500/example.jpg",
      "backdrop_path": null
    }
  ],
  "total_pages": 1,
  "total_results": 1
}
```

分页、ID、评分、类型和其他业务字段不重塑。嵌套图片字段同样改写，不额外增加 `poster_url` 或第二套引用。空图片值保持空值。`configuration.images.base_url`／`secure_base_url` 改成 `/v1/tmdb/images/`，尺寸数组等保持来源数据；上游返回的官方／CF 图片基址不会覆盖运行器显式 imageBase。

### 豆瓣 `request` 与 `search`

```json
{"path":"/rexxar/api/v2/subject/recent_hot/movie","query":{"category":"热门","type":"华语","start":0,"limit":20}}
```

```json
{"path":"/rexxar/api/v2/movie/123"}
```

```json
{"query":"示例"}
```

前两个发往 `/v1/douban/request`，第三个发往 `/v1/douban/search`。Rexxar 使用 `meowfilm-server` 的来源协议，返回豆瓣自身 JSON；完整网页搜索解析 `window.__DATA__`，输出其既有 `subjects.items[].target` 结构，不用搜索建议替代，不执行页面脚本。

Rexxar 精确读取范围（以下均加 `/rexxar/api/v2/` 前缀）：

```text
subject/recent_hot/{movie|tv}
{movie|tv}/{categories|showing|recommend}
{movie|tv}/{id}[/{celebrities|credits|photos|recommendations|recommend|trailers|videos|episodes}]
celebrity/{id}[/{works|photos}]
subject_collection/{name}[/items]
```

`id` 是数字串，`name` 是字母／数字／下划线。首页及分类沿用 server 的 `subject/recent_hot/{movie|tv}`，以 `category`／`type` 筛选，分类选项由响应 `tags`／`recommend_tags` 保留；不依赖独立 `/movie/categories` 接口。上列是运行器允许的只读路径，不保证所选上游支持每一条；上游 404 仍返回 `UPSTREAM_HTTP`，不回退或伪造分类数据。

query 保留豆瓣 `start`／`limit` 等原有参数，可使用标量或标量数组；不转换为 TMDB 分页。网页 search 当前仅接受 `query`，与参考实现一致不承诺分页参数，`show_more_subjects` 保留参考输出语义。

豆瓣字段示例：

```json
{
  "id": "123",
  "title": "豆瓣示例",
  "pic": {
    "normal": "/v1/douban/images/view/photo/s_ratio_poster/public/p123.jpg"
  },
  "rating": {"value":8.4,"max":10}
}
```

照片实体可同时包含页面链接和图片：例如详情 `cover.url` 是照片详情页，保留为业务链接；`cover.image.*.url` 才改成运行器图片路径。不是把 `cover` 下所有 URL 都改写或拒绝。

不伪造 TMDB 的 `page/results` 包装、评分尺度或 ID；也不把 server 的数据库缓存、`cache_status` 和反爬挑战求解器移入本运行器。需要登录／网页验证时明确返回 `WEB_INTERACTION_REQUIRED`，由后续客户端提示用户，不返回虚假的空列表；正常搜索词回显包含“验证码”等文字本身不等于验证页。

## 图片与稳定缓存身份

- TMDB：`GET /v1/tmdb/images/{size}/{file}`。
- 豆瓣：`GET /v1/douban/images/{source-image-path}`，图片规格已在来源路径中。
- 图片请求携带与 JSON 相同的当前会话认证，**不能**把凭据附在 URL query。
- 成功 HTTP 200 为上游图片原始二进制流，保留必要内容类型／长度等头，不使用 JSON／Base64，也不返回外部 Location。错误在发图片头之前返回 HTTP 502 的模块错误信封；开始传输后失败则终止连接，不向图片字节混入 JSON。
- 图片入口核对 MIME 与内容前导，拒绝伪装为 `image/*` 的 JSON／HTML 错误页；前导字节放回流中原样发送，不先缓冲整张图片。探测窗口固定 4 KiB，在窗口内无法识别的无尽空白／未闭合 XML 序言返回 `UPSTREAM_FORMAT`，这是预读资源边界，不是图片总大小限制。SVG 的 XML 声明、注释及 DOCTYPE 只作文本识别，不加载 DTD 或执行实体。完整图片解码仍由原生图片加载器负责。
- 图片路径不含代理域名、运行器端口、会话 token 或内存随机映射。修改 imageBase 后，旧路径未命中缓存的请求立即走新配置；已开始的请求完成它自己的配置快照。重启后同一路径仍可还原，不依赖之前抓取的数据驻留内存。
- 原生客户端以相对路径为图片缓存键；仅发请求时拼接当前运行器 baseUrl 和会话头。不能用含临时端口的完整 URL 做稳定键。TMDB 尺寸或豆瓣路径规格不同的图片仍是不同缓存项。
- 原生缓存淘汰、资源内容变化和正常失效策略不改变。图片响应使用 `Cache-Control: no-store`，不把上游 HTTP 缓存规则当成原生资源缓存身份。

JSON 请求和图片流都可取消；会话关闭会取消上游，并等待已受理工作／图片资源真实排空。数据请求不自动重试；TMDB 不跟随重定向，豆瓣由来源适配器处理普通跳转并隔离 Cookie，最多五跳，登录／挑战目标直接报告需要交互。图片重定向由运行器内部处理，最多五跳以终止循环，不转发 API 凭据。

## 错误与验证边界

模块错误码：

| code | 含义 |
| --- | --- |
| `INVALID_REQUEST` | 参数、设置、只读路径或资源引用不合法 |
| `CLOSED` / `CANCELLED` | 模块关闭／请求取消 |
| `NETWORK_ERROR` | 上游网络失败 |
| `UPSTREAM_HTTP` | 上游 HTTP 失败或不允许的重定向 |
| `UPSTREAM_FORMAT` | 响应格式不符或图片入口返回非图片 |
| `WEB_INTERACTION_REQUIRED` | 豆瓣登录／网页验证要求 |
| `STORAGE_ERROR` | 持久化读写失败；失败后重新读取真实文件，不承诺已提交 rename 回滚 |
| `CORRUPT_STORAGE` | 已有配置损坏或版本不支持；保留原文件 |

协议／会话错误仍使用 `runtime.http.v1` 的错误码。来源独立；一方失败不隐式调用另一方或合并数据。

运行器测试使用合成 HTTP 上游与假凭据，覆盖结构、图片二进制、持久化恢复、代理切换和取消排空；不代表真实 TMDB 账号权限、豆瓣 Cookie／反爬条件已在线验收。iOS／Windows 的 TMDB 搜索通过受信本地二进制图片通道消费稳定相对引用，不放宽普通外部图片 URL 的网络限制；客户端设置只编辑并提交运行器配置，不复制凭据持久化。豆瓣端侧接入不在影视搜索范围。
