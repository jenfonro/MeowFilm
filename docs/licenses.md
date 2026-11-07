# 第三方许可

全部第三方许可与来源声明集中在仓库根目录 `LICENSES/`，iOS 与 Windows 共用：

| 文件 | 内容 |
| --- | --- |
| `NOTICE.md` | 按上游项目列出来源、固定提交、版权方、许可及改编部分 |
| `Apache-2.0.txt` | iWebPlus / iWebPlayer、LX Music Desktop |
| `GPL-3.0-only.txt` | 本应用、MeowMusic、QQMusicApi |
| `api-enhanced-MIT.txt` | NeteaseCloudMusicApiEnhanced/api-enhanced |
| `KuGouMusicApi-MIT.txt` | MakcRe/KuGouMusicApi |
| `iwebplus-NOTICE.txt`、`meowmusic-NOTICE.txt` | 上游 NOTICE 原文 |
| `node-LICENSE.txt` | Windows 随应用发布的 Node.js |
| `nodejs-mobile-LICENSE.txt` | 仅 iOS 内嵌的 Node Mobile 18.20.4 及其第三方声明 |
| `qr-code-generator-LICENSE.txt` | QR Code generator |
| `quickjs-emscripten-LICENSE.txt` | 旧平台接入保留的 QuickJS／quickjs-emscripten MIT／版权声明，不进入当前 Windows 运行器 |
| `quickjs-ng-LICENSE.txt`、`openssl-LICENSE.txt`、`zlib-LICENSE.txt` | 通用运行器原生引擎及算法依赖，按 `scripts/script-host-native.json` 固定 |
| `quickjs-native-MIT.txt` | 客户端内嵌 QuickJS-NG 的 MIT／版权声明，源码独立锁定于 `scripts/quickjs-native.json` |
| `Unicode-3.0.txt` | 同一原生引擎 Unicode 表的完整 Unicode License V3／版权声明，由原始表头提取并独立锁定 |
| `catpawrunner-MIT.txt` | Cat 兼容实现所参考 catpawrunner 的 MIT 正文 |
| `npm-node-compat-MIT.txt` | 有限 Node 兼容依赖闭包中的 MIT 原始版权与许可文本 |
| `npm-node-compat-BSD-3-Clause.txt`、`npm-node-compat-ISC.txt` | 对应传递依赖的原始许可文本 |

每种许可正文只保留一份。代码与上游的对应关系记录在 `NOTICE.md`，不在代码目录中另放许可文件。

## 打包

`scripts/windows/build.ps1` 调用 `scripts/windows/package-licenses.mjs`，按固定清单把 Windows 使用的文件逐字节复制到 `build/windows/licenses`，并写入 `licenses-manifest.json`（源路径、输出路径、字节数、SHA-256）；`build-result.json` 记录该清单的哈希。Node 许可另随 `runtime/node.exe` 放在 `runtime/node-LICENSE.txt`。仅 iOS 使用的 Node Mobile 许可由 Xcode 资源阶段随 App 携带，不加入 Windows 清单；其哈希由 `scripts/ios-node-mobile.json` 固定并在准备构建输入时校验。

runtime-bridge／script-host 及其运行依赖的原始声明由公共打包器保留。当前 Windows 使用受信原生 QuickJS-NG／OpenSSL／zlib 与 source SDK，来源、补丁、构建输入、ABI／架构、产物和中央许可哈希按 `scripts/script-host-native.json` 复核；不复制旧 QuickJS WASM/asm.js。普通 npm 依赖按根 lock 解析并保留各包许可，中央归属及 catpawrunner 改编范围见 `NOTICE.md`。

公共来源运行时的 npm 依赖 `acorn@8.19.0`、`fontoxpath@3.34.0`、`linkedom@0.18.14`、`iconv-lite@0.6.3` 及其锁定的传递依赖随 runtime 闭包打包；打包器保留每个包的 `package.json` 与原始许可文件。完整包名、版本和许可类型见 `NOTICE.md`，实际发布闭包以 `runtime-manifest.json` 为准。

libVLC、.NET 及其他传递组件的发行义务需单独核对。清单中的 `redistributionPermissionVerified` 始终为 false，文件哈希或许可清单不等于分发授权。

新增或删除 Windows 使用的许可文件时，同步修改 `package-licenses.mjs` 的清单与 `tests/windows-licenses.test.mjs`；仅 iOS 使用的许可同步工程资源与 iOS 构建输入策略。

## 文件安全

只读取固定清单中的本仓库文件，不递归发现。拒绝不安全/相对/UNC/设备路径、链接/重解析/硬链接、空/过大来源、缺失文件及目录冒充文件。每次检查文件身份、读取前后内容和长度；输出是构建暂存中新建的目录，最终随整个构建覆盖安装。

源根与显式 outputRoot 各自有边界，输出不能跳出 outputRoot。输出目录与文件独占创建并重新核对完整 inventory/hash。错误使用固定 code。

客户端原生 QuickJS-NG 的派生源码、补丁与中央许可由 `scripts/quickjs-native.json` 单独锁定。iOS 静态链接该源码并由 Xcode 资源阶段携带 `quickjs-native-MIT.txt` 和 `Unicode-3.0.txt`；`prepare-ios-runtime.py` 与定向 Swift 测试入口核验其哈希及 Unicode 表头声明，不放宽脚本载荷的 native/WASM 策略。
