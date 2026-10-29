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

## 文档

见 [docs/README.md](docs/README.md)。