# 音乐元数据偏好与本地歌词

## 所有权

- `MusicLibraryOwner` 是原生音乐数据库的唯一业务 writer；永久歌曲规则见[共同歌曲仓库](../common/music/smart-playback.md)，图片规则见[共享展示资源](../common/music/presentation-resources.md)。
- 元数据平台顺序、启用集合和本地歌词均属于当前曲库控制 area；设置页、歌词页、平台协议和脚本不能直接写保护存储。
- 普通设置、读取本地歌词和保存本地歌词不调用脚本服务。只有实际查询已启用洛雪音源时才进入脚本执行边界。
- 设置控件即时保存，没有“保存设置”或“重新读取设置”按钮，也没有独立服务端配置副本。

## 当前控制区域

音乐数据库按实际动作选择 area，详见[当前数据库](music-native-library.md#唯一当前数据库)。未知区域和未消费扩展保留；当前动作使用的 schema／必需字段或真实关系损坏明确失败，不写回修复。脚本存储不在此库。

元数据相关区域为：

```text
native-metadata-policy
native-local-lyrics
```

区域尚未创建时，owner 使用当前内存默认值，表示用户尚未修改该项。区域一旦存在，在实际消费时必须通过当前 schema、必需字段与关系校验；未消费扩展不阻断其他动作，损坏值不能退化为默认。

不兼容的当前控制记录保持原样并明确失败。

## 元数据平台偏好

`native-metadata-policy` 的唯一当前形态为：

```json
{
  "schema": 1,
  "revision": 0,
  "platforms": ["wy", "tx", "kg", "kw", "mg"],
  "enabled": ["wy", "tx", "kg", "kw", "mg"]
}
```

- `platforms` 必须是五个平台的完整无重复排列。
- `enabled` 必须是无重复子集，可以为空。
- `music.metadata.settings.get` 返回当前权威设置。
- `music.metadata.settings.save` 必须提交准确 `ifRevision`、完整 `platforms` 与 `enabled`；成功后 revision 加一并立即生效。
- revision 冲突、待确认写入或格式错误不会通过额外刷新、延时重试或影子设置掩盖。
- 平台顺序同时约束歌词和封面元数据选择，但不改变音频播放来源优先级。
- 播放器起播复用歌曲当前封面，同曲候选增加保留仍被策略允许的已选 URL；只有缺图或加载失败才继续选择。显式变更平台顺序或启用集合重新按新策略选择，失败保存期间仍按现有权威确认流程处理，见[原生公共图像](artwork.md)。

## 歌词选择

自动歌词读取顺序为：

1. 当前歌曲已明确关联的本地歌词；
2. 按 `platforms` 顺序遍历 `enabled` 中的精确录音；
3. 当前平台歌词由同一 `MusicPlatformClient` 请求共同 Node 规范正文；
4. 既有脚本 lyric 回退由 `MusicLyricApplication`／`Scripting` 适配，不把真实平台失败当作空结果。

歌词遍历当前仓库已有、符合平台设置的候选，不为缺可选文本补搜其他歌曲。脚本歌词沿现有启用及声明入口顺序执行；原生不保留平台候选数量门禁。

`MusicLyricApplication` 对同一当前请求合并在途操作，并用策略代次、平台代次、歌曲身份和取消令牌围住结果。设置、账号、脚本源集合或歌曲变化会使旧缓存与旧回执失效；关闭等待已准入工作真实结束。

## 本地歌词导入

本地歌词链为：

```text
显式文件选择
→ 原生只读快照
→ 用户确认关联
→ 当前时间轴解析
→ MusicLibraryOwner CAS
→ 当前歌曲显示
```

文件入口只接受 `.lrc`、`.qrc`、`.yrc`、`.txt`：

- 完整读取，不设文件字节或文本字符配额；
- 接受严格 UTF-8，可带 BOM；UTF-16 LE/BE 必须带 BOM；
- 拒绝 ANSI 猜测、损坏编码、控制字符、UNC、设备路径、硬链接、重解析点及读取期间变化；
- 不扫描目录、不执行文本、不保存所选路径或文件名。

`music.lyrics.local.save` 只允许保存当前队列正在播放歌曲的准确身份。换曲、离页、关闭或旧读取晚到不能关联到新歌曲。

## 本地歌词区域

`native-local-lyrics` 当前 schema 为 1，items 为 `{songId,contentHash,lines}`；songId 是仓库永久 UUID，contentHash 仅绑定导入文本，不是歌曲身份。本地歌词不设首数、字节、文本或行词数量上限；保存通用时间轴，不保存路径、文件能力、凭据或媒体 URL。`music.lyrics.local.get/save/remove` 按永久引用读取／替换／移除，歌词关闭不删除本地内容。时间轴格式、有限时间值与当前歌曲身份仍须有效。

## 共享音乐图片与展示缓存

`MusicPresentationCache` 使用 Caches 下独立 `music-presentation-v2` 目录，图片按完整 URL 保存原网络字节，展示歌词按永久 songId 保存单条 JSON；二者合计按原始资源实际字节沿原 8 MiB／30 天预算。标准逐文件缓存首用只枚举文件长度／时间，后续读取、写入和播放续期只处理本资源，不存 Base64 或全缓存索引文档，也不排队完整缓存快照。旧图片／歌词格式不读取、兼容或自动清理。音乐专用 `ArtworkService` 使用该 byte port，解码缓存沿 32 MiB 像素预算而非固定条数，编码图片缓存单项准入为 4 MiB，普通传输与编码输入解码不设该总量拒绝；实际读取／解码／保存最多 4 项同时执行，排队不预读字节；WPF `ArtworkImage` 与播放器共用此实例。其他业务图片实例不接音乐磁盘 port。

封面选择写入同一歌曲仓库，`artworkSourceKey/artworkSourceImage/cover_url` 区分平台原图与实际选定 URL。`SongMetadataChanged` 在提交后通知当前搜索、目录、队列和播放投影。具体换图／共享取消／清理语义只在共同资源契约维护；这些接线尚未取得本轮运行验证。

## 取消、提交和错误

- 文件选择或确认取消不写入。
- CAS 准入前允许取消；准入后以真实提交结果为准，不能把取消当作回滚。
- `REVISION_CONFLICT`、`PENDING_WRITE`、`COMMIT_UNKNOWN`、`CORRUPT_STORAGE` 和限额错误都必须保留其实际语义。
- 页面只显示必要状态或错误；持久状态由唯一 owner 决定。
