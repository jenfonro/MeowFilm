# MeowVideoKernel

受控 FFmpeg 视频解复用／解码及 Apple sample-buffer 输出，不拥有业务、
HTTP 凭据、系统音频会话、Now Playing 或远程控制。音乐不使用此包。

输入入口为 `VideoSession.prepare(url:format:clearKeys:position:)` 和
`VideoDecoder.open(url:format:clearKeys:)`，不接受授权 URL 前缀，不实施
域名、origin、回环／私网或 manifest URL 图准入。相对／绝对引用、查询和
重定向交给真实 FFmpeg 协议及格式解析；子请求保留原 FFmpeg options／header
上下文，本包不重建上游凭据。取消／超时、实际格式错误与资源排空仍然生效。
在途 seek 保留用户目标，期间倍速／暂停只更新最终速率；已取消 seek 后的
明确 resume 则重新建立实际解码 feeding，而不是仅启动时钟。

在 Mac 的仓库根目录先准备对应平台（macOS 仅原生测试）：

```sh
python3 -I -B scripts/prepare-ios-video-kernel.py --sources-only
python3 -I -B scripts/prepare-ios-video-kernel.py --target macos-$(uname -m)
python3 -I -B scripts/prepare-ios-video-kernel.py --target ios-simulator-$(uname -m)
python3 -I -B scripts/prepare-ios-video-kernel.py --target ios-arm64
python3 -I -B scripts/prepare-ios-video-kernel.py --verify --target ios-arm64
```

没有默认全量或 `all`；`--target` 可重复，普通 mac 单测仅传 `macos-$(uname -m)`。
`--verify` 不构建，拒绝缺少请求 slice、旧脚本/锁/target recipe 或产物变动。
每次只准备请求的平台／架构，按[构建依赖缓存](../../docs/common/dependency-cache.md)
复用共享下载及已验证完整的依赖库。同配方命中跳过 configure／make，未命中时
在本副本 `.cache/ios-video-kernel` 中准备源码与增量 make 产物。
静态 XCFramework 位于本包被忽略的 `.cache/Artifacts`，不会从
SPM URL 下载二进制。缺少目标产物时必须运行准备入口，不能回退为假内核。

来源、原文许可哈希及改编记录见 `Provenance`；唯一许可正文和 App 资源清单
由主会话汇入 `LICENSES`，本包准备校验 archive 中的原始许可，不依赖许可暂存。
KSPlayer 固定来源为 GPLv3，不是 MIT。固定来源／配置／补丁哈希由
`scripts/ios-video-kernel.json` 控制。

源验证回执 `.cache/ios-video-kernel/source-receipt.json` 每源含
`revision/archiveSHA256/inventorySHA256/files`。最终回执
`native/VideoKernel/.cache/prepare-receipt.json` 含
`schema/status/identity/sourceLockSHA256/targets/artifacts`；每 target 含
`fingerprint/inputs/libraries/headers`，inputs 记录脚本/锁/源码、SDK、架构、
编译器和 configure。只把当前同 recipe 的 slice 装入静态 XCFramework。
