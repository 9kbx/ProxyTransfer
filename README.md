# ProxyTransfer

ProxyTransfer 用来把带账号密码的 HTTP 或 SOCKS5 代理转成可直接交付给客户端使用的 HTTP 转发代理。

典型场景是：

- 上游代理是 `http://user:pass@host:port` 或 `socks5://user:pass@host:port`
- 下游客户的软件不能二次开发
- 下游客户的软件只支持无账号密码的 HTTP 代理或简单代理地址输入

这个仓库目前包含 4 个项目：

- [ProxyTransfer](ProxyTransfer)：控制台示例工程，保留了 HttpClient、Puppeteer 和动态代理批次的 demo
- [ProxyTransfer.Tunnel](ProxyTransfer.Tunnel)：可复用类库，提供 HTTP/SOCKS5 到 HTTP 的本地/公网转发能力
- [ProxyTransfer.Api](ProxyTransfer.Api)：Minimal API 后端，负责导入代理、启动转发、查看状态、停止转发
- [ProxyTransfer.Web](ProxyTransfer.Web)：Vue 3 管理台，用来操作 API

## 环境要求

- .NET SDK 10.0+
- Node.js 24+
- npm 11+

已验证过的基础命令：

- `dotnet build ProxyTransfer.sln`
- `cd ProxyTransfer.Web && npm install`
- `cd ProxyTransfer.Web && npm run build`

## 项目结构

```text
ProxyTransfer.sln
ProxyTransfer/
ProxyTransfer.Tunnel/
ProxyTransfer.Api/
ProxyTransfer.Web/
```

## 快速启动

### 1. 启动后端 API

在仓库根目录执行：

```bash
cd ProxyTransfer.Api
dotnet run
```

默认监听地址由 [ProxyTransfer.Api/appsettings.json](ProxyTransfer.Api/appsettings.json) 控制，当前默认是：

```json
"TunnelHost": {
  "ListenAddress": "0.0.0.0",
  "PublicHost": "127.0.0.1",
  "ApiUrl": "http://0.0.0.0:5080"
}
```

启动后可以先访问：

- `http://127.0.0.1:5080/api/tunnels`
- `http://127.0.0.1:5080/api/batches`

### 2. 启动前端管理台

在另一个终端执行：

```bash
cd ProxyTransfer.Web
npm install
npm run dev
```

默认开发地址是：

- `http://localhost:5173`

Vite 开发代理已经在 [ProxyTransfer.Web/vite.config.ts](ProxyTransfer.Web/vite.config.ts) 中配置好，会把 `/api` 请求转发到 `http://localhost:5080`。

### 3. 使用管理台

进入页面后可以做这些操作：

- 粘贴 `proxy.txt` 内容批量导入 HTTP 或 SOCKS5 代理
- 手动新增单个 HTTP 或 SOCKS5 代理
- 为代理指定批次号、备注、监听地址、对外主机、公网端口
- 查看当前所有转发实例
- 复制已经启动的 HTTP 转发地址给客户端
- 停止单个代理
- 按导入批次批量停止

### 4. 生产构建前端

```bash
cd ProxyTransfer.Web
npm run build
```

构建产物输出到：

- [ProxyTransfer.Web/dist](ProxyTransfer.Web/dist)

这个目录可以交给 Nginx、Caddy 或其它静态文件服务部署。

## 使用方式

### 批量导入代理

管理台里的“批量导入 proxy.txt”支持每行一个代理，例如：

```text
http://user1:pass1@1.2.3.4:8080
socks5://user2:pass2@1.2.3.5:1080
user3:pass3@1.2.3.6:1080
```

说明：

- 带 `http://` 或 `socks5://` 会按显式 scheme 解析
- 不带 scheme 时，类库默认按 HTTP 解析
- 空行和以 `#` 开头的行会被忽略

如果批量导入时设置了：

- `ListenAddress = 0.0.0.0`
- `PublicHost = 203.0.113.10`
- `FirstListenPort = 40000`

那么导入后生成的转发出口会类似：

- `http://203.0.113.10:40000`
- `http://203.0.113.10:40001`
- `http://203.0.113.10:40002`

这些地址就是可以复制给客户端直接使用的无账号密码 HTTP 代理。

### 手动添加代理

适合临时业务场景。

你只需要填：

- 代理字符串，例如 `http://user:pass@host:port` 或 `socks5://user:pass@host:port`
- 可选批次号
- 可选备注
- 可选固定端口
- 是否立即启动

