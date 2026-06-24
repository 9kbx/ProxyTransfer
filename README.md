# ProxyTransfer

ProxyTransfer 用来把带账号密码的 HTTP 或 SOCKS5 代理转成可直接交付给客户端使用的本地转发代理。

典型场景是：

- 上游代理是 `http://user:pass@host:port` 或 `socks5://user:pass@host:port`
- 下游客户的软件不能二次开发
- 下游客户的软件只支持无账号密码的 HTTP 代理或 SOCKS5 代理地址输入

这个仓库目前包含 6 个主要项目/目录：

- [ProxyTransfer.Demo](ProxyTransfer.Demo)：控制台示例工程，保留了 HttpClient、Puppeteer 和动态代理批次的 demo
- [ProxyTransfer.Tunnel](ProxyTransfer.Tunnel)：可复用类库，提供 HTTP/SOCKS5 到 HTTP，以及 SOCKS5 到 SOCKS5 的本地/公网转发能力
- [ProxyTransfer.Tunnel.Test](ProxyTransfer.Tunnel.Test)：下游代理测试工程，用来验证 API 导入并启动后的 forwarded proxy 是否可用
- [ProxyTransfer.Api](ProxyTransfer.Api)：Minimal API 后端，负责导入代理、启动转发、查看状态、停止转发
- [ProxyTransfer.Web](ProxyTransfer.Web)：Vue 3 管理台，用来操作 API
- [ProxyTransfer.BrowserPy](ProxyTransfer.BrowserPy)：Python 版浏览器接入示例，用于快速验证代理在脚本化浏览器场景下的使用方式

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
ProxyTransfer.Demo/
ProxyTransfer.Tunnel/
ProxyTransfer.Tunnel.Test/
ProxyTransfer.Api/
ProxyTransfer.Web/
ProxyTransfer.BrowserPy/
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
  "ApiUrl": "http://0.0.0.0:5080",
  "DefaultStickyMinutes": 10,
  "FailureCooldownSeconds": 90,
  "ProbeIntervalSeconds": 60,
  "ProbeTimeoutSeconds": 10,
  "ProbeTargetHost": "example.com",
  "ProbeTargetPort": 443
}
```

新增字段说明：

- `DefaultStickyMinutes`：固定下游代理入口默认粘性时长，单位分钟
- `FailureCooldownSeconds`：某个上游连接失败后，被临时摘除的冷却时长，单位秒
- `ProbeIntervalSeconds`：后台主动探活周期，单位秒
- `ProbeTimeoutSeconds`：单次探活超时时间，单位秒
- `ProbeTargetHost` / `ProbeTargetPort`：主动探活时尝试 CONNECT 的目标地址

启动后可以先访问：

- `http://127.0.0.1:5080/api/tunnels`
- `http://127.0.0.1:5080/api/batches`
- `http://127.0.0.1:5080/api/upstream-pools`
- `http://127.0.0.1:5080/api/fixed-proxies`

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
- 为代理指定下游出口协议、批次号、备注、监听地址、对外主机、公网端口
- 查看当前所有转发实例
- 复制已经启动的 HTTP 或 SOCKS5 转发地址给客户端
- 一键复制全部运行中的下游代理，直接粘贴到 [ProxyTransfer.Tunnel.Test](ProxyTransfer.Tunnel.Test) 的 `proxy.txt`
- 停止单个代理
- 按导入批次批量停止
- 切换到“固定入口池模式”页面，批量导入上游池
- 为某个上游池创建固定下游代理入口，并查看池内健康状态、选择策略与最近选中的上游

### 3.2 固定入口池模式

这个模式适合下面的交付方式：

- 你只想给客户一个长期不变的下游代理地址，例如 `http://1.1.1.1:1234`
- 客户端无需知道真实上游代理，也无需在上游变更时调整配置
- 你希望由服务端在一批健康上游之间自动切换，并可按粘性会话、轮询或最少失败优先三种策略进行选择

工作方式如下：

1. 先把一批 HTTP 或 SOCKS5 上游导入某个上游池
2. 再创建一个固定下游代理入口，并把它绑定到这个上游池
3. 客户端始终使用固定入口地址
4. 服务端在每次新连接到来时，从池中选择一个当前健康的上游
5. 如果策略是 `sticky`，在 `stickyMinutes` 粘性窗口内，固定入口会尽量复用最近成功的上游；如果该上游失效，则自动切换
6. 如果策略是 `round-robin`，每个新连接会在当前健康上游之间顺序轮换
7. 如果策略是 `least-failures`，每个新连接会优先选择当前失败次数更少的健康上游

