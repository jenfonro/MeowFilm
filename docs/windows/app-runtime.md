# Windows 脚本启动与管道

`apps/windows/runtime/` 只保留 `bootstrap.mjs` 和 `entries/aggregate.mjs`。bootstrap 校验启动参数并连接原生已经创建的本机命名管道；薄 entry 从 `apps/runtime/modules/{music,cat}-script.mjs` 导入工厂，不承载脚本执行、协议或业务副本。

启动参数必须恰好为按序 `--meow-pipe meowfilm-<UUID> --meow-session <UUID>`；UUID 使用规范小写格式，不接受路径、远程管道或 TCP 地址。连接有界超时，失败销毁 socket，不执行业务工厂。session 是运行代次，不是身份凭据或认证秘密。

原生 launcher 拥有单实例 CurrentUserOnly 管道服务器，并在 RPC 前核对 child PID。连接成功后才调用公共 `createAppRuntime` 被动装配方法表；装配本身不读取用户文件或账号、不选择或执行业务源。原生 `AppDataPaths.ScriptRuntime` 计算稳定数据目录，`NodeRuntime` 清空继承环境后通过 `MEOWFILM_SCRIPT_DATA_ROOT` 注入该绝对路径；Node 不自行推导或读取 `%LOCALAPPDATA%`。Cat 在 start、status、refresh、install、enable、invoke 或 management 请求首次需要运行状态时读取该目录并按需创建回环 listener。

`runtime.describe(null)` 返回 protocol 1、wire 身份 `aggregate` 以及 `music@0.7.0`、`cat@0.1.0` 方法清单；消费者分别核对 `music.script.v2` 与 `cat.script.v1`。官方账号、目录、曲库、歌词、缓存和播放由原生 owner 承担，无脚本操作时不启动 Node。

连接 EOF／error／close 与重复关闭汇聚到公共模块 disposal；装配失败释放已连接的 pipe，业务实例由公共 owner 回收。清理失败使用固定错误；CLI 启动失败只输出 `WINDOWS_RUNTIME_START_FAILED`，不记录 argv、来源、凭据或底层异常。不自动重启或重放失败操作。

公共职责与隔离限制见 [共用核心](../runtime/script-runtime.md) 和 [Runtime bridge](../runtime/runtime-bridge.md)；进程恢复见 [显式恢复](runtime-recovery.md)，Windows 最终载荷见 [打包](runtime-packaging.md)。管道身份检查不是文件／进程／网络隔离的替代品。
