# Windows 音源脚本接入

App 装配后显式启动 `AppRuntimeClient` → 唯一 Node / HTTP 会话 → Windows 薄 entry → 公共 music 模块 → script-host。
启动不执行用户脚本，不检测；脚本服务失败不阻塞原生目录、曲库、账号和官方播放。
握手按序校验 `music@0.8.0`／`music.script.v3`、`cat@0.2.0`／`cat.script.v2`、`source@0.2.0`／`source.runtime.v2`、`pan@0.1.0`／`pan.runtime.v1`、`tmdb@0.1.0`／`tmdb.data.v1`、`douban@0.1.0`／`douban.data.v1`、`film@0.1.0`／`film.playback.v1`、`netease@0.1.0`／`netease.token.v1` 八模块。`MusicScriptOwner` 仅消费 music 模块；原生订阅协调器按需借用同一 runtime 取网易 token，见[歌单同步](music-library-sync.md)。

## 薄适配

`MusicScriptOwner` 将 `music.sources.*` 转发到公共模块。文件选择留在 Windows：原生已选文件快照经 `ImportSourceAsync` 完整上传原始字节，明确 installationId/sourceRevision，随后调用 `sources.add/update`；不再有传输 token、分片、Base64 或传输摘要。原生不持有脚本目录、源码 collection、下载器或 guest KV writer。
网络地址、编辑、更新、回退、启停、删除和检测均由[公共模块](../runtime/music-script-runtime.md)实现。
旧文件保留原样，不读取、不迁移、不清理。

`music.online.resolve` 只做一次指定 installation × platform × recording × quality 取链；校验录音质量反证、策略签名和刷新内容身份。
缺失平台由[统一选源会话](music-official-selection.md)通过 `music.songs.complete` 补搜一次，不由每个脚本重复搜。
`music.online.lyrics` 使用 `invoke(lyric)`，原生负责歌词开关及解析，不改变官方歌词流程。
普通调用直接处理 HTTP 信封的 `value`；歌词 invoke 的 value 就是脚本结果，不再套安装身份包装。音频字节不经过脚本控制接口。上传取消后仍等待实际发送任务结束，才归还并清理选中文件快照。

## 管理界面

表格为：选择｜启用｜名称与版本｜内置/本地/网络｜健康状态｜五平台胶囊｜检测/编辑/更新/删除。
内置行只能检测、切换启用；编辑/更新/删除保留禁用占位。批量删除需确认，只删除脚本；批量检测最多三个并发。
编辑浮层只改名称与网络地址，并提供可用时的回退入口。操作结果统一通知，启用不充当健康状态。
脚本检测事实共享持久化；内置源检测结果只在本次 App 会话保留。

存储、网络和请求绑定权限见[公共契约](../runtime/music-script-runtime.md)；启动与恢复见
[装配](app-runtime.md)、[显式恢复](runtime-recovery.md)。管道身份核对不是网络隔离，脚本可访问已开放的本机/私网 HTTP(S) 服务。
