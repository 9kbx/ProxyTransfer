#!/usr/bin/env bash
# package_release.sh — 在本地构建完整发布包，适合交付给客户自行部署。
#
# 用法:
#   ./scripts/package_release.sh                    # 默认 Release 配置
#   ./scripts/package_release.sh Debug              # 指定构建配置
#   ./scripts/package_release.sh Release v1.2.3     # 指定版本号
#
# 输出: .artifacts/release/proxytransfer-{version}.tar.gz

set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"

BUILD_CONFIGURATION="${1:-Release}"
VERSION="${2:-$(date +%Y%m%d-%H%M%S)}"
PACKAGE_NAME="proxytransfer-${VERSION}"
OUTPUT_DIR="$ROOT_DIR/.artifacts/release"
PACKAGE_DIR="$OUTPUT_DIR/$PACKAGE_NAME"

log()  { printf '\033[1;36m[package]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[package]\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31m[package]\033[0m %s\n' "$*" >&2; exit 1; }

require_command() {
	command -v "$1" >/dev/null 2>&1 || fail "缺少命令: $1"
}

# ── 前置检查 ──────────────────────────────────────────────

require_command dotnet
require_command npm
require_command tar

[[ -f "$ROOT_DIR/ProxyTransfer.Api/ProxyTransfer.Api.csproj" ]]   || fail "未找到 API 项目"
[[ -f "$ROOT_DIR/ProxyTransfer.TunnelHost/ProxyTransfer.TunnelHost.csproj" ]] || fail "未找到 TunnelHost 项目"
[[ -f "$ROOT_DIR/ProxyTransfer.Web/package.json" ]]               || fail "未找到 Web 项目"

log "构建配置: $BUILD_CONFIGURATION"
log "版本号:   $VERSION"
log "输出目录: $OUTPUT_DIR"

# ── 清理旧产物 ────────────────────────────────────────────

rm -rf "$PACKAGE_DIR"
mkdir -p "$PACKAGE_DIR"/{web,tunnel,config,docs}

# ── 1. 构建前端 ───────────────────────────────────────────

log "构建前端..."
cd "$ROOT_DIR/ProxyTransfer.Web"
npm install --silent
npm run build

# ── 2. 发布 API + 整合 ────────────────────────────────────

log "发布 API 项目..."
dotnet publish "$ROOT_DIR/ProxyTransfer.Api/ProxyTransfer.Api.csproj" \
	-c "$BUILD_CONFIGURATION" \
	-o "$PACKAGE_DIR/web" \
	--no-self-contained

# 复制前端产物到 wwwroot
cp -r "$ROOT_DIR/ProxyTransfer.Web/dist" "$PACKAGE_DIR/web/wwwroot"

# 复制 Dockerfile 和重启脚本
cp "$ROOT_DIR/ProxyTransfer.Api/Dockerfile" "$PACKAGE_DIR/web/"
cp "$SCRIPT_DIR/restart_web.sh" "$PACKAGE_DIR/web/"
chmod +x "$PACKAGE_DIR/web/restart_web.sh"

