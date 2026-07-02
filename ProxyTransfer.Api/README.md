# ProxyTransfer.Api

ProxyTransfer 的 Minimal API 后端服务，负责管理代理转发实例的生命周期，包括导入代理、启动/停止转发、上游池管理、固定入口管理以及代理连通性测试。

API 本身不直接持有代理端口，而是通过 HTTP 调用 [ProxyTransfer.TunnelHost](../ProxyTransfer.TunnelHost) 的控制面来实现转发实例的创建和调度。

## 配置

主配置文件为 `appsettings.json`，关键配置项说明：

| 配置路径 | 默认值 | 说明 |
|---|---|---|
| `TunnelHost:ManagementUrl` | `http://127.0.0.1:5081` | TunnelHost 控制面地址 |
| `TunnelHost:ApiUrl` | `http://0.0.0.0:5080` | API 自身监听地址 |
| `TunnelHost:ListenAddress` | `0.0.0.0` | 下游转发代理的默认监听网卡 |
| `TunnelHost:PublicHost` | `127.0.0.1` | 下游转发代理的对外主机名/IP |
| `TunnelHost:ManagementApiKey` | — | 调用 TunnelHost 的 API Key |
| `TunnelHost:DefaultStickyMinutes` | `10` | 固定入口默认粘性时长（分钟） |
| `TunnelHost:FailureCooldownSeconds` | `90` | 上游失败后冷却时长（秒） |
| `TunnelHost:ProbeIntervalSeconds` | `60` | 后台探活周期（秒） |
| `TunnelHost:ProbeTimeoutSeconds` | `10` | 单次探活超时（秒） |
| `TunnelHost:ProbeTargetHost` | `example.com` | 探活 CONNECT 目标 |
| `TunnelHost:ProbeTargetPort` | `443` | 探活 CONNECT 目标端口 |
| `Auth:ApiKey` | — | **必填**，前端调用 API 的鉴权 Key |
| `Cors:AllowedOrigins` | `[]` | 允许跨域的前端来源（空则允许任意） |

## 运行

```bash
cd ProxyTransfer.Api
dotnet run
```

所有 `/api/*` 请求需要在 Header 中携带 `x-apikey`（值与 `Auth:ApiKey` 一致）。

## 接口鉴权

前端通过 URL 参数 `?key=xxx` 传入 API Key，之后所有请求都会在 Header 中带上 `x-apikey`。

验证 Key 是否有效的接口：

```bash
curl -H 'x-apikey: your-key' http://127.0.0.1:5080/api/auth/validate
# => {"valid":true}
```

---

## API 接口总览

### 鉴权

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/auth/validate` | 验证 API Key 是否有效 |

### 单代理转发（经典模式）

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/tunnels` | 获取所有转发实例 |
| `GET` | `/api/batches` | 获取批次概览 |
| `POST` | `/api/tunnels/import` | 批量导入代理 |
| `POST` | `/api/tunnels` | 手动添加单个代理 |
| `POST` | `/api/tunnels/{id}/start` | 启动指定代理 |
| `POST` | `/api/tunnels/{id}/stop` | 停止指定代理 |
| `POST` | `/api/tunnels/stop-batch` | 按批次停止 |
| `POST` | `/api/tunnels/{id}/test` | 测试单个代理连通性 |
| `POST` | `/api/tunnels/test-batch` | 批量测试代理连通性 |

### 上游池管理

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/upstream-pools` | 获取所有上游池概览 |
| `GET` | `/api/upstream-pools/{poolId}` | 获取指定池详情（含健康状态） |
| `POST` | `/api/upstream-pools/import` | 批量导入上游到池 |
| `POST` | `/api/upstream-pools/{poolId}/append` | 向池中追加上游 |
| `POST` | `/api/upstream-pools/{poolId}/delete` | 从池中删除上游 |
| `POST` | `/api/upstream-pools/{poolId}/test` | 测试池内上游连通性 |

### 固定入口（固定入口池模式）

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/fixed-proxies` | 获取所有固定入口 |
| `POST` | `/api/fixed-proxies` | 创建固定入口 |
| `POST` | `/api/fixed-proxies/{id}/start` | 启动固定入口 |
| `POST` | `/api/fixed-proxies/{id}/stop` | 停止固定入口 |
| `DELETE` | `/api/fixed-proxies/{id}` | 删除固定入口 |
| `POST` | `/api/fixed-proxies/{id}/test` | 测试固定入口连通性 |

### 系统

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/port-range` | 获取可用的监听端口范围（从 TunnelHost 查询） |
| `GET` | `/api/test-history` | 获取测试历史记录 |

---

## 接口调用示例

> 以下示例假设 API 监听在 `http://127.0.0.1:5080`，API Key 为 `test-key-123`。为简洁起见，后续示例省略 `-H 'x-apikey: test-key-123'`，实际调用时请务必带上。

### 批量导入代理

```bash
curl -X POST http://127.0.0.1:5080/api/tunnels/import \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: test-key-123' \
  -d '{
    "proxyText": "http://user:pass@1.2.3.4:8080\nsocks5://user:pass@1.2.3.5:1080",
    "downstreamProtocol": "http",
    "batchId": "batch-a",
    "note": "海外线路 A",
    "listenAddress": "0.0.0.0",
    "publicHost": "203.0.113.10",
    "firstListenPort": 40000,
    "autoStart": true
  }'
```

响应：

