# TV_Server

> 🎬 **TV_Server** 是一个 Go + Vue 的影视聚合 Web 应用。它通过 CatPawOpen 的 `/spider/*` 能力完成站点搜索/详情/播放解析，并提供后台管理页面用于配置与维护。

<div align="center">

![Go](https://img.shields.io/badge/Go-1.23-00ADD8?logo=go&logoColor=white)
![Vue](https://img.shields.io/badge/Vue-3-42b883?logo=vuedotjs&logoColor=white)
![Vite](https://img.shields.io/badge/Vite-5-646cff?logo=vite&logoColor=white)
![SQLite](https://img.shields.io/badge/SQLite-3-003b57?logo=sqlite&logoColor=white)

</div>

---

## ✨ 功能特性

- 🔍 **多站点聚合**：搜索 / 详情 / 选集播放
- ▶️ **播放器**：支持 HLS / FLV / DASH（`hls.js` / `flv.js` / `shaka-player`）
- ❤️ **收藏 + 继续观看**：收藏与播放历史记录
- 🪄 **魔法匹配**：列表清洗正则 + 选集匹配规则（用于生成/匹配集数）
- 🚀 **GoProxy（可选）**：支持直链注册后透传播放（用于部分网盘场景）

## 🗺 目录

- [技术栈](#技术栈)
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

## 部署

通常搭配 CatPawOpen 一起使用。

### 方式一：本地运行（生产）

在 `TV_Server-Frontend/` 目录执行：

```bash
npm install
npm run build
```

在 `TV_Server/` 目录执行：

```bash
go build -o build/tvserver .
./build/tvserver -addr :8080
```

数据库默认写入 `TV_Server/` 目录下的 `data.db`。

## 默认账号

首次启动会初始化数据库并创建默认管理员账号：`admin/admin`。

## 环境变量

| 变量 | 说明 | 默认值 |
| --- | --- | --- |
| `TV_SERVER_ADDR` | TV_Server 监听地址 | `:8080` |
| `TV_SERVER_TRUST_PROXY` | 是否信任反代（`1`=开启） | `0` |
| `TV_SERVER_COOKIE_SECURE` | 登录 Cookie 是否 `Secure`（HTTPS 下建议设为 `1`） | `0` |
| `TV_SERVER_DB_FILE` | 指定 DB 文件路径 | 空 |
| `TV_SERVER_DATA_DIR` | 指定数据目录（DB 默认写入 `data.db`） | 空 |
| `ASSET_VERSION` | 静态资源版本号（用于前端资源刷新；未设置时 UI 显示 `beta`，资源使用时间戳） | 空 |

## 相关项目

- CatPawOpen：https://github.com/jenfonro/CatPawOpen
- GoProxy（可选）：https://github.com/jenfonro/GoProxy

## 致谢

- [MoonTV](https://github.com/666zmy/MoonTV) — 并由此启发
- [ArtPlayer](https://github.com/zhw2590582/ArtPlayer)
- [HLS.js](https://github.com/video-dev/hls.js)
- [flv.js](https://github.com/bilibili/flv.js)
- [Shaka Player](https://github.com/shaka-project/shaka-player)
