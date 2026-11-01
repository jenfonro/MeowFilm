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

公共来源运行时使用 QuickJS WebAssembly 隔离 guest，并由协议无关 capability ABI 提供 HTTP、Cookie、存储、编码、加解密、Web 交互和虚拟路由。当前模块为 `music@0.5.0`／`music.script.v2` 与 `source@0.1.0`／`source.runtime.v1`；支持 LX、Legado、异次元旧格式导入、轻悦时光 HTML 和 CatVod JS。

Windows 只负责随包 Node 的按需启动、命名管道和平台打包；目标电脑不需要安装 Node。公共 Reader/Cat 核心不依赖 Node、JVM 或 Windows API，后续平台只需实现相同 capability ABI。网络只校验 HTTP(S) 语法和资源边界，不声称阻断本机或私网。见 [公共运行时](docs/runtime/script-runtime.md)。

外部来源验收使用 [实际声明动作与联网报告](docs/runtime/source-acceptance.md)，不会修改或把用户来源打包进应用。

## 文档

见 [docs/README.md](docs/README.md)。
