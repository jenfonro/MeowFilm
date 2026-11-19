# Windows 音乐平台接入

共同规则只有[平台请求与上下文](../common/music/platform-client-context.md)一份；运行器方法见 [music.platform.v1](../runtime/music-platform.md)。本文件记录当前 Windows 接线，未将静态源码作为测试通过证据。

## 装配与调用

`MusicNativeApplication` 借用 App 的 `IRuntimeClient`，创建一个 `Platforms/MusicPlatformClient.cs`，绑定原生账号 session source。`MusicPlatformCatalog`、官方音频／歌词、账号 protocol、同步及反馈使用这个客户端，传业务 operation 和实际参数，不构造上游 HTTP。

`ReadAsync` 取得当前会话后进入 `RequestAsync`；`Publish` 串行调用 `music.platform.session.update` 并要求真实 `published:true`。客户端携带 `expectedSession` 关联当前 scope／generation，读取取消和失效由 scope token 与实际 active task 跟踪。写请求由 Node 返回真实状态回执；丢失交换不冒充未发送。

## 文档与账号交接

原生 `MusicAccountOwner` 将 platform／userId／cookie 明文保存于共享 SQLite 账号关系后发布授权；平台设备、QIMEI、匿名登记和 prepare 由 Node owner 保存在对应关系表，不建立第二份账号或匿名文档库。

显式外部账号文件继续走[现行文件契约](../common/music/account-transfer.md)，由原生文件 owner 检查和条件保存；不能把文件格式等同内部账号文档。歌曲仓库、引用队列和图片归属分别见[曲库](music-native-library.md)和[元数据／资源](music-metadata-policy.md)。

## 关闭与验证边界

客户端登记 publication／active task，撤销时取消旧读取，等待 Node session 回执；`DisposeAsync` 停止新准入，排空自身工作而不释放借用 runtime。App 在能力结束后释放共享运行器与存储。真实失败关闭、已派发写结算、账号 lease／CAS 和物理收尾仍需原生及运行器选测。

当前迁移没有本文件所覆盖的运行验证结论；不沿用删除前的客户端／wire 测试结果。后续按[逐端自查](../common/music/self-check.md)区分合成协议、原生消费者、构建及真实账号链路。
