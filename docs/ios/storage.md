# 加密文档存储

`MeowStorage` 提供同步 Data API 的加密文档存储。产品装配提供专用 Application Support 目录、稳定 Keychain service 和命名空间；调用方负责业务 JSON 编解码。

## 密钥

- `EncryptedDocumentKeyProvider` 提供只读 `readKey(namespace:)` 和只插入缺失项的 `createKeyIfAbsent(namespace:)`；密钥固定 32 字节。
- Keychain 实现使用 generic password、`whenUnlockedThisDeviceOnly`、不同步、data-protection Keychain，并禁止交互认证。
- 不自动轮换、重置或删除密钥；解密失败不回退明文，也不另建命名空间。

## 文件格式

- 文件内容：`MEOWEDOC` + version `01` + CryptoKit AES-GCM combined（nonce、密文、tag）。
- AAD 包含 header、namespace 和 document key 及其长度，交换到其他命名空间或文档时认证失败。
- 目录名为 `n-<SHA256(namespace)>`，文件名为 `d-<SHA256(key)>.meowenc`。
- 逻辑标识为 1～128 个受限 ASCII 字符；Keychain service 不超过 160 字节。

## 写入与失败

- 写入先在同目录 stage 完成加密、文件保护、权限、备份排除和 fsync，再 rename 替换。
- 覆盖或删除前先认证原文档。缺 key、错误 key、篡改、未知版本、锁定和 IO 失败使用封闭错误，不暴露路径、密钥或明文。
- 命名空间已有任何文件但缺少 key 时，不创建新 key。
- 默认上限：单文档明文 8 MiB、命名空间密文 32 MiB、文件 128 个；可配置硬上限分别为 64 MiB、256 MiB、1024 个。
- 同一命名空间只能有一个存储实例。跨进程事务、恶意同进程代码和回滚攻击不在契约范围内。
