# Windows App 构建与安装包

Windows 包装使用当前 `common-native` 通用运行器；仅 `--product aggregate` 是有效 wire 身份。业务通信为 `runtime.http.v1`，平台 manifest 的 transport 为 `windows-http-v1`。启动及生命周期见[平台接入](app-runtime.md)，公共引擎及契约见[共用核心](../runtime/script-runtime.md)。

## 构建与包装

1. `scripts/windows/bootstrap.ps1` 按根锁准备依赖。`ensure-runtime-native.mjs` 检查本机 ABI/架构、源码、SDK、根锁、构建回执及产物；已有结果有效则复用，否则调用 `scripts/quickjs/build-native.mjs` 正常增量构建。
2. `scripts/package-runtime.mjs` 使用 `common-native`，离线复制公共 entry/bootstrap/client/contracts/modules 和锁定运行依赖、受信原生 addon、source SDK 及来源信息。
3. `scripts/windows/package-runtime.mjs` 增加 Windows bootstrap 与薄 entry，重定位入口导入，并重新核对文件库存、输入及哈希。共享业务保持原字节，不创建 Windows modules 副本。
4. 根 `build-windows.ps1` 通过 `scripts/windows/build.ps1` 编译原生 App、包装运行器、加入锁定 Node 和许可，在构建锁保护下替换唯一 `build/windows/`。失败不替换上一份有效输出。

构建机需要锁定的 Node 24.21.0、npm、Git、.NET SDK 10+、Python、Perl、NASM、CMake 3.20+ 和 MSVC x64 工具链。原生宿主构建应在 Visual Studio x64 Developer PowerShell / Command Prompt 中运行，使 `cl`、`nmake` 与 SDK 路径有效；不是只安装 .NET 即可。源码依赖首次按 `scripts/script-host-native.json` 下载并校验，正常缓存使用 `tmp/build/runtime-native` 与 node-gyp 头文件缓存；头文件安装和校验共同遵循 node-gyp 的 `devdir` 配置，可通过正式 `npm_package_config_node_gyp_devdir` 环境变量指定共享缓存，未配置时沿用其默认目录。不按测试轮次复制缓存。

目标机只需完整 `build/windows/`，launcher 使用包内 `runtime/node.exe`，不查询 PATH 或借系统 Node。默认输出自带 .NET；`-FrameworkDependent` 版本另需匹配的 .NET Desktop Runtime。不能只复制 exe，也不需要目标机安装编译工具。

## Windows 安装与卸载

```powershell
.\build-windows.ps1 -Installer
```

该入口生成同一份完整应用及 `build/windows-installer/meowfilm-setup.exe`，安装包只支持 Release、自包含、win-x64；不接受 `-Configuration Debug` 或 `-FrameworkDependent`。构建机另外需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)：可将 `ISCC.exe` 放在 PATH，或安装到 Program Files／当前用户 `LocalAppData\Programs` 下的标准 `Inno Setup 6` 目录。普通应用构建不依赖 Inno Setup。安装器使用 Inno Setup 的原生安装／卸载引擎，保留其版权信息。

安装包包含应用、.NET Desktop Runtime、Node、脚本运行器、原生库及许可；目标电脑无需安装构建工具。默认无需管理员权限，安装到 `%LOCALAPPDATA%\Programs\meowfilm`，创建开始菜单入口，可选择创建桌面快捷方式，并登记到 Windows“已安装的应用”。重复安装沿用产品身份及原安装位置，按原安装清单删除过时文件；遇到不属于原安装的同名目标文件时停止，需选择其他安装目录。升级前须关闭 meowfilm，不强制终止运行中的应用。

用户数据固定存放于 `%LOCALAPPDATA%\meowfilm\aggregate`，与安装目录分离。交互卸载询问是否保留账号、设置、来源及缓存：默认保留，明确选择删除后才清理当前用户的数据；取消则不开始卸载。静默卸载保留数据。卸载只移除所属安装文件、快捷方式和系统登记，删除数据失败会报告；不会递归清理用户选择的整个安装目录或其他用户目录。

安装包先生成于 `tmp/windows-installer`，验证完成后替换固定输出；打包或编译失败保留上一份有效安装包。应用 EXE 的产品／文件版本是安装器版本来源；生成安装包不等于已完成签名、安装验收或发行发布。

## 产品图标

