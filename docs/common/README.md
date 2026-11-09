# 共同契约

本目录记录跨端一致的业务含义、请求时机、身份、权限和完成语义，不统一界面技术栈、目录名、类型名或底层实现。文档总索引见[文档](../README.md)。

## 契约索引

| 范围 | 文档 | 维护内容 |
| --- | --- | --- |
| 业务页面 | [页面数据请求](pages/data-requests.md) | 展示数据复用、资源完整性、用户动作及请求生命周期 |
| 影视搜索 | [搜索与播放交接](film/search.md) | 双端入口、参考整合规则、来源分页、稳定追加和已得数据复用 |
| 音乐平台 | [平台客户端上下文](music/platform-client-context.md) | 本地初始化、状态作用域、账号交接、协议消费与受控更新 |
| 音乐播放 | [歌曲资料与智能播放](music/smart-playback.md) | 共用歌曲事实、平台搜索证据、自动／手动来源、准备提交与错源 |
| 音乐验证 | [逐端自查](music/self-check.md) | 调用链核对、请求分类、合成场景及证据报告格式，不另定义业务规则 |
| 来源宿主能力 | [capability ABI](../runtime/source-capability-abi.md) | HTTP、Cookie、存储、编码、加解密、Web 交互和虚拟路由的跨平台语义 |

每项共同规则在对应职责文件中维护；端侧文档引用该文件，保留具体 API、协议字段、系统生命周期、存储格式、预算数值、测试入口和实现缺口。

## 端侧实现索引

| 端 | 装配与能力 | 页面与平台 | 存储 |
| --- | --- | --- | --- |
| iOS | [架构与职责](../ios/architecture.md) | [音乐能力](../ios/music.md) | [本地文档存储](../ios/storage.md) |
| Windows | [架构与职责](../windows/app-structure.md) | [客户端上下文](../windows/music-platform-client-context.md)、[平台目录](../windows/music-native-platforms.md)、[目录与频道](../windows/music-catalog.md)、[个人目录页面](../windows/music-catalog-personal-ui.md)、[账号](../windows/music-native-accounts.md) | [账号存储](../windows/music-account-storage.md)、[客户端文档](../windows/music-platform-client-context.md#文档与首次导入) |

## 契约与验证状态

共同契约是验收目标，不表示各端已经实现或通过验证。尚未落地的设计不得写成现有类型、namespace、存储成员或已完成能力；现行事实与缺口由对应端侧文档说明。

各端按实际调用链、请求记录和测试分别自查。单端通过不能代替另一端通过，源码核对、编译、替身测试、实际 UI 行为和受控实网验证也不能互相代替。实际报告使用[逐端自查](music/self-check.md)的格式。

修改共同契约时，同步核对受影响端的实现说明、消费者和验证证据；实现缺口不能靠改文档、删断言或把错误变为空结果消除。
