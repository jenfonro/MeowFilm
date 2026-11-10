# meowfilm

原生音乐应用：iPhone（UIKit）与 Windows（WPF / .NET）。

## 构建

```sh
./build-ios.sh            # macOS：iOS，产物位于 build/ios/：未签名 IPA、SHA-256 和构建记录
```

```powershell
.\build-windows.ps1       # Windows：构建结果为 build\windows\meowfilm.exe
.\build-windows.ps1 -Installer # 同时生成 build\windows-installer\meowfilm-setup.exe
```

Windows 默认生成 Release、win-x64 自包含版本；可选参数 `-Configuration Debug`、`-FrameworkDependent`。需要 PATH 上的 .NET SDK（10 或更高）、Node.js、npm、Git、CMake 3.20+ 与 Windows C 编译器。`-Installer` 另外需要 Inno Setup 6，且只接受 Release 自包含构建。日志与暂存文件位于 `tmp/`。

Windows 安装包默认安装到当前用户的 `%LOCALAPPDATA%\Programs\meowfilm`，提供开始菜单、可选桌面快捷方式和系统卸载入口。卸载时可选择保留或删除账号、设置、来源及缓存等用户数据，默认保留；静默卸载保留数据。安装、重复安装及卸载前须退出 meowfilm。详细行为见 [Windows 打包说明](docs/windows/runtime-packaging.md)。

上述工具只用于开发与构建。Windows 部署需要完整的 `build/windows/`，不能只复制 `meowfilm.exe`；`-FrameworkDependent` 版本另外需要匹配的 .NET Desktop Runtime。通用运行器使用原生引擎及 `runtime.http.v1`；iOS 通过内置 NodeMobile／MeowScriptHost framework 接入，构建与验证边界见[iOS 运行底座](docs/ios/runtime-preflight.md)。Windows 启动、HTTP 消费者和平台包装已接入，构建工具链见[Windows 打包说明](docs/windows/runtime-packaging.md)；公共或模拟器通过不等于所有平台可发布。

客户端影视规则另由 `native/JavaScript` 构建原生 `meowfilm_js.dll` 随 App 输出，与通用脚本宿主引擎无关；目标电脑不需要 CMake 或编译器。

## 测试

```sh
python3 -I -B scripts/check-source-scope.py
python3 -I -B -m unittest discover -s scripts/tests -p "test_<area>.py" -v
bash scripts/test-native.sh test --filter <pattern>
```

```powershell
.\scripts\windows\bootstrap.ps1
.\scripts\windows\restore.ps1
.\scripts\windows\test-node.ps1 -Path tests/<area>.test.mjs
.\scripts\windows\test.ps1 -Group <names>
```

Windows 上运行 Python 脚本时将 `python3` 换为 `python -X utf8`。iOS 原生编译与测试需要 macOS 和 Xcode（参考环境 Xcode 26.1.1）。

全量检查显式使用 `bash scripts/test-native.sh all`，或 Windows 测试入口的 `-All`。

## 脚本运行时

通用运行器由一个 Node 主控管理：LX 和 Reader／CatVod 来源分别使用逐实例原生线程与 QuickJS Runtime；CatNode 使用逐实例 Node Worker，直接执行 Node 能力，不再嵌套 QuickJS／WASM。源码一次完整 HTTP 上传，普通调用使用 JSON，请求与结果不走业务分片。当前模块为 `music@0.8.0`／`music.script.v3`、`cat@0.2.0`／`cat.script.v2`、`source@0.2.0`／`source.runtime.v2`。见 [公共运行时](docs/runtime/script-runtime.md)。

已同步的 Windows 业务采用随 App 启动公共宿主、恢复启用猫源的生命周期，官方原生业务不以 Node 为前置条件；Windows 使用私密管道启动握手和上述通用 HTTP 契约，不再通过管道发送业务 RPC。客户端魔法规则独立使用原生 QuickJS-NG，见[影视设置](docs/windows/film-settings.md)。

通用运行器构建使用 Node 24.21.0、C/C++ 编译工具、Perl 和 make（Windows 为对应原生工具链）：

```sh
npm ci --ignore-scripts
npm run build:runtime
node scripts/package-runtime.mjs --product aggregate --profile common-native --output /absolute/new/payload
```

构建缓存沿用 `tmp/build/runtime-native` 和 node-gyp 默认位置；打包只复制已验证的原生产物及依赖，不下载、不安装或修锁。该命令不构建平台 App。

LX 特权存储使用请求第二参数 `context.storage`。CatNode 保留有限 Node 风格能力、受控 HTTP 服务、direct Spider 和安装实例私有卷，不获得宿主任意 `require`、文件或进程访问；网络不按公网、私网、回环或局域网域名分类拒绝目标。source 的 CatVod 只接受独立 Spider 输入，不能代替 CatNode。Reader 的 Legado、异次元和轻悦时光规则通过[来源 capability ABI](docs/runtime/source-capability-abi.md)请求平台能力。Node 无关仅指 `source-runtime` 规则包，不表示 Node 兼容层或当前 Windows 承载能脱离 Node。见[公共运行时](docs/runtime/script-runtime.md)。

外部 LX、CatNode 和 Reader／CatVod 来源验收使用[实际声明动作与联网报告](docs/runtime/source-acceptance.md)，不会修改或把用户脚本打包进应用；底层来源支持不表示已接入阅读 UI 或其他平台客户端。

## 文档

见 [docs/README.md](docs/README.md)。
