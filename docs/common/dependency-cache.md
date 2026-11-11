# 构建依赖缓存

开发目录以 macOS／Linux 的 `~/Developer`、Windows 的 `E:/Developer` 为根。共享缓存由依赖准备脚本直接创建；不需要把每个工作副本的 `.cache` 或 `tmp/build` 整体移出。

| 内容 | 默认位置 | 复用方式 |
| --- | --- | --- |
| FFmpeg 及 Apple 视频来源归档 | `Developer/cache/ffmpeg/downloads/<sha256>.tar.gz` | 核对锁定下载哈希；相同归档跨副本复用 |
| Node Mobile、QuickJS、OpenSSL、zlib、Node headers、Windows 视频其他依赖归档 | `Developer/cache/downloads/<sha256>` | 完整下载并核对哈希后原子发布 |
| Apple FFmpeg 静态库与头文件 | `Developer/cache/ffmpeg/artifacts/<fingerprint>/` | 同配方加锁构建、验证并发布，等待者复查后复用 |
| node-gyp 头文件缓存 | `Developer/cache/node-gyp` | 使用 node-gyp 原生版本布局；已有有效 `devdir` 配置优先 |

node-gyp 的 `npm_package_config_node_gyp_devdir` 优先于 `npm_config_devdir`；安装、校验和后续构建使用同一有效目录。npm 与 NuGet 继续使用工具自身的持久缓存设置，构建脚本不覆盖用户全局配置。

构建回执要求工作区相对路径的下载包保留私有副本，不能用外部符号链接代替。补丁后的源码、OpenSSL／FFmpeg 编译目录、项目安装状态、Node addon、平台 framework、XCFramework 和 App 包仍归当前工作副本。共享下载使用唯一暂存文件并排他发布；首次并发下载可以各自完成，但不会覆盖已有完整归档。Apple 视频归档另使用按内容身份的文件锁，避免重复下载。

Apple FFmpeg 成品身份包含锁定来源、补丁、脚本配方、平台／架构、编译选项、SDK 与编译器身份；工作副本路径、分支和提交号不作为身份。命中后核对库和头文件的完整清单与哈希，复制到当前副本 `.cache/ios-video-kernel/install/<target>`，跳过 `configure`／`make`。打包仍从本副本生成 `native/VideoKernel/.cache/Artifacts`。不同目标、配置或工具链独立缓存；Windows 视频插件及通用 Node 原生宿主目前只共享下载和原生工具缓存，编译产物保留各副本的正常增量复用。

失败或中断不发布完整成品；进程退出释放 Apple 视频文件锁。共享文件损坏时报告错误，不把损坏结果当命中或静默覆盖。旧副本已有缓存不自动扫描、迁移或清理；旧 FFmpeg 回执不符合新配方时会构建一次并发布新共享成品。

脚本行为回归入口：

```sh
python3 -I -B -m unittest discover -s native/VideoKernel/Tests/Tools -p 'test_*.py'
python3 -I -B -m unittest discover -s scripts/tests -p 'test_prepare_ios_runtime.py'
node --test tests/dependency-download.test.mjs
```
