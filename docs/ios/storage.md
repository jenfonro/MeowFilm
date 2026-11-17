# 本地文档存储

`MeowStorage` 提供两种同步 Data API 文档存储，按数据敏感性选用。产品装配提供专用目录和命名空间；调用方负责业务 JSON 编解码与有效性。

| 数据 | 存储 | 位置 |
| --- | --- | --- |
| 通用／音乐外观、音乐搜索偏好、播放音量 | 原生 UserDefaults | 当前 App domain，不经运行器 |
| 永久歌曲仓库、引用队列、历史、设置（`music.library.v3`） | 普通文档 | Application Support 下 `meowfilm/documents`，参与备份 |
| 小说书架／历史、阅读排版与净化规则（`novel.library.v1`） | 普通文档 | `meowfilm/documents/novel.library.v1/library.json`，参与备份 |
| 影视规则、智能播放偏好 | 普通文档 | `meowfilm/documents/{film.rules.v1,film.preferences.v1}/settings.json`，参与备份 |
| 猫源来源意图、网盘凭据、TMDB／豆瓣配置 | 加密文档 | `meowfilm/documents/protected-settings` 下按 namespace/key 散列存放，排除备份 |
| 视频映射／选项／删除记录 | 普通文档 | `meowfilm/documents/film.video-sources.v1`，参与备份 |
| 音乐脚本安装、源码与私有数据 | Node 私有文档 | Application Support 下 `meowfilm/script-runtime/music`，参与备份 |
| 共享图片／歌词（`music.presentation-resources.v1`）、来源观察（`music.source-checks.v1`）、首页展示（`music.home.display.v1`）、官方音频定位（`music.platform.audio-locators.v1`） | 普通文档 | Caches 下 `meowfilm/documents` |
| 平台匿名协议状态 | Node 私有文档 | Application Support 下 `meowfilm/script-runtime/music-platform`，不保存原生账号凭据 |
| 账号凭据（`<bundle>.music.platform.accounts.v1`） | `MeowAccounts` Keychain 凭据 | `afterFirstUnlockThisDeviceOnly`，不同步；bundle 使用稳定产品身份 |
| 实际播放后的音频 | 音频文件缓存 | Caches 下 `meowfilm.music.audio-resources.v1`，Data Protection class C |

## 普通文档

- `DocumentStore` 原样保存调用方字节，路径为 `<目录>/<namespace>/<key>.json`，可在 App 外直接查看和编辑；不加密，不加完整性信封，不要求内容可解析。
- namespace 和 key 为 1～128 个小写 ASCII 字母、数字、`.`、`_`、`-`，首字符为字母或数字，不含 `..`。只用小写，避免不区分大小写的卷把两个标识映射到同一文件。
- 单文档上限默认 8 MiB，属于资源边界，装配可按消费者调整；写入、比较替换和读取都先按上限检查。命名空间不限制文档数量和总字节。
- 写入在同目录生成唯一 stage，写完 fsync 后 rename 替换目标。`compareAndSwap` 在同一锁内比较当前文件内容，相同才替换；比较的是内容，不是单调版本。外部编辑后的内容就是下次比较的当前内容，不视为损坏。
- 目标必须是单一硬链接的普通文件。文档、命名空间目录或基目录为符号链接、目录或多硬链接时按 unsafePath 拒绝读取，不跟随也不修改链接目标；替换和删除只作用于本 store 的目录项。
- 命名空间中的未知文件和中断遗留的 stage 不阻塞读写，也不会被自动删除。
- 一个命名空间只用一个 store 实例。跨进程事务和恶意同进程代码不在契约范围内。
- 文件沿用系统默认 Data Protection（首次解锁后可读），锁屏后仍可读写。

### 音乐数据源偏好

