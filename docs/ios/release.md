# 构建、发布与安装

## 构建

`build-ios.sh` 是 iOS 产品构建入口：使用 Xcode 构建 Release／iphoneos／arm64 未签名 IPA，在 `tmp/` 暂存和校验，成功后整体替换 `build/ios/`。

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

`scripts/test-native.sh` 构建的 Simulator App 使用本机 ad-hoc 签名和仅 Simulator SDK 生效的 `Meowfilm-Simulator.entitlements`，以使用真实 Keychain。`scripts/check-simulator-signing.py` 核对平台、Bundle ID、ad-hoc 签名和应用身份，拒绝描述文件、团队权限和其他 Keychain 组。

## 应用图标

`apps/ios/Artwork/AppIcon.svg` 是图标矢量源；`Assets.xcassets/AppIcon.appiconset` 保存 iPhone 所需尺寸和 1024px marketing 图，均为不透明 RGB PNG。
