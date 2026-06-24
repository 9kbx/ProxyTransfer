# ProxyTransfer.Tunnel.Test

这个项目用于验证 ProxyTransfer.Api 导入并启动后的下游代理是否可用。

当前它有两种测试模式：

- 单代理一对一测试：适合确认某一个下游代理是否可用
- 固定下游代理动态切换观察测试：适合重复请求同一个固定入口，并结合 API 返回的最近上游信息观察是否发生自动切换

它不引用 [ProxyTransfer.Tunnel](../ProxyTransfer.Tunnel)，而是直接使用最基础的 `HttpClient` handler 来测试代理：

- HTTP 代理走 `HttpClientHandler`
- SOCKS5 代理走 `SocketsHttpHandler`

默认情况下，它会读取 `proxy.txt` 的第一条有效代理来做一对一测试。代理格式支持：

- `http://host:port`
- `socks5://host:port`

测试方式是通过代理访问 `https://api.ipify.org/`，确认请求是否成功以及出口 IP 是什么。

## 使用方式

### 单代理一对一测试

1. 先启动 [ProxyTransfer.Api](../ProxyTransfer.Api) 和 [ProxyTransfer.Web](../ProxyTransfer.Web)
2. 在管理台导入并启动代理
3. 在“转发实例”区域复制一个正在运行的下游代理
4. 把这个代理保存到当前目录的 `proxy.txt` 第一行
5. 运行：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test
```

也可以显式指定模式：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test -- single
```

也可以直接传代理地址：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test -- single http://127.0.0.1:40000
```

或者指定其它文件路径：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test -- single /absolute/path/to/proxy.txt
```

### 固定下游代理动态切换观察测试

这个模式会重复访问同一个固定下游代理入口，并在 API 可达时同步读取 `/api/fixed-proxies`，输出：

- 当前观察到的出口 IP
- API 返回的最近选中上游
- 与上一轮相比是否发生了出口切换
- 与上一轮相比是否发生了上游切换

推荐前置条件：

1. 已在管理台“固定入口池模式”中导入上游池并创建固定入口
2. 固定入口已经启动
3. 最好把 `stickyMinutes` 设得较小，例如 `1`
4. 上游池里至少有两条可用上游，否则即使请求成功也看不到切换

使用方式：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test -- fixed http://127.0.0.1:1234
```

默认会尝试访问本地 API：`http://127.0.0.1:5080`。

如果 API 地址不同，可以显式指定：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test -- fixed http://127.0.0.1:1234 --api-base-url http://127.0.0.1:5080
```

如果你已经知道固定入口的 ID，也可以传入，以避免通过 `forwardedProxy` 反查：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test -- fixed http://127.0.0.1:1234 --fixed-id 5dcb7df6-7e37-47dc-8d11-31285021bc8c
```

还可以指定轮询次数和轮询间隔：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test -- fixed http://127.0.0.1:1234 --count 12 --interval-seconds 10
```

说明：

- 如果 API 不可达，程序会降级为“只观察出口 IP”
- 如果总观察时长小于 `stickyMinutes`，且当前上游一直正常，测试期间可能看不到切换
- 如果出现连接失败，固定入口有机会在后续请求中切换到其它健康上游

## proxy.txt 示例

```text
http://127.0.0.1:40000
```

空行和以 `#` 开头的行会被忽略；如果文件里有多条代理，当前测试程序只会取第一条有效代理。 
