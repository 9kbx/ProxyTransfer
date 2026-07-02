# ProxyTransfer.TunnelHost

`ProxyTransfer.TunnelHost` 是独立于 API 的长期运行进程，负责真正持有代理监听端口和转发实例。它是整个 ProxyTransfer 系统的代理转发引擎。

API 项目（[ProxyTransfer.Api](../ProxyTransfer.Api)）通过 HTTP 调用 TunnelHost 的控制面来创建、启动、停止、删除转发实例，TunnelHost 自身不暴露前端页面。

## 架构定位

```mermaid
flowchart LR
    Client[下游客户端] -->|无认证代理| TH[TunnelHost<br/>持有监听端口]
    TH -->|认证代理| US[上游代理]
    API[ProxyTransfer.Api] -->|HTTP API| TH
    Web[管理台] -->|HTTP API| API
```

## 当前能力

- 独立运行 HTTP 控制面，默认监听 `http://0.0.0.0:5081`
- 支持创建一对一代理实例（`direct`）
- 支持创建上游池代理实例（`pool`），含粘性会话、轮询、最少失败优先三种策略
- 支持启动、停止、删除、查询实例
- 支持监听端口范围约束，可限制自动分配和手动指定的端口区间
- 将实例定义持久化到 `App_Data/tunnel-host-state.json`
- 进程重启后自动恢复 `DesiredRunning=true` 的实例（用户主动停止的不会恢复）
- 关机时保留 `DesiredRunning` 标记，确保重启后运行中的实例自动恢复

## 配置

配置文件为 `appsettings.json`，所有配置位于 `TunnelHost` 节：

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `NodeId` | 机器名 | 节点标识，用于多节点调度 |
| `ManagementUrl` | `http://0.0.0.0:5081` | 控制面监听地址 |
| `ListenAddress` | `0.0.0.0` | 下游转发代理的默认监听网卡 |
| `PublicHost` | `127.0.0.1` | 下游转发代理的默认对外主机名/IP |
| `ListenPortRangeStart` | — | 可选，允许的最小监听端口 |
| `ListenPortRangeEnd` | — | 可选，允许的最大监听端口 |
| `StateFilePath` | `App_Data/tunnel-host-state.json` | 持久化状态文件路径 |
| `ManagementApiKey` | — | **必填**，所有 `/api/*` 请求须携带 `x-apikey` |
| `DefaultStickyMinutes` | `10` | 默认粘性会话时长（分钟） |
| `FailureCooldownSeconds` | `90` | 上游失败后冷却时长（秒） |

未配置 `ManagementApiKey` 时，TunnelHost 会直接拒绝启动，避免在"端口全开放"的服务器上暴露未鉴权控制面。

## 运行

```bash
dotnet run --project ProxyTransfer.TunnelHost
```

所有 `/api/*` 请求需要在 Header 中携带 `x-apikey`。

---

## API 接口总览

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/host` | 查询节点运行状态 |
| `GET` | `/api/host/port-range` | 获取配置的监听端口范围 |
| `GET` | `/api/instances` | 查询全部转发实例 |
| `GET` | `/api/instances/{id}` | 查询单个实例详情 |
| `POST` | `/api/direct` | 创建一对一代理实例 |
| `POST` | `/api/pools` | 创建上游池代理实例 |
| `POST` | `/api/instances/{id}/start` | 启动指定实例 |
| `POST` | `/api/instances/{id}/stop` | 停止指定实例 |
| `DELETE` | `/api/instances/{id}` | 删除指定实例 |

---

## 接口调用示例

> 以下示例假设 TunnelHost 监听在 `http://127.0.0.1:5081`，API Key 为 `change-me`。

### 查询节点状态

```bash
curl -H 'x-apikey: change-me' http://127.0.0.1:5081/api/host
```

响应：

```json
{
  "nodeId": "my-server",
  "managementUrl": "http://0.0.0.0:5081",
  "totalCount": 5,
  "runningCount": 3,
  "startedAt": "2026-06-24T08:00:00+00:00"
}
```

### 获取端口范围

```bash
curl -H 'x-apikey: change-me' http://127.0.0.1:5081/api/host/port-range
```

已配置范围时：

```json
{ "startPort": 40000, "endPort": 40800, "message": null }
```

未配置时：

```json
{ "startPort": null, "endPort": null, "message": "未配置端口范围，系统将使用操作系统随机端口。" }
```

### 查询全部实例

```bash
curl -H 'x-apikey: change-me' http://127.0.0.1:5081/api/instances
```

### 查询单个实例