注意：

- 固定的是“客户端配置的下游代理地址”
- 不固定的是“目标网站最终看到的真实出口 IP”
- 如果需要真正意义上的固定出口 IP，应给客户独享上游代理，或者把池规模控制得非常小并接受故障切换时 IP 变化

### 3.1 测试导入后的下游代理

当前仓库新增了 [ProxyTransfer.Tunnel.Test](ProxyTransfer.Tunnel.Test) 项目，专门用于测试 API 导入并启动后的下游代理。

推荐流程：

1. 在管理台导入并启动代理
2. 在“转发实例”区域点击“复制运行中代理”
3. 把内容粘贴到 [ProxyTransfer.Tunnel.Test/proxy.txt](ProxyTransfer.Tunnel.Test/proxy.txt)
4. 在仓库根目录执行：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test
```

测试项目会按每行一个地址依次验证：

- `http://host:port`
- `socks5://host:port`

它会通过这些下游代理请求 `https://api.ipify.org/`，并输出是否成功以及出口 IP。

如果你已经在 [ProxyTransfer.Tunnel.Test/proxy.txt](ProxyTransfer.Tunnel.Test/proxy.txt) 中保存了代理列表，也可以直接进入项目目录执行：

```bash
cd ProxyTransfer.Tunnel.Test
dotnet run
```

当前 `proxy.txt` 的真实格式就是每行一个下游代理，例如：

```text
http://127.0.0.1:50802
http://127.0.0.1:50761
socks5://127.0.0.1:50717
socks5://127.0.0.1:50654
```

测试成功时，控制台会输出每个代理的出口 IP、耗时和成功/失败统计。

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

如果批量导入时选择：

- `DownstreamProtocol = http`

那么导入后生成的转发出口会类似：

- `http://203.0.113.10:40000`
- `http://203.0.113.10:40001`
- `http://203.0.113.10:40002`

这些地址就是可以复制给客户端直接使用的无账号密码 HTTP 代理。

如果批量导入时选择：

- `DownstreamProtocol = socks5`

那么生成的转发出口也可以是：

- `socks5://203.0.113.10:41000`

这个场景由 `Socks5ProxyTunnel` 提供，适合下游软件支持 SOCKS5 但不支持填写用户名密码的情况。

约束规则：

- 上游支持 HTTP 和 SOCKS5
- 下游支持 HTTP 和 SOCKS5
- 上游省略 scheme 时，系统会按 HTTP 处理
- 下游协议可以独立选择，不再受上游是 HTTP 还是 SOCKS5 的限制

### 手动添加代理

适合临时业务场景。

你只需要填：

- 代理字符串，例如 `http://user:pass@host:port` 或 `socks5://user:pass@host:port`
- 下游出口协议：`http` 或 `socks5`
- 可选批次号
- 可选备注
- 可选固定端口
- 是否立即启动

### 停止代理

支持两种方式：

- 停止单个转发实例
- 按批次号停止整批导入的代理

### 固定入口池模式推荐流程

如果你的目标是给客户交付一个固定地址，但希望系统自动切换健康上游，推荐按下面顺序操作：

1. 启动 [ProxyTransfer.Api](ProxyTransfer.Api) 和 [ProxyTransfer.Web](ProxyTransfer.Web)
2. 切换到管理台的“固定入口池模式”
3. 批量导入一组 HTTP 或 SOCKS5 上游到某个池，例如 `pool-vip-a`
4. 确认池内健康数大于 0
5. 创建一个固定入口，指定 `PublicHost`、`ListenPort`、`DownstreamProtocol` 和可选的 `StickyMinutes`
6. 把生成的 `forwardedProxy` 交给客户端长期使用
7. 在池内健康状态表中观察主动探活结果和最近失败信息

### 推荐联调流程

如果你的目标是把上游代理导入后尽快验证是否能交付给业务客户端，推荐按下面顺序操作：

1. 启动 [ProxyTransfer.Api](ProxyTransfer.Api) 和 [ProxyTransfer.Web](ProxyTransfer.Web)
2. 在管理台里批量导入上游 HTTP 或 SOCKS5 代理
3. 选择下游协议为 HTTP 或 SOCKS5，并启动转发
4. 在“转发实例”里确认状态为 `Running`
5. 点击“复制运行中代理”
6. 把内容粘贴到 [ProxyTransfer.Tunnel.Test/proxy.txt](ProxyTransfer.Tunnel.Test/proxy.txt)
7. 运行 `dotnet run --project ProxyTransfer.Tunnel.Test`

