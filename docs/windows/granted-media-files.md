# Windows 本地媒体文件授权

`MeowFilm.Platform.Files.GrantedMediaFiles` 是用户经 Windows 原生文件选择器明确选择的本地媒体路径 owner。它不向 Node、脚本 RPC 或平台网络请求暴露路径。

## 当前持久格式

- namespace：`music.files.v1`。
- 根对象只有 `Schema`、`Items`，`Schema` 必须是规范整数 `1`。
- `Items` 中每项只有 `Id`、`Path`、`Title`。
- `Path` 必须是规范化的普通本地绝对路径，不能是 UNC、设备路径或 reparse 路径，扩展名必须属于构造时固定白名单。
- `Title` 必须等于当前路径的无扩展名文件名。
- `Id` 固定为 `local:` 加规范路径大写不变量 UTF-8 的 SHA-256 小写十六进制摘要。
- 文档、条目、字段、标识和摘要均有固定数量及大小上限；重复或额外字段、重复条目、错误 fingerprint、非规范数字及任一损坏条目都会使整份文档失败。

只有文档不存在表示尚无授权。空字节、schema 不匹配或字段无效均返回格式错误。

## 读写与失败边界

- 所有读写由同一 owner 串行化。
- 写入使用读取到的 plaintext fingerprint 做一次 CAS；冲突不重试、不盲写。
- 写入回执必须等于待写字节的 fingerprint，否则结果为 `COMMIT_UNKNOWN`。
- 读取所得明文字节在解码后清零；写入序列化缓冲在调用结束后清零。
- 损坏或非当前文档保持原样；`ListAsync`、`ResolveAsync` 和 `GrantAsync` 均明确失败。

文件授权只保存用户选择关系，不复制媒体字节，也不等同于缓存或下载。媒体文件被移动、删除或路径后来变得不安全时，解析明确失败，不自动寻找替代路径。
