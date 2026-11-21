# Aether 视频内核构建边界

`MeowAetherKernel` 是唯一公开的 **dynamic** Swift Package product。上层只依赖
该 product；`AetherEngine`、`AetherFFmpegBuild`、Dovi 和九个 AetherLib 模块
在此动态链接边界内编译/链接，不从 MeowKit 或 App 直接导入。适配器源码由
`Sources/MeowAetherKernel` 持有；本包不拥有业务或系统媒体会话。

公开 `AetherVideoSession` 只向 `MeowPlaybackVideo` 提供准备、实际首帧、输出、
章节、命令与关闭接口，上游类型经 `internal import` 保留在动态边界内。
默认内核、候选回退和章节跳过分别由[iOS 影视](../../docs/ios/film.md)与
[播放器设置](../../docs/ios/film-settings.md#播放器设置)说明，不在本包决定业务策略。

## 锁定准备

```sh
python3 -I -B scripts/prepare-ios-aether-kernel.py --target ios-arm64
python3 -I -B scripts/prepare-ios-aether-kernel.py --target ios-simulator-x86_64
python3 -I -B scripts/prepare-ios-aether-kernel.py --target macos-x86_64
python3 -I -B scripts/prepare-ios-aether-kernel.py --target ios-arm64 --verify
```

目标可重复指定。`scripts/ios-aether-kernel.json` 固定 AetherEngine 7.68.0
源码、FFmpegBuild 3.15.0 与 LibDovi 2.2.0 归档、上游许可、实际编译闭包
和本地补丁身份。没有浮动 SwiftPM 依赖，不编译 SMB、CLI、示例或可选
TrueHD/FEL traits。准备不是 Swift 编译或真实播放验收。

下载和完整、只读的已验证输入共享于 `~/Developer/cache/aether-engine`，
通过按下载/配方原子文件锁协调；本副本可写输入位于 `.cache/`。
既有缓存每次复核文件/链接清单，未登记改动拒绝覆盖。
App 最低 iOS18，主机测试最低 macOS15。二进制保留 device arm64、
simulator arm64/x86_64 和 macOS arm64/x86_64 三组原生切片。
macOS 目标只执行原生测试，不提供 Mac 客户端。上游正式支持 Xcode27、
26.3 为 best effort；仓库参考 Xcode26.1.1，工具链兼容须以实际编译结果确认。

KSPlayer 的 FFmpeg 8.1.2 为另一条静态依赖，Aether 的 FFmpegBuild 3.15.0
提供 FFmpeg 8.1.3 动态库。不能让 Aether 的 `av_*` undefined 符号绑定到
App／KS 静态符号；包 graph 分开并不等于运行链接隔离，须由实际 App
two-level library ordinal 与完整闭包检查确认。

上游九个 iOS framework 的 plist 写成最低 26.0，但 Mach-O 实际最低
16.0。准备先验证 Mach-O，再仅修正该已知元数据错误为 16.0；不修改
二进制或降低 App 的 iOS18 要求。每次使用验证架构、平台、最低版本、
two-level dylib、install name 和完整动态依赖闭包。Dovi 为静态 archive，
只链接进本动态 wrapper。

## 源码补丁

维护 `patches/*.patch`，并在锁的 `patches` 数组登记：

```json
{
  "path": "native/AetherKernel/patches/example.patch",
  "sha256": "<patch SHA-256>",
  "files": {
    "AetherEngine/path.swift": {
      "upstream": "<before SHA-256>",
      "patched": "<after SHA-256>"
    }
  }
}
```

路径相对于准备输入根目录，不是上游仓库根目录。新增/删除文件对应
`upstream`/`patched` 为 `null`。补丁零 fuzz 应用，结果逐文件验证。
将已登记补丁逆向映射回锁定原始清单后，须匹配
`preparedInventorySHA256`；无需为每次适配器补丁重写原始闭包 pin。
不得直接编辑 `.cache` 或共享缓存来绕过来源校验。

### 宿主适配补丁

当前锁登记 `patches/runtime-host.patch`，涉及上游 Loading、AetherEngine、
EngineLog、SoftwarePacketReadAhead、SoftwarePlaybackHost、NativeAVPlayerHost 与
HLSVideoEngine 七个源码文件；逐文件 before／after 与 patch hash 以锁为准，不因文档说明放宽校验。
该补丁的源码改动包括：

- `hostManagedLifecycle` 使嵌入模式不安装上游生命周期观察、不配置进程全局
  AVAudioSession category；wrapper 另显式关闭上游 Now Playing owner／commands、
  renderer 音频激活和停止时停用，保留 App 唯一系统 owner。
- `outputRate` 提供实际输出速率；`stopAndDrain()` 等待登记的原生 HLS、
  software 及缩略图清理任务。load 失败／换代时也等待当地 HLS session 排空。
- HLS producer 在取消后等待真实 finish，不把原来的三秒等待超时算作排空；
  software 先中断源，再等待 demux／feed／seek／still 队列和 packet worker，
  最后释放 decoder／demuxer。packet worker 使用 DispatchGroup 确认退出。
- `mediaCapabilityUnsupported(_:)` 分类有限的 typed DASH／HLS／视频能力错误，
  不将网络、超时或任意错误一律变成格式回退；业务回退仍属于上层候选。
- DASH probe 失败时排空当地 demuxer 并保留原始 typed 错误，不在 codec-NONE
  路径重开后丢失能力分类；结构不支持与网络／文档错误仍分别处理。
- native PiP 的明确 AVPlayer 播放／暂停意图回到统一 Controller；应用自身的
  rate 写入、缓冲及 EOF 不冒充外部命令，回调按当前 native host 隔离。
- `EngineLog.diagnosticsEnabled=false` 关闭上游原始诊断投递；对 App 仅暴露有限
  `AetherVideoFailure`，不输出 URL、headers、Cookie 或原始外部异常。

以上是已写入源码及锁的适配事实，不是排空、音频隔离、错误传播或 PiP 已实测通过。
不得以 stop 返回、Task 已取消、layer ready 或锁校验成功代替真实资源退出／首帧。

## 实际 App 门禁

```sh
python3 -I -B scripts/prepare-ios-aether-kernel.py \
  --target ios-arm64 --verify-app /absolute/path/Meowfilm.app
```

该入口不下载，核对 wrapper 的 two-level 动态边界、实际 FFmpeg undefined
符号的库归属、九库闭包、架构、最低版本和许可证。打包的原生代码/数据
sections 必须与锁定切片一致；签名及符号剥离导致的 LINKEDIT 变化不作为
代码变更。`build-ios.sh` 和 `verify-native.sh` 的 Simulator App 编译后调用该门禁；
其他直接构建入口须另行对实际 `.app` 执行。
`--verify-app` 中的实际 symbol 归属读取使用 Mac 的 `xcrun nm`；Linux
准备与单测不冒充已通过 App 链接、签名、设备播放或 HDR 验收。

许可与来源见[中央声明](../../LICENSES/NOTICE.md)的 Aether 条目。Aether 的 App Store
例外不扩张到其 FFmpeg、Dovi 或其他依赖，也不免除对应源码义务。

## 平台验收边界

须分别取得 Apple 工具链编译与实际 App 动态符号门禁、Aether 真实首帧／seek／
取消／关闭、同 receipt 的有界 Aether→KS 回退，以及 KS 4／6／8X 音视频和无音轨
实际倍率结果。Aether native／software PiP 的命令回到唯一 Controller、恢复与排空
须单独验证；HDR／Dolby Vision 输出须按设备、profile 和实际显示证据说明，不承诺
profile7／FEL，不以模拟器、准备输入或配置检查宣称优于 KSPlayer。