这样可以在真正交付给业务客户端前，先用统一的烟雾测试脚本把所有 forwarded proxy 验一遍。

## API 接口说明

当前后端暴露的核心接口如下：

- `GET /api/tunnels`：返回全部转发实例
- `GET /api/batches`：返回批次概览
- `POST /api/tunnels/import`：批量导入代理
- `POST /api/tunnels`：添加单个代理
- `POST /api/tunnels/{id}/start`：启动指定代理
- `POST /api/tunnels/{id}/stop`：停止指定代理
- `POST /api/tunnels/stop-batch`：按批次停止
- `GET /api/upstream-pools`：返回全部上游池概览
- `GET /api/upstream-pools/{poolId}`：返回指定上游池详情与健康状态
- `POST /api/upstream-pools/import`：批量导入上游池
- `GET /api/fixed-proxies`：返回全部固定下游代理入口
- `POST /api/fixed-proxies`：创建固定下游代理入口
- `POST /api/fixed-proxies/{id}/start`：启动指定固定入口
- `POST /api/fixed-proxies/{id}/stop`：停止指定固定入口

### `POST /api/tunnels/import`

请求示例：

```json
{
  "proxyText": "http://user:pass@1.2.3.4:8080\nsocks5://user:pass@1.2.3.5:1080",
  "downstreamProtocol": "http",
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
  "downstreamProtocol": "http",
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
  "downstreamProtocol": "socks5",
  "listenAddress": "0.0.0.0",
  "publicHost": "203.0.113.10",
  "listenPort": 41000
}
```

字段说明：

- `downstreamProtocol = "http"`：创建无认证 HTTP 出口
- `downstreamProtocol = "socks5"`：创建无认证 SOCKS5 出口，上游可以是 HTTP 或 SOCKS5

### `POST /api/tunnels/stop-batch`

请求示例：

```json
{
  "batchId": "batch-a"
}
```

### `POST /api/upstream-pools/import`

请求示例：

```json
{
  "proxyText": "http://user:pass@2.2.2.2:8080\nsocks5://user:pass@2.2.2.3:1080",
  "poolId": "pool-vip-a",
  "note": "VIP 客户 A 的候选上游池"
}
```

说明：

- `proxyText`：每行一个上游代理，支持 HTTP 和 SOCKS5
- `poolId`：可选；留空时自动生成
- `note`：可选；用于前端显示和运维备注

响应示例：

```json
{
  "poolId": "pool-vip-a",
  "importedCount": 2,
  "totalCount": 2,
  "items": [
    {
      "id": "d4b9bf52-ec0c-4d8f-9c55-95e8beef40d1",
      "poolId": "pool-vip-a",
      "proxy": "http://2.2.2.2:8080",
      "proxyDisplay": "http://user:***@2.2.2.2:8080",
      "status": "Unknown",
      "failureCount": 0,
      "createdAt": "2026-06-24T08:30:00+00:00",
      "lastCheckedAt": null,
      "lastSuccessAt": null,
      "lastFailureAt": null,
      "disabledUntil": null,
      "lastError": null
    }
  ]
}
```

### `GET /api/upstream-pools/{poolId}`

响应示例：

```json
{
  "poolId": "pool-vip-a",
  "note": "VIP 客户 A 的候选上游池",
  "totalCount": 6,
  "healthyCount": 4,
  "createdAt": "2026-06-24T08:30:00+00:00",
  "updatedAt": "2026-06-24T08:45:00+00:00",
  "items": [
    {
      "id": "d4b9bf52-ec0c-4d8f-9c55-95e8beef40d1",
      "poolId": "pool-vip-a",
      "proxy": "socks5://2.2.2.3:1080",
      "proxyDisplay": "socks5://user:***@2.2.2.3:1080",
      "status": "Healthy",
      "failureCount": 0,
      "createdAt": "2026-06-24T08:30:00+00:00",
      "lastCheckedAt": "2026-06-24T08:44:55+00:00",
      "lastSuccessAt": "2026-06-24T08:44:55+00:00",
      "lastFailureAt": null,
      "disabledUntil": null,
      "lastError": null
    }
  ]
}
```