```json
{
  "batchId": "batch-a",
  "importedCount": 2,
  "items": [
    {
      "id": "a1b2c3d4-...",
      "batchId": "batch-a",
      "note": "海外线路 A",
      "remoteProxy": "http://1.2.3.4:8080",
      "remoteProxyDisplay": "http://user:***@1.2.3.4:8080",
      "downstreamProtocol": "http",
      "listenAddress": "0.0.0.0",
      "publicHost": "203.0.113.10",
      "requestedListenPort": 40000,
      "activeListenPort": 40000,
      "forwardedProxy": "http://203.0.113.10:40000",
      "status": "Running",
      "createdAt": "2026-06-24T08:30:00+00:00",
      "startedAt": "2026-06-24T08:30:00+00:00",
      "stoppedAt": null,
      "lastError": null
    }
  ]
}
```

### 手动添加单个代理

```bash
curl -X POST http://127.0.0.1:5080/api/tunnels \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: test-key-123' \
  -d '{
    "proxy": "http://user:pass@1.2.3.4:8080",
    "downstreamProtocol": "socks5",
    "batchId": "manual",
    "note": "VIP 客户专用",
    "listenAddress": "0.0.0.0",
    "publicHost": "203.0.113.10",
    "listenPort": 41000,
    "autoStart": true
  }'
```

### 启动/停止代理

```bash
# 启动（可覆盖下游协议、监听地址等参数）
curl -X POST http://127.0.0.1:5080/api/tunnels/{id}/start \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: test-key-123' \
  -d '{
    "downstreamProtocol": "socks5",
    "listenAddress": "0.0.0.0",
    "publicHost": "203.0.113.10",
    "listenPort": 41000
  }'

# 停止
curl -X POST http://127.0.0.1:5080/api/tunnels/{id}/stop \
  -H 'x-apikey: test-key-123'
```

### 按批次停止

```bash
curl -X POST http://127.0.0.1:5080/api/tunnels/stop-batch \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: test-key-123' \
  -d '{"batchId": "batch-a"}'
```

### 测试代理连通性

```bash
# 测试单个代理
curl -X POST http://127.0.0.1:5080/api/tunnels/{id}/test \
  -H 'x-apikey: test-key-123'

# 批量测试运行中的代理
curl -X POST http://127.0.0.1:5080/api/tunnels/test-batch \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: test-key-123' \
  -d '{"batchId": "batch-a", "runningOnly": true}'
```

### 导入上游池

```bash
curl -X POST http://127.0.0.1:5080/api/upstream-pools/import \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: test-key-123' \
  -d '{
    "proxyText": "http://user:pass@2.2.2.2:8080\nsocks5://user:pass@2.2.2.3:1080",
    "poolId": "pool-vip-a",
    "note": "VIP 客户 A 的候选上游池"
  }'
```

### 查看上游池详情

```bash
curl -H 'x-apikey: test-key-123' \
  http://127.0.0.1:5080/api/upstream-pools/pool-vip-a
```

### 向上游池追加代理

```bash
curl -X POST http://127.0.0.1:5080/api/upstream-pools/pool-vip-a/append \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: test-key-123' \
  -d '{"proxyText": "socks5://user:pass@3.3.3.3:1080"}'
```

### 测试池内上游

```bash
# 测试全部上游
curl -X POST http://127.0.0.1:5080/api/upstream-pools/pool-vip-a/test \
  -H 'x-apikey: test-key-123'

# 测试指定上游
curl -X POST http://127.0.0.1:5080/api/upstream-pools/pool-vip-a/test \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: test-key-123' \
  -d '{"upstreamIds": ["d4b9bf52-ec0c-4d8f-9c55-95e8beef40d1"]}'
```

### 创建固定入口

```bash
curl -X POST http://127.0.0.1:5080/api/fixed-proxies \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: test-key-123' \
  -d '{
    "poolId": "pool-vip-a",
    "downstreamProtocol": "http",
    "note": "客户 A 固定入口",
    "listenAddress": "0.0.0.0",
    "publicHost": "1.1.1.1",
    "listenPort": 1234,
    "selectionPolicy": "sticky",
    "stickyMinutes": 10,
    "autoStart": true
  }'
```

`selectionPolicy` 支持三种策略：

- `sticky`：粘性会话，在设定时间窗内尽量复用最近成功的上游
- `round-robin`：轮询，每个新连接在健康上游之间依次轮换
- `least-failures`：最少失败优先，优先选择当前失败次数更少的健康上游

### 测试固定入口

```bash
curl -X POST http://127.0.0.1:5080/api/fixed-proxies/{id}/test \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: test-key-123' \
  -d '{"iterationCount": 6, "intervalSeconds": 5}'
```

### 获取端口范围

```bash
curl -H 'x-apikey: test-key-123' \
  http://127.0.0.1:5080/api/port-range
```

响应示例：

```json
{
  "startPort": 40000,
  "endPort": 40800,
  "message": null
}
```

### 获取测试历史

```bash
# 获取单代理测试历史
curl -H 'x-apikey: test-key-123' \
  'http://127.0.0.1:5080/api/test-history?mode=single'

# 获取固定入口测试历史
curl -H 'x-apikey: test-key-123' \
  'http://127.0.0.1:5080/api/test-history?mode=fixed'

# 按资源 ID 过滤
curl -H 'x-apikey: test-key-123' \
  'http://127.0.0.1:5080/api/test-history?mode=fixed&resourceId=5dcb7df6-7e37-47dc-8d11-31285021bc8c'
```
