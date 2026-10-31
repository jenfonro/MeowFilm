# meowfilm

原生音乐应用：iPhone（UIKit）与 Windows（WPF / .NET）。

## 构建

```sh
./build-ios.sh            # macOS：iOS，产物位于 build/ios/：未签名 IPA、SHA-256 和构建记录
```

```powershell
.\build-windows.ps1       # Windows：构建结果为 build\windows\meowfilm.exe
```

Windows 默认生成 Release、win-x64 自包含版本；可选参数 `-Configuration Debug`、`-FrameworkDependent`。需要 PATH 上的 .NET SDK（10 或更高）、Node.js、npm 与 Git。日志与暂存文件位于 `tmp/`。

上述工具只用于开发与构建。部署时复制完整的 `build/windows/`，其中包含 .NET、`runtime/node.exe`、脚本核心和 QuickJS WASM；目标电脑不需要安装 Node、npm 或 .NET SDK。不能只复制 `meowfilm.exe`；`-FrameworkDependent` 版本另外需要匹配的 .NET Desktop Runtime。

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

原始 LX 音源与 Cat 脚本共用一套 script-host／QuickJS WebAssembly 核心，当前模块为 `music@0.6.0`／`music.script.v2` 和 `cat@0.1.0`／`cat.script.v1`。Windows 只保留按需启动、命名管道、持久数据根和平台打包；官方原生业务无脚本操作时不启动 Node。LX 特权存储使用请求第二参数 `context.storage`；Cat 只获得安装实例私有卷、有限 Node 风格能力和受控回环服务，不获得宿主任意 `require`、文件、进程或私网访问。见 [公共运行时](docs/runtime/script-runtime.md)。

外部 LX/Cat 脚本验收使用 [实际声明动作与联网报告](docs/runtime/source-acceptance.md)，不会修改或把用户脚本打包进应用。

## 文档

见 [docs/README.md](docs/README.md)。
