# Windows 原生设置与运行器消费

通用、音乐、影视的设置由相应原生 owner 直接读写每个 App 唯一的 SQLite 数据库。运行器停止或故障不阻止本地设置，不因查看、保存设置启动或恢复运行器。影视规则执行、元数据请求、网盘验证及二维码登录仍由公共 Node owner 完成；离线时这些在线操作真实失败。共同 schema、事务及凭据边界见[共享 SQLite 契约](../common/settings-storage.md)。

## Typed settings 与提交

App 原生 owner 使用同一 `NativeDatabase`，将以下配置写入规范化关系和 typed columns。revision/CAS 按 owner 或 provider 行提交；真实事务成功后才发布值。页面取消不能撤销已经受理的持久写入，冲突、损坏和写入失败不当成空配置或保存成功。

| 通知块 | 数据表/关系 | 内容与 owner |
| --- | --- | --- |
| `cat.sources` | `windows_cat_settings` 及子表 | 网关选项与猫源来源意图；原生 owner |
| `pan.accounts` | `windows_pan_accounts` | provider 单行凭据与 account/row revision；原生 owner |
| `tmdb` | `windows_tmdb`、`windows_tmdb_settings` | typed 设置字段；原生 owner |
| `douban` | `windows_douban`、`windows_douban_settings` | typed 设置字段；原生 owner |
| `film.rules` | `windows_film_rules` 及规则子表 | 分组规则与排序关系；原生 owner |
| `film.preferences` | `windows_film_preferences` 及列表子表 | typed 偏好与有序项；原生 owner |

凭据在所属业务表中纯明文保存；不使用 DPAPI、Keychain、应用层加密或独立凭据存储。密码、Cookie、Token 不进入通知、日志或第二份运行器设置副本。没有运行器消费者的纯原生设置不发送通知。

TMDB／豆瓣只写入 owner 声明的 typed settings fields，忽略未消费的 patch 成员，不保留未归属的扩展 JSON。文本及端点规范化：文本去首尾空白，普通 HTTP(S) 地址规范化并去尾斜线，TMDB 空地址恢复默认；豆瓣代理模板保留有效的单个 `{url}`／`{encodedUrl}` 占位。非法地址、控制字符及无效代理草稿在提交前拒绝，不等运行器在线后才发现。patch 先覆盖明确编辑的字段，不预检将被丢弃的旧值；API 返回完整本 provider DTO，因此所有最终字段在提交前必须可消费，未编辑的缺失／NULL 字段不会被静默补齐。

智能播放偏好在原生提交前校验并规范化实际编辑的字段：顺序和关键词去首尾空白，拒绝空项或重复项，未知顺序 ID 和短顺序按编辑后的值保留，不要求默认 ID 全集，也不补入未选项。设置与有序项分别以 typed columns/relations 持久化；修改一个组不改写其他组；读取在具体字段或当前可见控件消费时解析，不以未消费组或 iOS 开关的业务形状作为前置条件。

“设置已保存”和“运行器已应用”分开确认。保存不等待运行器响应，通知失败不回滚本地配置。原生 owner 只向当前启动／活跃代次发送改变的块名，不保存离线通知、不重放保存、不为通知恢复连接；下一次启动直接读取最终关系行。

## 私密配置能力

PID 验证的 CurrentUserOnly 启动／存活管道在 Windows App 装配中提供设置专用 capability，不暴露任意 SQL、数据库文件或 owner，不提供给用户脚本。运行器经授权 repository 操作同一个 SQLite 数据库。

- `settings.read` 请求为 `{kind,id,block}`，返回该块的类型化读取投影与 revision。
- `settings.pan.update` 仅允许 `pan.accounts`，用于运行器取得新凭据后的 provider 行 revision-CAS 回写。
- 回执为 `{kind:"settings.result",id,ok,document,revision}`；存储失败回执为封闭错误码，不包含配置。
- 请求只消费对应动作所需的唯一 kind／id／block 及写入 command；命令包含 provider、mode、accountRevision、rowRevision、credentials，准确关联、块权限及行 CAS 仍核对。
- 每帧最多 8 MiB，按换行分帧；未知请求、越权块、损坏帧或管道断开关闭该运行代次。
- Node 在对外就绪前读取全部设置块。HTTP `POST /v1/settings/changed` 只接收 `{block}`，使用 `runtime.http.v1` 信封；`RuntimeHttpClient` 的本地方法名为 `runtime.settings.changed`。通知中没有文档或凭据。