- `music.library.v3` envelope 的 `records` 包含 `catalogSource` 区，key 为 JSON 字符串 `["meowfilm","music",3,"device","catalogSource"]`。
- 记录字段封闭：`{"schema":1,"revision":0,"platform":"wy"}`；platform 只允许 `wy`／`kg`，revision 必须为非负 JSON 安全整数。记录缺失时默认网易云、revision 0，读取不写盘；未知字段、非法类型、版本或平台明确返回 `corruptDocument`，保留原文件。
- 保存使用文档 transaction CAS，并核对该记录独立的 revision；成功切换加一，同值保存不写盘，过期 revision 返回 `staleRevision`。与 playback 的 `settingsRevision` 无关，不重写 library、queue、playback 或 history。

## 加密文档

`EncryptedDocumentStore` 提供通用敏感文档 API，影视四个敏感设置块使用该存储。音乐装配不再用它保存平台设备会话：匿名协议状态归 Node，原生音乐账号凭据使用上表中的 Keychain store。

### 密钥

- `EncryptedDocumentKeyProvider` 提供只读 `readKey(namespace:)` 和只插入缺失项的 `createKeyIfAbsent(namespace:)`；密钥固定 32 字节。
- Keychain 实现使用 generic password、`afterFirstUnlockThisDeviceOnly`、不同步、data-protection Keychain，并禁止交互认证。密钥与账号凭据读取时只接受当前配置的保护类别，不符时拒绝，不在读取时修改 Keychain 项。
- 不自动轮换、重置或删除密钥；解密失败不回退明文，也不另建命名空间。
- 用户明确确认的本机数据清除是例外，执行边界见下文；正常读取仍不迁移或修复旧密钥。

### 文件格式

- 文件内容：`MEOWEDOC` + version `01` + CryptoKit AES-GCM combined（nonce、密文、tag）。
- AAD 包含 header、namespace 和 document key 及其长度，交换到其他命名空间或文档时认证失败。
- 目录名为 `n-<SHA256(namespace)>`，文件名为 `d-<SHA256(key)>.meowenc`。
- 逻辑标识为 1～128 个受限 ASCII 字符；Keychain service 不超过 160 字节。

### 写入与失败

- 写入先在同目录 stage 完成加密、文件保护（`completeUntilFirstUserAuthentication`）、权限、备份排除和 fsync，再 rename 替换。读取不修改已有文件或目录的保护等级。
- 覆盖或删除前先认证原文档。缺 key、错误 key、篡改、未知版本、锁定和 IO 失败使用封闭错误，不暴露路径、密钥或明文。
- `compareAndSwap(key:expected:replacement:)` 在同一 store 锁内认证并解密当前内容，与 expected 明文字节精确比较后才加密原子替换。缺失／存在、内容不符返回 `revisionConflict`；不因冲突生成新密钥或修改原文档。
- 命名空间已有任何文件但缺少 key 时，不创建新 key。
- 默认上限：单文档明文 8 MiB、命名空间密文 32 MiB、文件 128 个；可配置硬上限分别为 64 MiB、256 MiB、1024 个。
- 同一命名空间只能有一个存储实例。跨进程事务、恶意同进程代码和回滚攻击不在契约范围内。
- 原子 rename 后没有可失败步骤。当前原生账号提交的保存失败、冲突或未知结果按其真实安全存储回执处理，不由普通缓存写入推断；账号发布边界见[音乐平台客户端上下文](../common/music/platform-client-context.md)。

## 锁屏与当前数据

- 凭据、普通文档和音频缓存在首次解锁后均可读。App 锁屏时不撤销音乐服务，锁屏播放、自动下一首和锁屏切歌沿用正常流程；音乐服务每次启动准入一次，只在关闭时撤销。首次解锁前启动在仅 audio 后台模式下不可达，不另行处理。
- 音频缓存只接受 class C，保护等级不符按缓存未命中处理，不在打开时修改已有文件的等级。
- 不提供旧凭据、密钥、加密文件或音频缓存保护等级的兼容、迁移或读取时修复。
- 音乐仅读写当前唯一格式；从隔离空数据建立的新仓库、引用队列和缓存可正常保存重载，不探测、采用或自动清理旧版本数据。