```bash
curl -H 'x-apikey: change-me' http://127.0.0.1:5081/api/instances/a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

### 创建一对一代理实例

```bash
curl -X POST http://127.0.0.1:5081/api/direct \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: change-me' \
  -d '{
    "remoteProxy": "http://user:pass@1.2.3.4:8080",
    "downstreamProtocol": "http",
    "note": "测试代理",
    "batchId": "batch-a",
    "listenAddress": "0.0.0.0",
    "publicHost": "203.0.113.10",
    "listenPort": 40000,
    "autoStart": true
  }'
```

响应示例：

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "nodeId": "my-server",
  "kind": "direct",
  "note": "测试代理",
  "batchId": "batch-a",
  "poolId": null,
  "downstreamProtocol": "http",
  "listenAddress": "0.0.0.0",
  "publicHost": "203.0.113.10",
  "requestedListenPort": 40000,
  "activeListenPort": 40000,
  "desiredRunning": true,
  "remoteProxy": "http://1.2.3.4:8080",
  "remoteProxyDisplay": "http://user:***@1.2.3.4:8080",
  "upstreams": [],
  "healthyUpstreamCount": 0,
  "selectionPolicy": null,
  "stickyMinutes": null,
  "forwardedProxy": "http://203.0.113.10:40000",
  "lastSelectedUpstream": null,
  "lastSelectedUpstreamDisplay": null,
  "status": "Running",
  "createdAt": "2026-06-24T08:30:00+00:00",
  "startedAt": "2026-06-24T08:30:00+00:00",
  "stoppedAt": null,
  "lastError": null
}
```

字段说明：

- `kind`：`"direct"` 表示一对一代理，`"pool"` 表示上游池代理
- `forwardedProxy`：可交付给客户端直接使用的下游代理地址
- `remoteProxyDisplay`：凭据已掩码的上游代理地址
- `lastSelectedUpstreamDisplay`：最近一次被选中的上游代理（凭据已掩码），仅 pool 类型有效

### 创建上游池代理实例

```bash
curl -X POST http://127.0.0.1:5081/api/pools \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: change-me' \
  -d '{
    "poolId": "pool-vip-a",
    "upstreams": [
      "http://user:pass@2.2.2.2:8080",
      "socks5://user:pass@2.2.2.3:1080"
    ],
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

`selectionPolicy` 可选值：

| 值 | 说明 |
|---|---|
| `sticky` | 粘性会话，在设定时间窗内尽量复用最近成功的上游 |
| `round-robin` | 轮询，每个新连接在健康上游之间依次轮换 |
| `least-failures` | 最少失败优先，优先选择当前失败次数更少的健康上游 |

### 启动/停止实例

```bash
# 启动（可覆盖下游协议、监听地址等参数）
curl -X POST http://127.0.0.1:5081/api/instances/{id}/start \
  -H 'Content-Type: application/json' \
  -H 'x-apikey: change-me' \
  -d '{
    "downstreamProtocol": "socks5",
    "listenAddress": "0.0.0.0",
    "publicHost": "203.0.113.10",
    "listenPort": 41000
  }'

# 停止
curl -X POST http://127.0.0.1:5081/api/instances/{id}/stop \
  -H 'x-apikey: change-me'
```

### 删除实例

```bash
curl -X DELETE http://127.0.0.1:5081/api/instances/{id} \
  -H 'x-apikey: change-me'
```

---

## 持久化与重启恢复

实例定义保存在 `StateFilePath` 指定的 JSON 文件中。每个实例有一个 `DesiredRunning` 字段：

| 场景 | `DesiredRunning` | 重启后 |
|---|---|---|
| 用户主动 Stop | `false` | 不会自动启动 |
| 应用正常关机 | 保持 `true` | ✅ 自动恢复 |
| 应用崩溃 | 保持 `true` | ✅ 自动恢复 |
| Delete 删除 | 条目被移除 | 不存在 |

## 端口分配规则

当配置了 `ListenPortRangeStart` / `ListenPortRangeEnd` 时：

- 显式指定端口必须在范围内，否则返回错误
- 不指定端口时（`listenPort` 为 `null` 或 `-1`），在范围内随机选择一个当前可用的端口
- 端口为 `0` 时使用操作系统随机端口（不检查范围）

当未配置端口范围时，系统使用操作系统随机端口。

## 设计边界

- TunnelHost 只负责运行态，不负责用户提交的代理原文、测试日志、上游池业务规则管理
- API 调用 TunnelHost 控制面来管理代理生命周期
- 当前版本先面向单节点可用，但接口和 `NodeId` 已为多节点扩展留出空间
