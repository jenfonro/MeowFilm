# AGENTS.md

meowfilm 的 iPhone 与 Windows 原生客户端，当前实现音乐能力：
- iOS：UIKit 负责应用、窗口、导航、呈现与手势，Swift Package 提供业务、平台、账号、播放、缓存和持久化能力。
- Windows：WPF / .NET 原生桌面应用；Node 运行时仅在执行用户导入的 JS 音源脚本时按需启动。

## 命令

### iOS

```sh
python3 -I -B scripts/generate-ios-project.py         # 生成 apps/ios/Meowfilm.xcodeproj
python3 -I -B scripts/check-source-scope.py           # 源码、依赖与工程成员检查
python3 -I -B -m unittest discover -s scripts/tests -p "test_<area>.py" -v
bash scripts/test-native.sh test --filter <pattern> # macOS：受影响的 Swift 测试
bash scripts/test-native.sh all                    # 显式完整代码检查
python3 -I -B scripts/test-ui-platform.py --simulator <UDID> --result tmp/test-results/<name>.xcresult [--test <name>]
./build-ios.sh                                        # macOS：构建并替换 build/ios/
```

- Windows 将 `python3` 换为 `python -X utf8`。
- 需要 macOS 与 Xcode；参考环境为 Xcode 26.1.1。iOS 部署目标 17.0，macOS 仅用于原生单测执行。

### Windows

```powershell
.\build-windows.ps1                        # 构建完整应用，输出 build\windows\meowfilm.exe
.\build-windows.ps1 -Configuration Debug   # Debug 构建；-FrameworkDependent 生成依赖系统 .NET 的版本
.\scripts\windows\bootstrap.ps1            # 按锁安装 Node 工作区依赖
.\scripts\windows\restore.ps1              # 按锁恢复原生依赖
.\scripts\windows\test-node.ps1 -Path tests/<area>.test.mjs
.\scripts\windows\test-node.ps1 -All       # 显式完整 Node 检查
.\scripts\windows\test.ps1 -Group <names>  # 增量编译并运行所选原生测试组
.\scripts\windows\test.ps1 -All            # 显式完整原生测试
```

- 需要 PATH 上的 .NET SDK（10 或更高）、Node.js、npm 与 Git；参考环境为 .NET SDK 10.0.402、Node.js 24.21.0。
- 随 App 发布的 Node 运行时由 `scripts/windows/node-runtime.json` 锁定，并与 `LICENSES/node-LICENSE.txt` 对应；构建时 PATH 上的 `node.exe` 须与其一致。

## 目录

- `apps/ios/` — 唯一 iOS App 工程
  - `Application` — 应用与 scene 生命周期、窗口、产品身份
  - `Composition` — 资源、服务、播放、账号和同步装配
  - `Navigation` — 根容器、页面导航与呈现路由
  - `Presentation` — 主页面外壳、页头、底部导航和播放器停靠区
  - `UITests` — 共享 UIKit 组件测试
- `native/MeowKit/` — iOS Swift Package
  - `MeowContracts`、`MeowJSON`、`MeowStorage`、`MeowAccounts`、`MeowPlayback`、`MeowUI` — 共享能力
  - `MeowMusicFeature`、`MeowMusicPlatform`、`MeowMusicNativeService` — 音乐界面、平台和原生业务
- `apps/windows/app/` — Windows 应用入口与装配
- `apps/windows/src/` — Windows 原生库
  - `MeowFilm.Core` — 基础契约
  - `MeowFilm.Platform` — 受保护存储、命名管道、子进程、文件授权
  - `MeowFilm.Desktop` — 窗口、导航、设置入口、主题、通知
  - `MeowFilm.Media` — 媒体播放与自适应流
  - `MeowFilm.SystemMedia` — 系统媒体传输控件
  - `MeowFilm.AppHost` — 启动、注入与关闭
  - `MeowFilm.Capabilities` — 按职责组织的业务能力（Accounts、Platforms、Catalog、Search、Recommendations、Library、Sync、Playback、PlaybackCache、Lyrics、Scripting、Feedback、Settings、Presentation、Composition）
- `apps/windows/tests/` — Windows 原生测试
- `apps/windows/runtime/`、`apps/runtime/` — JS 音源脚本运行时入口与传输
- `packages/` — 脚本运行时使用的 Node 工作区包
- `scripts/` — iOS 工程生成、结构检查、测试、构建打包与发布，`scripts/tests/` 为脚本测试；Node 共用工具
- `scripts/windows/` — Windows 构建、测试、打包与结构检查
- `tests/` — Node 测试
- `docs/` — 架构、模块、契约与发布说明，索引见 `docs/README.md`
- `LICENSES/` — 全部第三方许可与来源声明（`NOTICE.md`）；Windows 构建时复制到 `build/windows/licenses`
- `build/ios/`、`build/windows/` — 各平台最新一份产物，每次构建成功后整体覆盖；不入 Git
- `tmp/` — 日志、测试结果、构建暂存与任务工作记录（`tmp/working/`），可随时整体删除；不入 Git
- 许可文件只放在 `LICENSES/`，每种许可正文一份；改编新的上游代码时在 `NOTICE.md` 补充来源，增删文件时同步 `scripts/windows/package-licenses.mjs`。

