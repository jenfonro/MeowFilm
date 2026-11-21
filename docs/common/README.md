# 共同契约

本目录记录跨端一致的业务含义、请求时机、身份、权限和完成语义，不统一界面技术栈、目录名、类型名或底层实现。文档总索引见[文档](../README.md)。

## 契约索引

| 范围 | 文档 | 维护内容 |
| --- | --- | --- |
| 构建依赖 | [依赖缓存](dependency-cache.md) | 共享下载、已验证依赖成品、工具缓存与工作副本私有状态 |
| 校验与失败 | [拒绝条件与数据消费边界](validation-boundaries.md) | 构建／启动禁令、按实际字段消费、覆盖旧值、错误传播及有效行为回归 |
| 业务页面 | [页面数据请求](pages/data-requests.md) | 展示数据复用、资源完整性、用户动作、展示／执行生命周期与读取恢复 |
| 共享存储与设置 | [共享 SQLite 存储契约](settings-storage.md) | 单库关系模型、owner、事务、故障边界、离线设置与运行器通知 |
| 影视搜索与身份 | [搜索与播放交接](film/search.md) | 双端入口、稳定业务定位、参考整合规则、来源分页、稳定追加和活跃数据交接 |
| 电影原生播放 | [媒体输入与核心生命周期](film/playback.md) | 显式媒体请求上下文、核心与自定义界面边界、静默候选及真实排空 |
| 小说阅读 | [小说业务与阅读](novel/reading.md) | 来源与书籍身份、搜索／书架／历史、净化、阅读锚点、换源及资源生命周期 |
| 音乐平台 | [平台请求与上下文](music/platform-client-context.md) | 共同 Node 配方、参数／字段消费、匿名连续性、账号交接及保留边界 |
| 音乐账号 | [账号导入与导出](music/account-transfer.md) | 双端明文文件、凭据与设备关联、批次冲突选择和验证后保存 |
| 音乐播放 | [歌曲资料与智能播放](music/smart-playback.md) | 永久歌曲身份、来源唯一归属、逐记录事务、引用队列、来源发现及选源 |
| 音乐展示资源 | [共享图片与展示资源](music/presentation-resources.md) | 首页快照、封面引用、独立 URL 图片缓存、冷热读取及清理隔离 |
| 音乐设置 | [保存回执与未知结果](music/settings-receipts.md) | 提交后关页、稳定 owner 提示、后续同项成功及范围隔离 |
| 音乐队列 | [播放来源与自动续播](music/playback-sessions.md) | 全局循环、来源会话、候选与插队、续取、恢复和入口资格 |
| 音乐歌单 | [所有权与订阅关系](music/playlist-ownership.md) | 自管内容、网络引用、平台订阅及同步隔离 |
| 播放反馈 | [真实播放反馈](music/playback-feedback.md) | 共用事实、平台准入、计量、来源归属、结束与安全待发 |
| 音乐验证 | [逐端自查](music/self-check.md) | 调用链核对、请求分类、合成场景及证据报告格式，不另定义业务规则 |
| 来源宿主能力 | [capability ABI](../runtime/source-capability-abi.md) | HTTP、Cookie、存储、编码、加解密、Web 交互和虚拟路由的跨平台语义 |
| Debug 模式与调试接口 | [跨端 Debug 模式与调试接口](debug-mode.md) | 运行期 `--debug` 开关、Node 握手透传、16 类结构化日志与不脱敏 `$blob` 外置、本地 Control API、底层诊断与跨端 CLI 契约 |

每项共同规则在对应职责文件中维护；端侧文档引用该文件，保留具体 API、协议字段、系统生命周期、存储格式、预算数值、测试入口和实现缺口。

## 端侧实现索引

| 端 | 装配与能力 | 页面与平台 | 存储 |
| --- | --- | --- | --- |
| iOS | [架构与职责](../ios/architecture.md) | [音乐能力](../ios/music.md) | [本地存储](../ios/storage.md) |
| Windows | [架构与职责](../windows/app-structure.md) | [平台接入](../windows/music-platform-client-context.md)、[平台目录](../windows/music-native-platforms.md)、[目录与频道](../windows/music-catalog.md)、[个人目录页面](../windows/music-catalog-personal-ui.md)、[账号](../windows/music-native-accounts.md) | [账号存储](../windows/music-account-storage.md)、[原生歌曲仓库](../windows/music-native-library.md) |

## 契约与验证状态

共同契约是验收目标，不表示各端已经实现或通过验证。尚未落地的设计不得写成现有类型、namespace、存储成员或已完成能力；现行事实与缺口由对应端侧文档说明。

各端按实际调用链、请求记录和测试分别自查。单端通过不能代替另一端通过，源码核对、编译、替身测试、实际 UI 行为和受控实网验证也不能互相代替。实际报告使用[逐端自查](music/self-check.md)的格式。

修改共同契约时，同步核对受影响端的实现说明、消费者和验证证据；实现缺口不能靠改文档、删断言或把错误变为空结果消除。
