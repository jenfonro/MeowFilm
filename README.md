# MeowFilm
<p align="center">
<img src="frontend/public/favicon.svg" alt="MeowFilm" width="120" />
</p>
> MeowFilm 是一个基于 Go + Vue 的影视聚合 Web 应用，提供 UI、账号与配置管理、聚合与播放等核心能力；解析能力由自定义脚本通过 catpawrunner 提供。

<div align="center">

![Go](https://img.shields.io/badge/Go-1.23-00ADD8?logo=go&logoColor=white)
![Vue](https://img.shields.io/badge/Vue-3-42b883?logo=vuedotjs&logoColor=white)
![Vite](https://img.shields.io/badge/Vite-5-646cff?logo=vite&logoColor=white)
![SQLite](https://img.shields.io/badge/SQLite-3-003b57?logo=sqlite&logoColor=white)

</div>

---

## ✨ 功能特性

- 🔌 **插件化站点解析**：通过 catpawrunner `/spider/*` 对接你自己的脚本/规则
- 🔍 **聚合能力**：搜索 / 详情 / 选集 / 播放（基于站点解析结果）
- ❤️ **收藏 + 继续观看**：收藏与播放历史记录
- 🪄 **魔法匹配**：列表清洗正则 + 选集匹配规则（用于生成/匹配集数）
- 🚀 **GoProxy（可选）**：用于部分网盘场景的直链透传/播放优化

## 🗺 目录

- [技术栈](#技术栈)
- [快速开始](#快速开始)
- [部署](#部署)
- [默认账号](#默认账号)
- [环境变量](#环境变量)
- [相关项目](#相关项目)
- [致谢](#致谢)

## 技术栈

| 分类 | 主要依赖 |
| --- | --- |
| 前端 | Vue 3 + Vite（多页面构建） |
| 后端 | Go（`net/http`） |
| 数据库 | SQLite（`go-sqlite3`） |
| 播放 | `artplayer` + `hls.js` + `flv.js` + `shaka-player` |

## 快速开始

> 必须先配好 **catpawrunner 地址** + **站点列表** 才能正常搜索/播放。

推荐直接看：`docs/使用说明.md`（只讲配置与使用）。

## 部署

通常搭配 catpawrunner 一起使用（catpawrunner 负责加载/运行站点脚本）。

### 方式一：本地运行（推荐脚本）

前后端在同一个仓库中：`backend/` 为 Go 后端，`frontend/` 为 Vue 前端。

在仓库根目录执行：

```bash
bash build-all.sh
```

启动：

```bash
MEOWFILM_ADDR=":8080" ./build/meowfilm
```

说明：

- `build-all.sh` 会构建前端并同步到后端内嵌目录 `backend/public/dist`，再构建后端二进制。
- 如果你只想构建后端（二进制），可以先保证 `backend/public/dist/index.html` 存在，然后执行 `./build.sh`。
- 也可以用 `./start.sh` 一键 `build-all.sh` + 启动（默认 `MEOWFILM_DEBUG=1` 且 `MEOWFILM_DATA_DIR=./build/`）。

## 默认账号

首次启动会初始化数据库并创建默认管理员账号：`admin/admin`。

## 环境变量

| 变量 | 说明 | 默认值 |
| --- | --- | --- |
| `MEOWFILM_ADDR` | 监听地址 | `:8080` |
| `MEOWFILM_TRUST_PROXY` | 是否信任反代（`1`=开启） | `0` |
| `MEOWFILM_COOKIE_SECURE` | 登录 Cookie 是否 `Secure`（HTTPS 下建议设为 `1`） | `0` |
| `MEOWFILM_DB_FILE` | 指定 DB 文件路径 | 空 |
| `MEOWFILM_DATA_DIR` | 指定数据目录（DB 默认写入 `data.db`） | 空 |
| `MEOWFILM_DEBUG` | 启用调试接口（如 `/listdebug`、`/searchdebug`） | `0` |
| `ASSET_VERSION` | 静态资源版本号（用于前端资源刷新；未设置时 UI 显示 `beta`，资源使用时间戳） | 空 |

## 相关项目

- catpawrunner：https://github.com/jenfonro/catpawrunner
- GoProxy（可选）：https://github.com/jenfonro/GoProxy

## 致谢

- [MoonTV](https://github.com/666zmy/MoonTV) — 并由此启发
- [ArtPlayer](https://github.com/zhw2590582/ArtPlayer)
- [HLS.js](https://github.com/video-dev/hls.js)
- [flv.js](https://github.com/bilibili/flv.js)
- [Shaka Player](https://github.com/shaka-project/shaka-player)

## 站源观看记录同步

站源通过 `/play` 响应中的 `"watchReport": true` 自行开启。未返回或不为布尔 `true` 时不触发，MeowFilm 不判断站名。

MeowFilm 将已有的播放开始、进度、暂停/停止事件发送给该次播放使用的 Runner、runtime 和站源：

```http
POST /<runtimeId>/spider/<site>/<type>/report
Content-Type: application/json

{"id":"原始 /play 的 id","flag":"原始 /play 的 flag","sessionId":"当前播放会话","positionSeconds":123.5,"durationSeconds":600,"event":"progress"}
```

站源完成同步后返回 `{"ok":true}`。`id` 是实际选中集的原始播放 ID，不是详情页 ID 或媒体直链；脚本可以直接透传，不必维护映射。`event` 为 `started`、`progress`、`paused` 或 `stopped`；位置和时长单位均为秒。

- 网页：首帧以及既有 `/api/playhistory` 进度上报都携带绑定与真实位置；暂停/结束发送最后位置。复用原有 12 秒上报周期，不新开计时器。
- Emby：绑定保留在播放缓存，Progress/Stopped 的 PositionTicks/RunTimeTicks 换算为秒后同步。仅获取 PlaybackInfo 或零进度不触发。
- **开始上报成功后，后续进度仍继续上报**。回退/拖动后的真实位置也继续传递；同一播放保持 sessionId，不把位置替换成整部影片时长。
- 失败不阻断播放或本地历史，后续既有进度事件继续发送。站源自行选择上游账号，例如其 bridge 所用 Cookie。
- CatPawRunner 现有通用路由即可转发 `/report`，无需修改核心。

测试：`cd frontend && node --test tests/*.test.cjs`；后端在构建前端资源后执行 `cd backend && go test ./...`。
