# Windows App 脚本载荷打包

Windows meowfilm App 的脚本载荷由以下两个阶段生成。打包参数 --product aggregate 是保留的 wire 身份；其他值明确拒绝。普通原生业务不在 Node 中运行。

## 两个构建阶段

- scripts/package-runtime.mjs：离线解析已安装且锁定的必要依赖，复制 `apps/runtime/bootstrap.mjs`、`apps/runtime/modules/{music,cat}-script.mjs`、runtime-bridge、script-host、有限 Node 兼容依赖、引擎依赖与必要声明；当前组装 profile 为 windows-native，返回 platform-pending，不能把它当作可执行载荷。
- scripts/windows/package-runtime.mjs：只补齐 Windows 命名管道 bootstrap 与 `entries/aggregate.mjs`，将公共工厂导入重定位到同一共享模块，核对文件清单与所有哈希后才返回 complete；不复制 Windows 业务 module。

完整 App 构建从根 build-windows.ps1 进入；它调用 scripts/windows/build.ps1，由同一个构建锁保护依赖准备、打包和 `build/windows` 覆盖安装。每次构建在 tmp/publish 重新生成暂存目录，并加入 `scripts/windows/node-runtime.json` 锁定的 Node 运行时与其许可；第三方许可见 `../licenses.md`。

目标电脑使用完整 `build/windows/`，原生 launcher 只从应用的 `runtime/node.exe` 启动，不查询 PATH 或借用系统 Node。默认发布同时包含 .NET Desktop Runtime；构建主机的 Node/npm/.NET SDK 不是运行依赖。此发布目录需要整体复制，不能仅分发 exe；`-FrameworkDependent` 才要求目标机另装匹配的 .NET Desktop Runtime。

## 输入闭包

运行 descriptor 为 `music@0.6.0` 与 `cat@0.1.0`，响应契约为 `music.script.v2` 与 `cat.script.v1`；载荷清单 modules 固定为 `music, cat`，product 保持 aggregate。

```text
app/bootstrap.mjs
app/modules/cat-script.mjs
app/modules/music-script.mjs
app/windows/bootstrap.mjs
app/windows/entries/aggregate.mjs
node_modules/@meowfilm/{runtime-bridge,script-host}
node_modules/quickjs-emscripten-core
node_modules/@jitl/{quickjs-wasmfile-release-sync,quickjs-ffi-types}
node_modules/{buffer,events,path-browserify,readable-stream,supports-color,util,...}
```

源码根由脚本自身位置解析。API root/output 都是显式本机绝对路径；output 是构建本次新建的暂存子目录，不接受 UNC、源树覆盖或有链接的祖先。

workspace 必须在根 package/lock 中声明并安装为准确指向本仓库的 link，输出成为普通目录。只有选定两包及其实际运行/peer依赖可以入包；不扫描未入闭包的开发依赖，不借用全局、NODE_PATH 或其他工作区。

公共目录只允许当前 bootstrap 和共享 music/Cat 模块；Windows 目录只允许 bootstrap、薄 entry 和 entries 目录，额外文件／业务 modules 目录明确拒绝，不静默丢弃源码。共享模块保持原字节，只有平台入口导入需要重定位。

## 受信 QuickJS WASM

`scripts/quickjs-runtime.json` 固定 `0.32.0` 的 core、release-sync 和传递依赖 ffi-types，记录实际 npm tarball URL／SRI、package.json SHA-256、许可原文 SHA-256 和唯一必要 WASM：

```text
@jitl/quickjs-wasmfile-release-sync/dist/emscripten-module.wasm
503134 bytes
SHA-256 105c3bed22d457e43e3d1c3c1c6959fda62a8fe06f0fc8a985303c3a2be72232
```

引擎依赖声明须是精确版本；安装 manifest、根锁身份／来源／SRI、MIT 声明、包 LICENSE 与中央 `LICENSES/quickjs-emscripten-LICENSE.txt` 均需匹配受审查记录。只在受信 package 的精确相对路径放行上述 WASM，并流式复核长度和真实文件哈希；同包额外 `.wasm`、其他包／workspace 中的同名 WASM、native binary、链接或错误版本仍拒绝。不安装 debug／asyncify 引擎变体，不全局放行二进制。

可信 `quickjs-engine.js` 使用 `node:fs/promises`，通过 `import.meta.resolve('@jitl/quickjs-wasmfile-release-sync/wasm')` 读取该锁定文件；包 exports／公开文件目标与实际 payload 必须对应。文件读取属于可信引擎，不向 guest 开放。

输出 dependencies 记录引擎 manifest／license 哈希，`trustedWasm` 记录唯一允许文件及其许可；inputs 包含 engine policy、中央许可、根 manifest／lock 以及每个复制源码／依赖文件的哈希，并在完成时重新核对输入未改变。runtime 文件和每个引擎包原始 LICENSE 都进入逐字节 inventory，中央许可同时进入 App 的 licenses 清单。

## 保留的安全约束

- 每个依赖位置对应根锁记录；名称、精确安装版本、声明、peer metadata、semver、registry HTTPS 来源、SRI 语法分别核对，不能悄悄修锁或绕过缺失的最近依赖去找祖先。
- 支持 scoped、nested、hoisted、peer、optional 与合法依赖循环；未安装的可选边明确记录。安装了的可选依赖不能绕过锁/版本/原生二进制检查。
- 工作区 files 规则、公开 exports/main、真实文件存在性必须一致。Registry 已安装目录不重做 npm-pack 的负向规则。测试、夹具、缓存及隐私文件不打包，许可/NOTICE/来源声明不因 files 裁剪而丢失。
- 拒绝路径穿越、大小写/Unicode 冲突、额外链接/重解析、非受信 native/WASM 和非法公开文件目标。不会执行待打包代码、安装器或包脚本，也不会下载依赖或修锁。
- 新文件独占创建、输入读取与输出身份复核、完整 byte/hash inventory；失败时保留 failed 清单，暂存目录由下次构建重新生成。runtime-manifest.json 不自哈希。

除上述三个 QuickJS 包外，Cat 有限 Node 兼容层还使用锁定的纯 JavaScript 依赖闭包；直接依赖为 `@opentelemetry/api`、`buffer`、`events`、`path-browserify`、`readable-stream`、`supports-color` 和 `util`。其传递依赖按根 lock 实际解析，包内许可原文保留，中央归属见 `LICENSES/NOTICE.md` 和 `npm-node-compat-*.txt`。受审查引擎记录来自实际 registry tarball／安装文件核对；普通 JS 依赖仍执行来源/SRI/manifest/闭包检查，但不冒充独立上游签名。打包本身离线，不重新拉取 tarball。

`scripts/windows/prepare-test-bundles.mjs` 在仓库 tmp 内的显式绝对测试目录调用同一闭包；已存在的 aggregate bundle 不覆盖。回归分别验证真实 release-sync 引擎离线初始化、Windows 入口／管道、v2 请求存储与受控 HTTP 服务访问，不冒充外部脚本验收。外部样本可通过 [验收工具的 `--payload`](../runtime/source-acceptance.md) 指向完整发布 runtime，并由其中的 node.exe 执行，以验证不借用工作区依赖。