### 停止代理

支持两种方式：

- 停止单个转发实例
- 按批次号停止整批导入的代理

## API 接口说明

当前后端暴露的核心接口如下：

- `GET /api/tunnels`：返回全部转发实例
- `GET /api/batches`：返回批次概览
- `POST /api/tunnels/import`：批量导入代理
- `POST /api/tunnels`：添加单个代理
- `POST /api/tunnels/{id}/start`：启动指定代理
- `POST /api/tunnels/{id}/stop`：停止指定代理
- `POST /api/tunnels/stop-batch`：按批次停止

### `POST /api/tunnels/import`

请求示例：

```json
{
  "proxyText": "http://user:pass@1.2.3.4:8080\nsocks5://user:pass@1.2.3.5:1080",
  "batchId": "batch-a",
  "note": "海外线路 A",
  "listenAddress": "0.0.0.0",
  "publicHost": "203.0.113.10",
  "firstListenPort": 40000,
  "autoStart": true
}
```

### `POST /api/tunnels`

请求示例：

```json
{
  "proxy": "http://user:pass@1.2.3.4:8080",
  "batchId": "manual",
  "note": "VIP 客户",
  "listenAddress": "0.0.0.0",
  "publicHost": "203.0.113.10",
  "listenPort": 41000,
  "autoStart": true
}
```

### `POST /api/tunnels/{id}/start`

请求示例：

```json
{
  "listenAddress": "0.0.0.0",
  "publicHost": "203.0.113.10",
  "listenPort": 41000
}
```

### `POST /api/tunnels/stop-batch`

请求示例：

```json
{
  "batchId": "batch-a"
}
```

## 各项目的作用

### [ProxyTransfer.Tunnel](ProxyTransfer.Tunnel)

这是核心类库，给其它 .NET 项目引用时主要使用两个类型：

- [ProxyTransfer.Tunnel/Socks5ProxyEndpoint.cs](ProxyTransfer.Tunnel/Socks5ProxyEndpoint.cs)
- [ProxyTransfer.Tunnel/Socks5ProxyTunnel.cs](ProxyTransfer.Tunnel/Socks5ProxyTunnel.cs)

#### `Socks5ProxyEndpoint` 的作用

- 解析 HTTP 或 SOCKS5 代理字符串
- 提供带掩码的展示地址 `SafeDisplayUri`
- 提供标准化后的代理地址 `ProxyUri`

支持格式：

- `http://user:pass@host:port`
- `socks5://user:pass@host:port`
- `user:pass@host:port`

#### `Socks5ProxyTunnel` 的作用

- 建立一个本地或公网可访问的 HTTP CONNECT 转发入口
- 把下游 HTTP CONNECT 请求转发到上游 HTTP 或 SOCKS5 代理
- 处理 HTTP Basic 或 SOCKS5 用户名密码认证
- 通过 `DisposeAsync` 停止监听并释放资源

最常见的调用方式是：

```csharp
using ProxyTransfer.Tunnel;
using System.Net;

var endpoint = Socks5ProxyEndpoint.Parse("socks5://user:pass@1.2.3.4:1080");

await using var tunnel = await Socks5ProxyTunnel.StartAsync(
    endpoint,
    IPAddress.Parse("0.0.0.0"),
    40000,
    "203.0.113.10"
);

Console.WriteLine(tunnel.LocalProxyUri);
```

### [ProxyTransfer.Api](ProxyTransfer.Api)

这是服务端项目，负责：

- 接收前端导入的代理列表
- 管理每个转发实例的生命周期
- 返回当前运行状态
- 停止单个实例或整批实例

核心文件：

- [ProxyTransfer.Api/Program.cs](ProxyTransfer.Api/Program.cs)
- [ProxyTransfer.Api/ProxyTunnelRegistry.cs](ProxyTransfer.Api/ProxyTunnelRegistry.cs)
- [ProxyTransfer.Api/Contracts.cs](ProxyTransfer.Api/Contracts.cs)
- [ProxyTransfer.Api/ProxyTunnelHostOptions.cs](ProxyTransfer.Api/ProxyTunnelHostOptions.cs)

#### 配置文件说明

主配置文件是 [ProxyTransfer.Api/appsettings.json](ProxyTransfer.Api/appsettings.json)。

##### `TunnelHost.ListenAddress`

作用：转发代理监听在哪个网卡地址上。

