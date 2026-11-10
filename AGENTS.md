# AGENTS.md

meowfilm 的 iPhone 与 Windows 原生客户端：
- iOS：UIKit 负责应用、窗口、导航、呈现与手势，Swift Package 提供原生业务、平台、账号、播放和持久化能力。公共 Node 运行器随 App 启动恢复启用猫源，并持有影视智能播放、业务数据缓存与本地播放资料；客户端影视规则静态链接原生 QuickJS。
- Windows：WPF / .NET 原生桌面应用；公共 Node 运行器随 App 启动，影视使用同一智能播放与数据契约，音乐脚本仅在启用、调用或显式检测时执行。既有原生音乐、本地收藏和规则管理不因新增影视业务而依赖运行器；客户端影视规则内嵌原生 QuickJS。

## 命令

### iOS

```sh
python3 -I -B scripts/generate-ios-project.py         # 生成 apps/ios/Meowfilm.xcodeproj
python3 -I -B scripts/prepare-ios-runtime.py          # Node 24 + npm ci：准备内嵌运行器构建输入
python3 -I -B scripts/prepare-ios-video-kernel.py --target ios-arm64 # 锁定源码准备视频内核；测试入口选择对应 Mac/模拟器目标
python3 -I -B scripts/check-source-scope.py           # 源码、依赖与工程成员检查
python3 -I -B -m unittest discover -s scripts/tests -p "test_<area>.py" -v
bash scripts/test-native.sh test --filter <pattern> # macOS：受影响的 Swift 测试
bash scripts/test-native.sh all                    # 显式完整代码检查
python3 -I -B scripts/test-ui-platform.py --simulator <UDID> --result tmp/test-results/<name>.xcresult [--test <name>]
./build-ios.sh                                        # macOS：构建并替换 build/ios/
```

- Windows 将 `python3` 换为 `python -X utf8`。
- 需要 macOS、Xcode、Node 24 与 `npm ci --ignore-scripts`；参考环境为 Xcode 26.1.1。iOS 部署目标 17.0，macOS 仅用于原生单测执行。

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
- 原生规则引擎需要 CMake 3.20+ 和 Windows C 编译器（MSVC，或通过标准 CMake 环境选择的工具链）；源码随仓库锁定，普通增量构建自动生成 DLL，不下载依赖或使用预编译来源不明的二进制。
- 通用脚本宿主的 Node addon 还需要 Python、Perl、NASM 与 MSVC x64 开发环境（`cl`／`nmake` 在 PATH）；首次构建按 `scripts/script-host-native.json` 下载校验依赖，后续复用默认缓存。
- 随 App 发布的 Node 运行时由 `scripts/windows/node-runtime.json` 锁定，并与 `LICENSES/node-LICENSE.txt` 对应；构建时 PATH 上的 `node.exe` 须与其一致。
- 默认完整发布目录自带 Node、引擎依赖和 .NET，不要求目标电脑安装开发工具；`-FrameworkDependent` 另需系统 .NET Desktop Runtime。

## 目录

- `apps/ios/` — 唯一 iOS App 工程
  - `Application` — 应用与 scene 生命周期、窗口、产品身份
  - `Composition` — 资源、服务、播放、账号和同步装配
  - `Navigation` — 根容器、页面导航与呈现路由
  - `Presentation` — 主页面外壳、页头、底部导航和播放器停靠区
  - `UITests` — 共享 UIKit 组件测试
  - `RuntimeTests` — 真实 App 宿主的内嵌运行器与页面生命周期测试
  - `runtime` — iOS 受信启动／生命周期交接与公共 HTTP 运行器薄 entry
- `native/MeowKit/` — iOS Swift Package
  - `MeowContracts`、`MeowJSON`、`MeowStorage`、`MeowAccounts`、`MeowPlayback`、`MeowUI`、`MeowRuntime` — 共享能力
  - `MeowMusicFeature`、`MeowMusicPlatform`、`MeowMusicNativeService` — 音乐界面、平台和原生业务
  - `MeowFilmFeature` — 影视数据源、首页、详情、收藏、规则设置与来源管理