运行器重读被通知块并更新真正的消费者。猫源撤销旧输入工作、解析当前来源并调度替换；网盘撤销旧账号代次的请求／登录会话；元数据失效旧配置和相关业务缓存；影视规则更新新任务使用的快照，偏好重核准备候选与发现范围，不强停已开始的媒体。旧任务不得覆盖新配置或发布迟到凭据。现有播放、业务历史和无关设置块不因保存被删除。HTTP 成功值 `{notified:true}` 只确认通知处理；猫源实际替换由模块生命周期继续执行，不能据此宣布装载成功。

## 猫源意图与安装事实

原生猫源配置保留安装身份、名称、类型、网络地址或选择文件引用、启用意图及 `inputRevision`。本地选择记录文件绝对路径与获准字节的 SHA-256，不保存第二份脚本源码；运行器读取该选择文件作为新输入时核对哈希。匹配已装载身份时复用运行器已有源码文件，不承诺每次启动重新核对所选文件或安装缓存的历史哈希。原生不下载网络猫源或执行脚本。网络编辑不读取附带的本地路径；本地编辑才读取选中文件并绑定哈希。管理列表只投影 scripts，不提前解码未用 port／providers／mappings；坏业务条目局部隔离，无关修改保留当前 schema 可表示的 typed 行；只修改准确目标，不将隔离后的健康列表整份保存。新增检查实际已占用身份，删除不解码被弃旧值，编辑只读取合并所需材料并在提交前验证最终目标；歧义身份不允许被新增、编辑或删除覆盖。当前固定关系没有任意 JSON 扩展槽，实际存储关系／类型错误仍明确失败。

运行器 Cat 安装事实使用 `cat_installations` 及有序配置关系，记录实际输入身份、版本、私有配置和站点；不另存客户端可编辑来源意图。源码仍在运行器 `sources`，脚本私有卷及加载／停止生命周期仍由公共 Cat owner 管理。

保存类型／地址／本地文件改变后，活跃运行器按新输入立即替换，不以“更新”作为前置步骤。新网络输入没有匹配文件时自行下载，旧本地文件不能冒充网络输入；停用源不执行。名称编辑不重建实例。失败保留已保存来源但不把旧实例标为新来源正常，迟到下载不能覆盖后保存输入；类型切换不强制启用或恢复已删除视频源。

## 网盘凭据回写

进入账号页只读原生配置，不自动刷新账号、不显示读取进度条。手动凭据保存和退出使用原生 CAS；二维码、验证及自动刷新通过运行器。运行器取得新凭据时仅通过私密设置能力回写同一原生账号行，以 provider 的 account revision 和 row revision 隔离并发换号；CAS 冲突后重读并返回 `STALE_ACCOUNT`，不自动合并或重试，不同 provider 互不覆盖，也不因无关账号更新制造冲突。自动轮换凭据保持当前 provider revision，手动换号、退出及二维码确认替换递增 revision。confirmed 必须在真实凭据持久化成功后发布。单 provider 读取和修改只消费该账号的 revision 与已知凭据，拒绝未知 provider／字段，其他账号行保持不变。替换或清除作用于所选 provider 的已知凭据，不将附加输入扩展为请求凭据。

原生账号读取按 provider 隔离不可消费的记录，采用[公共网盘契约](../runtime/pan-runtime.md)的 `{provider,errorCode:"CORRUPT_STORAGE"}` 单项错误投影；不提供 revision／configured／status／fields，不回填旧成功状态。数据库读取、当前身份或结构核验失败仍整体失败；未知 provider 不注册能力。错误行显示“凭据损坏”并禁用登录、编辑、刷新和退出，健康兄弟照常可用；无关提交不重写坏项。单项错误不是在线成功或持久提交回执，实际写入仍取得所选账号的真实 revision 并进行 CAS，不能用缺失身份或默认 0 修复坏 revision。当前重新读取到健康记录才恢复操作权限，迟到错误仍受页面／单行动作代次隔离。

## 当前格式与启动

启动只接受当前单库 schema identity 与版本。不会探测、读取、转换、迁移、兼容、双写、回退旧 JSON 文件或数据库，也不会因格式错误自动删除数据；旧用户文件保留不动。当前运行器安装事实、源码、私有卷和业务行继续由各自 owner 管理。

验证入口是相应 Windows 原生设置／真实运行器组及定向 Node 测试；入口、编译、替身、桌面 UI 和真实运行器结果须分别记录，文档不代表运行通过。
