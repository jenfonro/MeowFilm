# 构建、发布与安装

## 构建

`build-ios.sh` 是 iOS 产品构建入口：使用 Xcode 构建 Release／iphoneos／arm64 未签名 IPA，在 `tmp/` 暂存和校验，成功后整体替换 `build/ios/`。

App 部署目标及 MeowKit／AetherKernel 最低 iOS18；macOS15 仅为 `scripts/verify-native.sh` 主机 SwiftPM 快速编译校验目标，不是 Mac 产品。仓库参考 Xcode26.1.1；Aether 7.68.0 上游以 Xcode27 为正式支持、26.3 为 best effort，工具链兼容须以实际编译结果确认，不由最低部署版本或准备通过推定。

构建机需要使用 `scripts/script-host-native.json` 中 `build.node` 锁定的 Node 版本及根锁依赖 `npm ci --ignore-scripts`；客户端不需要用户安装 Node。`prepare-ios-runtime.py` 校验 `scripts/ios-node-mobile.json` 锁定的 Node Mobile 18.20.4 archive／许可，复用 `.cache/nodejs-mobile/ios/NodeMobile.xcframework`，并按 iOS SDK／架构准备 `MeowScriptHost.framework` 和公共载荷。`scripts/ios/package-runtime.mjs` 包装公共 `common-native` 打包器，包含 `complete`／`runtime.http.v1` manifest、iOS entry 及其业务模块、规则 SDK、根锁依赖和适用许可，不包含 Windows 入口或 WASM／asm.js 引擎。模块装配由实际 entry 和 provider 接线决定，准备工具不重复冻结完整模块数组；失败仍保留原完整载荷，不用旧载荷继续构建。

`build-ios.sh`、`verify-native.sh` 自动准备这些输入；直接运行 Xcode 前先准备相同目标的载体与载荷。可用 `MEOWFILM_BUILD_NODE` 指定构建工具 Node 路径；它不改变 App 内嵌版本。工程嵌入并签名 NodeMobile 和 MeowScriptHost，载荷及适用许可随 App 分发。影视规则／搜索程序由 Node 加载，共用 `scripts/script-host-native.json` 锁定的 QuickJS-NG 0.18.0 原生载体、派生补丁与 Unicode 表，不再通过 SwiftPM 静态链接另一套客户端规则核心；中央引擎 MIT 与 `Unicode-3.0.txt` 由 Xcode 随 App 打包。CI 两个工作流均恢复锁定依赖，不使用系统已装 Node 的隐式状态。

音乐输入由 `scripts/prepare-ios-audio-kernel.py` 按 `scripts/ios-audio-kernel.json` 锁定 FFmpeg 9.0.1，四个静态音频库封装为只导出 `meow_audio_*` 的动态 `CMeowAudioKernel.framework`，经 XCFramework／SwiftPM 进入 `MeowAudio`，由 Xcode 嵌入并随 App 签名。音乐目标不链接视频内核。`build-ios.sh` 准备 `ios-arm64`，`verify-native.sh` 选择对应 Mac／模拟器目标；实现边界见[独立音乐核心](music-audio-core.md)。

视频输入由 `scripts/prepare-ios-video-kernel.py` 按 `scripts/ios-video-kernel.json` 锁定的 FFmpeg 8.1.2 源码、补丁与目标构建；`build-ios.sh` 准备 `ios-arm64`，`verify-native.sh` 选择对应 Mac／模拟器目标，静态 XCFramework 产物与回执位于 `native/VideoKernel/.cache/`。`native/VideoKernel` 提供自有 Swift 主视频内核 `MeowVideoKernel`，经独立 `MeowPlaybackVideo` 适配接入，承担解复用、异步 VideoToolbox 解码、`AVSampleBufferDisplayLayer` 输出、实时音频、字幕、轨道章节、静默候选与排空，不拥有业务或 App 系统媒体会话。视频来源与适用许可引用 `LICENSES` 的唯一正文。目标产物准备和工程资源齐全不代表合并后的 iOS App 已编译、签名或真实播放通过，须分别取得平台证据。

