# 客户端原生 JavaScript

QuickJS-NG 0.18.0 的 C 引擎由 `scripts/quickjs-native.json` 锁定上游提交、归档哈希、当前逐文件哈希及中央许可。`quickjs/` 仅包含构建引擎所需文件；不包含命令行、libc、模块加载或网络／文件绑定。引擎许可见 `LICENSES/quickjs-native-MIT.txt`；实际编入 DLL 的 `libunicode-table.h` 另含 Unicode License V3／1991–2026 Unicode 版权声明，原文（只去 C 注释定界符）单独保存为 `LICENSES/Unicode-3.0.txt`。来源校验同时核对表、声明哈希和两处正文一致；Windows 明确许可清单分发这份声明及其 manifest 条目，不能由 Node 的 ICU 声明替代。

本地派生补丁 `patches/regexp-compile-interrupt.patch` 为正则编译、执行及全局空替换添加中断轮询；后行断言的字节码搬移、分支与字符集合并逐步检查，线性解析、捕获预扫描（含字符类内部）及寄存器分析按批次检查。预扫描失败显式沿捕获计数、命名组查询与索引生成调用者返回，不缓存不完整的计数；编译中断通过 `LRE_RET_TIMEOUT` 传播为不可捕获的引擎中断，不冒充正则语法错误。

执行器统一在 opcode 调度、正／反方向引用比较，以及捕获记录查找、失败回溯、正／负前瞻的动态栈展开与复制内循环中累计工作；每10000步调用一次现有中断回调，不依赖失败匹配或后向跳转才计数。已覆盖的跳转分支不再重复计数；字符集二分查找最多16步，捕获候选/重置至多255项，仍由 opcode 检查覆盖，不在每次固定小循环内重复调用宿主。全局删除的所有者循环仍在每次匹配前实际查询中断，不能依赖每次 `lre_exec` 重置的计数；单次匹配成本不固定，因此不把多次匹配凑成另一层轮询批次。中断仍经 `LRE_RET_TIMEOUT`、不可捕获异常和原有释放出口返回，不改变规则语法、长度或匹配/替换语义。锁文件同时记录同一完整派生补丁哈希与被修改文件的上游哈希；构建检查当前源码及补丁的精确字节。

复建方式：复用已下载的锁定 `sourceUrl` 归档（不存在时才获取），核对 `archiveSha256` 后在 Git 工作树以外的临时目录解包（去掉顶层目录），核对 `patch.upstreamFiles`，在该目录执行 `patch --batch --forward --fuzz=0 -p1 --input=<仓库绝对路径>/native/JavaScript/patches/regexp-compile-interrupt.patch`。确认三个文件均已应用，不能将跳过补丁视作成功；生成文件必须与锁中的 `files` 哈希一致，MIT 许可及 Unicode 表头声明须与中央许可一致。普通构建直接使用仓库已锁定的派生源码，无需下载或现场应用补丁。

`meowfilm_js.c` 是薄 C ABI：create 执行应用固定程序并持有 `globalThis.meowfilmProcess`；invoke 接收 JSON 数据并返回独立分配的 UTF-8 JSON；free／destroy 回收返回缓冲区和引擎。原生句柄及全部调用由同一后台线程持有，托管平台适配为 `NativeJavaScriptJsonEngine`。业务规则不在 C 或 C# 中重写。

Windows 正常项目构建调用 `scripts/windows/build-native-javascript.ps1`，缓存位于本目录默认 `build/`，DLL 输出到 `build/bin/<Configuration>/`。引擎内存 64 MiB、栈 512 KiB、每次调用截止 2 秒；这是进程内执行资源边界，不是规则条数／文本业务限制。引擎中断回调观察取消、关闭和截止时间，异常后销毁当前上下文，下次调用重新初始化同一固定程序，不切换后端。Dispose 等待已接纳工作全部结算及真实原生释放。

本目录可供未来原生平台复用，但本轮仅装配和验证 Windows；不声称其他平台已支持。Node 脚本运行器继续使用原有独立引擎与契约。