### `POST /api/fixed-proxies`

请求示例：

```json
{
  "poolId": "pool-vip-a",
  "downstreamProtocol": "http",
  "note": "客户 A 固定入口",
  "listenAddress": "0.0.0.0",
  "publicHost": "1.1.1.1",
  "listenPort": 1234,
  "selectionPolicy": "sticky",
  "stickyMinutes": 10,
  "autoStart": true
}
```

说明：

- `poolId`：固定入口绑定到哪个上游池
- `downstreamProtocol`：对外暴露为 `http` 或 `socks5`
- `listenPort`：可选；不填则随机端口
- `selectionPolicy`：上游选择策略，支持 `sticky`、`round-robin`、`least-failures`
- `stickyMinutes`：固定入口优先复用最近成功上游的时长，单位分钟；仅 `sticky` 策略使用
- `autoStart`：是否创建后立即启动

响应示例：

```json
{
  "id": "5dcb7df6-7e37-47dc-8d11-31285021bc8c",
  "poolId": "pool-vip-a",
  "note": "客户 A 固定入口",
  "downstreamProtocol": "http",
  "listenAddress": "0.0.0.0",
  "publicHost": "1.1.1.1",
  "requestedListenPort": 1234,
  "activeListenPort": 1234,
  "forwardedProxy": "http://1.1.1.1:1234",
  "selectionPolicy": "sticky",
  "stickyMinutes": 10,
  "totalUpstreamCount": 6,
  "healthyUpstreamCount": 4,
  "lastSelectedUpstream": "socks5://2.2.2.3:1080",
  "lastSelectedUpstreamDisplay": "socks5://user:***@2.2.2.3:1080",
  "status": "Running",
  "createdAt": "2026-06-24T08:46:00+00:00",
  "startedAt": "2026-06-24T08:46:00+00:00",
  "stoppedAt": null,
  "lastError": null
}
```

### `GET /api/fixed-proxies`

这个接口会返回当前所有固定入口及其最近选中的上游摘要。前端“固定入口池模式”页面就是基于它来展示：

- 当前固定入口地址
- 绑定的上游池
- 当前健康上游数
- 最近一次选中的上游
- 当前状态、错误信息和启动/停止时间

### `POST /api/fixed-proxies/{id}/start`

这个接口当前不需要请求体，直接启动已创建的固定入口。

### `POST /api/fixed-proxies/{id}/stop`

这个接口当前不需要请求体，直接停止指定固定入口。

## 各项目的作用

### [ProxyTransfer.Tunnel](ProxyTransfer.Tunnel)

这是核心类库，给其它 .NET 项目引用时主要使用以下类型：

- [ProxyTransfer.Tunnel/ProxyEndpoint.cs](ProxyTransfer.Tunnel/ProxyEndpoint.cs)
- [ProxyTransfer.Tunnel/IProxyTunnel.cs](ProxyTransfer.Tunnel/IProxyTunnel.cs)
- [ProxyTransfer.Tunnel/HttpProxyTunnel.cs](ProxyTransfer.Tunnel/HttpProxyTunnel.cs)
- [ProxyTransfer.Tunnel/Socks5ProxyTunnel.cs](ProxyTransfer.Tunnel/Socks5ProxyTunnel.cs)

#### `ProxyEndpoint` 的作用

- 解析 HTTP 或 SOCKS5 代理字符串
- 提供带掩码的展示地址 `SafeDisplayUri`
- 提供标准化后的代理地址 `ProxyUri`

支持格式：

- `http://user:pass@host:port`
- `socks5://user:pass@host:port`
- `user:pass@host:port`

#### `IProxyTunnel` 的作用

- 统一抽象本地转发隧道的最小公共能力
- 暴露监听地址、对外主机、本地端口、本地代理地址、上游代理地址
- 允许业务代码只依赖接口，而不是绑定具体隧道实现

#### `HttpProxyTunnel` 的作用

- 建立一个本地或公网可访问的 HTTP CONNECT 转发入口
- 把下游 HTTP CONNECT 请求转发到上游 HTTP 或 SOCKS5 代理
- 处理 HTTP Basic 或 SOCKS5 用户名密码认证
- 通过 `DisposeAsync` 停止监听并释放资源

最常见的调用方式是：