## 清除本机用户数据

- 设置首页最底部独立分组 → 清空本机数据。二次确认说明不可撤销、清除范围以及需关闭并重新打开 App；取消不产生请求。
- 确认后只持久写入 Application Support 下独立的 `meowfilm-reset.pending`（排除备份），停止播放、同步、脚本和业务访问，并切换为待重启界面。此时尚未清理；不会强制退出或在原进程热重建服务，前后台切换也不会执行清理。内嵌 Node 每进程只能启动一次。App 等待规则／来源 owner 取消排空、音乐模块终态、共享 bridge 和 Node 线程实际结束；仍需新进程才进行删除。
- 下次冷启动由 `MeowfilmLaunchController` 在创建 `MeowfilmAssembly` 前执行清理。清除 Application Support/meowfilm、Caches/meowfilm、Caches/meowfilm.music.audio-resources.v1、临时目录下的 meowfilm（运行器回退位置），以及当前 App UserDefaults domain、App Cookie 与 URL 缓存。
- Keychain 只清除当前账号与影视设置密钥的精确 services：`dev.meowfilm.aggregate.music.platform.accounts.v1`、`dev.meowfilm.aggregate.film.settings.keys.v1`；不读取、枚举或清除旧设备／旧曲库 services。只删除本地 data-protection generic-password 项，不枚举整个 access group、不按 class 全删，不先读取或验证项的保护属性。侧载后的偏好域使用实际 bundle ID，Keychain services 仍使用稳定产品 ID。
- 不调用云端退出／歌单／收藏删除，不删除 App 外导入原文件、链接目标或其他 service。应用根目录中未知的本地文件也属于重置范围，不尝试解密损坏文档。
- 清理可幂等重做，缺失项视为成功。只有全部完成才删除待清除标志并创建新业务装配；中断或任一失败保留标志，阻断业务启动，在统一信息框提供重试，不将部分清理报告为成功。签名权限问题不能靠清除自动修复。

## 音源文档

公共音乐模块唯一写入 `meowfilm/script-runtime/music`，使用 Node 的 `PrivateDocuments` 保存安装目录、内容寻址源码和请求绑定的 guest KV；格式与隔离规则以[共用 LX 契约](../runtime/music-script-runtime.md)为准。数据不混入曲库同步，不使用 Keychain 或加密文档。

`NativeMusicScriptService` 仅适配 `music.script.v3`，不维护原生脚本 `DocumentStore`、下载器或反向 storage RPC。旧原生脚本数据不读取、迁移、双写或主动删除；用户明确清除本机数据时仍按整个应用数据根清理。

Cat 安装事实、源码与私有卷继续使用 `meowfilm/script-runtime/cat`，可编辑来源意图属于下述原生设置块；客户端视频选项、排序、检测与删除记录仍属于 `film.video-sources.v1`。不以公共独立入口的默认根目录替代 App 数据根，再用空 status 清掉客户端记录；不扫描、采用或清理旧安装。

内置官方源开关属于 `music.library.v3` 的 playback 记录 `officialSources`，值为 `wy`／`tx`／`kg` 的无重复数组；新建文档显式写入全部启用，当前格式文档必须包含该数组，空数组全部关闭，不接受缺失、null 或未知平台。写入使用既有 settingsRevision／文档 CAS，不创建第二份设置事实来源。

## 影视设置

唯一 owner、离线提交、通知及在线回写语义只在[共同契约](../common/settings-storage.md)维护。iOS 的 `MeowfilmNativeSettings` 构造并共享六个 `FilmSettingsDocument`，页面与私密通道使用同一实例；不依赖 RuntimeSession 连接读取或保存。通知器弱引用既有会话，避免文档与受信读取能力反向延长会话生命周期；会话释放后仍可本地保存，不创建替代运行器。

