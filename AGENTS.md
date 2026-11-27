# AGENTS.md

meowfilm 的 iPhone 与 Windows 原生客户端：iOS 为 UIKit App + Swift Package（`native/MeowKit`），Windows 为 WinUI 3／.NET 桌面应用；两端共用 Node 运行器（`apps/runtime`），承担影视规则与会话、猫源和音乐平台请求。

本文件定义 meowfilm 的项目规范、限制与索引（目录、构建命令、文档入口）；系统行为与模块契约见 [`docs/README.md`](docs/README.md)，实跑验证与故障排查见 [`AGENTS_DEBUG.md`](AGENTS_DEBUG.md)。

## 目录

```text
apps/
  ios/                         iOS App 工程
    Application/               应用与 scene 生命周期、窗口
    Composition/               服务、播放、账号与同步装配
    Navigation/                根容器、页面导航与呈现路由
    Presentation/              主页面外壳、页头、底部导航、播放器停靠区
    Debug/                     --debug 调试宿主（Host、Navigation、Inspection、Actions）
    runtime/                   iOS 运行器启动与 entry
  windows/
    app/                       应用入口与装配（Program.cs、App.xaml）
    runtime/                   Windows 私密启动管道与 aggregate entry，不保留 module 副本
    native/FilmKernel/         影视解复用、解码、音频时钟与 HDR／SDR 输出内核（C ABI），不含 UI 与音乐
    src/
      MeowFilm.Core/           基础契约、播放作用域、运行器客户端
      MeowFilm.Threading/      DispatcherQueue 调度、取消与计时器
      MeowFilm.Platform/       SQLite 存储、资源文件、运行器子进程会话、Debug 服务
      MeowFilm.Desktop/        窗口外壳 Shell、主题 Theme、通用控件 Controls／Management／Dialogs、图片 Artwork、视口 Viewport、通知
      MeowFilm.Audio/          音乐解码、PCM／DSP 与 WASAPI 输出
      MeowFilm.Media/          视频播放与自适应流
      MeowFilm.SystemMedia/    系统媒体传输控件
      MeowFilm.AppHost/        启动、注入与关闭
      MeowFilm.Capabilities/   业务能力
        Music/                 Accounts、Catalog、Library、Lyrics、Playback、PlaybackCache、Recommendations、Search、Settings、Sync、Composition 等
        Film/                  Home、Library、Playback、Rules、Search、Settings、Sources、Composition 等
        Novel/                 Domain、Presentation、Storage、Composition
        Pan/ History/ Search/  网盘、历史、全局搜索
        Shared/                跨域共享的账号、目录、控件、设置与主题
  runtime/                     通用运行器：entry.mjs、bootstrap.mjs 装配；modules/ 为九个模块实现（契约见 docs/runtime/）
native/
  MeowKit/Sources/             iOS Swift Package
    MeowUI/                    通用 UI：Navigation（页面基类、反馈）、Settings、List、Catalog、Artwork 等
    MeowPlayback/ MeowAudio/ MeowPlaybackVideo/                  播放控制、音频会话、视频适配
    MeowStorage/ MeowRuntime/ MeowAccounts/ MeowContracts/ MeowJSON/ MeowDebug/   存储、运行器客户端、账号、契约、JSON、调试
    MeowMusicFeature/          音乐界面
    MeowMusicNativeService/    音乐原生业务
    MeowMusicPlatform/         音乐平台、账号与播放缓存
    MeowFilmFeature/           影视
    MeowNovelFeature/          小说
  AudioKernel/ VideoKernel/ AetherKernel/   iOS 音频、视频与可选 Aether 内核，不拥有业务或系统媒体会话
packages/                      共享 Node 包（职责见 docs/runtime/）
  storage/                     双库 SQLite Schema（schema/）、生成产物（generated/）与引擎
  script-host/                 脚本实例生命周期；native/ 为 QuickJS 原生 addon
  source-runtime/              来源规则与 capability ABI，不依赖 Node
  runtime-bridge/              HTTP 请求、取消与连接生命周期
  pan-runtime/                 网盘能力，通用默认入口不装配
  film-playback/               影视会话与业务缓存
  film-rules/ film-search/     影视纯计算程序，不在客户端执行或持久化
  netease-token/               网易订阅 token
  music-platform/              音乐平台请求配方，不持久保存原生账号、歌曲仓库或图片
  novel-rules/                 小说规则
scripts/                       Schema 生成 storage/、iOS 工程与内核准备 ios/ 及 prepare-ios-*.py、QuickJS 构建 quickjs/、Windows 构建与检查 windows/、跨端 debug CLI debug.mjs
docs/                          common/（共同契约：film、music、novel、pages）、ios/、windows/、runtime/；索引 docs/README.md
LICENSES/                      第三方许可与 NOTICE.md
build/                         各平台最新构建产物，不入 Git
tmp/                           日志、构建暂存与任务工作记录 tmp/working/，不入 Git
```

- 许可正文每种一份放在 `LICENSES/`；改编上游代码时在 `NOTICE.md` 补充来源，增删时同步平台打包清单；删除或移动移植代码前确认许可来源。