# 清理发布目录中不需要的文件
rm -rf "$PACKAGE_DIR/web"/*.pdb 2>/dev/null || true

log "API 发布完成 ($(find "$PACKAGE_DIR/web" -type f | wc -l | tr -d ' ') 个文件)"

# ── 3. 发布 TunnelHost ────────────────────────────────────

log "发布 TunnelHost 项目..."
dotnet publish "$ROOT_DIR/ProxyTransfer.TunnelHost/ProxyTransfer.TunnelHost.csproj" \
	-c "$BUILD_CONFIGURATION" \
	-o "$PACKAGE_DIR/tunnel" \
	--no-self-contained

# 复制 Dockerfile 和重启脚本
cp "$ROOT_DIR/ProxyTransfer.TunnelHost/Dockerfile" "$PACKAGE_DIR/tunnel/"
cp "$SCRIPT_DIR/restart_tunnel.sh" "$PACKAGE_DIR/tunnel/"
chmod +x "$PACKAGE_DIR/tunnel/restart_tunnel.sh"

# 清理
rm -rf "$PACKAGE_DIR/tunnel"/*.pdb 2>/dev/null || true

log "TunnelHost 发布完成 ($(find "$PACKAGE_DIR/tunnel" -type f | wc -l | tr -d ' ') 个文件)"

# ── 4. 收集文档 ───────────────────────────────────────────

log "收集文档..."

# 主 README
cp "$ROOT_DIR/README.md" "$PACKAGE_DIR/docs/"

# 各子项目 README
for project in ProxyTransfer.Api ProxyTransfer.TunnelHost ProxyTransfer.Tunnel.Test ProxyTransfer.Web ProxyTransfer.BrowserPy; do
	local_readme="$ROOT_DIR/$project/README.md"
	if [[ -f "$local_readme" ]]; then
		cp "$local_readme" "$PACKAGE_DIR/docs/${project}.md"
	else
		warn "未找到文档: $project/README.md"
	fi
done

log "文档收集完成 ($(find "$PACKAGE_DIR/docs" -type f | wc -l | tr -d ' ') 个文件)"

# ── 5. 创建配置文件模板 ───────────────────────────────────

log "创建配置文件模板..."

# web.runtime.env（将 change-me 和占位符留给客户修改）
cat > "$PACKAGE_DIR/config/web.runtime.env" << 'EOF'
# ── Web/API 容器运行时配置 ────────────────────────────────
# 使用前请修改以下标记为 <修改> 的项。

DOCKER_BIN=docker
CONTAINER_RESTART=unless-stopped

# <修改> 对外端口映射。格式: 对外端口:容器内端口
# 第一个是 Web 管理台端口，第二个是代理端口范围。
CONTAINER_PORTS=15080:8080|64000-65000:64000-65000

# 数据持久化和生产配置挂载（只读）。
CONTAINER_VOLUMES=/apps/proxytransfer/web/data:/app/App_Data|/apps/proxytransfer/config/web.appsettings.Production.json:/app/appsettings.Production.json:ro

# <修改> TunnelHost__ManagementApiKey 和 Auth__ApiKey
# ManagementUrl 必须用容器名 proxytransfer-tunnel:5081（同 Docker 网络内通信）。
CONTAINER_ENVS=ASPNETCORE_ENVIRONMENT=Production|TunnelHost__ApiUrl=http://0.0.0.0:8080|TunnelHost__ManagementUrl=http://proxytransfer-tunnel:5081|TunnelHost__ManagementApiKey=change-me|Auth__ApiKey=change-me
CONTAINER_NETWORK=proxytransfer

# 可选参数。
# CONTAINER_EXTRA_ARGS=--log-opt=max-size=10m|--log-opt=max-file=3
EOF

# tunnel.runtime.env
cat > "$PACKAGE_DIR/config/tunnel.runtime.env" << 'EOF'
# ── TunnelHost 容器运行时配置 ──────────────────────────────
# 使用前请修改以下标记为 <修改> 的项。

DOCKER_BIN=docker
CONTAINER_RESTART=unless-stopped

# <修改> 对外端口映射。
CONTAINER_PORTS=15081:5081|64000-65000:64000-65000

# 数据持久化和生产配置挂载（只读）。
CONTAINER_VOLUMES=/apps/proxytransfer/tunnel/data:/app/App_Data|/apps/proxytransfer/config/tunnel.appsettings.Production.json:/app/appsettings.Production.json:ro

# <修改> TunnelHost__ManagementApiKey（必须与 web.runtime.env 中一致）
CONTAINER_ENVS=ASPNETCORE_ENVIRONMENT=Production|TunnelHost__ManagementUrl=http://0.0.0.0:5081|TunnelHost__ManagementApiKey=change-me
CONTAINER_NETWORK=proxytransfer

# 可选参数。
# CONTAINER_EXTRA_ARGS=--log-opt=max-size=10m|--log-opt=max-file=3
EOF

# web.appsettings.Production.json
cat > "$PACKAGE_DIR/config/web.appsettings.Production.json" << 'EOF'
{
  "Auth": { "ApiKey": "change-me" },
  "Cors": { "AllowedOrigins": [] }
}
EOF

# tunnel.appsettings.Production.json
cat > "$PACKAGE_DIR/config/tunnel.appsettings.Production.json" << 'EOF'
{
  "TunnelHost": {
    "NodeId": "prod-node-1",
    "ListenPortRangeStart": 64000,
    "ListenPortRangeEnd": 65000,
    "ManagementApiKey": "change-me"
  }
}
EOF

log "配置文件模板创建完成"

# ── 6. 创建部署说明（README）─────────────────────────────

cat > "$PACKAGE_DIR/docs/部署说明.md" << 'ENDDEPLOY'
# ProxyTransfer 部署说明

## 环境要求

- Docker（服务端只需要 Docker，无需 .NET SDK 或 Node.js）
- 操作系统：Linux（amd64）

## 目录结构

解压后得到的目录结构：

```text
proxytransfer-{version}/
├── docs/                               # 所有项目文档
│   ├── README.md                       # 主 README
│   ├── ProxyTransfer.Api.md            # API 项目文档
│   ├── ProxyTransfer.TunnelHost.md     # TunnelHost 文档
│   ├── ProxyTransfer.Tunnel.Test.md    # 测试工具文档
│   ├── ProxyTransfer.Web.md            # 管理台文档
│   ├── ProxyTransfer.BrowserPy.md      # Python 示例文档
│   └── 部署说明.md                      # 本文件
├── config/                             # 部署配置文件（需修改）
│   ├── web.runtime.env                 # Web 容器运行时参数
│   ├── tunnel.runtime.env              # Tunnel 容器运行时参数
│   ├── web.appsettings.Production.json # Web 生产配置
│   └── tunnel.appsettings.Production.json
├── web/                                # API + 管理台构建产物
│   ├── Dockerfile
│   ├── restart_web.sh                  # Web 容器重启脚本
│   └── wwwroot/                        # 前端静态文件
└── tunnel/                             # TunnelHost 构建产物
    ├── Dockerfile
    └── restart_tunnel.sh               # Tunnel 容器重启脚本
```

## 快速部署步骤

### 1. 创建持久化目录

```bash
mkdir -p /apps/proxytransfer/web/data \
         /apps/proxytransfer/tunnel/data \
         /apps/proxytransfer/config
```

### 2. 放置配置文件

把 `config/` 目录下的文件复制到 `/apps/proxytransfer/config/`，并按需修改：

```bash
cp config/* /apps/proxytransfer/config/
```

**必须修改的项**（搜索 `change-me`）：

| 文件 | 配置项 | 说明 |
|---|---|---|
| `web.runtime.env` | `TunnelHost__ManagementApiKey` | 调用 TunnelHost 的 API Key |
| `web.runtime.env` | `Auth__ApiKey` | 前端管理台登录 Key |
| `tunnel.runtime.env` | `TunnelHost__ManagementApiKey` | **必须与上面一致** |
| `web.appsettings.Production.json` | `Auth.ApiKey` | 同上 |
| `tunnel.appsettings.Production.json` | `ManagementApiKey` | 同上 |

**按需修改的项**：

| 文件 | 配置项 | 说明 |
|---|---|---|
| `web.runtime.env` | `CONTAINER_PORTS` | 对外端口映射 |
| `tunnel.runtime.env` | `CONTAINER_PORTS` | 对外端口映射 |
| `tunnel.appsettings.Production.json` | `ListenPortRangeStart/End` | 代理监听端口范围 |
| `web.appsettings.Production.json` | `Cors.AllowedOrigins` | 允许的前端来源 |

### 3. 复制构建产物

```bash
cp -r web /apps/proxytransfer/web/app
cp -r tunnel /apps/proxytransfer/tunnel/app
```

### 4. 启动容器

```bash
cd /apps/proxytransfer/web/app && ./restart_web.sh
cd /apps/proxytransfer/tunnel/app && ./restart_tunnel.sh
```

首次运行会自动创建 Docker 网络 `proxytransfer` 并拉取 .NET 运行时基础镜像。

### 5. 验证

```bash
# 查看容器状态
docker ps --filter name=proxytransfer

# 查看 Web 日志
docker logs proxytransfer-web

# 访问管理台
curl http://127.0.0.1:15080/
```

## 管理台登录

浏览器打开 `http://<服务器IP>:15080`，在 URL 后追加 `?key=你的ApiKey` 即可登录。

## 更新

后续收到新版本后，只需替换 `web/` 和 `tunnel/` 目录下的文件，然后执行重启脚本即可。

## 常用运维命令

```bash
# 重启 Web 容器
cd /apps/proxytransfer/web/app && ./restart_web.sh

# 重启 Tunnel 容器
cd /apps/proxytransfer/tunnel/app && ./restart_tunnel.sh

# 查看容器日志（最近 50 行）
docker logs --tail 50 proxytransfer-web
docker logs --tail 50 proxytransfer-tunnel

# 进入容器
docker exec -it proxytransfer-web bash
```
ENDDEPLOY

log "部署说明创建完成"

# ── 7. 打包 ────────────────────────────────────────────────

log "打包..."

cd "$OUTPUT_DIR"
tar -czf "${PACKAGE_NAME}.tar.gz" "$PACKAGE_NAME"

# 计算文件大小
PACKAGE_SIZE=$(du -h "${PACKAGE_NAME}.tar.gz" | cut -f1)

# 可选：计算目录大小
PACKAGE_DIR_SIZE=$(du -sh "$PACKAGE_NAME" | cut -f1)

log "================================================"
log "✅ 打包完成!"
log ""
log "   文件: ${OUTPUT_DIR}/${PACKAGE_NAME}.tar.gz"
log "   大小: ${PACKAGE_SIZE}"
log "   解压后: ${PACKAGE_DIR_SIZE}"
log ""
log "   交付给客户后，请客户参考 docs/部署说明.md 进行部署。"
log "================================================"
