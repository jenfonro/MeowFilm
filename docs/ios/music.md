# 音乐能力

共同规则见[页面数据请求](../common/pages/data-requests.md)和[平台客户端上下文](../common/music/platform-client-context.md)；本端按[逐端自查](../common/music/self-check.md)的报告格式验证，本文件保留 iOS 能力及实现差异。

## 范围

- 页面：推荐、频道、我的、搜索与结果、公开及本地歌单、设置、账号、完整播放器、歌词、队列和历史。
- 官方平台：网易云音乐、QQ 音乐、酷狗音乐。
- 本地能力：加密曲库与凭据、显式曲库导入与回滚、播放队列和历史、系统媒体会话。
- 当前不提供第三方脚本音源、音箱控制或局域网供流、用户下载及离线库。

## 播放与音质

- 网易云标准音质不做本地会员拦截，以平台实际返回决定能否播放。
- 高音质检查真实权益；试听、受保护或无权限资源不能作为完整歌曲。
- 页面显示平台实际返回的音质，不用请求音质代替。
- 平台错误按实际原因分类，诊断信息不得包含凭据、完整签名 URL 或原始响应。

## 页面数据请求

- 首页由 `MusicNativeHomeService.swift` 组织账号首页或匿名目录；目录解析保留歌曲与歌单摘要，`browseEnrich` 批量处理当前缺失的必要展示字段。
- `MusicPublicBrowseViewModel` 保存已加载页及 tab 选择；`MusicPublicBrowseSectionTab.loadItems` 在显式选择时补读并回写页缓存。
- 聚合搜索由 `MusicSearchFusion` 在本地分组、去重和排序，消费已有 ID、MID、hash 与 `sourceKey`；融合过程不调用平台详情接口。
- QQ／酷狗公开歌单使用真实当前成员页，读取 source 保留已观察位置供完整动作核对。网易云账号歌单保留完整成员批次，首次只补当前 30 项，后续页按需投影与补读。
- 完整歌单保存由 `MusicNativeRemoteCollectionActions.swift` 取得完整 source、核对成员覆盖及快照后进入曲库事务；候选匹配、权限／音质和音频解析留在播放链。
- UIKit 图片通过 `MusicArtworkHTTP` 的独立会话读取，不使用 CookieJar 或凭据存储；单次读取上限为 4 MiB，HTTPS 来源与重定向在传输边界检查。

## 平台客户端上下文

App 通过 `MeowfilmResources` 与 `MeowfilmAssembly` 注入受保护客户端存储和稳定 owner；具体平台交接如下。

| 平台 | 所有者与交接 |
| --- | --- |
| 网易云 | 安装客户端资料由 `NativeMusicClientService` 在独立受保护文档中持有，公开读取、新登录及适用的播放读取复用。已有凭据的不同设备绑定保留原身份；账号授权及绑定资料仍由凭据 writer 验证和保存。 |
| QQ | 公开客户端 owner 继续使用既有 `music.qq-public.v1` 文档、QIMEI 与匿名 session；账号 adapter 取得同一客户端资料，但账号 auth 不进入公开实例或匿名文档。 |
| 酷狗 | 安装 seed、派生 MID 与已确认 dfid 由客户端 owner 保存，新登录复用；登记只在协议需要的明确业务中发生。导入账号的不同设备绑定不替换成安装标识。 |

- `NativeMusicClientService` 使用 `music.client-context.<platform>.v1` 文档；QQ 仍使用 `music.qq-public.v1`。文档校验及预期修订保存由 `MusicClientContextPersistence.swift` 提供，账号授权仍通过账号 lease／CAS 交接。
- QQ 设备协商复用 `MusicAccountSingleFlight`，由 `MusicQQDevice.swift` 消费客户端范围。独立构造且未注入持久化的客户端上下文仅保留内存状态，App 装配不采用这种易失构造。

## 缓存

- 实际播放后完整缓存音频，命中、失效、坏文件回退网络和清理由原生服务负责。
- 歌词、封面和播放展示缓存与音频缓存分别管理。
- 缓存不是下载管理；曲库、队列和同步数据不能作为可淘汰缓存。

## 来源与许可

音乐界面和行为参考 iWebPlus `b900c259d991a73768cc36a28b48539b31961668`（早期来源 iWebPlayer `731b2517ad71fde1ba7250b24ffb9798cf01128e`）；部分平台实现参考 meowmusic `cc22a7ee6eb7d02532df59ced788be0205d450ea`。对应许可与声明位于 `LICENSES/`，移植源码中的 SPDX 和作者声明保留。
