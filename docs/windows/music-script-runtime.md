# Windows 音源脚本接入

## 平台与公共实现

唯一 Node 入口为 `apps/windows/runtime/entries/aggregate.mjs`：

```text
Windows 原生脚本 owner
  → 按需启动 Node、建立命名管道
  → Windows bootstrap / 薄 entry
  → apps/runtime/bootstrap.mjs
  → apps/runtime/modules/music-script.mjs
  → 共用 script-host / LX / QuickJS
```

Windows 源码目录只保留启动／管道 bootstrap 与薄 entry，没有 `modules/music-script.mjs` 副本。公共执行核心、`context.storage` 权限与网络边界见 [共用运行时](../runtime/script-runtime.md)，RPC 和实例格式见 [music.script.v2](../runtime/music-script-runtime.md)。

## 原生归属与传输

原生 `MusicScriptOwner` 拥有已选文件快照、安装元数据、启用状态、版本、取消、持久事务及存储会话绑定。导入／列表不启动 Node；明确启用或实际调用选中脚本时才装载相应实例。官方平台账号、目录、曲库、队列、缓存、播放和设置不以脚本进程为前置条件。

源元数据只接受 schema 3，权限仅含 `storage`。读取不会创建或写回文档，不提供旧格式迁移或旧实现回退；不符合当前格式的记录明确失败，并保持持久文档原字节不变。源记录与曲库、队列共属同一音乐文档，校验失败时不部分加载或自动重建。正常修改仍使用原生 owner 的同一 CAS 和未知提交恢复机制。

命名管道传输有界源码分片和控制 JSON，不传输媒体字节。反向存储 RPC 只转发明确 installation/site/generation 的会话请求，不授予通用文档、文件、进程或官方账号权限。全局 `meow.storage`、startup 和 background 不能借助原生适配层获得隐式权限。

握手身份为 `aggregate`、protocol 1，descriptor 同时包含 `music@0.5.0` 和 `source@0.1.0`；本模块业务响应为 `music.script.v2`，旧 descriptor 不作为当前契约接纳。启动参数与管道生命周期见 [Windows 装配](app-runtime.md)，连接失败与显式恢复见 [恢复](runtime-recovery.md)。

Windows 进程／管道身份检查不等于脚本网络隔离；公共 `validateURL` 不阻止访问已开放的本机或私网 HTTP(S) 服务。其他官方业务的授权和网络策略保持独立。

公共运行器保留脚本声明的扩展 site/action；Windows 当前的曲库、搜索和播放消费者仍按各自业务契约工作，不能将公共 `search`／`musicSearch` 调用成功写成原生搜索界面已接入这些扩展。`music.online.describe` 的 `trustedScriptsOnly` 为 false，表示没有作者／域名信任名单，不代表网络访问或上游内容可信。
