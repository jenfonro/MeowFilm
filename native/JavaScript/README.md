# 客户端原生 JavaScript

QuickJS-NG 0.18.0 的原始 C 引擎由 `scripts/quickjs-native.json` 锁定提交、归档哈希、逐文件哈希及中央许可。`quickjs/` 仅包含构建引擎所需原始文件；不包含命令行、libc、模块加载或网络／文件绑定。许可见 `LICENSES/quickjs-native-MIT.txt`。

`meowfilm_js.c` 是薄 C ABI：create 执行应用固定程序并持有 `globalThis.meowfilmProcess`；invoke 接收 JSON 数据并返回独立分配的 UTF-8 JSON；free／destroy 回收返回缓冲区和引擎。原生句柄及全部调用由同一后台线程持有，托管平台适配为 `NativeJavaScriptJsonEngine`。业务规则不在 C 或 C# 中重写。

Windows 正常项目构建调用 `scripts/windows/build-native-javascript.ps1`，缓存位于本目录默认 `build/`，DLL 输出到 `build/bin/<Configuration>/`。引擎内存 64 MiB、栈 512 KiB、每次调用截止 2 秒；这是进程内执行资源边界，不是规则条数／文本业务限制。引擎中断回调观察取消、关闭和截止时间，异常后销毁当前上下文，下次调用重新初始化同一固定程序，不切换后端。Dispose 等待已接纳工作全部结算及真实原生释放。

本目录可供未来原生平台复用，但本轮仅装配和验证 Windows；不声称其他平台已支持。Node 脚本运行器继续使用原有独立引擎与契约。
