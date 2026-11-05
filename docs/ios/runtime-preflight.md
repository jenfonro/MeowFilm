# 内嵌 Node 运行底座预检

本工具验证“iOS 内嵌 Node 承载公共运行器”，不重写 LX／Cat 协议或管理逻辑。它是独立诊断 App，与 Meowfilm 共用受审查载荷及框架构建输入，不能代替真实 App 宿主测试或正式发布打包。产品接入见[音源管理](music.md#音源管理)。

## 运行

需要 macOS、Xcode 和已经启动的 iOS 模拟器。先用构建机 Node 24 和根锁生成受审查的 iOS JS 载荷；构建工具版本与嵌入 App 的 Node 版本是两回事：

```sh
npm ci --ignore-scripts
mkdir -p tmp/ios-node-preflight
python3 -I -B scripts/prepare-ios-runtime.py
python3 -I -B scripts/ios-node-preflight.py --simulator <UDID>
```

准备工具复用 Node Mobile archive／解包缓存，并生成替换 `.cache/ios-runtime/Runtime`。构建机 Node 仅为打包工具，不是 iOS App 运行依赖。

`ios-embedded` 装配复用公共打包器，按根锁和 `scripts/quickjs-runtime.json` 审核 manifest、许可与引擎文件。它包含公共 music／Cat 模块及 asmjs，不携带 WASM 引擎、Windows 启动入口或开发测试夹具。`package-ios-runtime.mjs` 补齐 iOS entry 与 bootstrap 后生成 `status: complete`、`platform: ios`、`transport: ios-socketpair-v1` 的载荷；预检只接受该完整清单。

工具下载并校验上游 `nodejs-mobile/nodejs-mobile` 的 `v18.20.4` iOS release archive，复用 `.cache/nodejs-mobile/`，不从源码维护 Node 分叉。它分别编译链接模拟器与 iOS arm64 宿主，嵌入对应的 `NodeMobile.framework` 和已核对文件清单／哈希的 JS 载荷，再将诊断 App 安装到指定模拟器。使用正式后端代码，不在 App 内临时替换引擎。产物、日志和结果使用 `tmp/ios-node-preflight/`；仅清理诊断 App 自己的旧结果，不操作 Meowfilm 数据。

设备目标是无开发者签名的编译／链接检查，不代表真机已安装运行。诊断 App 的 ad-hoc 签名不是发行签名，也不能代替 Meowfilm 的生产组件装配。

## 验证内容与成功判据

- 实际 Node 版本、架构、平台、WebAssembly 不可用及 iOS asmjs 载荷身份。
- 无 ICU 构建下的严格 UTF-8 检查，不用替换字符接收损坏的消息或源码。
- 文件写入、文件／目录 fsync、rename、读回及清理。
- SHA-256、AES-GCM 和 WebCrypto。
- Node HTTP 回环请求、响应及 listener 关闭。
- Worker 消息，以及运行死循环后实际终止线程。
- 原生 `socketpair` 与公共 `createDescriptorPeer`／`createAppRuntime` 双向调用、描述查询及关闭。
- 公共 music／Cat 模块加载、真实 QuickJS 初始化、LX 与 Cat 安装／调用／停用／关闭。
- 独立 guest 堆、BigInt、Promise job、20 MiB 扩容后的 JSON／字节帧桥，以及真实 LX 死循环停用和重新启用。

每项结果记录在 `result.json`，应用日志在 `simulator.log`。只有全部用例、原生对端检查和 `node_start` 返回状态都通过，工具才返回零；构建成功但 guest 引擎不可用仍返回非零，不回退到宿主 `eval` 或 Node `vm` 执行用户脚本。

Node 18 已结束上游维护。采用它不等于获得上游安全维护。`MeowfilmRuntimeTests` 在真实 App 进程中覆盖描述查询和 LX 导入／启用／取链／私有存储／歌词／停用／删除；独立预检的通过不能代替这些证据。前后台长时间恢复、真机运行与外部真实源业务验收需要分别验证。

官方 Node Mobile 18.20.4 的 iOS 产物没有 ICU，也没有可用的 `WebAssembly`。公共通信与源码解码不依赖 ICU；iOS 固定选择同源 `@jitl/quickjs-asmjs-mjs-release-sync@0.32.0`，不是 WASM 失败后的自动回退。它仍在独立 QuickJS 数字堆中执行用户脚本，保留受控桥、既有权限和 Worker 终止，但没有 WASM 的完整越界及间接调用 trap。此差异已作为 iOS 接入方案接受；iOS 系统沙箱不替代本 App 内的数据、网络和资源防护。桌面继续使用原 WASM 后端。
