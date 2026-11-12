# 永久书源仓库与原生交互

`source@0.3.0` 使用 `source.runtime.v2`，与既有 `load/unload/invoke/status/close` 同时装配。运行器独占 `<dataRoot>/source/library.json`，原生客户端只缓存 `SourceRecord`，书架、历史、阅读锚点和排版仍由原生业务拥有。没有 `dataRoot` 的独立测试模块只保留会话内仓库；正式 App 沿用公共 `dataRoot` 装配。

所有方法使用 `source.runtime.` 前缀，HTTP 为 `POST /v1/source/<operation>`。响应成功为 `{ok:true,contract:"source.runtime.v2",value}`，失败为 `{ok:false,contract:"source.runtime.v2",code}`，错误不携带源代码、Cookie 或底层异常。

## 管理方法

| operation | 输入 | value |
| --- | --- | --- |
| `library.list` | `{}` | `{sources:[SourceRecord]}` |
| `library.get` | `{installationId}` | `{source:SourceRecord,definition:string}` |
| `library.previewImport` | `{text?,url?,fileName?}`，text/url 二选一 | `{items:[{index,name,sourceUrl,group,protocol,exists,definition}],failures:[{index,name,code}]}` |
| `library.import` | `{text?,url?,fileName?}`，text/url 二选一 | `{sources:[SourceRecord],failures:[{index,name,code}]}` |
| `library.save` | `{installationId,expectedRevision,definition:string}` | `{source:SourceRecord}` |
| `library.update` | `{installationId,expectedRevision,patch:{name?,group?,enabled?,enabledExplore?,order?}}` | `{source:SourceRecord}` |
| `library.remove` | `{installationId,expectedRevision}` | `{removed:true}` |
| `library.export` | `{installationIds:[string]}` | `{text:string}` |
| `library.invoke` | `{installationId,revision,action,payload}` | 动作原值 |

`SourceRecord` 为 `installationId,revision,name,sourceUrl,group,order,enabled,enabledExplore,protocol,actions:[string]`，另投影用于管理排序／操作的 `weight,lastUpdateTime,respondTime,hasExplore`；恢复失败时带 `errorCode`。generation 不属于公开记录。revision 为正整数，每次接受的定义或管理修改递增，旧 revision 返回 `STALE_SOURCE`。order 是有符号安全整数，列表按 order、名称、installationId 排序。无源返回空列表。

导入接受 Legado、异次元 JSON 对象或数组、`sources/bookSources/data` 数组容器、逐行 JSON，以及轻悦时光 HTML／HTML 对象。每个文件或 URL 由客户端各调用一次，定义数组由运行器逐项解析、验证与试运行；结果的 index 从 0 起，失败不抹掉已成功项。同 `bookSourceUrl` 更新原 installationId；无此字段的独立 HTML 使用导入 URL 或文件名建立稳定来源身份。URL 导入使用运行器 HTTP，按最终响应文本解析，不把文件来源视为脚本权限。源码继续遵循现有 16 MiB 格式边界。

`library.previewImport` 仅下载、解析和识别定义，不安装、不执行候选、不修改永久库或创建私有源数据。URL 只下载一次；返回的定义包含来源身份和当前管理元数据，客户端确认所选条目后将这些定义一次性传给 `library.import`，不再下载。预览也受取消／关闭排空约束，失败条目不隐藏有效项。

`library.get` 返回原协议定义加上当前管理的名称、分组、启停和排序，未改规则与扩展值保留原类型；编辑保存不能把列表操作恢复为旧元数据。

save 先试运行完整候选，随后原子替换仓库；语法、初始化、取消或写盘失败保留原定义及 revision，并恢复可用的旧运行实例。禁用不删除定义或私有状态，重新启用恢复；remove 删除该源及其私有值，撤销 Web、图片和运行任务。客户端不保存原始定义用来恢复安装；get 仅供编辑，export 仅包含定义和管理字段，不导出 Cookie、缓存和私有变量。

仓库文件整体写入临时文件、fsync、rename，沿用 `PrivateDocuments` 的私有权限与链接校验。损坏文件返回 `CORRUPT_STORAGE`，不可读／不可写返回 `STORAGE_ERROR`，均不以空库覆写。运行器关闭前排空已接受的写入和真实宿主资源；仅取消信号不算清理完成。

## 小说纯规则

