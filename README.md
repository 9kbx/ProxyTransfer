# ProxyTransfer

ProxyTransfer 用来把带账号密码的 HTTP 或 SOCKS5 代理转成可直接交付给客户端使用的本地转发代理。

典型场景是：

- 上游代理是 `http://user:pass@host:port` 或 `socks5://user:pass@host:port`
- 下游客户的软件不能二次开发
- 下游客户的软件只支持无账号密码的 HTTP 代理或 SOCKS5 代理地址输入

这个仓库目前包含 8 个主要项目/目录：

- [ProxyTransfer.Tunnel](ProxyTransfer.Tunnel)：可复用类库，提供 HTTP/SOCKS5 转发能力
- [ProxyTransfer.TunnelHost](ProxyTransfer.TunnelHost)：独立转发引擎进程，持有监听端口，支持持久化与重启自动恢复
- [ProxyTransfer.Api](ProxyTransfer.Api)：Minimal API 后端，通过 HTTP 调用 TunnelHost 管理代理生命周期
- [ProxyTransfer.Web](ProxyTransfer.Web)：Vue 3 管理台，经典代理 + 固定入口池两种模式
- [ProxyTransfer.Tunnel.Test](ProxyTransfer.Tunnel.Test)：下游代理连通性测试工具
- [ProxyTransfer.Demo](ProxyTransfer.Demo)：控制台示例工程（HttpClient、Puppeteer、动态代理池）
- [ProxyTransfer.BrowserPy](ProxyTransfer.BrowserPy)：Python 浏览器接入示例

## 环境要求

- .NET SDK 10.0+
- Node.js 24+
- npm 11+

```bash
dotnet build ProxyTransfer.sln
cd ProxyTransfer.Web && npm install && npm run build
```

## 快速启动

### 1. 启动 TunnelHost（转发引擎）

```bash
cd ProxyTransfer.TunnelHost
dotnet run
```

默认监听 `http://0.0.0.0:5081`，配置详见 [ProxyTransfer.TunnelHost/README.md](ProxyTransfer.TunnelHost/README.md)。

### 2. 启动后端 API

```bash
cd ProxyTransfer.Api
dotnet run
```

默认监听 `http://0.0.0.0:5080`，配置详见 [ProxyTransfer.Api/README.md](ProxyTransfer.Api/README.md)。

### 3. 启动前端管理台

```bash
cd ProxyTransfer.Web
npm install
npm run dev
```

默认开发地址 `http://localhost:5173`，Vite 自动将 `/api` 请求代理到后端。

### 4. 测试下游代理

在管理台导入并启动代理后，复制运行中的代理地址到测试项目：

```bash
cd ProxyTransfer.Tunnel.Test
dotnet run
```

详细测试模式（固定入口切换观察、多线程测试等）见 [ProxyTransfer.Tunnel.Test/README.md](ProxyTransfer.Tunnel.Test/README.md)。

## 功能概览

### 经典代理模式
- 批量导入代理（每行一个 HTTP/SOCKS5 上游）
- 手动添加单个代理，可指定固定端口
- 上下游协议独立选择（HTTP→HTTP、HTTP→SOCKS5、SOCKS5→HTTP、SOCKS5→SOCKS5）
- 按批次停止、单代理启停、连通性测试

### 固定入口池模式
- 将一批上游汇聚到池中，创建一个固定下游入口给客户端
- 三种上游选择策略：粘性会话（sticky）、轮询（round-robin）、最少失败优先（least-failures）
- 上游故障自动切换，后台定期探活

## 部署

### 架构概览

```mermaid
flowchart LR
    subgraph Dev["开发机"]
        ENV[".env<br/>服务器IP/用户/路径"]
        PUBWEB["publish_web.sh"]
        PUBTUN["publish_tunnel.sh"]
    end
    subgraph Server["服务器"]
        CFG["/apps/proxytransfer/config/<br/>web.runtime.env / tunnel.runtime.env<br/>*.appsettings.Production.json"]
        WEBC["proxytransfer-web 容器<br/>:15080→8080"]
        TUNC["proxytransfer-tunnel 容器<br/>:15081→5081"]
    end
    ENV --> PUBWEB -->|"SSH + rsync"| WEBC
    ENV --> PUBTUN -->|"SSH + rsync"| TUNC
    WEBC <-->|"容器名通信"| TUNC
```

### 文件角色速查

| 文件 | 位置 | 用途 |
|---|---|---|
| `scripts/.env` | **开发机** ⚠️不提交 Git | 发布脚本核心配置（服务器IP、用户、部署路径、容器名） |
| `scripts/.env.example` | **开发机** | `.env` 模板，`cp .env.example .env` 后填入真实值 |
| `scripts/publish_web.sh` | **开发机** | 构建前端+API → rsync到服务器 → 远端 docker build+run |
| `scripts/publish_tunnel.sh` | **开发机** | 构建TunnelHost → rsync到服务器 → 远端 docker build+run |
| `scripts/restart_web.sh` | **服务器** | 由publish自动同步；运维可在服务器手动重建+重启Web容器 |
| `scripts/restart_tunnel.sh` | **服务器** | 由publish自动同步；运维可在服务器手动重建+重启Tunnel容器 |
| `scripts/remote.runtime.env.example` | **模板** | Web容器运行时配置模板，部署到服务器后改名为 `web.runtime.env` |
| `scripts/remote.tunnel.runtime.env.example` | **模板** | Tunnel容器运行时配置模板，部署到服务器后改名为 `tunnel.runtime.env` |
| `ProxyTransfer.Api/Dockerfile` | 随发布同步 | API服务镜像定义 |
| `ProxyTransfer.TunnelHost/Dockerfile` | 随发布同步 | TunnelHost镜像定义 |

