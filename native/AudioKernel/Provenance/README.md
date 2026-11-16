# 来源与许可

- C decoder 为本项目原创实现，以公开 FFmpeg API 与项目 Windows decoder 的既有行为为参考。没有引用视频内核或第三方播放器适配代码。
- 独立 Apple 来源锁为 `scripts/ios-audio-kernel.json`。其 `source` 完整复制已验证的 Windows `apps/windows/native/AudioKernel/audio-kernel.lock.json` 来源事实，不复制 Windows DLL/binding/toolchain/avfilter 配方。
- FFmpeg 9.0.1，tag `n9.0.1`，commit `bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa`。
- 官方源归档 SHA256：`cf38e0e28c7e5605942c4a77755349b0145804a397af37eb1fb4c77cb237f635`。
- 来源树 SHA256：`7cdc17943e6f819c958c951747509388ef969c59f83671f9f35dedb5be2439ad`。算法与 Windows 统一：目录逐层按名称排序，逐文件写入相对路径、NUL、原文、NUL。Apple 准备重新核对归档/树/版本/四库 ABI/许可正文；源代码未打补丁。
- 签名、key URL/哈希和已验证指纹是上游 Windows 来源记录，不宣称本次又执行了一次 GPG 验签。
- 本构建禁用 GPL、version3、nonfree、avfilter、swscale、视频 decoder 与自带程序。四库保持 LGPL 2.1-or-later 范围；HLS AES128 使用上游 libavutil/crypto，不移植密码学代码。

许可原文复用仓库现有 `LICENSES/LGPL-2.1-only.txt`、`LICENSES/FFmpeg-LICENSE.md`，不另复制许可正文。

本独立 Apple 组件、精确来源及动态封装事实统一记录于[第三方声明](../../../LICENSES/NOTICE.md)。iOS 工程随 App 打包既有许可正文与 `FFmpeg-audio-NOTICES.txt`，见[构建与发布](../../../docs/ios/release.md)。

对应源码由本仓库 C adapter／构建配方和上述未修改上游归档组成。发行者仍须提供适用源码、修改声明及重建／重链接或替换 LGPL 组件所需的应用源码与构建输入；动态封装、签名和 iOS 安装限制均不豁免这些义务。构建产物存在不代表发行义务已经完成。
