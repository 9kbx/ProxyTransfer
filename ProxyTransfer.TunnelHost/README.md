# ProxyTransfer.TunnelHost

`ProxyTransfer.TunnelHost` 是独立于 API 的长期运行进程，负责真正持有代理监听端口和转发实例。

## 当前能力

- 独立运行 HTTP 控制面，默认监听 `http://0.0.0.0:5081`
- 支持创建一对一代理实例
- 支持创建上游池代理实例
- 支持启动、停止、删除、查询实例
- 将实例定义持久化到 `App_Data/tunnel-host-state.json`
- 进程重启后自动恢复 `DesiredRunning=true` 的实例

## 配置

配置位于 `appsettings.json` 的 `TunnelHost` 节：

- `NodeId`: 节点标识，后续给多节点调度使用
- `ManagementUrl`: 控制面监听地址
- `ListenAddress`: 默认监听 IP
- `PublicHost`: 默认对外暴露主机名/IP
- `StateFilePath`: 持久化状态文件路径
- `ListenPortRangeStart` / `ListenPortRangeEnd`: 可选的监听端口范围
- `ManagementApiKey`: 必填，所有 `/api/*` 请求都必须带 `x-apikey`

未配置 `ManagementApiKey` 时，TunnelHost 会直接拒绝启动，避免在“端口全开放”的服务器上暴露未鉴权控制面。

## 运行

```bash
dotnet run --project ProxyTransfer.TunnelHost/ProxyTransfer.TunnelHost.csproj
```

调用控制面接口时需要带上请求头：

```bash
curl -H 'x-apikey: CHANGE-ME' http://127.0.0.1:5081/api/host
```

## 主要接口

- `GET /api/host`: 查询节点状态
- `GET /api/instances`: 查询全部实例
- `GET /api/instances/{id}`: 查询单个实例
- `POST /api/direct`: 创建一对一代理实例
- `POST /api/pools`: 创建上游池代理实例
- `POST /api/instances/{id}/start`: 启动实例
- `POST /api/instances/{id}/stop`: 停止实例
- `DELETE /api/instances/{id}`: 删除实例

## 设计边界

- TunnelHost 只负责运行态，不负责用户提交的代理原文、测试日志、上游池业务规则管理
- API 后续应改为持久化业务配置，并通过控制面调用 TunnelHost
- 当前版本先面向单节点可用，但接口和 `NodeId` 已为多节点扩展留出空间