- `native/VideoKernel/` — Apple 平台视频解码与 sample-buffer 输出；锁定 FFmpeg 源码及本地适配，不拥有业务或系统媒体会话
- `apps/windows/app/` — Windows 应用入口与装配
- `apps/windows/src/` — Windows 原生库
  - `MeowFilm.Core` — 基础契约
  - `MeowFilm.Platform` — 本地文档存储与敏感信息保护、私密启动管道、HTTP 子进程会话、文件授权
  - `MeowFilm.Desktop` — 窗口、导航、设置入口、主题、通知
  - `MeowFilm.Media` — 媒体播放与自适应流
  - `MeowFilm.SystemMedia` — 系统媒体传输控件
  - `MeowFilm.AppHost` — 启动、注入与关闭
  - `MeowFilm.Capabilities` — 按职责组织的业务能力（Accounts、Platforms、Catalog、Search、Recommendations、Library、Sync、Playback、PlaybackCache、Lyrics、Scripting、Feedback、Settings、Presentation、Composition）
- `apps/windows/tests/` — Windows 原生测试
- `apps/runtime/` — 通用 HTTP 运行器装配与入口；`createRuntime` 默认管理 music.script.v3／music@0.8.0、cat.script.v2／cat@0.2.0、source.runtime.v2／source@0.2.0 三个模块。iOS 与 Windows aggregate 显式加装 pan、tmdb、douban、film 共七模块，保留各自平台受信启动，不保留旧运行器协议回退；数据、媒体与会话契约见 `docs/runtime/metadata-data.md`、`docs/runtime/film-playback.md`。
- `apps/windows/runtime/` — 仅 Windows 私密启动／存活管道 `bootstrap.mjs` 与薄 `entries/aggregate.mjs`，从公共目录导入业务模块，不保留 module 副本
- `packages/script-host/` — 统一实例生命周期；LX 与 portable 来源使用共享原生绑定、逐实例线程／QuickJS Runtime，CatNode 使用逐实例 Node Worker 与原生 Node 能力。
- `packages/source-runtime/` — Node 无关的 Reader 规则、CatVod Spider adapter 与来源 capability ABI；不替代 CatNode 的输入契约或 Node 兼容层
- `packages/runtime-bridge/` — runtime.http.v1 完整 HTTP 请求／响应、取消与连接生命周期
- `packages/pan-runtime/` — 内置网盘账号、分享与取址能力；同进程异步 I/O，不创建脚本执行环境
- `packages/film-playback/` — 影视智能会话、唯一业务数据缓存、本地历史/人工匹配/屏蔽/偏好，以及受控二进制媒体流；不复制原生收藏或图片缓存
- `packages/film-search/` — 双端客户端共享的影视搜索整合规则资源，由各自原生 QuickJS 执行；不在通用运行器聚合搜索
- `scripts/` — iOS 工程生成、结构检查、测试、构建打包与发布，`scripts/tests/` 为脚本测试；Node 共用工具
- `scripts/windows/` — Windows 构建、测试、打包与结构检查
- `tests/` — Node 测试
- `docs/` — 架构、模块、契约与发布说明，索引见 `docs/README.md`
- `LICENSES/` — 全部第三方许可与来源声明（`NOTICE.md`）；Windows 构建时复制到 `build/windows/licenses`
- `build/ios/`、`build/windows/` — 各平台最新一份产物，每次构建成功后整体覆盖；不入 Git
- `tmp/` — 日志、测试结果、构建暂存与任务工作记录（`tmp/working/`），可随时整体删除；不入 Git
- 许可文件只放在 `LICENSES/`，每种许可正文一份；改编新的上游代码时在 `NOTICE.md` 补充来源，增删时同步使用该组件的平台打包清单（Windows 为 `scripts/windows/package-licenses.mjs`，iOS 为工程资源及构建输入策略）。

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
- 运行器拥有脚本安装、源码、网络来源地址及私有数据；客户端可持有供原生界面读取的列表／检测快照，不形成第二安装事实源或反向脚本存储 owner。脚本执行内存不做快照持久化；未来缓存需求不预建实现。
- 公共脚本层不依赖 Windows 源码或启动／管道实现。LX 请求特权存储绑定显式 `context.storage`，不以 ambient 异步上下文授予全局／startup／background 权限。CatNode 的公开网络不使用域名授权名单，也不按公网、私网或链路本地分类拒绝目标；实例已登记回环端口仍映射到实际端点，`socketPath`／fd 属于文件／IPC 隔离继续拒绝；文件只映射安装实例私有卷。source-runtime 规则包不依赖 Node，平台能力通过[来源 capability ABI](docs/runtime/source-capability-abi.md)注入；各输入契约的能力边界不得互相冒充，Node 兼容层不因此具备无 Node 承载能力。
- 依赖显式且单向，不形成循环依赖或隐式初始化顺序。不用全局事件总线、服务定位器、无边界单例或跨模块直接读写绕过契约。
- 新增能力前先检查现有实现。公共抽取以通用语义、稳定契约和实际使用为依据；共享层提供机制，业务层决定策略，不给共享实现堆叠业务分支。

