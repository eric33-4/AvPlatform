# AvPlatform

基于 **.NET 10 + Vue 3 + EF Core + SQLite** 的模块化多渠道 Web 聚合平台。

项目通过统一的渠道适配器屏蔽不同上游协议，浏览器只访问平台 API。签名、解密、HTML 解析、缓存、节点切换和媒体代理全部由后端处理。

```text
Vue 3
  ↓ 统一 JSON / 同源媒体地址
ASP.NET Core Web API
  ├─ ChannelAdapter：home / search / detail / play
  ├─ IMemoryCache + SQLite 两级缓存
  ├─ 签名、加解密、HTML 解析和节点切换
  └─ HLS / MP4 同源代理、Range 支持
```

## 技术栈

- 后端：.NET 10、ASP.NET Core Web API
- 前端：Vue 3、TypeScript、Vite、hls.js
- 数据：SQLite、Entity Framework Core
- 缓存：IMemoryCache + SQLite
- 日志：Serilog，包含系统运行日志和接口调用日志
- 部署：Docker Compose、Nginx
- 包管理：NuGet Central Package Management
- 页面：自适应 PC、平板和手机

## 当前支持的渠道

目前已接入 **11 个真实渠道**，均复用同一个 `ChannelsController`、缓存层和媒体代理。

| 渠道 | 接入方式 | 内容类型 | 当前能力 | 状态 |
|---|---|---|---|---|
| YXFM | AES-CBC 加密 API | 音频 | 首页、搜索过滤、详情、免费剧集、HLS 播放 | 已跑通 |
| AIJAV | GDAPI 签名 + AES-256-CBC | 视频 | 首页、真实搜索、详情令牌、HLS 播放 | 已跑通 |
| ONE | 双 MD5 签名 + AES-CBC | 视频 | 首页、搜索、详情、MP4 Range 播放 | 已跑通 |
| TXVLOG | AES-ECB JSON 信封 | 视频 | 首页、广告过滤、搜索、详情、HLS 播放 | 已跑通 |
| MISSAV | HTML 解析 | 视频 | 首页、详情、媒体提取和播放 | 已跑通，见下方限制 |
| XVIDEOS | HTML 解析 | 视频 | 首页、搜索、详情、HLS/MP4 播放 | 已跑通 |
| INSAV | GDAPI AES-256-CBC | 动画 | 免费内容首页、搜索、无状态详情、HLS 播放 | 已跑通，仅展示 `private=0` 内容 |
| SFTV | HTML + Cookie + iframe | 视频 | 首页、搜索、详情、动态 iframe、HLS 播放 | 已跑通 |
| RRYY | HTML + Box 解析 | 视频 | 首页、搜索、详情、Box HLS 解析 | 已跑通 |
| HSCK | HTML 解析 | 视频 | 首页、搜索、详情、HLS 播放 | 已跑通 |
| 91PRON | HTML + 编码脚本 | 视频 | 首页、搜索、详情、签名 MP4 Range 播放 | 已跑通 |

## 计划支持的渠道

渠道按“优先复用现有协议和基础设施”的原则逐步接入，不会一次性复制几十套控制器和缓存逻辑。

### 下一优先级

| 渠道 | 类型 | 计划 |
|---|---|---|
| MW | HTML Parse | 复用 HTML 渠道协议 |
| PDL | HTML Parse | 复用现有 HTML 解析与 HLS 提取基础设施 |
| 91SP | HTML/API 混合 | 复用动态节点、Token 和短视频 HLS 基础设施 |

### 后续候选

`91AV`、`91ZPC`、`HGSP`、`HXSP`、`TTAV`、`JAVDB` 等 API 渠道将根据协议复用程度、节点可用性和维护成本排序接入。

> “计划支持”表示已经进入候选列表，不代表当前版本已经可用。

## 使用 HTTP 本地启动

HTTP 模式适合日常开发和手工调试，不需要 Docker。

### 环境要求

- .NET 10 SDK
- Node.js `22.18+` 或 `24.12+`
- npm

默认端口：