| `FilmSettingsBlock`／通知块 | namespace | 存储 |
| --- | --- | --- |
| `.catSources`／`cat.sources` | `film.cat-sources.v1` | 加密 |
| `.panAccounts`／`pan.accounts` | `pan.accounts.v1` | 加密 |
| `.tmdb`／`tmdb` | `film.tmdb.v1` | 加密 |
| `.douban`／`douban` | `film.douban.v1` | 加密 |
| `.rules`／`film.rules` | `film.rules.v1` | 普通 |
| `.preferences`／`film.preferences` | `film.preferences.v1` | 普通 |

每块 key 均为 `settings`。规则和偏好分别使用 `{version:1,rules}`、`{version:1,preferences}`；元数据为 `{version:1,settings}`，网盘为 `{version:1,accounts}`。猫源结构见[Cat 原生意图](../runtime/cat-script-runtime.md#原生来源意图注入)。文档包装层负责对象 JSON、8 MiB 明文字节边界及内容 CAS，领域消费者负责各自字段、默认值和有效性；本地 `update(_:)` 只在真实变更提交后调用通知。

敏感块在上述 `protected-settings` 根下使用 AES-GCM；Keychain service 为 `dev.meowfilm.aggregate.film.settings.keys.v1`，每 namespace 一把 32 字节 key，沿用本页保护与失败规则。`snapshot()` 提供内容 fingerprint，受信 `compareAndSwap(expected:document:)` 只由 App 装配授予 pan 在线凭据回写，不反向通知同一运行器；私密帧见[运行底座](runtime-preflight.md#原生设置私密通道)。

`film.video-sources.v1/catalog` 仍由 DocumentStore CAS 保存客户端视频映射、排序、四选项、检测结果、只读安装／站点展示元数据及 tombstone。新写内容为 v2，不保存来源的 name／sourceType／sourceUrl／enabled／localPath／localHash，也不存脚本源码、运行端口或 generation；可编辑字段只从 `.catSources` 投影。仅消费当前 v2 目录，不补全旧版本或缺失的 inputRevision。当前来源按身份采用可用缓存，坏缓存不影响其他来源；未采用的行与扩展在无关提交中保留，状态不能冒充 ready。它不是来源意图或安装事实源，状态观察及串行边界见[影视设置](film-settings.md#猫源与公共宿主)。

### 当前文档消费

本开发版以空文档和当前契约为基准；启动只读取原生设置，不探测、采用、转换、重写或删除 Node 旧设置，也不提供旧文件回退。当前 owner、Keychain 保护、明文字节 CAS 与通知保持不变；真实文档解码、身份、资源和存储失败仍明确报告。

原生设置使用 `RuntimeJSON` 保留未知数字和 UTF-8 不同键。设置更新只替换实际编辑字段，未知 patch 不入文档；原文档扩展与其他条目保持精确值。Cat 列表不预检未消费的端口、网盘映射或 providers；无法消费及歧义身份条目不进入列表，修改健康条目保留这些原始行。Pan 手动输入先投影本平台凭据，整份替换选中凭据；其他账号、根及记录扩展不被覆盖。实际凭据类型、revision、签名和确认回执仍在消费处检查。

`PanSettingsStore.accounts()` 将损坏的平台记录投影为该 provider 的 `CORRUPT_STORAGE`，没有有效 revision 或账号状态；同时丢弃该平台先前的内存在线观察，不影响健康兄弟。替换或清空坏账号仍因无法取得真实 revision 而失败，不补零、不绕过 CAS；错误投影只用于显示，不写回文档。根包装、解密和真实存储失败不降级为某个账号的可用状态。

原生设置密钥缺失、认证失败与格式错误仍按当前存储契约报错。清除全部用户数据继续使用既有冷启动重置流程，不在当前进程热重建 Node。
