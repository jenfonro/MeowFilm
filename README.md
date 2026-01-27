# MeowFilm
<p align="center">
<img src="public/dist/favicon.svg" alt="MeowFilm" width="120" />
</p>
> MeowFilm 是一个 Go + Vue 的影视聚合 Web 应用。该应用只提供 UI、账号与配置管理、播放与聚合能力；搭配 CatPawOpen 加载自定义脚本使用。
> MeowFilm 是一个基于 Go + Vue 的影视聚合 Web 应用，提供 UI、账号与配置管理、聚合与播放等核心能力；解析能力由 CatPawOpen 提供，可加载你的自定义脚本。

<div align="center">

![Go](https://img.shields.io/badge/Go-1.23-00ADD8?logo=go&logoColor=white)
![Vue](https://img.shields.io/badge/Vue-3-42b883?logo=vuedotjs&logoColor=white)
![Vite](https://img.shields.io/badge/Vite-5-646cff?logo=vite&logoColor=white)
![SQLite](https://img.shields.io/badge/SQLite-3-003b57?logo=sqlite&logoColor=white)

</div>

---

## ✨ 功能特性

- 🔌 **插件化站点解析**：通过 CatPawOpen `/spider/*` 对接你自己的脚本/规则
- 🔍 **聚合能力**：搜索 / 详情 / 选集 / 播放（基于站点解析结果）
- ❤️ **收藏 + 继续观看**：收藏与播放历史记录
- 🪄 **魔法匹配**：列表清洗正则 + 选集匹配规则（用于生成/匹配集数）
- 🚀 **GoProxy（可选）**：用于部分网盘场景的直链透传/播放优化

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

通常搭配 CatPawOpen 一起使用（CatPawOpen 负责加载/运行站点脚本）。

### 方式一：本地运行（生产）

在前端目录执行：

```bash
npm ci
npm run build
```

在后端目录执行：

```bash
go build -o build/meowfilm .
./build/meowfilm -addr :8080
```

数据库默认写入当前目录的 `data.db`（或通过环境变量指定）。

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