`apps/windows/app/Assets/AppIcon.ico` 是 Windows 编译输入，包含 16、24、32、48、64、128、256 像素七个尺寸；EXE、WPF 默认窗口图标及安装器共用该产品图标，快捷方式和卸载入口引用安装的 EXE。ICO 像素由既有正式产品图 `apps/ios/Assets.xcassets/AppIcon.appiconset/Icon-1024.png` 分别以 LANCZOS 缩放生成，未重绘或修改 iOS 资源。普通构建直接使用已提交 ICO，无需 Python、浏览器或图像工具。

## 输入闭包与信任边界

Windows 平台模块按序为 `music@0.8.0`、`cat@0.2.0`、`source@0.2.0`、`pan@0.1.0`、`tmdb@0.1.0`、`douban@0.1.0`、`film@0.1.0`，契约分别为 `music.script.v3`、`cat.script.v2`、`source.runtime.v2`、`pan.runtime.v1`、`tmdb.data.v1`、`douban.data.v1`、`film.playback.v1`；平台 manifest 与实际 entry、原生 describe 严格一致。通用默认 `createRuntime` 仍为原三模块。完整 HTTP 源码/JSON 不使用平台业务分片。

载荷包含公共 `app/` 入口及七模块、`app/windows/` 两个包装文件，以及 `@meowfilm/{runtime-bridge,script-host,source-runtime,pan-runtime,film-playback,film-rules,film-search}` 和根锁解析的运行依赖。`script-host/build/Release/` 的原生 addon、SDK、构建回执只按公共策略精确路径及哈希放行，不加载 WASM/asm.js 或任意附加 native binary。

`scripts/script-host-native.json` 固定 QuickJS-NG、OpenSSL、zlib、Node headers、补丁、构建工具和输入；回执记录真实平台/架构、源码树、文件哈希和构建命令。Node-API ABI、来源、许可、SDK 和所有输入必须匹配；打包不重建、不安装、不修锁。中央许可及包内实际许可均按库存复制，见[许可说明](../licenses.md)。

- root/output 为显式本机绝对路径，拒绝 UNC、源树覆盖、链接或重解析祖先。
- workspace 安装链接必须准确指向本仓库；输出成为普通目录，不借用全局或其他工作区依赖。
- 根锁身份、来源、SRI、版本、peer/optional 关系、导出及实际文件逐项核对。不得绕过缺失的最近依赖去找祖先。
- 拒绝路径穿越、大小写/Unicode 冲突、非受信二进制、额外平台文件；测试/缓存/隐私文件不入包，许可不因 files 裁剪丢失。
- 新输出独占创建，完成前核对完整 byte/hash inventory 与输入未变；失败保留 failed 清单。manifest 不自哈希。

影视规则与搜索程序属于 Node 载荷，由公共 script-host 原生 addon 执行并使用独立 Runtime／Context。Windows Capabilities 不再内嵌两份 JS 资源，Platform 不再构建／复制独立规则 DLL，App 不登记客户端规则文档。规则唯一存储、预览及搜索投影通过现有 film HTTP／SSE 访问；缺失运行器时返回真实错误，不保留旧引擎依赖或回退。源码与许可使用 `scripts/script-host-native.json` 的共同锁和构建回执。

视频内核同样不属于 Node 载荷。`MeowFilm.Media.csproj` 通过 `scripts/windows/build-video-kernel.ps1` 增量构建并复制私有 `libmeowfilm_ffmpeg_plugin.dll` 和 receipt；输入由 `apps/windows/native/VideoKernel/video-kernel.lock.json` 锁定 FFmpeg 8.1.2、VLC 3.0.24 SDK、libxml2、ABI／架构及配置。现有 libVLC 仍唯一解码／渲染，插件只解复用，具体见[原生媒体实现](media-playback.md)。原生 DLL 构建证据不代表合并后 Media C#、整 App、真实帧或最终分发闭包已验证；不得手动替换 DLL 或放宽运行器 native scope。

## 验证入口

`scripts/windows/test.ps1 -Group <names>` 准备同一真实原生 bundle 后编译并执行选定原生测试；`test-node.ps1 -Path tests/<area>.test.mjs` 执行选定 Node 测试。两者保留真实退出码，空集、跳过和未完成不算通过。测试副本/输出由本工作区固定 tmp 目录管理。

显式 `-All` 才运行完整对应测试范围；默认按实际变更选择。Windows 根测试选择不加载 iOS 专属测试。真实运行器集成覆盖 HTTP、LX/source 原生执行、Cat Worker、取消、持久数据及资源关闭；合成场景不冒充真实第三方来源或媒体播放验收。外部来源可按[动态验收工具](../runtime/source-acceptance.md)的 `--payload` 指向完整载荷，但不属于每次平台迁移的默认验证。