```csharp
using ProxyTransfer.Tunnel;
using System.Net;

var endpoint = ProxyEndpoint.Parse("socks5://user:pass@1.2.3.4:1080");

await using var tunnel = await HttpProxyTunnel.StartAsync(
  endpoint,
  IPAddress.Parse("0.0.0.0"),
  40000,
  "203.0.113.10"
);

Console.WriteLine(tunnel.LocalProxyUri);
```

如果希望把带认证的上游 SOCKS5 转成无认证的下游 SOCKS5，可以这样：

```csharp
using ProxyTransfer.Tunnel;
using System.Net;

var endpoint = ProxyEndpoint.Parse("socks5://user:pass@1.2.3.4:1080");

await using var tunnel = await Socks5ProxyTunnel.StartAsync(
  endpoint,
  IPAddress.Parse("0.0.0.0"),
  41000,
  "203.0.113.10"
);

Console.WriteLine(tunnel.LocalProxyUri);
```

#### `Socks5ProxyTunnel` 的作用

- 建立一个本地或公网可访问的无认证 SOCKS5 转发入口
- 把下游 SOCKS5 CONNECT 请求转发到上游 SOCKS5 代理
- 自动处理上游 SOCKS5 的用户名密码认证
- 通过 `DisposeAsync` 停止监听并释放资源

现在 API 和管理台都已经支持任意上游 HTTP/SOCKS5 转成下游 HTTP/SOCKS5，客户端可以独立选择需要的下游协议。

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

#### Demo 配置说明

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

#### 管理台中的复制能力

当前页面里有两种复制方式：

- 单条“复制”：复制某一个运行中实例的 `forwardedProxy`
- “复制运行中代理”：一次复制全部 `Running` 状态的下游代理，每行一个

第二种方式就是给 [ProxyTransfer.Tunnel.Test](ProxyTransfer.Tunnel.Test) 准备的，可以直接粘贴到 [ProxyTransfer.Tunnel.Test/proxy.txt](ProxyTransfer.Tunnel.Test/proxy.txt)。

### [ProxyTransfer.Tunnel.Test](ProxyTransfer.Tunnel.Test)

这是专门的下游代理测试项目，用来验证 API 已经创建并启动的 forwarded proxy 是否真的可用。

它不依赖 [ProxyTransfer.Tunnel](ProxyTransfer.Tunnel) 项目本身，而是在测试项目内直接完成代理地址解析，并使用最基础的 `HttpClient` handler 做代理请求：

- HTTP 下游代理使用 `HttpClientHandler`
- SOCKS5 下游代理使用 `SocketsHttpHandler`

入口在 [ProxyTransfer.Tunnel.Test/Program.cs](ProxyTransfer.Tunnel.Test/Program.cs)。

#### Tunnel.Test 运行方式

```bash
dotnet run --project ProxyTransfer.Tunnel.Test
```

或者：

```bash
cd ProxyTransfer.Tunnel.Test
dotnet run
```

如果你希望测试其它文件中的代理列表，也可以直接传入路径：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test -- /absolute/path/to/proxy-list.txt
```

#### 测试规则

- 每行一个下游代理，支持 `http://host:port` 和 `socks5://host:port`
- 空行和以 `#` 开头的注释行会被忽略
- 重复代理会自动去重
- 项目会按顺序逐条发起请求，并输出每条代理的成功或失败结果
- 全部成功时退出码为 `0`，存在失败时退出码为 `1`

#### 推荐输入文件

默认读取 [ProxyTransfer.Tunnel.Test/proxy.txt](ProxyTransfer.Tunnel.Test/proxy.txt)。

文件内容示例：

```text
http://127.0.0.1:50802
http://127.0.0.1:50761
socks5://127.0.0.1:50717
socks5://127.0.0.1:50654
```

### [ProxyTransfer.Demo](ProxyTransfer.Demo)

这是原始控制台 demo 项目，主要用于验证和保留以下能力：

- 基于 `proxy.txt` 载入上游代理列表
- HttpClient 使用代理
- Puppeteer 通过本地 HTTP 中转访问 SOCKS5 上游
- 动态代理池和轮询取用

入口在 [ProxyTransfer.Demo/Program.cs](ProxyTransfer.Demo/Program.cs)。

#### Demo 运行方式

```bash
cd ProxyTransfer.Demo
dotnet run
```

#### 配置说明

这个项目没有 `appsettings.json`，主要配置来自：

- [ProxyTransfer.Demo/proxy.txt](ProxyTransfer.Demo/proxy.txt)

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