### 服务器端目录结构

首次部署前需在服务器上创建：

```text
/apps/proxytransfer/
├── config/                           # 手动创建，含运行时配置
│   ├── web.runtime.env               # 从 remote.runtime.env.example 复制修改
│   ├── tunnel.runtime.env            # 从 remote.tunnel.runtime.env.example 复制修改
│   ├── web.appsettings.Production.json
│   └── tunnel.appsettings.Production.json
├── web/
│   ├── app/                          # publish_web.sh 自动同步到此
│   │   ├── restart_web.sh            # 服务器端重启脚本（自动同步）
│   │   └── （dotnet publish 产物 + wwwroot）
│   └── data/                         # 容器内 /app/App_Data 映射
└── tunnel/
    ├── app/                          # publish_tunnel.sh 自动同步到此
    │   ├── restart_tunnel.sh         # 服务器端重启脚本（自动同步）
    │   └── （dotnet publish 产物）
    └── data/                         # 容器内 /app/App_Data 映射
```

### 首次服务器初始化

推荐 **在本地构建完整包** 的方式 —— 服务器端不需要安装 .NET SDK 和 Node.js，只需 Docker。

#### 步骤 1：本地构建并打包

```bash
cd ProxyTransfer

# 构建前端
cd ProxyTransfer.Web && npm install && npm run build && cd ..

# 发布 API（含前端 wwwroot）
dotnet publish ProxyTransfer.Api -c Release -o .artifacts/publish/web
cp -r ProxyTransfer.Web/dist .artifacts/publish/web/wwwroot
cp ProxyTransfer.Api/Dockerfile .artifacts/publish/web/
cp scripts/restart_web.sh .artifacts/publish/web/
chmod +x .artifacts/publish/web/restart_web.sh

# 发布 TunnelHost
dotnet publish ProxyTransfer.TunnelHost -c Release -o .artifacts/publish/tunnel
cp ProxyTransfer.TunnelHost/Dockerfile .artifacts/publish/tunnel/
cp scripts/restart_tunnel.sh .artifacts/publish/tunnel/
chmod +x .artifacts/publish/tunnel/restart_tunnel.sh

# 打包
tar -czf proxytransfer-deploy.tar.gz -C .artifacts/publish web tunnel
```

#### 步骤 2：上传并解压

```bash
scp proxytransfer-deploy.tar.gz root@你的服务器IP:/tmp/
ssh root@你的服务器IP

mkdir -p /apps/proxytransfer/web/app /apps/proxytransfer/web/data \
         /apps/proxytransfer/tunnel/app /apps/proxytransfer/tunnel/data \
         /apps/proxytransfer/config

tar -xzf /tmp/proxytransfer-deploy.tar.gz -C /apps/proxytransfer/
```

#### 步骤 3：创建运行时配置文件

**`web.runtime.env`**（参考 `scripts/remote.runtime.env.example`）：

```bash
cat > /apps/proxytransfer/config/web.runtime.env << 'EOF'
DOCKER_BIN=docker
CONTAINER_RESTART=unless-stopped
CONTAINER_PORTS=15080:8080|64000-65000:64000-65000
CONTAINER_VOLUMES=/apps/proxytransfer/web/data:/app/App_Data|/apps/proxytransfer/config/web.appsettings.Production.json:/app/appsettings.Production.json:ro
CONTAINER_ENVS=ASPNETCORE_ENVIRONMENT=Production|TunnelHost__ApiUrl=http://0.0.0.0:8080|TunnelHost__ManagementUrl=http://proxytransfer-tunnel:5081|TunnelHost__ManagementApiKey=你的安全Key
CONTAINER_NETWORK=proxytransfer
EOF
```

> `TunnelHost__ManagementUrl` 必须用容器名 `proxytransfer-tunnel:5081`，同一 Docker 网络内通过容器名通信。

**`tunnel.runtime.env`**（参考 `scripts/remote.tunnel.runtime.env.example`）：

```bash
cat > /apps/proxytransfer/config/tunnel.runtime.env << 'EOF'
DOCKER_BIN=docker
CONTAINER_RESTART=unless-stopped
CONTAINER_PORTS=15081:5081|64000-65000:64000-65000
CONTAINER_VOLUMES=/apps/proxytransfer/tunnel/data:/app/App_Data|/apps/proxytransfer/config/tunnel.appsettings.Production.json:/app/appsettings.Production.json:ro
CONTAINER_ENVS=ASPNETCORE_ENVIRONMENT=Production|TunnelHost__ManagementUrl=http://0.0.0.0:5081|TunnelHost__ManagementApiKey=你的安全Key
CONTAINER_NETWORK=proxytransfer
EOF
```

