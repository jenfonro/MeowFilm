# Windows 脚本连接显式恢复

## 契约与影响

- `IRuntimeRecovery` / `IRuntimeRecoveryScope` 是进程内生命周期契约；真实 `NodeRuntime.IsConnected` 报告实际连接。连接使用 `aggregate` 身份，按序核对 `music@0.8.0`、`cat@0.2.0`、`source@0.3.0`、`pan@0.1.0`、`tmdb@0.1.0`、`douban@0.1.0`、`film@0.1.0`、`netease@0.1.0`；业务响应分别为 `music.script.v3`、`cat.script.v2`、`source.runtime.v2`、`pan.runtime.v1`、`tmdb.data.v1`、`douban.data.v1`、`film.playback.v1`、`netease.token.v1`。连接恢复重建进程和 HTTP 会话，不自动恢复旧影视 attempt 的授权；影视恢复与媒体资源生命周期见[影视播放契约](../runtime/film-playback.md)。LX/source 的逐实例原生 QuickJS 线程、Cat 的逐实例 Node Worker 和按需 token Worker 由公共宿主管理。
- 当前只有唯一 App、AppHost/Core 和脚本运行时适配器。既有原生音乐账号、曲库、固定 API 和播放不以连接为前置条件；不向 Node 开放普通文档权限。Cat 的设置和私有卷由稳定 dataRoot 持久化，进程恢复后从当前文件重新读取，不靠再次同步账号恢复状态。
- 首个脚本请求前显式注册 scope，最多 32 个，重复/迟到注册拒绝。无全局事件总线、全局当前源或跨 App 进程共享。

## 实际链路

1. App 装配后显式调用 `AppRuntimeClient.Start()`，启动唯一 Node 进程，宿主启动本身不执行音乐脚本；影视随后恢复启用猫源，共享同一次连接。页面可见性不控制进程启动。
2. 失败保留为失败。普通请求不自动创建新进程、重放失败操作或激活全部音源。
3. 用户在原生脚本页明确重试，或在猫源管理页明确刷新失败连接：退役旧 transport/进程、等待释放，再排空已接纳任务并撤销旧会话身份。
4. 音乐撤销旧执行身份，由公共音乐模块读取唯一脚本目录，不通过原生 Library 重写不确定数据，下次明确操作才装载指定音源。猫源管理刷新在恢复握手后重新读取运行器设置并恢复启用猫源；不重放失败的导入、更新或检测。
5. 新连接发布与准入原子化，旧代次成功被拒绝；多个重试共享同一恢复任务，恢复中拒绝新脚本调用。
6. 离页只取消 UI 等待，不丢失恢复归属；App 关闭取消并等待恢复及迟到 factory。退役失败仍是失败，不能并发启动第二套进程。

`ResumeAfterRuntimeRecovery` 只做同步非阻塞准入更新，不重入 runtime、执行 IO 或自动请求源。清理信号不等于资源释放。命名管道/PID/Job/会话检查不代表恶意脚本沙箱。

实例卡住、取消和实际资源退出遵循[共用运行时](../runtime/script-runtime.md#quickjs-执行边界)：source 初始化/请求60秒，清理3秒；LX/source 的原生执行线程观察取消，Cat 等待真实Worker退出。没有内存配额或OOM自动重启，不重放失败调用。source capability 的物理任务和profile退休按[共同ABI](../runtime/source-capability-abi.md)继续跟踪，线程退出或signal触发不能冒充不合作I/O已完成。
