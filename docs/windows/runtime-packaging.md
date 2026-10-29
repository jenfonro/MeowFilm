# Windows App 脚本载荷打包

Windows meowfilm App 的脚本载荷由以下两个阶段生成。打包参数 --product aggregate 是保留的 wire 身份；其他值明确拒绝。普通原生业务不在 Node 中运行。

## 两个构建阶段

- scripts/package-runtime.mjs：离线解析已安装且锁定的必要依赖，复制传输 bootstrap、runtime-bridge、script-host 和必要声明；仅支持 windows-native，返回 platform-pending，不能把它当作可执行载荷。
- scripts/windows/package-runtime.mjs：补齐 Windows 命名管道 bootstrap、entries/aggregate.mjs 和 modules/music-script.mjs，核对静态导入重定位、实际文件清单与所有哈希，完成后才返回 complete。

完整 App 构建从根 build-windows.ps1 进入；它调用 scripts/windows/build.ps1，由同一个构建锁保护依赖准备、打包和 `build/windows` 覆盖安装。每次构建在 tmp/publish 重新生成暂存目录，并加入 `scripts/windows/node-runtime.json` 锁定的 Node 运行时与其许可；第三方许可见 `../licenses.md`。

## 输入闭包

载荷 module inventory 只能为 music@0.4.0；清单字段 product 保持 aggregate。

源码根由脚本自身位置解析。API root/output 都是显式本机绝对路径；output 是构建本次新建的暂存子目录，不接受 UNC、源树覆盖或有链接的祖先。

workspace 必须在根 package/lock 中声明并安装为准确指向本仓库的 link，输出成为普通目录。只有选定两包及其实际运行/peer依赖可以入包；不扫描未入闭包的开发依赖，不借用全局、NODE_PATH 或其他工作区。

## 保留的安全约束

- 每个依赖位置对应根锁记录；名称、精确安装版本、声明、peer metadata、semver、registry HTTPS 来源、SRI 语法分别核对，不能悄悄修锁或绕过缺失的最近依赖去找祖先。
- 支持 scoped、nested、hoisted、peer、optional 与合法依赖循环；未安装的可选边明确记录。安装了的可选依赖不能绕过锁/版本/原生二进制检查。
- 工作区 files 规则、公开 exports/main、真实文件存在性必须一致。Registry 已安装目录不重做 npm-pack 的负向规则。测试、夹具、缓存及隐私文件不打包，许可/NOTICE/来源声明不因 files 裁剪而丢失。
- 拒绝路径穿越、大小写/Unicode 冲突、额外链接/重解析、原生二进制和非法公开文件目标。不会执行待打包代码、安装器或包脚本。
- 新文件独占创建、输入读取与输出身份复核、完整 byte/hash inventory；失败时保留 failed 清单，暂存目录由下次构建重新生成。runtime-manifest.json 不自哈希。

当前生产包没有 registry 运行依赖。文件 hash 证明构建输入/输出对应，不代替上游 tarball 验证、发行签名或许可授权。
