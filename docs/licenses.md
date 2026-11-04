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
| `node-LICENSE.txt` | 随应用发布的 Node.js |
| `qr-code-generator-LICENSE.txt` | QR Code generator |
| `quickjs-emscripten-LICENSE.txt` | QuickJS 与 quickjs-emscripten 的原始 MIT／版权声明，随受信 release-sync 引擎发布 |
| `catpawrunner-MIT.txt` | Cat 兼容实现所参考 catpawrunner 的 MIT 正文 |
| `npm-node-compat-MIT.txt` | 有限 Node 兼容依赖闭包中的 MIT 原始版权与许可文本 |
| `npm-node-compat-BSD-3-Clause.txt`、`npm-node-compat-ISC.txt` | 对应传递依赖的原始许可文本 |

每种许可正文只保留一份。代码与上游的对应关系记录在 `NOTICE.md`，不在代码目录中另放许可文件。

## 打包

`scripts/windows/build.ps1` 调用 `scripts/windows/package-licenses.mjs`，按固定清单把上述文件逐字节复制到 `build/windows/licenses`，并写入 `licenses-manifest.json`（源路径、输出路径、字节数、SHA-256）；`build-result.json` 记录该清单的哈希。Node 许可另随 `runtime/node.exe` 放在 `runtime/node-LICENSE.txt`。

runtime-bridge／script-host、三个 `0.32.0` QuickJS 包和 Cat 有限 Node 兼容依赖的原始声明由脚本载荷打包器保留。引擎的根锁、包 manifest／LICENSE、唯一 release-sync WASM 和中央许可按 `scripts/quickjs-runtime.json` 复核；该中央文件原样来自 release-sync 包，包含 Jake Teton-Landis 与 Fabrice Bellard／Charlie Gordon 的声明。普通 npm 依赖按根 lock 解析并保留各包许可，中央归属及 catpawrunner 改编范围见 `NOTICE.md`。

libVLC、.NET 及其他传递组件的发行义务需单独核对。清单中的 `redistributionPermissionVerified` 始终为 false，文件哈希或许可清单不等于分发授权。

新增或删除许可文件时，同步修改 `package-licenses.mjs` 的清单与 `tests/windows-licenses.test.mjs`。

## 文件安全

只读取固定清单中的本仓库文件，不递归发现。拒绝不安全/相对/UNC/设备路径、链接/重解析/硬链接、空/过大来源、缺失文件及目录冒充文件。每次检查文件身份、读取前后内容和长度；输出是构建暂存中新建的目录，最终随整个构建覆盖安装。

源根与显式 outputRoot 各自有边界，输出不能跳出 outputRoot。输出目录与文件独占创建并重新核对完整 inventory/hash。错误使用固定 code。
