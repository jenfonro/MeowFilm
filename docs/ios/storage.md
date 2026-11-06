# 本地文档存储

`MeowStorage` 提供两种同步 Data API 文档存储，按数据敏感性选用。产品装配提供专用目录和命名空间；调用方负责业务 JSON 编解码与有效性。

| 数据 | 存储 | 位置 |
| --- | --- | --- |
| 曲库、队列、历史、设置（`music.library.v1`） | 普通文档 | Application Support 下 `meowfilm/documents`，参与备份 |
| 影视规则与视频映射／选项／删除记录 | 普通文档 | `meowfilm/documents/film.magic-rules.v1`、`film.video-sources.v1`，参与备份 |
| 脚本目录、源码与私有数据（`music.scripts.v1`） | 普通文档 | Application Support 下 `meowfilm/documents`，参与备份 |
| 播放展示、来源发现、首页快照、账号 URL 与副歌点缓存 | 普通文档 | Caches 下 `meowfilm/documents` |
| 客户端设备会话（`music.client-context.<平台>.v1`）、QQ 设备身份（`music.qq-public.v1`） | 加密文档 | Application Support 下 `meowfilm/encrypted-documents-v1`，排除备份 |
| 账号凭据（`<bundle>.music.accounts.v1`） | `MeowAccounts` Keychain 凭据 | `afterFirstUnlockThisDeviceOnly`，不同步 |
| 实际播放后的音频 | 音频文件缓存 | Caches 下 `meowfilm.music.audio-cache.v1`，Data Protection class C |

## 普通文档

- `DocumentStore` 原样保存调用方字节，路径为 `<目录>/<namespace>/<key>.json`，可在 App 外直接查看和编辑；不加密，不加完整性信封，不要求内容可解析。
- namespace 和 key 为 1～128 个小写 ASCII 字母、数字、`.`、`_`、`-`，首字符为字母或数字，不含 `..`。只用小写，避免不区分大小写的卷把两个标识映射到同一文件。
- 单文档上限默认 8 MiB，属于资源边界，装配可按消费者调整；写入、比较替换和读取都先按上限检查。命名空间不限制文档数量和总字节。
- 写入在同目录生成唯一 stage，写完 fsync 后 rename 替换目标。`compareAndSwap` 在同一锁内比较当前文件内容，相同才替换；比较的是内容，不是单调版本。外部编辑后的内容就是下次比较的当前内容，不视为损坏。
- 目标必须是单一硬链接的普通文件。文档、命名空间目录或基目录为符号链接、目录或多硬链接时按 unsafePath 拒绝读取，不跟随也不修改链接目标；替换和删除只作用于本 store 的目录项。
- 命名空间中的未知文件和中断遗留的 stage 不阻塞读写，也不会被自动删除。
- 一个命名空间只用一个 store 实例。跨进程事务和恶意同进程代码不在契约范围内。
- 文件沿用系统默认 Data Protection（首次解锁后可读），锁屏后仍可读写。

## 加密文档

`EncryptedDocumentStore` 只保存设备会话等敏感文档。

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
- 命名空间已有任何文件但缺少 key 时，不创建新 key。
- 默认上限：单文档明文 8 MiB、命名空间密文 32 MiB、文件 128 个；可配置硬上限分别为 64 MiB、256 MiB、1024 个。
- 同一命名空间只能有一个存储实例。跨进程事务、恶意同进程代码和回滚攻击不在契约范围内。
- 原子 rename 后没有可失败步骤。网易云／酷狗／QQ 设备文档的同步写入错误不会因发生在保存阶段就变成 `COMMIT_UNCONFIRMED`；保护属性不符为 `STORAGE_POLICY_MISMATCH`，缺签名权限为 `STORAGE_PERMISSION_DENIED`，其他确定失败按存储／访问／损坏等类别报告。真正明确报告的未知提交（例如账号恢复流程）仍保留原语义。
- 设备文档适配器在 OSLog `dev.meowfilm.aggregate` / `storage` 记录封闭错误类别、读写阶段和请求诊断 ID，不记录路径、Keychain service、凭据或任意底层错误文本。故障注入能验证分类，不能替代签名真机故障诊断。

## 锁屏与旧数据