> ⚠️ 把 `你的安全Key` 替换为强随机字符串。Web 和 Tunnel 的 `ManagementApiKey` 要一致。

#### 步骤 4：创建生产环境 appsettings.Production.json

> 只需覆盖与默认值不同的字段，无需复制全部配置。

```bash
# Web 生产配置
cat > /apps/proxytransfer/config/web.appsettings.Production.json << 'EOF'
{
  "Auth": { "ApiKey": "你的安全Key" },
  "Cors": { "AllowedOrigins": ["https://你的域名"] }
}
EOF

# TunnelHost 生产配置
cat > /apps/proxytransfer/config/tunnel.appsettings.Production.json << 'EOF'
{
  "TunnelHost": {
    "NodeId": "prod-node-1",
    "ListenPortRangeStart": 64000,
    "ListenPortRangeEnd": 65000
  }
}
EOF
```

#### 步骤 5：启动容器

```bash
cd /apps/proxytransfer/web/app && ./restart_web.sh
cd /apps/proxytransfer/tunnel/app && ./restart_tunnel.sh
```

### 日常发布流程

首次初始化后，日常更新只需在开发机执行：

```bash
# 确保 scripts/.env 已配置（cp .env.example .env 后修改）
./scripts/publish_web.sh    # 更新 API + Web
./scripts/publish_tunnel.sh # 更新 TunnelHost
```

脚本自动完成：本地构建 → rsync 增量同步 → 远端 `docker build && docker rm -f && docker run`。

### 服务器端运维

无需开发机参与，直接在服务器上重启容器：

```bash
cd /apps/proxytransfer/web/app && ./restart_web.sh
cd /apps/proxytransfer/tunnel/app && ./restart_tunnel.sh
```

也可以指定其他运行时配置文件：

```bash
./restart_web.sh /path/to/custom.runtime.env
```

### 运行时配置参数说明

#### `*.runtime.env` 通用变量

| 变量 | 示例 | 说明 |
|---|---|---|
| `CONTAINER_PORTS` | `15080:8080\|64000-65000:64000-65000` | `\|` 分隔多个 `-p` 端口映射 |
| `CONTAINER_VOLUMES` | `/data:/app/App_Data\|...json:ro` | `\|` 分隔多个 `-v` 卷挂载 |
| `CONTAINER_ENVS` | `VAR1=val1\|VAR2=val2` | `\|` 分隔多个 `-e` 环境变量 |
| `CONTAINER_NETWORK` | `proxytransfer` | Docker 网络名，Web 和 Tunnel **必须共用同一网络** |
| `CONTAINER_EXTRA_ARGS` | `--log-opt max-size=10m` | 可选，直接追加到 `docker run` |

#### Web 与 Tunnel 运行时配置差异

| 差异点 | Web | Tunnel |
|---|---|---|
| 对外管理端口 | `15080:8080` | `15081:5081` |
| 卷路径前缀 | `/apps/proxytransfer/web/` | `/apps/proxytransfer/tunnel/` |
| ManagementUrl | `http://proxytransfer-tunnel:5081`（容器名） | `http://0.0.0.0:5081`（自己监听自己） |

### 配置覆盖链

容器内 .NET 配置按以下优先级合并（后者覆盖前者）：

```
appsettings.json（源码默认值）
  → appsettings.Production.json（只读卷挂载）
  → Dockerfile ENV
  → CONTAINER_ENVS（最终运行时覆盖）
```

### publish 脚本的 `.env` 变量

| 变量 | 说明 |
|---|---|
| `REMOTE_HOST` | 服务器 IP 或域名 |
| `REMOTE_USER` | SSH 用户（如 root） |
| `WEB_REMOTE_PATH` | Web 发布目标路径（如 `/apps/proxytransfer/web/app`） |
| `TUNNEL_REMOTE_PATH` | Tunnel 发布目标路径 |
| `WEB_REMOTE_RUNTIME_ENV_PATH` | Web 运行时配置路径（`/apps/proxytransfer/config/web.runtime.env`） |
| `TUNNEL_REMOTE_RUNTIME_ENV_PATH` | Tunnel 运行时配置路径 |
| `CONTAINER_NETWORK` | Docker 网络名 |

## 各项目详细文档

| 项目 | 文档 |
|---|---|
| API 后端 | [ProxyTransfer.Api/README.md](ProxyTransfer.Api/README.md) — 全部 20+ 接口说明、curl 示例、配置详解 |
| TunnelHost 引擎 | [ProxyTransfer.TunnelHost/README.md](ProxyTransfer.TunnelHost/README.md) — 控制面接口、重启恢复机制、端口分配规则 |
| Tunnel.Test 测试 | [ProxyTransfer.Tunnel.Test/README.md](ProxyTransfer.Tunnel.Test/README.md) — 单代理测试、固定入口切换观察、多线程测试 |