## 命令

### iOS

```sh
python3 -I -B scripts/generate-ios-project.py         # 生成 apps/ios/Meowfilm.xcodeproj
python3 -I -B scripts/prepare-ios-runtime.py          # Node 24 + npm ci：准备内嵌运行器构建输入
python3 -I -B scripts/prepare-ios-audio-kernel.py --target ios-arm64 # 锁定独立音乐内核；原生校验入口选择对应 Mac/模拟器目标
python3 -I -B scripts/prepare-ios-video-kernel.py --target ios-arm64 # 锁定源码准备视频内核；原生校验入口选择对应 Mac/模拟器目标
python3 -I -B scripts/prepare-ios-aether-kernel.py --target ios-arm64 # 锁定可选 Aether 内核及动态依赖；不代表编译或播放验收
python3 -I -B scripts/check-source-scope.py           # 源码、依赖与工程成员检查
bash scripts/verify-native.sh                         # macOS：源码边界、原生编译、链接与 Simulator 签名校验
./build-ios.sh                                        # macOS：构建并替换 build/ios/
```

- Windows 将 `python3` 换为 `python -X utf8`。
- 需要 macOS、Xcode、Node 24 与 `npm ci --ignore-scripts`；iOS 部署目标 18.0，macOS 15.0 仅用于原生包编译与校验。构建与签名细节见 [iOS 发布](docs/ios/release.md)。

### Windows

```powershell
.\build-windows.ps1                        # 构建完整应用，输出 build\windows\meowfilm.exe
.\build-windows.ps1 -Configuration Debug   # Debug 构建；-FrameworkDependent 生成依赖系统 .NET 的版本
.\scripts\windows\bootstrap.ps1            # 按锁安装 Node 工作区依赖
.\scripts\windows\restore.ps1              # 按锁恢复原生依赖
npm run verify                             # 依赖锁、包边界、原生 JS 与 Windows 应用结构静态检查
node scripts/windows/verify-projects.mjs   # Windows 项目边界检查
```

- 需要 .NET SDK 10 或更高、Node.js、npm 与 Git；脚本宿主 Node addon 另需 Python、Perl、NASM 与 MSVC x64 环境（`cl`／`nmake` 在 PATH）。Node 版本以 `scripts/script-host-native.json` 的 `build.node` 为准，构建时 PATH 上的 `node.exe` 须符合 `scripts/windows/node-runtime.json` 锁定的版本与哈希；打包细节见 [Windows 运行器打包](docs/windows/runtime-packaging.md)。

## 架构

| 层次 | 主要责任 | 边界限制 |
| --- | --- | --- |
| 应用入口与外壳 | 启动、装配、全局导航、公共布局、全局资源 | 不承载领域业务策略，不重复实现基础能力 |
| 业务模块 | 业务模型、规则、流程、页面内容与业务状态 | 不重复建设外壳，不访问其他模块私有实现或存储 |
| 共享能力 | 通用机制、基础设施与最小公共契约 | 不反向依赖应用或业务，不内置业务分派 |
| 平台与外部适配 | 系统能力、外部协议与契约实现 | 平台细节不扩散到业务核心，不替业务层决定策略 |

- 按真实职责和变化原因组织目录、文件与类型；名称反映实际责任，通用能力使用中性名称，领域专属能力保留业务语义。
- 每项职责与每份可变状态都有明确的所有者、作用域和事实来源；模块间通过最小公开契约协作，明确输入、输出、生命周期和错误语义。可推导状态优先计算，不重复存储。
- 依赖显式且单向，不形成循环依赖或隐式初始化顺序；不用全局事件总线、服务定位器、无边界单例或跨模块直接读写绕过契约。
- 跨页面业务状态和长期资源由稳定所有者管理，不依赖视图重建或显示切换；页面可见性与资源活跃状态分别判断。
- 异步操作明确取消、失效和完成语义：由发起方取消并等待旧任务结束后再切换，不另加 generation／epoch 一类过期票据；旧结果不得覆盖新状态，不靠猜测等待时间。
- 运行器、脚本宿主与来源规则的所有权和能力边界见[脚本运行时](docs/runtime/script-runtime.md)及各运行器契约。

## 实现

- **开发期禁止旧数据与旧逻辑兼容**：
  - 数据库、本地配置与内部业务记录的 schema／格式版本固定为 1，不递增、不识别旧表／旧字段、不写迁移或升级／回退分支；不匹配的旧数据直接拒绝，并发 revision／generation 仍按事务变化递增。已确认保存的数据在运行中完成持久化，不依赖正常退出或 iOS 终止回调。
  - 接口、协议、状态与内部逻辑改动直接落到最新实现；不保留旧字段别名、旧参数／旧枚举兜底、过渡兼容层、回退开关或新旧逻辑并存。
- 校验、失败传播与清理遵循[拒绝条件与数据消费边界](docs/common/validation-boundaries.md)：普通 API 按实际字段消费，不做未消费字段预检、镜像断言或静默吞错；typed 存储的列／关系、身份／权限、CAS、执行信任与排空边界保留。
- 内部业务数据与结构化缓存遵循[共享 SQLite 存储契约](docs/common/settings-storage.md)：永久库与可重建缓存库物理分离，使用手写强类型 `STRICT` 关系表；禁止内部 JSON 文件、整份业务 JSON 入库与伪关系 JSON 投影框架。

