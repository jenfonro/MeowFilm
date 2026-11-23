# Windows 音乐平台目录

共同的配方、字段消费、分页、匿名状态和错误规则见[平台请求](../common/music/platform-client-context.md)，永久歌曲及匹配规则见[歌曲仓库契约](../common/music/smart-playback.md)。本文件只记录 Windows 当前适配事实。

## 所有权与装配

`MusicNativeApplication` 创建单个 `MusicPlatformClient`，借用 App 运行器并连接 `MusicPlatformCatalog`。`Platforms` 中的目录、艺人、榜单、chorus、VIP 和官方音频适配消费共同规范结果；上游签名、HTTP、QIMEI、XEAPI 和平台私有响应解析位于 `packages/music-platform`。

## 平台能力

| 平台 | 当前既有消费者 |
| --- | --- |
| `wy` | 搜索、歌单／专辑／艺人、首页／频道、搜索发现、FM／心动／推荐、风格偏好、官方音频、歌词、chorus、账号与同步／反馈 |
| `tx` | 搜索、歌曲／集合、个人目录、官方音频、歌词、账号与同步／反馈 |
| `kg` | 搜索、歌曲／集合、首页／榜单／目录、个人目录、官方音频、歌词、chorus、账号与同步；Windows 反馈仍有[集成缺口](music-playback-feedback.md#平台边界) |
| `kw`、`mg` | 现有公共搜索、歌曲／歌单和歌词消费，不新增账号或官方播放能力 |

`MusicPlatformCatalog` 将共同 track／page／collection 等结果映射到原生模型。歌曲通过 `MusicLibraryOwner` 采用到唯一仓库；返回原生资料含永久 songId 和平台 `sources`，每个来源保留 `sourceKey/source/musicInfo`，平台定位仍在 musicInfo。原生不以返回 ID／条数或旧页面快照二次证明普通结果；直接歌曲节点保留原卡片可见性、权限提示与播放来源。必须 items／目录列表在所属操作消费，缺失与合法空列表分开，坏项不能拖累健康兄弟或被全部丢弃成成功空页。

## 分页、首页与榜单

KW／MG 集合 continuation 传完整 hop `{page,batch,index}`；QQ／KG 页保留实际 consumed offset，原生 UI 传回真实 continuation。目录登记跳过隐藏卡片，预览只使用实际展示的前三项 preview.song，排名预览投影当前仓库资料，不为卡片补发音频或跨平台搜索。

目录在发布前异步读取所需歌曲及预览的已确认仓库快照；等待期间按加载代次、导航与账号观察失效，旧结果不发布。当前页及保留目录缓存接收歌曲 ID 通知并异步更新，回访也重新投影当前仓库资料，不为普通显示补发请求。目录 owner 和呈现快照读取保留 `STORAGE_UNAVAILABLE`、`CORRUPT_STORAGE`、`STORAGE` 分类，不将本地存储异常标为平台格式错误。

`MusicHomeSnapshotStore` 使用 `music.home.v3` 保存当前首页展示；读取及恢复投影强制在后台执行，即使底层缓存同步完成也不在 UI 同步读库；页面恢复与实时请求分离，实际图片由音乐专用共享 `ArtworkService` 处理。卡片页与个人目录 UI 见[目录](music-catalog.md)、[个人目录](music-catalog-personal-ui.md)。

## 设置、生命周期与验证

每日推荐风格偏好由同一个目录 owner 转发；真实远端保存保留账号关联、已发送回执及 unknown，不因浮层关闭回滚。账号发布／读取取消／关闭由[平台客户端](music-platform-client-context.md)和原生账号 lease 管理。Node 返回的真实错误由原生映射，普通内容变化不产生 targetChanged。

来源、固定上游 pin 和许可见 `LICENSES/NOTICE.md`；两端公共载荷携带原有必要正文。

## 原生音乐榜详情

`MusicSearchCoordinator`、`NativeMusicSearchDiscovery` 与 catalog 消费共同榜单结果；WinUI 目录／搜索页保留实际原始名次与可操作成员投影，缺可选排名不制造位置。当前显示和永久资料的更新由歌曲仓库通知驱动，不因此重新请求平台。
