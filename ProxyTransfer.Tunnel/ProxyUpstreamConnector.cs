using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProxyTransfer.Tunnel;

internal static class ProxyUpstreamConnector
{
    public static Task EstablishConnectionAsync(
        NetworkStream upstreamStream,
        ProxyEndpoint remoteProxy,
        string targetHost,
        int targetPort,
        CancellationToken token
    )
    {
        return remoteProxy.Protocol switch
        {
            ProxyProtocol.Socks5 => Socks5Protocol.EstablishUpstreamConnectionAsync(
                upstreamStream,
                remoteProxy,
                targetHost,
                targetPort,
                token
            ),
            ProxyProtocol.Http => EstablishHttpProxyConnectionAsync(
                upstreamStream,
                remoteProxy,
                targetHost,
                targetPort,
                token
            ),
            _ => throw new InvalidOperationException(
                $"不支持的上游代理协议: {remoteProxy.Protocol}"
            ),
        };
    }

    private static async Task EstablishHttpProxyConnectionAsync(
        NetworkStream upstreamStream,
        ProxyEndpoint remoteProxy,
        string targetHost,
        int targetPort,
        CancellationToken token
    )
    {
        var builder = new StringBuilder()
            .Append("CONNECT ")
            .Append(targetHost)
            .Append(':')
            .Append(targetPort)
            .Append(" HTTP/1.1\r\nHost: ")
            .Append(targetHost)
            .Append(':')
            .Append(targetPort)
            .Append("\r\nProxy-Connection: Keep-Alive\r\n");

        if (remoteProxy.HasCredentials)
        {
            var rawCredentials = $"{remoteProxy.UserName}:{remoteProxy.Password}";
            var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawCredentials));
            builder.Append("Proxy-Authorization: Basic ").Append(encodedCredentials).Append("\r\n");
        }

        builder.Append("\r\n");

        var requestBytes = Encoding.ASCII.GetBytes(builder.ToString());
        await upstreamStream.WriteAsync(requestBytes, token).ConfigureAwait(false);

        var response = await ReadHttpHeaderAsync(upstreamStream, token).ConfigureAwait(false);
        ValidateHttpConnectResponse(response);
    }

    private static async Task<string> ReadHttpHeaderAsync(
        NetworkStream clientStream,
        CancellationToken token
    )
    {
        var buffer = new byte[1024];
        var builder = new StringBuilder();

        while (!builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var bytesRead = await clientStream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new IOException("上游 HTTP 代理在返回完整响应头前断开。");
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
            if (builder.Length > 16 * 1024)
            {
                throw new InvalidOperationException("上游 HTTP 代理响应头过大，已拒绝处理。");
            }
        }

        return builder.ToString();
    }

    private static void ValidateHttpConnectResponse(string response)
    {
        var firstLine = response.Split("\r\n", 2, StringSplitOptions.None)[0];
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var statusCode))
        {
            throw new InvalidOperationException("HTTP 代理返回了无效的响应。");
        }

        if (statusCode != (int)HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"HTTP CONNECT 失败，状态码: {statusCode}");
        }
    }
}