## 状态与生命周期

- 每份可变状态有明确的事实来源、作用域和更新所有者；可推导状态优先计算，不重复存储。
- 跨页面业务状态和长期资源由稳定所有者管理，不依赖视图重建或显示切换；页面可见性与资源活跃状态分别判断。
- 异步操作明确取消、失效和完成语义，旧结果不得覆盖新状态；完成由实际执行者确认，不靠猜测等待时间。

## 实现

- 满足需求及必要的可控、可观测、可验证和生命周期要求后，综合运行性能、实现复杂度和长期维护成本选择方案；单处性能差异较小也计入，避免各处开销叠加。不增加未要求的功能、框架、配置或扩展点。
- 技术栈、框架与实现方式按具体职责逐项选择，同样综合运行性能、可控性、实现复杂度和长期维护成本。
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
- 通用运行器原生引擎仅按 `scripts/script-host-native.json` 的精确来源、版本、ABI／架构、补丁、根锁、构建输入及文件／许可哈希放行；用 `node scripts/quickjs/build-native.mjs` 构建，再由 `scripts/package-runtime.mjs --product aggregate --profile common-native --output <absolute-directory>` 复制受信产物。无 WASM／asm.js 回退；其他 native/WASM 仍拒绝，打包不安装／修锁。平台包装尚未迁移时，不将通用运行器通过表述为平台 App 通过。
- 客户端影视规则引擎独立按 `scripts/quickjs-native.json` 锁定源码与许可，通过 `native/JavaScript` 薄 C ABI 构建；不属于 Node 脚本宿主闭包，也不放宽通用打包的二进制策略。
- iOS 脚本引擎使用 App 内置 `MeowScriptHost.framework`，复用通用原生核心与固定依赖，构建目标明确区分 device／simulator。原生载体随 App 签名；构建回执、内置身份和 JS／SDK 清单不能用签名前 Mach-O 全文件 hash 代替安装后签名边界，也不能据此放宽桌面 `.node` 校验。
- 长期测试验证现行行为与契约；删除／重构后的旧实现及引用清理检查只做当次验证，通过后移除，不堆入长期测试。功能、安全、取消与资源释放等有效回归保留。
- 编译成功、测试替身和真实业务验证分别记录，不互相冒充；不为刷绿删除有效断言、隐藏失败或用空测试集代替验证。
- 外部脚本由 `scripts/accept-sources.mjs` 按实际声明动作验收；未归因、宿主缺失能力、资源超限或清理失败不能因初始化成功而放行。报告只保留脱敏结构和固定分类，见 `docs/runtime/source-acceptance.md`。
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