唯一新增计算方法为 `rules.process {request}`，即 `POST /v1/source/rules.process`；版本仍为 `source@0.3.0/source.runtime.v2`。成功 value 直接是原纯规则结果，不再套一层结果对象：

| request.operation | request 其余字段 | value |
| --- | --- | --- |
| `identity` | `book` | `{name,author,key}` |
| `merge` | `books` | `{books}`，保留 `groupKey` 与各源 `candidates` |
| `matchChapter` | `chapters,anchor` | `{index:int|null,requiresSelection:bool}` |
| `validateRule` | `rule` | `{valid,errors:[string]}` |
| `transform` | `title,blocks,rules,book?,source?,conversion?` | `{title,blocks}` |
| `matchUrl` | `pattern,url` | boolean |

`matchUrl` 使用无额外 flags 的 `new RegExp(pattern).exec(url)`，仅原匹配从 0 开始且长度等于完整 URL 才返回 true；不重写正则或将部分命中当作成功。请求只允许这六种 operation 及表中字段，不接受任意程序、源码或执行入口。源定义中的脚本仍走既有隔离来源调用。

`@meowfilm/novel-rules` 提供受信纯程序，`source-rules.mjs` 通过共享 `NativeJsonProgram` 在独立 Runtime／线程执行。原生一次提交完整规则列表；Node owner 按原 order 升序、相同 order 保持输入顺序，跳过禁用规则，每条以 `timeoutMillisecond`（缺省 3000ms）调用，最后以空规则和单独 3000ms 执行简繁转换。预算为正安全整数，不增加业务上限，也不以固定总超时截断多规则。普通小说计算默认 3000ms，`matchUrl` 为 2000ms；其他 `NativeJsonProgram.invoke(value,{signal,timeoutMs})` 调用的既有默认仍为 2000ms。每次预算在排队请求获得执行时使用，取消贯穿整次 transform。

JS 公开 `createRuntimeClient().call("source.runtime.rules.process",...)` 使用共享 `requestWithoutDeadline()`，不以客户端默认 30000ms 或单次 `options.timeout` 截断排队及多规则执行；调用方用 `signal` 取消，客户端关闭撤销本地交换，Node owner 继续负责真实资源排空。其他 RPC 的默认及显式 timeout 不变。

只在全部步骤成功后返回完整正文；异常、超时或取消不返回中间正文。`validateRule` 保留原校验结果；非法 RPC／正则／启用规则或转换参数返回 `INVALID_REQUEST`，原生执行超时为 `TIMEOUT`，取消为 `CANCELLED`，关闭为 `CLOSED`，原生资源预算失败为 `LIMIT`，载体身份或 ABI 不符为 `CAPABILITY_UNAVAILABLE`，其他执行异常为 `SOURCE_FAILURE`，不携带规则或正文。失败调用等待实际原生线程和 Runtime 释放，后续调用才重建同一程序；关闭拒绝排队工作并等待全部已接受的 transform 和真实资源排空。

净化规则配置、书架、历史、偏好与原文仍由原生存储拥有；Node 只执行请求中的计算，不创建规则配置仓库。原生仅在页面／会话仍有效时发布结果；远端 URL 匹配结束不能恢复已取消或退休的 Web 待办，也不获得 Cookie／profile 写权限。

## Reader 调用上下文

payload 显式传 `book`、`chapter` 和稳定 `bookUrl`，缺省顶层 URL 可从相应对象补齐。正文应同时传 `nextChapterUrl`，或在 `book.catalog/book.chapters`（也可顶层 catalog）和 chapter.index 中提供下一章；分页不会跟随此章地址。目录支持 `preUpdateJs`、`book.tocHtml`、多 nextTocUrl 和字段规则；正文支持多 nextContentUrl、title、imageStyle、合并后 replaceRegex 和有序块。重复页去重，循环结束，不设置固定页数截断；超时、取消及已有 DOM／解析预算仍生效。

content 返回 `{title,chapter,chapterUrl,content,blocks,imageStyle,nextUrls,type}`，`blocks` 的公开结构只有 `{type:"text",text}` 或 `{type:"image",src,alt}`，图片地址按所属页面最终 URL 解析。book／chapter.variable 导入本次调用并写回对应结果，各并行动作隔离。`source.get/put/setVariable`、cache 和 Cookie 由源仓库持久化；只恢复源私有数据，不恢复 QuickJS 执行内存。jsLib 的 JSON 库名→HTTP URL 映射在每个 generation 初始化时获取并缓存，普通 jsLib 字符串继续作为内联脚本。

