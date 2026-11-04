# 本地文档存储

`MeowStorage` 提供两种同步 Data API 文档存储，按数据敏感性选用。产品装配提供专用目录和命名空间；调用方负责业务 JSON 编解码与有效性。

| 数据 | 存储 | 位置 |
| --- | --- | --- |
| 曲库、队列、历史、设置（`music.library.v1`） | 普通文档 | Application Support 下 `meowfilm/documents`，参与备份 |
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
- Keychain 实现使用 generic password、`afterFirstUnlockThisDeviceOnly`、不同步、data-protection Keychain，并禁止交互认证。早期版本创建的 `whenUnlockedThisDeviceOnly` 密钥在下次成功读取时原位改为新类别；其他类别仍按策略不符拒绝。账号凭据按相同规则采用旧项。
- 不自动轮换、重置或删除密钥；解密失败不回退明文，也不另建命名空间。

### 文件格式

- 文件内容：`MEOWEDOC` + version `01` + CryptoKit AES-GCM combined（nonce、密文、tag）。
- AAD 包含 header、namespace 和 document key 及其长度，交换到其他命名空间或文档时认证失败。
- 目录名为 `n-<SHA256(namespace)>`，文件名为 `d-<SHA256(key)>.meowenc`。
- 逻辑标识为 1～128 个受限 ASCII 字符；Keychain service 不超过 160 字节。

### 写入与失败

- 写入先在同目录 stage 完成加密、文件保护（`completeUntilFirstUserAuthentication`）、权限、备份排除和 fsync，再 rename 替换。早期版本以 complete 保护写入的文件和目录，在下次成功读取后改为新类别。
- 覆盖或删除前先认证原文档。缺 key、错误 key、篡改、未知版本、锁定和 IO 失败使用封闭错误，不暴露路径、密钥或明文。
- 命名空间已有任何文件但缺少 key 时，不创建新 key。
- 默认上限：单文档明文 8 MiB、命名空间密文 32 MiB、文件 128 个；可配置硬上限分别为 64 MiB、256 MiB、1024 个。
- 同一命名空间只能有一个存储实例。跨进程事务、恶意同进程代码和回滚攻击不在契约范围内。

## 锁屏与旧数据

- 凭据、设备会话、普通文档和音频缓存在首次解锁后均可读。App 锁屏时不撤销音乐服务，锁屏播放、自动下一首和锁屏切歌沿用正常流程；音乐服务每次启动准入一次，只在关闭时撤销。首次解锁前启动在仅 audio 后台模式下不可达，不另行处理。
- 早期版本以 class B 写入的音频缓存条目在下次成功打开时改为 class C。
- 普通数据曾以加密文档保存在 `encrypted-documents-v1`。升级后不读取、不迁移、不删除这些旧文档及其 Keychain 密钥，曲库从空开始。