- 凭据、设备会话、普通文档和音频缓存在首次解锁后均可读。App 锁屏时不撤销音乐服务，锁屏播放、自动下一首和锁屏切歌沿用正常流程；音乐服务每次启动准入一次，只在关闭时撤销。首次解锁前启动在仅 audio 后台模式下不可达，不另行处理。
- 音频缓存只接受 class C，保护等级不符按缓存未命中处理，不在打开时修改已有文件的等级。
- 不提供旧凭据、密钥、加密文件或音频缓存保护等级的兼容、迁移或读取时修复。
- 普通数据曾以加密文档保存在 `encrypted-documents-v1`。升级后不读取、不迁移、不删除这些旧文档及其 Keychain 密钥，曲库从空开始。

## 清除本机用户数据

- 设置 → 本机数据 → 清除本机用户数据。二次确认说明不可撤销、清除范围以及需关闭并重新打开 App；取消不产生请求。
- 确认后只持久写入 Application Support 下独立的 `meowfilm-reset.pending`（排除备份），停止播放、同步、脚本和业务访问，并切换为待重启界面。此时尚未清理；不会强制退出或在原进程热重建服务，前后台切换也不会执行清理。内嵌 Node 每进程只能启动一次。App 等待规则／来源 owner 取消排空、音乐卸载、共享 bridge 和 Node 线程实际结束；仍需新进程才进行删除。
- 下次冷启动由 `MeowfilmLaunchController` 在创建 `MeowfilmAssembly` 前执行清理。清除 Application Support/meowfilm、Caches/meowfilm、Caches/meowfilm.music.audio-cache.v1、临时目录下的 meowfilm（运行器回退位置），以及当前 App UserDefaults domain、App Cookie 与 URL 缓存。
- Keychain 按稳定产品 ID `dev.meowfilm.aggregate` 下的精确 service 清除：账号凭据、网易云／酷狗客户端设备、QQ 设备，以及旧版曲库、播放展示、来源发现、账号 URL 缓存密钥。只删除本地 data-protection generic-password 项，不枚举整个 access group、不按 class 全删，不先读取或验证旧项保护属性。侧载后的偏好域使用实际 bundle ID，Keychain service 仍使用稳定产品 ID。
- 不调用云端退出／歌单／收藏删除，不删除 App 外导入原文件、链接目标或其他 service。应用根目录中未知的本地文件也属于重置范围，不尝试解密损坏文档。
- 清理可幂等重做，缺失项视为成功。只有全部完成才删除待清除标志并创建新业务装配；中断或任一失败保留标志，阻断业务启动，在统一信息框提供重试，不将部分清理报告为成功。签名权限问题不能靠清除自动修复。

## 音源文档

`NativeMusicScriptService` 唯一持有普通 `DocumentStore` 命名空间 `music.scripts.v1`，目录、源码和私有数据全部明文存储，不使用 Keychain 或加密文档。安装目录与脚本源码不混入曲库同步：

- `catalog`：schema 1、revision、安装 UUID、有界头部元数据、源码 SHA-256／字节数、启用状态、上次声明平台及安装时间；上限 1 MiB。
- `source.<sha256>`：严格 UTF-8 原始脚本字节，相同内容复用；单源 16 MiB。
- `storage.<installationId>`：按 site／key 的 JSON 值，与当前 grant、installation、generation 绑定。每安装 512 KiB，每 site 64 KiB／64 项，每值 16 KiB、最多 32 sites；这些是私有数据资源边界，与 Windows owner 一致。
- 文件位于 `documents/music.scripts.v1/<key>.json`，其中 source 文件保存原始源码字节而非 JSON。单文档资源上限为 16 MiB，不限制命名空间文档数与总字节。安装／启停操作串行完成；读取或导入不执行脚本。删除先撤销运行授权并清除私有存储，再删除目录条目与最后一个引用的源码。

内置官方源开关仍属于 `music.library.v1` 的 playback 记录 `officialSources`，值为 `wy`／`tx`／`kg` 的无重复数组；新建文档显式写入全部启用，已有文档必须包含该数组，空数组全部关闭，不接受缺失、null 或未知平台。写入使用既有 settingsRevision／文档 CAS，不创建第二份设置事实来源。

## 影视设置

`film.magic-rules.v1/rules` 保存共享 JS 的四组文档；`film.video-sources.v1/catalog` 保存客户端视频映射、排序、四选项、检测结果、猫源管理元数据及 tombstone。均以 DocumentStore CAS 写入后发布状态，不存脚本源码、运行端口或 generation。Node 在 `meowfilm/script-runtime` 保存自身安装和私有卷，不拥有客户端视频选项。详细身份、删除恢复与调用边界见[影视设置](film-settings.md)。
