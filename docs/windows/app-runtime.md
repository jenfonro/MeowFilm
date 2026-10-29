# App 脚本传输装配

本目录只保留 `bootstrap.mjs`。`createAppRuntime` 被动装配有限方法表，连接由 Windows 命名管道入口注入；这里不创建监听器、不访问文件或账号、不选择业务源。

当前唯一运行入口是 `apps/windows/runtime/entries/aggregate.mjs`。`runtime.describe(null)` 返回 protocol 1、wire 身份 `aggregate` 及 `music@0.4.0` 方法清单。原生客户端核对身份、版本和方法后才接纳脚本请求。

普通账号、目录、曲库、歌词、缓存和播放均由 Windows 原生代码拥有；当前 App 不装配下载管理。

装配和关闭保持一次性资源所有权；连接关闭后不再接纳调用，各模块关闭使用 all-settled 收齐结果，清理失败返回固定错误，不记录源、凭据或启动参数。不自动重启或重放失败操作。

Windows 参数和进程边界见 `apps/windows/runtime/bootstrap.mjs` 与 `runtime-recovery.md`。
