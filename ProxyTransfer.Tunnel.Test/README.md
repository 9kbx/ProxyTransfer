# ProxyTransfer.Tunnel.Test

这个项目用于验证 ProxyTransfer.Api 导入并启动后的下游代理是否可用。

它不引用 [ProxyTransfer.Tunnel](../ProxyTransfer.Tunnel)，而是直接使用最基础的 `HttpClient` handler 来测试代理：

- HTTP 代理走 `HttpClientHandler`
- SOCKS5 代理走 `SocketsHttpHandler`

它会读取 `proxy.txt` 中的代理列表，按每行一个地址依次测试：

- `http://host:port`
- `socks5://host:port`

测试方式是通过代理访问 `https://api.ipify.org/`，确认请求是否成功以及出口 IP 是什么。

## 使用方式

1. 先启动 [ProxyTransfer.Api](../ProxyTransfer.Api) 和 [ProxyTransfer.Web](../ProxyTransfer.Web)
2. 在管理台导入并启动代理
3. 在“转发实例”区域复制正在运行的下游代理
4. 把复制结果保存到当前目录的 `proxy.txt`
5. 运行：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test
```

也可以指定其它文件路径：

```bash
dotnet run --project ProxyTransfer.Tunnel.Test -- /absolute/path/to/proxy-list.txt
```

## proxy.txt 示例

```text
http://127.0.0.1:40000
socks5://127.0.0.1:41000
```

空行和以 `#` 开头的行会被忽略。
