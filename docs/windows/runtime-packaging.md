# Windows App 脚本载荷打包

Windows 包装使用当前 `common-native` 通用运行器；仅 `--product aggregate` 是有效 wire 身份。业务通信为 `runtime.http.v1`，平台 manifest 的 transport 为 `windows-http-v1`。启动及生命周期见[平台接入](app-runtime.md)，公共引擎及契约见[共用核心](../runtime/script-runtime.md)。

## 构建与包装

1. `scripts/windows/bootstrap.ps1` 按根锁准备依赖。`ensure-runtime-native.mjs` 检查本机 ABI/架构、源码、SDK、根锁、构建回执及产物；已有结果有效则复用，否则调用 `scripts/quickjs/build-native.mjs` 正常增量构建。
2. `scripts/package-runtime.mjs` 使用 `common-native`，离线复制公共 entry/bootstrap/client/contracts/modules 和锁定运行依赖、受信原生 addon、source SDK 及来源信息。
3. `scripts/windows/package-runtime.mjs` 增加 Windows bootstrap 与薄 entry，重定位入口导入，并重新核对文件库存、输入及哈希。共享业务保持原字节，不创建 Windows modules 副本。
4. 根 `build-windows.ps1` 通过 `scripts/windows/build.ps1` 编译原生 App、包装运行器、加入锁定 Node 和许可，在构建锁保护下替换唯一 `build/windows/`。失败不替换上一份有效输出。

构建机需要锁定的 Node 24.21.0、npm、Git、.NET SDK 10+、Python、Perl、NASM、CMake 3.20+ 和 MSVC x64 工具链。原生宿主构建应在 Visual Studio x64 Developer PowerShell / Command Prompt 中运行，使 `cl`、`nmake` 与 SDK 路径有效；不是只安装 .NET 即可。源码依赖首次按 `scripts/script-host-native.json` 下载并校验，正常缓存使用 `tmp/build/runtime-native` 与 node-gyp 默认头文件缓存；不按测试轮次复制缓存。

目标机只需完整 `build/windows/`，launcher 使用包内 `runtime/node.exe`，不查询 PATH 或借系统 Node。默认输出自带 .NET；`-FrameworkDependent` 版本另需匹配的 .NET Desktop Runtime。不能只复制 exe，也不需要目标机安装编译工具。

## 输入闭包与信任边界

Windows 平台模块为 `music@0.8.0`、`cat@0.2.0`、`source@0.2.0`、`tmdb@0.1.0`，契约分别为 `music.script.v3`、`cat.script.v2`、`source.runtime.v2`、`tmdb.data.v1`；平台 manifest 与实际 entry、原生 describe 严格一致。通用默认 `createRuntime` 仍为原三模块。完整 HTTP 源码/JSON 不使用平台业务分片。

载荷包含公共 `app/` 入口及模块、`app/windows/` 两个包装文件，以及 `@meowfilm/{runtime-bridge,script-host,source-runtime}` 和根锁解析的运行依赖。`script-host/build/Release/` 的原生 addon、SDK、构建回执只按公共策略精确路径及哈希放行，不加载 WASM/asm.js 或任意附加 native binary。

`scripts/script-host-native.json` 固定 QuickJS-NG、OpenSSL、zlib、Node headers、补丁、构建工具和输入；回执记录真实平台/架构、源码树、文件哈希和构建命令。Node-API ABI、来源、许可、SDK 和所有输入必须匹配；打包不重建、不安装、不修锁。中央许可及包内实际许可均按库存复制，见[许可说明](../licenses.md)。

- root/output 为显式本机绝对路径，拒绝 UNC、源树覆盖、链接或重解析祖先。
- workspace 安装链接必须准确指向本仓库；输出成为普通目录，不借用全局或其他工作区依赖。
- 根锁身份、来源、SRI、版本、peer/optional 关系、导出及实际文件逐项核对。不得绕过缺失的最近依赖去找祖先。
- 拒绝路径穿越、大小写/Unicode 冲突、非受信二进制、额外平台文件；测试/缓存/隐私文件不入包，许可不因 files 裁剪丢失。
- 新输出独占创建，完成前核对完整 byte/hash inventory 与输入未变；失败保留 failed 清单。manifest 不自哈希。

客户端影视规则仍独立使用 `native/JavaScript` 和 `scripts/quickjs-native.json`，正常 .NET 增量构建生成 `meowfilm_js.dll`；它不是 Node addon，也不混入运行器闭包。固定规则程序嵌入 Capabilities，规则数据由客户端持有。

## 验证入口

`scripts/windows/test.ps1 -Group <names>` 准备同一真实原生 bundle 后编译并执行选定原生测试；`test-node.ps1 -Path tests/<area>.test.mjs` 执行选定 Node 测试。两者保留真实退出码，空集、跳过和未完成不算通过。测试副本/输出由本工作区固定 tmp 目录管理。

显式 `-All` 才运行完整对应测试范围；默认按实际变更选择。Windows 根测试选择不加载 iOS 专属测试。真实运行器集成覆盖 HTTP、LX/source 原生执行、Cat Worker、取消、持久数据及资源关闭；合成场景不冒充真实第三方来源或媒体播放验收。外部来源可按[动态验收工具](../runtime/source-acceptance.md)的 `--payload` 指向完整载荷，但不属于每次平台迁移的默认验证。