## 代码组织

- 按真实职责和变化原因组织目录、文件与类型；名称反映实际责任。通用能力使用中性名称，领域专属能力保留业务语义。
- 拆分必须形成清晰职责和调用边界，不以拆文件、改名、移动目录或增加转发层代替解耦。
- 遵循现有命名、格式、导入和注释约定，不夹带与任务无关的风格整理。
- 模块的实现背景、来源、协议与特殊约束写入 `docs` 对应文档，不写入本文件。

## 职责与依赖

| 层次 | 主要责任 | 边界限制 |
| --- | --- | --- |
| 应用入口与外壳 | 启动、装配、全局导航、公共布局、全局资源 | 不承载领域业务策略，不重复实现基础能力 |
| 业务模块 | 业务模型、规则、流程、页面内容与业务状态 | 不重复建设外壳，不访问其他模块私有实现或存储 |
| 共享能力 | 通用机制、基础设施与最小公共契约 | 不反向依赖应用或业务，不内置业务分派 |
| 平台与外部适配 | 系统能力、外部协议与契约实现 | 平台细节不扩散到业务核心，不替业务层决定策略 |

- 每项职责有明确所有者；模块间通过最小公开契约协作，明确输入、输出、生命周期和错误语义。
- 依赖显式且单向，不形成循环依赖或隐式初始化顺序。不用全局事件总线、服务定位器、无边界单例或跨模块直接读写绕过契约。
- 新增能力前先检查现有实现。公共抽取以通用语义、稳定契约和实际使用为依据；共享层提供机制，业务层决定策略，不给共享实现堆叠业务分支。

## 状态与生命周期

- 每份可变状态有明确的事实来源、作用域和更新所有者；可推导状态优先计算，不重复存储。
- 跨页面业务状态和长期资源由稳定所有者管理，不依赖视图重建或显示切换；页面可见性与资源活跃状态分别判断。
- 异步操作明确取消、失效和完成语义，旧结果不得覆盖新状态；完成由实际执行者确认，不靠猜测等待时间。

## 实现

- 满足需求及必要的可控、可观测、可验证和生命周期要求后，选择总体复杂度最低的方案；不增加未要求的功能、框架、配置或扩展点。
- 优先复用已有实现、标准库和平台能力；替代实现保持所需业务语义。类型准确表达身份、状态和约束，不用任意字符串和静默默认值掩盖错误。
- 问题沿真实所有者、调用链、数据流和生命周期定位根因，在拥有该职责的边界修正；不以外层特判、影子状态、延时、重试、旁路或新旧实现并存堆叠补丁，修正后删除失效路径。
- 兼容、迁移和废弃依据明确的版本与数据承诺设计，不无依据地增加兼容层或长期维护多套实现。

## 变更范围

- 只修改当前任务必需的内容；每处改动能追溯到需求或必要影响。
- 修改公共契约、数据格式或共享实现时，同步更新受影响的消费者、配置、资源、测试和 `docs`。
- 清理本次修改产生的孤立代码；既有无关问题单独指出，不扩大为未经授权的重构。
- 删除或移动移植代码前确认许可来源；仍在使用的许可文件随代码保留。

## 前端

- 禁止纯解释性文本：不添加功能介绍、操作引导、设计意图、占位说明或重复解释；只保留必要的标题、控件标签、业务内容、状态、错误、操作确认及无障碍名称。
- 操作结果与错误通知走统一的通知或信息框，不作为文本插入页面布局；需要确认或重试的操作保留在对应浮层中。

## 验证

- 按风险覆盖实际受影响的契约与消费者：编译、静态检查、单元、组件或集成测试；必要时覆盖失败、取消、恢复和资源释放路径。
- 可机械核对的目录、依赖和契约约束接入 `scripts`（Windows 为 `scripts/windows`）中的现有检查。
- 长期测试验证现行行为与契约；一次性核对随任务结束清理，不冻结具体实现或退役清单。
- 编译成功、测试替身和真实业务验证分别记录，不互相冒充；不为刷绿删除有效断言、隐藏失败或用空测试集代替验证。
- 交付时说明实际修改范围、验证结果和未覆盖项。

## 安全与协作

- 密码、令牌、Cookie、密钥和真实用户数据不得进入源码、文档、日志或测试样例。
- 不覆盖他人未提交的修改，不擅自清理用户数据；删除或移动前核对绝对路径。
- 提交、推送和破坏性操作须有当次明确授权。

## 文档组织

- 按使用范围和真实职责维护文档：共同契约放在 `docs/common/`，端侧 API、系统生命周期、协议、存储与实现差异放在对应端目录；总入口为[文档索引](docs/README.md)。
- 共同规则以[共同契约索引](docs/common/README.md)中的对应职责文件为唯一来源；端侧直接引用，只补充本端事实与差异，不复制规则或建立重复别名文件。
- 拆分必须形成独立说明职责；移动、删除或调整范围时同步全部索引和引用，避免失效相对链接及多份同义说明。
- 区分共同契约、现行实现、待实施设计和验证证据；未落地的类型、接口、数据格式或存储身份不得写成已实现，完成实施后同步对应端侧说明。