| 服务 | 地址 |
|---|---|
| Vue Web | <http://localhost:5173> |
| Web API | <http://localhost:5200> |
| 健康检查 | <http://localhost:5200/health> |
| OpenAPI | <http://localhost:5200/openapi/v1.json> |

### 1. 启动后端

在项目根目录执行：

```powershell
dotnet run --project src/AvPlatform.WebApi --launch-profile http
```

终端出现以下内容表示 API 已启动：

```text
Now listening on: http://localhost:5200
```

### 2. 启动前端

打开另一个终端：

```powershell
cd src/avplatform.webclient
npm install
npm run dev
```

依赖安装完成后，以后通常只需执行：

```powershell
cd src/avplatform.webclient
npm run dev
```

访问 <http://localhost:5173>。Vite 会自动把 `/api` 和 `/health` 代理到 `http://localhost:5200`。

### 3. 手工验证

浏览器测试顺序：

```text
选择渠道 → 刷新首页 → 搜索 → 打开详情 → 点击播放
```

也可以直接访问 API：

```powershell
curl http://localhost:5200/health
curl http://localhost:5200/api/channels
curl "http://localhost:5200/api/channels/one/home?refresh=true"
curl "http://localhost:5200/api/channels/txvlog/search?q=日本&refresh=true"
```

`refresh=true` 会跳过已有渠道缓存，重新请求上游。

### 4. 本地数据位置

```text
src/AvPlatform.WebApi/data/avplatform.db
src/AvPlatform.WebApi/data/logs/
```

两个终端分别按 `Ctrl+C` 即可停止服务。

## 使用 Docker 启动

### 环境要求

- Docker Desktop，或安装了 Docker Engine 与 Docker Compose v2 的 Linux/WSL 环境
- 确保本机端口 `8080` 和 `5200` 未被占用

### 1. 构建并启动

在项目根目录执行：

```powershell
docker compose up --build -d
```

启动完成后访问：

| 服务 | 地址 |
|---|---|
| Vue Web | <http://localhost:8080> |
| Web API | <http://localhost:5200> |
| 健康检查 | <http://localhost:5200/health> |
| OpenAPI | <http://localhost:5200/openapi/v1.json> |

Web 容器中的 Nginx 会把 `/api` 和 `/health` 转发到 API 容器。

### 2. 查看状态和日志

```powershell
docker compose ps
docker compose logs -f api
docker compose logs -f web
```

### 3. 停止项目

```powershell
docker compose down
```

SQLite、Data Protection 密钥和日志保存在 Docker 命名卷 `avplatform-data` 中，普通 `docker compose down` 不会删除数据。

如需重新构建镜像：

```powershell
docker compose up --build -d
```

## 当前限制

1. MISSAV 的搜索容易触发上游 `403`，当前会明确降级为首页结果过滤，不伪造搜索能力。
2. MISSAV 的媒体 CDN 会检查 TLS 指纹。Windows HTTP 模式已验证；Linux Docker 环境仍可能被 Cloudflare 拒绝。
3. HTML 渠道依赖上游 DOM 和脚本结构，上游改版后需要同步维护解析规则。
4. ONE、TXVLOG 和 AIJAV 依赖动态节点或短期 Token；平台会自动刷新，但中央配置服务仍是外部依赖。
5. 当前主要覆盖首页和搜索第一页，统一分页模型尚未完成。
6. INSAV 大部分站点内容要求登录；平台只展示并代理 `site=3` 中明确标记为 `private=0` 的免费内容。
7. SFTV 播放依赖中央配置中的短期 Cookie；Cookie 失效时会等待下一次中央配置刷新。
8. RRYY 的播放地址依赖 Box 解析节点，详情站和 Box 节点任一失效都会导致播放不可用。

## 项目结构

```text
AvPlatform/
├─ src/
│  ├─ AvPlatform.Domain/          # 领域模型和渠道协议
│  ├─ AvPlatform.Infrastructure/  # EF Core、SQLite 和缓存
│  ├─ AvPlatform.WebApi/          # API、渠道适配器和媒体代理
│  └─ avplatform.webclient/       # Vue 3 客户端
├─ tests/                         # 自动化测试
├─ Directory.Packages.props       # NuGet 中央包管理
└─ docker-compose.yml
```