可选 Aether 输入由 `scripts/prepare-ios-aether-kernel.py` 按 `scripts/ios-aether-kernel.json` 锁定源码、九个动态 FFmpeg 依赖库、Dovi 和宿主补丁。`build-ios.sh` 准备 `ios-arm64`；`verify-native.sh` 准备对应 Mac／simulator 目标。直接使用 Xcode／SwiftPM 前也须准备匹配输入，不能以 device 切片代替模拟器。唯一公开 dynamic `MeowAetherKernel` 的链接隔离、已知 plist 修正与补丁哈希规则见[Aether 构建边界](../../native/AetherKernel/README.md)，不在此复制锁清单。

`build-ios.sh` 和 `verify-native.sh` 的 Simulator App 编译后调用 Aether `--verify-app`；直接调用其他构建入口时须另行对实际 `.app` 执行该门禁。它核对 two-level dylib、FFmpeg 符号归属、原生 sections、依赖闭包与许可；准备／`--verify` 成功不代替实际 App 链接门禁，更不代替播放验收。

App 资源包含项目 `NOTICE.md`、现有 LGPL／FFmpeg 许可正文、`FFmpeg-audio-NOTICES.txt` 及 Aether／Dovi 等适用的中央许可；不重复许可正文。第三方来源与义务见[中央声明](../../LICENSES/NOTICE.md)，Apple 音频配方另见 `native/AudioKernel/Provenance/README.md`；动态封装、App Store 例外和签名均不豁免依赖各自的发行义务。

下载及 FFmpeg 完整依赖库按[构建依赖缓存](../common/dependency-cache.md)跨副本复用。缓存命中仍验证来源与文件身份，同配方正在构建时等待；源码、编译目录和安装输入归本副本，目标 slice 打包到上述核心目录。`--sources-only`、`--target`、`--verify` 调用方式不变。

设备与模拟器目标分别校验，不能按构建机架构猜测产物平台。原生构建回执记录签名前文件身份；侧载重签后的运行校验依赖正式签名域及内置构建身份，不要求 Mach-O 签名区保持原始 hash。该规则只适用于 App 内置载体，不开放上传原生代码或削弱桌面引擎校验。

`build/ios/` 只包含：

- `meowfilm-beta-ios.ipa`
- `meowfilm-beta-ios.sha256`
- `beta-ios-build.json`（commit、源码是否有未提交修改、版本、构建号、Xcode、IPA 哈希）

构建号取 `MEOW_BUILD_NUMBER`，其次 `GITHUB_RUN_NUMBER`，否则为 `1`。

## beta-ios

- `.github/workflows/beta-ios.yml` 只在 `main` 手动触发。
- 构建成功后发布到滚动 tag `beta-ios` 的 prerelease，始终不设为 Latest，不上传 Actions Artifact。
- 只有当前 `main` 的干净构建能更新 beta；过时运行不覆盖。
- CI 不使用 Apple 证书、描述文件、账号或签名 secret。

## 产品身份

| 项目 | 规则 |
| --- | --- |
| Bundle ID | 固定 `dev.meowfilm.aggregate` |
| 显示名 | meowfilm |
| CFBundleVersion | 构建号映射为单调三段版本 |
| 应用扩展 | 无；新增扩展需要单独决定身份 |
| 内部数据命名空间 | 使用固定产品身份，不随签名团队或安装标识变化 |

打包校验 Bundle ID、iPhoneOS 平台、arm64、无扩展和无描述文件，并移除工具链生成的签名。

## AltStore

未签名 IPA 由 AltStore 使用用户账号重签。AltStore 可能改写安装标识，App 不以安装标识作为启动条件。保持同一 Bundle ID 和签名团队时按升级安装；换团队、卸载或签名配置变化不保证 Keychain 与容器连续，Apple 证书、App ID 和刷新限制仍然适用。

## Simulator 签名

`scripts/verify-native.sh` 构建的 Simulator App 使用本机 ad-hoc 签名和仅 Simulator SDK 生效的 `Meowfilm-Simulator.entitlements`，声明应用身份与调试权限。`scripts/check-simulator-signing.py` 核对平台、Bundle ID、ad-hoc 签名和应用身份，拒绝描述文件、团队权限和其他 Keychain 组；这些签名检查不表示凭据存放于 Keychain。

## 应用图标

`apps/ios/Artwork/AppIcon.svg` 是图标矢量源；`Assets.xcassets/AppIcon.appiconset` 保存 iPhone 所需尺寸和 1024px marketing 图，均为不透明 RGB PNG。
