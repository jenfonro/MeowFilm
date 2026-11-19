# 共享 SQLite 存储契约

每个 App 数据根只有一个在线 SQLite 数据库 `storage-v1/meowfilm.sqlite3`。iOS 原生客户端、Windows 原生客户端及同一 App 内的 Node 运行器连接到该库；iOS 和 Windows 各自使用本机数据根，不通过 SQLite 文件跨设备共享。统一表定义由 `packages/storage/schema/*.sql` 生成并校验，根 schema identity 不匹配即启动失败，不执行数据库升级。

## 存储格式与边界

- 应用内部业务记录和结构化缓存使用关系表、typed columns、外键、唯一约束、索引及有序子关系；关系和实体有明确 owner，不把整份业务对象序列化为 TEXT/BLOB 保存。第三方动态值只使用有界 typed value-tree relations。
- JSON 仅作为网络、Node/原生私密 IPC、脚本 API、构建元数据与用户明确导入／导出的交换格式；编码或解码 JSON 以形成 API 快照时不表示数据库储存 JSON 文档。
- 第三方脚本自有 private volume 由脚本及宿主文件 capability 按第三方约定管理，不由应用把 JSON 文件禁令扩展到第三方格式，也不以扩展名过滤其内容。用户选择导出的 JSON 文件为正常外部交换文件。
- 真实源代码、媒体、图片、歌词及其他不可变/流式二进制资源按所属资源 owner 使用文件；数据库保存强类型索引和身份，不将大块媒体嵌入 JSON。
- 应用持有的 Cookie、Token、密码及其他可恢复凭据以普通明文 typed fields 存放在所属业务表，与记录 revision 同一事务提交。无 Keychain/DPAPI 凭据副本、应用加密层、本地密钥文件或额外解锁口令。系统沙箱、文件权限、账号授权隔离、撤销及日志脱敏仍然生效。

## Owner 与并发写入

| 数据类别 | 唯一业务 owner | 可写参与方与协作 |
| --- | --- | --- |
| 通用和平台设置 | 端侧原生设置 owner，定义于 `ios_*`／`windows_*` 表 | UI 直接写本库；Node 只取得 capability 授予的块读取/有限字段更新。设置事务成功后可通知当前运行器重新读取，通知不承载设置值 |
| TMDB／豆瓣设置 | 端侧设置 owner；如运行器独立装配则由运行器 settings owner 管理其自身同一根 | App 注入模式由客户端写，运行器读取；独立工具模式由 Node 写关系表。运行时元数据/API cache 由运行器 cache owner 写同一数据库 |
| 网盘凭据 | 原生 Pan credential owner | UI 原生保存，Node 登录/刷新后经 provider 单项 revision CAS 回写。换号、撤销与自动刷新在对应业务事务中校验账号及行 revision |
| 音乐账号凭据 | 原生账号 owner | Node 执行外部认证/刷新配方但不另存账号；原生 owner 对同一 SQLite 行提交并发布授权 lease。外部 JSON 账号导出仍由用户显式触发 |
| 运行器脚本安装/私有数据 | 每个脚本/runtime owner | 运行器持有安装关系、每键 typed KV 与源码身份；宿主 private volume 文件 API 只代表第三方脚本自管文件，不等于其业务 JSON 成为数据库 payload |
| 持久业务/重建缓存 | 对应平台业务 owner | 共享 SQLite 事务入口；重建缓存有按表大小/TTL/owner policy 回收，不另设文件 JSON cache store |

SQLite 写事务序列化物理修改；跨 owner 的 stale-write 防护由 owner revision/CAS、唯一 operation receipt 和具体行 revision 实现。不得用全库一个共享内存快照覆盖其他 writer；禁止跨文件双写和把两个 owner 合并成一个无边界 writer。仅已授权 owner 可获得对应 repository/capability；这是应用层权限，不声称 SQLite 表级 ACL。

## 事务、耐久与故障

- 每个连接启用外键与 `trusted_schema=OFF`，WAL journal、`synchronous=FULL` 和关闭自动 checkpoint。SQLite 是本机事务型数据库；WAL 不意味着多主高可用或跨设备集群。
- 短事务保持同步一致性；单个用户动作涉及的字段、子关系、缓存索引和 revision 一并提交。UI/Node 之外的长计算或网络等待不得持有写事务。请求按读取窗口和查询边界执行，不为单条记录读写强制物化整表。
- 数据库写操作有界准入与内存预算；已受理事务返回真实提交回执，取消不谎报回滚；关闭先拒绝新任务、等待已受理操作完成，再 checkpoint 并释放连接。共享写入口在 iOS SQLite queue 和 Windows/Node worker lane 上串行，跨进程维护使用文件互斥。
- 初次新建只允许当前固定 schema。空新路径可以初始化；已有空文件、身份/版本/hash/结构不符、损坏或孤立 WAL/journal 失败关闭，保留现场。不探测旧 JSON、兼容旧 DB、迁移、双写、回退或自动删除已有用户数据。
- 空间不足、只读、锁竞争、I/O、损坏与约束冲突以稳定错误分类返回；业务记录损坏不得伪装为空配置。缓存可按所属表的明确边界重新生成，但不能因缓存异常删除或重建整个数据库。
- SQLite 可用在线 backup API 取得单一一致快照；备份不是在线的第二事实源。恢复必须是相同当前 schema 与产品身份的完整库，且在开放业务前按 owner 使可重建缓存失效。不在本契约加入自动备份频率/保留策略。
- 真实性能须用目标设备、正式平台 driver 和代表性数据验证；schema、编译或内存 driver 测试不证明设备 I/O 时延。

端侧数据库结构、driver、启动生命周期与已实测行为分别见 [iOS 存储](../ios/storage.md)、[Windows 本地存储](../windows/protected-storage.md)；运行器所需的配置投影和私密通道见 [Runtime bridge](../runtime/runtime-bridge.md)。
