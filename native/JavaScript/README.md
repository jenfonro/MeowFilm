# 共享 QuickJS 原生核心

QuickJS-NG 0.18.0 的 C 引擎由 `scripts/script-host-native.json` 锁定上游提交、归档哈希、当前逐文件哈希及中央许可。`quickjs/` 是宿主 addon 和 iOS framework 唯一编译的 QuickJS 源码；不包含命令行、libc、模块加载或网络／文件绑定。引擎许可见 `LICENSES/quickjs-ng-LICENSE.txt`；实际编入原生引擎的 `libunicode-table.h` 另含 Unicode License V3／1991–2026 Unicode 版权声明，原文（只去 C 注释定界符）单独保存为 `LICENSES/Unicode-3.0.txt`。来源校验同时核对表、声明哈希和两处正文一致；不能由 Node 的 ICU 声明替代。

本地派生补丁 `patches/regexp-compile-interrupt.patch` 为正则编译、执行及全局空替换添加中断轮询；后行断言的字节码搬移、分支与字符集合并逐步检查，线性解析、捕获预扫描（含字符类内部）及寄存器分析按批次检查。预扫描失败显式沿捕获计数、命名组查询与索引生成调用者返回，不缓存不完整的计数；编译中断通过 `LRE_RET_TIMEOUT` 传播为不可捕获的引擎中断，不冒充正则语法错误。

执行器统一在 opcode 调度、正／反方向引用比较，以及捕获记录查找、失败回溯、正／负前瞻的动态栈展开与复制内循环中累计工作；每10000步调用一次现有中断回调，不依赖失败匹配或后向跳转才计数。已覆盖的跳转分支不再重复计数；字符集二分查找最多16步，捕获候选/重置至多255项，仍由 opcode 检查覆盖，不在每次固定小循环内重复调用宿主。全局删除的所有者循环仍在每次匹配前实际查询中断，不能依赖每次 `lre_exec` 重置的计数；单次匹配成本不固定，因此不把多次匹配凑成另一层轮询批次。中断仍经 `LRE_RET_TIMEOUT`、不可捕获异常和原有释放出口返回，不改变规则语法、长度或匹配/替换语义。锁文件同时记录同一完整派生补丁哈希与被修改文件的上游哈希；构建检查当前源码及补丁的精确字节。

另共享 `packages/script-host/native/task-context.patch` 的任务身份、Promise／await／generator／finalizer 传播和真实 ArrayBuffer 统计，以及 `arraybuffer-ownership.patch` 的转移失败所有权修复。0.18 的外部 ArrayBuffer realloc 回调负责非零扩缩、失败保留旧内存和零长度释放；旧 UTF16 CString 和 BigInt 补丁已由上游实现覆盖，其回归保留。

复建使用 `node scripts/quickjs/build-native.mjs` 或 iOS 对应的 `build-ios.mjs`。首次准备时在默认缓存中临时解包锁定归档，严格按锁中顺序应用三个补丁，再核对共享源码全部文件哈希及 MIT／Unicode 许可。普通增量构建直接编译同一份已校验源码，不另保留第二份用于编译的 QuickJS。

公开 `@meowfilm/script-host/native-json` 的 `NativeJsonProgram` 接收源码，沿 `globalThis.meowfilmProcess` 执行纯程序。`invoke(value,{signal})` 只以 JSON 字符串跨 N-API，完整保留 JSON 的深层结构、数值及孤立 UTF16，不继承通用对象桥的深度或 Unicode 数据约束。输入在接纳时序列化；输出在 QuickJS 内序列化后由 Node 解析。程序不提供 Node、网络、文件、宿主 capability 或定时器。

每个 program 持有独立线程／Runtime／Context，复用同平台脚本宿主原生载体；多个搜索会话可共享一个 program，调用串行执行，排队取消仅撤销该项。引擎内存 64 MiB、栈 512 KiB、每次执行截止 2 秒，仅作执行资源边界，不限制规则条数、文本或结果长度。取消、超时或执行异常等待真实线程 join 和 Runtime 释放后，未取消队列用同一源码重建同一内核继续执行。`close()` 撤销队列、打断当前计算并等待真实释放；关闭后不再接纳请求。