源私有数据在实际 generation 的 profile 中读写，候选执行使用隔离副本。只有当前已发布且未取消的 generation 可写回仓库。失败候选、退休请求、停用、删除及关闭后的迟到写入被拒绝；重启和启用会从持久状态向新 generation 注入副本。

## 受信图片

`library.image {installationId,revision,url,bookUrl,kind:"cover"|"content"}` 返回 `{reference:"/v1/source/images/<opaque>"}`。运行器使用源请求头、源 Cookie、URL 选项下载二进制，执行封面／正文图片解码规则，检查图片类型后保留原始大小。普通图片不经 JSON Buffer／数字数组。

客户端通过同一运行器端点 `GET reference`，得到 `image/png|jpeg|gif|webp|avif|bmp` 原始字节；HTTP 沿用[公共运行器](runtime-bridge.md)的无鉴权与跨域契约，不向公共图片客户端转交源站 Cookie。没有卡片缩略图大小或像素降采样。`library.releaseImage {reference}` → `{released:true}` 用于离开视口／页面时释放，重复释放幂等；源退休和关闭同时撤销引用及活跃流。reference 是会话内不透明路径，不是持久图片身份；过期引用返回 `SOURCE_NOT_FOUND` 或 `STALE_SOURCE`。图像像素解码由平台图片呈现层完成。

既有低层 `invoke image` 仍保留 ABI 的 body 语义，供兼容诊断和直接规则调用；产品图片消费统一走 library.image 的二进制通道。

## Web 待办与购章

`web.pending {}` → `{requests:[{id,installationId,mode,request}]}`；`web.respond {id,result?,cancelled?}` → `{accepted:true}`。取消用 `{id,cancelled:true}`，完成用 `{id,result}`，两者互斥。只有存在源调用／登录活动时原生客户端轮询。pending 不公开 generation、profile 或 host scope。

mode/request 严格沿用 [capability ABI](source-capability-abi.md)，没有第二个 Web 格式：

- Legado request 为 `mode:"request"`，request 带 `url,headers,method?,body?,sourceRegex,overrideUrlRegex`；正文 webJs 另带 `script`。
- Legado `login/browser/openUrl/verificationCode/webview/rule` 保持各自请求字段，如 `url,title,headers,html,script,sourceRegex,overrideUrlRegex,delayTime`。
- 轻悦时光 bridge 使用对应方法名为 mode，request 为 `{arguments:[...]}`；原始槽位（包括 `null`）保持不变，不能压缩参数后再判断 URL。运行器可另补 `headers`，平台应按来源 ABI 解释参数。

owner 覆盖 `request.cookies` 为当前源 HTTP profile 全部未过期 Cookie，每项为 `{name,value,domain,hostOnly,path,secure,httpOnly,sameSite,expires}`。domain 没有前导点；sameSite 为 `null` 或 `Lax`／`Strict`／`None`，expires 为 `null` 或 Unix 毫秒。jar 持久化这些属性，旧记录缺少 httpOnly／sameSite 时分别恢复为 false／null。原生以 cookies 为权威种子初始化隔离临时 Web store，并剔除大小写无关的 Cookie 请求头；不得从 Cookie 字符串重建根路径 Cookie。源代码传入的 cookies 数组不能覆盖 owner 状态。

已有 Cookie 仍可注入受信 `request.headers.Cookie`，不写日志。源显式 Cookie 请求头由 owner 大小写无关地合并到本次临时种子：当前 URL 已匹配的同名同值 Cookie 保持原作用域和属性；同名不同值复用匹配作用域；没有匹配项才生成当前 host-only、URL 默认目录路径的 Cookie，HTTPS 时带 Secure。临时种子不会提前落盘，也不会扩大已有同值 Cookie 的作用域。HTML-first 的 `webview`／`webViewGetSource` 取原 arguments[1] 为 URL，不把 HTML 或后面的脚本误作地址；未定义的 QYSG 方法不推测额外参数布局。

