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

源元数据只接受 schema 4，权限仅含 `storage`。metadata 保存导入时的 `sourceHash/sourceBytes`、版本和权限；`music.script-sources.v2` collection 将每份源码保存为完整普通 `.js` 文件，不加密、不做落盘分片。激活直接读取当前文件，不核对导入时 hash 或长度；手工编辑后重新启用或重启运行当前内容，实际文件仍受单脚本 16 MiB 上限约束。文件隔离、源码原子写入与正常 metadata 修改的 CAS／未知提交恢复保持不变。开发阶段不迁移旧加密源码或内联格式，不提供回退，也不自动清理旧文件。

普通原生曲库、偏好、缓存及脚本 KV 明文保存；应用自身账号 Cookie、设备会话继续使用 DPAPI，脚本自行保存的账号数据不由运行器特殊加密。

命名管道传输有界源码分片和控制 JSON 分片，不传输媒体字节。原生内部 `MusicScriptTransport` 负责大参数上传、大结果读取与完整性核对，`MusicScriptOwner.ExecuteAsync` 及播放调用方仍接收普通业务 JSON，不承担分片协议。源码暂存与控制参数暂存分离，传输规则见 [公共 LX 契约](../runtime/music-script-runtime.md#控制数据传输)；原生入口和返回值不再施加 512 KiB 控制信封上限。

反向存储 RPC 只转发明确 installation/site/generation 的会话请求，不授予通用文档、文件、进程或官方账号权限。全局 `meow.storage`、startup 和 background 不能借助原生适配层获得隐式权限。持久存储的每 key 16 KiB、每 site 64 KiB、每安装 512 KiB 是原生数据契约，仍然生效，不是控制消息限额，见 [原生曲库](music-native-library.md)。

握手身份为 `aggregate`、protocol 1、`music@0.7.0`；业务响应为 `music.script.v2`，旧 descriptor 不作为当前契约接纳。启动参数与管道生命周期见 [Windows 装配](app-runtime.md)，连接失败与显式恢复见 [恢复](runtime-recovery.md)。

Windows 进程／管道身份检查不等于脚本网络隔离；公共 `validateURL` 不阻止访问已开放的本机或私网 HTTP(S) 服务。其他官方业务的授权和网络策略保持独立。

公共运行器保留脚本声明的扩展 site/action；Windows 当前的曲库、搜索和播放消费者仍按各自业务契约工作，不能将公共 `search`／`musicSearch` 调用成功写成原生搜索界面已接入这些扩展。`music.online.describe` 的 `trustedScriptsOnly` 为 false，表示没有作者／域名信任名单，不代表网络访问或上游内容可信。
