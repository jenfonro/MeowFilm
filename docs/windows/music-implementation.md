# Windows 音乐能力实现

## 产品范围

Windows 当前交付一个原生 meowfilm App，并接入音乐相关能力。

- 不是独立 Music 产品。
- 不装配下载管理。
- 不用网页或 Node 代替 WPF、原生业务 owner 和原生播放链。
- 不为其他平台预设兼容层。

## 主要职责

| 目录 | 责任 |
| --- | --- |
| `Composition` | 唯一音乐装配、导航、生命周期和公开 seam |
| `Accounts` | 三平台账号、登录、设备身份和账号详情 |
| `Platforms` | 固定官方平台 HTTP、协议和歌曲 DTO |
| `Catalog` | 平台目录、个人内容和歌单读取／提交 |
| `Search` | 歌曲／歌单聚合搜索、策略和请求代次 |
| `Recommendations` | 每日推荐、持续推荐和唯一 queue lane |
| `Library` | 曲库、队列、历史、设置、歌词快照和脚本源元数据 |
| `Sync` | 网易云／QQ／酷狗收藏和歌单同步 |
| `Playback` | 官方候选、授权、取链、恢复和播放状态 |
| `PlaybackCache` | 可淘汰播放候选、高潮和完整音频文件缓存 |
| `Lyrics` | 原生歌词查询、解析、选择和展示快照 |
| `Scripting` | 原始 LX 音源脚本管理和必要 Node 调用 |
| `Feedback` | 播放上报及其唯一设置 owner |
| `Settings` / `Presentation` | 原生即时设置投影和 WPF 视图 |

依赖方向保持：

```text
Windows App
  -> MusicCapability / MusicNativeApplication
  -> 各能力 owner
  -> Platform / Media / Storage / Core
```

基础层不反向依赖能力或 App。

## 原生与 Node

官方网易云、QQ、酷狗链全部由原生 owner 承担：

```text
账号
搜索
推荐
目录
同步
官方取链
歌词
缓存
播放反馈
持久设置
```

Node 只执行原始 LX JavaScript：

```text
music.sources.*
music.online.describe
music.online.resolve
music.online.lyrics
music.online.cancel
music.transfer.*   // 仅 kind=script
```

不运行脚本时，官方业务不启动或依赖 Node。Node 没有通用文档访问、任意文件、进程、账号凭据或 App 内部状态能力。

## 脚本源生命周期

原生 `MusicScriptOwner` 拥有安装元数据、权限、实例代次、取消和持久事务；Node host 只拥有实际 JS 执行会话。

- 安装输入来自原生已选择文件的只读字节快照。
- 设置页允许一次明确选择多份 `.js`；导入只持久当前源元数据并保持停用，不连接音源服务器、不执行脚本。
- transfer 只接受 `.js`、规范 SHA-256、严格 UTF-8 和顺序 Base64 分片。
- 当前 transfer 只允许 `kind=script`。
- 每个 installation、platform 和 generation 都显式指定。
- 更新启用中的实例时，先撤销旧 grant，再验证候选；候选 metadata 与脚本 storage 通过同一 library envelope CAS 发布。
- source rollback 只是在当前安装实例的 `current`／`previous` 之间切换当前脚本版本，不解释或转换旧数据格式。
- 迟到脚本回调、旧 grant 和旧 generation 不能覆盖当前实例。

## 曲库、队列和播放

- `MusicLibraryOwner` 是曲库、队列、历史和设置的唯一 writer。
- `MusicRecommendationQueuePort` 串行所有最终 queue mutation。
- 队列只持久歌曲身份、公开元数据、occurrence 和上下文，不持久短期媒体 URL 或账号凭据。
- 官方播放先取得精确录音候选并完成授权，再交原生播放器。
- LX 取链必须指定 installation、platform、generation、song/sourceKey 和 quality；不能遍历脚本或静默换源。
- 只有真实播放器 `Started` 事件记录播放历史并开始反馈。

## 缓存

缓存与用户下载完全分离：

- 候选缓存保存当前官方候选和授权相关公开信息。
- 高潮缓存保存有限的播放高潮结果。
- 音频文件缓存保存完整已开始播放的字节，但读取时仍重新执行当前录音授权。
- 缓存 schema、索引、文件身份或保护失败时明确报告；不把损坏文档当 miss，不自动重建旧格式。
- 普通可淘汰缓存不可用可以回到网络读取，但不能回退到旧格式或跳过授权。

## 设置

设置由实际 owner 直接持久化并立即生效：

- 平台账号、播放上报、收藏同步、歌单同步各只有一个控制位置。
- 歌曲搜索和歌单搜索使用独立当前策略。
- 音质、外观、歌词／封面平台顺序和播放设备设置分别由其 owner 保存。
- 音源页直接管理当前导入、启停、更新、回退、删除及官方／脚本平台目标；选择目标立即保存，不保留草稿。
- CAS 失败时 UI 读取当前权威结果用于恢复显示，不重放用户操作。

## 请求生命周期

所有网络能力必须定义：

```text
owner
触发入口
请求身份与参数
generation
在途去重
取消条件
分页终止
缓存语义
失败范围
刷新条件
```

规则：

- 启动只获取恢复当前 App 和首个可见页面所需的数据。
- 页面、栏目、详情、更多面板和分页按进入／可见／操作再获取。
- 布局、绑定和集合变化不得无条件再次触发同一请求。
- 同一身份和参数在同一 generation 内只有一个有效请求。
- 旧请求结果不能覆盖新页面、筛选、账号、歌曲或脚本实例。
- 鉴权、协议、格式和权限错误不得自动重试。

## 数据与协议

数据与协议的持久化行为：

- 保持现有 App 数据身份和命名空间。
- 当前格式变化时同时修改 producer、consumer、契约和全部夹具。
- 不兼容数据保持原样并明确失败；未经用户授权不删除、清空、覆盖或改写。