## 前端

- **两端通用**：
  - 栏目、卡片和列表的显示内容只随内容回执、显式刷新／筛选及账号等业务状态变化更新；滚动、布局、尺寸、裁剪、命中区域和绘制不触发业务读取、显示重建或刷新，也不扫描控件树。
  - 图片、取色等资源在第一次可见时请求一次，之后在当前页面内复用：滚出再滚回不重新请求，列表容器回收视图不释放资源；只有数据项删除、页面卸载或资源身份变化才释放。页面隐藏后不再发起新请求，未完成的请求取消，关闭时等它们实际结束。
  - 数据回执按稳定身份增量更新实际变化的元素，保留未变化的集合、控件和资源；加载状态、选择及无关属性通知不得清空重填列表或重建整页。
  - 普通文字统一使用系统常规字体（Normal／Regular），包括标题、按钮、菜单、标签、选中态和歌词；层级只通过字号区分，不使用 Medium、SemiBold、Bold、斜体或状态切换粗细。仅阅读器内可自定义的正文排版保留用户字体／加粗设置；纯图标／符号图形不属于普通文字。
  - 不添加功能介绍、操作引导、设计意图、占位说明等纯解释性文本；只保留必要的标题、控件标签、业务内容、状态、错误、操作确认及无障碍名称。
  - 操作结果与错误通知走统一的通知或信息框，不作为文本插入页面布局；需要确认或重试的操作保留在对应浮层中。
- **iOS**（现行实现见 [`docs/ios/architecture.md`](docs/ios/architecture.md)，该文档描述现状，不构成本节之外的强制要求）：
  - 纯 UIKit：iOS App 与 `MeowKit` UI 模块不使用 SwiftUI（零 `import SwiftUI`、`UIHostingController`、`UIViewRepresentable`），不做跨框架桥接或整树重建。
  - 设置页共用 `MeowUI/Settings/` 底座与 `PageTheme`；弹窗、确认与错误／操作反馈由 `MeowUI` 通用组件统一呈现，需要确认的操作与非阻塞提示分开；业务页不自建同类组件。
  - 主题经显式协议分发，不递归遍历 `subviews`；子视图不调用 `superview?.layoutIfNeeded()` 或 `window?.layoutIfNeeded()`；协调器不按具体业务控制器类型做 `is` 判断。
  - 各域图片／取色存储由装配根持有，无全局单例；列表保持 cell 复用、经 `MeowUI/List/` 增量更新，不整表 `reloadData()`，不用非虚拟化 `UIStackView` 承载长列表。
  - 页面可见性（含被模态遮挡、宿主隐藏）由导航／呈现层统一计算并下发，不可见页面不被唤醒；高频播放状态与低频页面状态分开发布，宽 `ObservableObject` 按字段观察；主线程不做音频喂流、封面模糊或像素取色。
  - `MeowUI`、各 Feature 与 `apps/ios/Debug` 按职责建子目录，不平铺堆放跨子域文件。

## 验证

- **禁止残缺版测试与替身验证**：不编写或使用单元测试、UI／业务回归测试、Mock／替身夹具、仅服务于测试的生产代码钩子、外部盲扫控件树／模拟键鼠自动化，也不用一次性探针或编译／静态检查冒充运行验收（红线清单见 [`AGENTS_DEBUG.md`](AGENTS_DEBUG.md)）。
- **统一用 `--debug` 模式实跑验收**：功能、界面交互、状态流转与故障排查通过正常构建产物的 `--debug` 模式完成（操作见 [`AGENTS_DEBUG.md`](AGENTS_DEBUG.md)，协议见 [`docs/common/debug-mode.md`](docs/common/debug-mode.md)）。调试会话目录可保留不脱敏原文用于排查，仅限本机，不纳入版本控制或外部共享。
- 目录、依赖、打包与边界的机械检查保留在 `scripts`（如 `scripts/verify-boundaries.mjs`、`scripts/verify-dependency-lock.mjs`、`scripts/check-source-scope.py`、`scripts/windows/verify-app-structure.mjs`、`scripts/windows/verify-projects.mjs`）。构建与启动只检查实际输入、依赖边界、身份、权限与交付完整性；不增加文件／导入批准表、固定数量、源码清单快照、关键字扫描禁令或例外白名单；新增拒绝条件须说明保护的实际消费边界。
- 外部脚本由 `scripts/accept-sources.mjs` 按实际声明动作验收，见 [`docs/runtime/source-acceptance.md`](docs/runtime/source-acceptance.md)。

## 文档组织

- 共同契约放在 `docs/common/`，以[共同契约索引](docs/common/README.md)中的对应文件为唯一来源；端侧文档只补充本端事实与差异，不复制规则。总入口为[文档索引](docs/README.md)；移动、删除或调整文档时同步全部索引和引用。
- 文档区分共同契约、现行实现与待实施设计。