常见取值：

- `127.0.0.1`：只允许本机访问
- `0.0.0.0`：允许外部访问
- 指定内网 IP：只允许某张网卡访问

##### `TunnelHost.PublicHost`

作用：返回给前端和客户端查看的对外主机名或公网 IP。

例如：

- `127.0.0.1`
- `192.168.1.8`
- `203.0.113.10`
- `proxy.example.com`

这个值不会决定真正监听在哪张网卡上，它主要决定“返回给用户复制的代理地址长什么样”。

##### `TunnelHost.ApiUrl`

作用：Minimal API 自己监听的地址和端口。

默认值：

- `http://0.0.0.0:5080`

##### `Cors.AllowedOrigins`

作用：允许哪些前端站点跨域调用 API。

- 空数组：当前实现会退回到允许任意来源
- 指定数组：只允许白名单来源访问

开发环境覆盖配置在 [ProxyTransfer.Api/appsettings.Development.json](ProxyTransfer.Api/appsettings.Development.json)，默认放开了：

- `http://localhost:5173`

### [ProxyTransfer.Web](ProxyTransfer.Web)

这是 Vue 3 管理台。

核心文件：

- [ProxyTransfer.Web/src/App.vue](ProxyTransfer.Web/src/App.vue)
- [ProxyTransfer.Web/src/style.css](ProxyTransfer.Web/src/style.css)
- [ProxyTransfer.Web/vite.config.ts](ProxyTransfer.Web/vite.config.ts)

#### 运行命令

- `npm run dev`：开发模式
- `npm run build`：生产构建
- `npm run preview`：本地预览构建产物

#### 配置说明

##### Vite 开发代理

[ProxyTransfer.Web/vite.config.ts](ProxyTransfer.Web/vite.config.ts) 中的：

```ts
server: {
  port: 5173,
  proxy: {
    '/api': {
      target: 'http://localhost:5080',
      changeOrigin: true,
    },
  },
}
```

作用：

- 本地开发时把 `/api` 请求代理到后端
- 避免浏览器跨域问题

##### `VITE_API_BASE_URL`

前端代码里支持通过环境变量覆盖 API 地址；如果不配，就默认用相对路径 `/api`。

适合场景：

- 前后端分开部署
- 静态站点不经过同域反代

例如可以新建 `.env.production`：

```env
VITE_API_BASE_URL=https://proxy-api.example.com
```

### [ProxyTransfer](ProxyTransfer)

这是原始控制台 demo 项目，主要用于验证和保留以下能力：

- 基于 `proxy.txt` 载入代理列表
- HttpClient 使用代理
- Puppeteer 通过本地 HTTP 中转访问 SOCKS5 上游
- 动态代理池和轮询取用

入口在 [ProxyTransfer/Program.cs](ProxyTransfer/Program.cs)。

#### 运行方式

```bash
cd ProxyTransfer
dotnet run
```

#### 配置说明

这个项目没有 `appsettings.json`，主要配置来自：

- [ProxyTransfer/proxy.txt](ProxyTransfer/proxy.txt)

格式示例：

```text
resume9338:4SNFWPnE3R@172.96.7.70:50100
socks5://resume9338:4SNFWPnE3R@172.96.7.70:50101
http://resume9338:4SNFWPnE3R@40.27.110.250:50100
```

规则：

- 如果没有 scheme，默认按 `http` 解析
- 如果写了 `socks5://`，就按 SOCKS5 处理
- 如果写了 `http://`，就按 HTTP 代理处理

## 生产部署建议

当前仓库已经可以跑起来，但正式环境建议你至少补上这些：

- API 鉴权，例如 API Key、JWT 或反向代理白名单
- 代理数据持久化，避免服务重启后丢失内存状态
- 防火墙规则，只开放你需要交付给客户的端口范围
- 前端静态资源通过 Nginx 或 Caddy 提供
- API 通过 systemd、Docker 或进程守护工具托管

## 当前限制

目前实现有几个明确边界：

- API 中的代理状态保存在内存里，服务重启后不会恢复
- 还没有账号体系、权限控制和审计日志
- 还没有代理健康检查、失败重试和 429 降载逻辑
- 还没有 TLS 终止、限流和生产级安全加固

如果你后面要继续做正式环境版本，优先级通常应该是：

1. 鉴权
2. 持久化
3. 进程托管和反向代理
4. 代理健康检查和配额控制