统一结果封套为 `{value:<原ABI结果>,url:<最终HTTP(S)地址>,headers:{"set-cookie":[string]},cookieChanges?:[{url,setCookie:string}]}`。headers 中的 Set-Cookie 仍按响应 URL 处理；cookieChanges 的每条 URL 必须是对应 Cookie 的实际 HTTP(S) origin，跨域 A→B 不得全部写成最终页面域。原生应对注入后基线做差异，未改 Cookie 不回写；删除用 `Max-Age=0`，保留 hostOnly／Path／Secure／HttpOnly／SameSite／有效期，`__Host-` 不添加 Domain。owner 在当前 generation／取消 fence 内用一次持久化事务处理整批有效变化，失败回滚，然后仅把 value 交回脚本，cookieChanges 不进入 guest。

iOS 公开 WebKit／Foundation API 对 SameSite=None 与未声明的投影有损：仅为完整作用域相同的实际 Cookie 延续 seed／baseline 中已知的 None，实际 Lax／Strict 优先，未知 nil 不猜测。新建／撤去或同键重建等不可区分的 SameSite 变化不承诺无损回传；不影响删除、逐 origin、取消与源隔离要求。平台细节见 [iOS 小说](../ios/novel.md)。

验证码、rule、webview 或 QYSG bridge 的标量放在 value，`null`、`false` 和数字不因封装改变类型；request 的 value 为原 `{url,statusCode?,headers?,data|html|body}` 响应对象或 HTML 字符串。原三字段封套、外部能力直接返回原 ABI 值及旧式动态响应对象的 `headers["set-cookie"]` 继续有效。平台 Web 会话不得把 Cookie 放入公共图片会话，并按 installationId 隔离 Cookie 与存储。

### 已核实的 Web helper ABI

以下 Legado 参数依据 Reader `src/search/legadoScriptBridge.js:1055–1151`，返回处理依据 `src/search/legadoRuntime.js:180–235`；不以本仓库平台当前行为反推契约。

| 脚本方法 | 原参数顺序 | 原生结果及 guest 返回 |
| --- | --- | --- |
| `java.startBrowser`／`startBrowserAwait`／`showBrowser` | `(url,title)` | 原生回页面 HTML 或响应对象；guest 经 StrResponse 包装，支持 `.body()`、`.url()`、`.raw()`、`.code()` 等，不把 bool 当作页面响应的实现。 |
| `java.webView` | `(html,url,script="",delayTime=1000)` | 原生执行页面／脚本，guest 得原 ABI 值；第四参数是延时，不是正则。 |
| `java.webViewGetSource` | `(html,url,script="",sourceRegex="",delayTime=1000)` | sourceRegex 命中时返回资源 **URL**，不是下载该资源的 body。 |
| `java.webViewGetOverrideUrl` | `(html,url,script="",overrideUrlRegex="",delayTime=1000)` | overrideUrlRegex 命中时返回导航 URL。 |

Reader 的 browser 经过 `ensureStrResponse`（bridge:741–747、1130–1151）；runtime:180–199 完成验证后重新请求目标页面。sourceRegex 的实据是 Reader `cloud_js/edgeone.js:785–790` 的 `reportResource → finishWebView("",targetUrl)`，以及 750–759 的 `body: matchedResourceUrl || stringifyWebViewResult(body)`；override 分支 792–798 调用 `finishWebView(targetUrl)`。因此 runtime:234 的 `return result.body` 不代表获取匹配资源正文。Reader 普通 Web 结果会转文本，本 v2 则按既定标量约定保留 `null`／boolean／number，不强制字符串化。

QYSG 已知调用保留方法名与原 arguments；登录 fallback 的 `[loginUrl,sourceName,"{}"]` 仅是现有调用形状，不能据此定义第三参数格式。限定参考中没有 `startBrowser`／`webviewajax`／`Dp` 完整签名或统一返回类型证据，不套用 Legado 的 StrResponse，也不新增推测格式。

pending 的真实调用取消／超时、源退休、禁用／删除和关闭会立即撤销待办；迟到 respond 返回 `STALE_SOURCE`，不能重建 Cookie。用户关闭交互应 respond cancelled，取消不会执行购章。

只有来源声明 `ruleContent.payAction` 或 QYSG `pay` 时 actions 才含 `payAction`。平台先展示并确认书籍／章节对象，用户明确确认后才调用 `library.invoke action:"payAction"`；普通 content 或 Web 呈现不会调用它。payload 必须提供 bookUrl 和 chapterUrl／chapter.chapterUrl。Legado URL 动作在该显式调用中进入受控 browser；脚本动作返回来源结果。成功后平台重新调用 content；取消保持当前阅读状态。此契约不绕过登录、验证码或支付确认，也不使用真实付费账号验证。
