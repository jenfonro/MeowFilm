# Windows 原生离线媒体输入

`INativeMediaReadLease` 与 `NativePlayback.OpenReadAsync` 是当前 App 播放已授权本地字节的唯一 Media 入口。租约代表已经由能力 owner 授权的不可变表示，不是路径、URL、下载记录或永久文件权限。

## 租约契约

- `Content` 必须可读、可 seek、不可写，`Length` 必须与流实际长度相同且在 0 至 1 TiB。
- `Revoked` 必须可取消，并在文件授权、缓存授权、账号许可或所属能力失效时立即触发。
- 调用 `OpenReadAsync` 即转移租约独占所有权，包括参数拒绝、预取消、dispatcher 不可用和打开失败。
- 调用方不得再读、关闭或复用已转移的流；Media 在所有路径恰好调用一次 `DisposeAsync`。
- owner、identity 和 title 只用于当前播放身份与显示，不用于打开文件或寻找替代内容。

## 格式识别

Media 只检查前 512 字节并选择固定 demux candidate：

- RIFF/WAVE、AVI；
- MP4／M4A family 的 `ftyp`；
- FLAC、Ogg、Matroska／WebM；
- ID3 或受支持帧头的 MPEG audio／AAC；
- 当前 `MEOWVOD1` 自适应离线归档。

文件名、扩展名、MIME、标题和元数据不能决定 demux。选择后附加 `,none`，禁止 libVLC 回退到 playlist 或自适应网络发现。未知格式明确为 `MEDIA_READ_FORMAT_UNSUPPORTED`。

`MEOWVOD1` 必须完整符合当前 archive version、索引、资源关系、长度、binding 和 payload 校验；另一版本、额外字段、损坏或截断均明确失败并保留原始内容。

## 读取与撤权

- callback 使用固定 64 KiB 缓冲，不复制完整文件，不创建临时媒体文件。
- 每次 read 和 seek 前后都复核撤权、流长度、当前位置和当前表示；改变、截断、异常 seek 分别使用稳定错误。
- 撤权先关闭租约并解除阻塞读取，再由播放器等待原生 callback 和 buffered playback 真实退休。
- demux 读到 EOF 不代表授权可以提前释放；租约保持到当前播放器终止或被替换。
- 取消、撤权、读取失败和释放失败不能被报告为自然播放结束。

缓存与用户下载保持分离。Media 不扫描目录，也不把失败缓存改成网络授权。
