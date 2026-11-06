# 客户端原生 JavaScript

QuickJS-NG 0.18.0 的原始 C 引擎由 `scripts/quickjs-native.json` 锁定提交、归档哈希、逐文件哈希及中央许可。`quickjs/` 仅包含构建引擎所需原始文件；不包含命令行、libc、模块加载或网络／文件绑定。许可见 `LICENSES/quickjs-native-MIT.txt`。

`meowfilm_js.c` 是薄 C ABI：create 执行应用固定程序并持有 `globalThis.meowfilmProcess`；invoke 接收 JSON 数据并返回独立分配的 UTF-8 JSON；free／destroy 回收返回缓冲区和引擎。原生句柄及全部调用由同一后台线程持有，平台适配为 Windows `NativeJavaScriptJsonEngine` 与 Swift `NativeJavaScriptJSONEngine`。业务规则不在 C、C# 或 Swift 中重写。

Windows 正常项目构建调用 `scripts/windows/build-native-javascript.ps1`，缓存位于本目录默认 `build/`，DLL 输出到 `build/bin/<Configuration>/`。引擎内存 64 MiB、栈 512 KiB、每次调用截止 2 秒；这是进程内执行资源边界，不是规则条数／文本业务限制。引擎中断回调观察取消、关闭和截止时间，异常后销毁当前上下文，下次调用重新初始化同一固定程序，不切换后端。Dispose 等待已接纳工作全部结算及真实原生释放。

iOS 经本目录 SwiftPM 包静态链接同一组 C 源码，以专用 Foundation Thread 调用 ABI，资源边界与 Windows 一致。`close()` 等待队列结算和真实 C 上下文销毁；超时、取消、执行异常后重建同一固定程序。`FilmRuleProgram` 资源包直接携带 `packages/film-rules/film-rules.js`，不复制规则。构建／定向 Swift 测试前运行 `scripts/verify-native-javascript.mjs` 校验锁源与许可。Node 脚本运行器继续使用原有独立引擎与契约。
