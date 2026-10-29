# 音乐目录与频道契约

## 所有权

- `MusicNativeCatalogOwner` 是固定平台目录、频道、歌单／歌手详情和目录动作的唯一原生 owner。
- `MusicCapability` 的 Catalog presentation 只负责当前页面状态、用户意图、代次与取消；不保存第二份目录真值。
- 网易云、QQ、酷狗请求由原生 `IMusicPlatformCatalog` 完成。普通目录读取不启动 Node。

## 当前命令面

只接受以下当前命令：

- `music.catalog.describe`
- `music.catalog.search`
- `music.catalog.chorus`
- `music.catalog.song`
- `music.catalog.playlist`
- `music.catalog.home`
- `music.catalog.channels`
- `music.catalog.channel`
- `music.catalog.personalPlaylists`
- `music.catalog.daily`
- `music.catalog.fm`
- `music.catalog.similar`
- `music.catalog.heartbeat`
- `music.catalog.queueRecommendations`
- `music.catalog.album`
- `music.catalog.artist`
- `music.catalog.playlist.prepare`
- `music.catalog.playlist.commit`
- `music.catalog.playlist.discard`

未知命令、额外字段、错误平台、错误游标、非规范数字或不完整身份必须明确失败，不能猜测另一种格式。

## 页面读取与请求生命周期

- 推荐、频道、搜索、个人歌单和详情分别持有自己的可见请求，不因进入一个页面展开全部平台或全部栏目。
- 网易云频道头部固定为“精选、歌单广场、排行榜、歌手、VIP、欧美、电音、华语、情歌、二次元”；“更多”只展示当前官方面板的 35 个频道并保持固定顺序，不把已退出表面的原创、新歌重新混入。上游 `music_top_tab_acg` 在原生目录边界归一为当前稳定身份 `acg`，歌手是已有原生 owner 的固定频道，不依赖动态顶部目录返回。
- 歌单广场固定公开“推荐、官方、华语、摇滚、民谣、电子、轻音乐”筛选，筛选值与 opaque cursor 共同绑定 continuation，不能跨筛选混页。歌手目录使用 `area:type:initial` 当前组合 token，三组筛选共享同一服务端快照但各自保留选中项。VIP 页只读取真实推荐歌曲和名称明确为黑胶 VIP／SVIP 的榜单，不把普通榜单改名混入。
- 歌手卡进入 `catalog-artist/wy/{id}`，只发一次 `music.catalog.artist`，同一响应发布歌手身份、头像和完整有界 `hotSongs`。热门歌曲“播放全部”只消费当前已发布批次，不再次读取歌手详情或预取专辑。
- 首次进入当前页面或当前栏目时读取；分页只读取下一个未加载游标，到达末尾后停止。
- 页面、平台、筛选、账号或搜索词变化会同步提升代次并取消旧请求；旧回执不能覆盖新页面状态。
- 卡片悬停预览按卡片身份和页面代次单飞，同卡片并发等待者只共享一个物理请求。单个等待者取消不影响其他等待者；最后等待者取消、缓存分页切换、任一页面代次推进或同代次可见 snapshot 替换都会退休并取消已不可见卡片的预览。取消后的请求在应用调用真实结算前仍计入最多两个物理预览并发并由关闭流程等待；同代次 snapshot 替换只退休预览表面，不推进全局页面代次，也不误退仍有效的搜索会话。
- 相同页面身份的有效内存快照可以复用。返回歌手频道恢复原筛选、分页和滚动状态，不重复读取；显式刷新才建立新代次，不通过布局、绑定或可见性回调递归刷新。
- `RetireCatalogSession()` 清理当前展示、分页、私有账号戳和在途请求；`InvalidateCatalogAction()` 只撤销尚未提交的目录动作。

## 完整歌单动作

- 读取歌单和改变本地曲库／队列是两个阶段。`prepare` 完整读取并验证同一歌单快照，返回短期、单次使用 token；此时不修改曲库或队列。
- `commit` 只允许 `play` 或 `save`，并核对 token、目标歌曲、队列版本、账号与目录代次。提交进入唯一 `MusicLibraryOwner` 串行边界。
- `prepare` 的成功回执即使与取消交错也必须先被实际接收；presentation 取得 token 后再 `discard`，不能丢失回执并把 stage 留给 TTL。
- `discard` 只释放未消费 token。取消或失败不会生成半歌单；提交结果未知时不得重放提交。
- `commit` 一旦进入曲库 CAS 就等待真实回执。已确认成功的提交必须重新读取当前曲库／队列；若原播放意图已经过期，只同步状态，不启动旧媒体。
- token 数量、歌曲数量、序列化字节数、页数和总时限均有界。超限或重复游标明确失败，不返回截断结果冒充完整成功。

## 身份与错误

- 歌曲、录音、歌单及账号使用平台原生身份；标题、封面或列表位置不能替代身份。
- QQ／酷狗缺少事务快照时会在提交前重新核对完整身份和顺序，但不宣称上游具备事务保证。
- 权限、身份、格式、账号、取消、超时和存储冲突保持各自错误，不通过另一平台、脚本或旧格式掩盖。
- 目录读取不写账号，不自动播放，不自动收藏，也不把缓存命中当作权限。
