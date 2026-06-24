using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProxyTransfer.Tunnel;

public sealed class Socks5ToHttpTunnel : IProxyTunnel
{
    private readonly ProxyEndpoint _remoteProxy;
    private readonly TcpListener _server;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _stopRegistration;
    private readonly Task _acceptLoopTask;
    private bool _disposed;

    private Socks5ToHttpTunnel(
        ProxyEndpoint remoteProxy,
        IPAddress listenAddress,
        int localPort,
        string publicHost,
        CancellationToken cancellationToken
    )
    {
        _remoteProxy = remoteProxy;
        ListenAddress = listenAddress;
        PublicHost = string.IsNullOrWhiteSpace(publicHost) ? listenAddress.ToString() : publicHost;

        _server = new TcpListener(listenAddress, localPort);

        if (cancellationToken.CanBeCanceled)
        {
            _stopRegistration = cancellationToken.Register(
                static state =>
                {
                    var tunnel = (Socks5ToHttpTunnel)state!;
                    tunnel._cts.Cancel();
                    tunnel._server.Stop();
                },
                this
            );
        }

        _server.Start();
        LocalPort = ((IPEndPoint)_server.LocalEndpoint).Port;
        _acceptLoopTask = RunAcceptLoopAsync(_cts.Token);
    }

    public IPAddress ListenAddress { get; }

    public string PublicHost { get; }

    public int LocalPort { get; }

    public string LocalProxyUri => $"http://{FormatHost(PublicHost)}:{LocalPort}";

    public string RemoteProxyUri => _remoteProxy.ProxyUri;

    public static Task<Socks5ToHttpTunnel> StartAsync(
        ProxyEndpoint remoteProxy,
        IPAddress? listenAddress = null,
        int localPort = 0,
        string? publicHost = null,
        CancellationToken cancellationToken = default
    )
    {
        var tunnel = new Socks5ToHttpTunnel(
            remoteProxy,
            listenAddress ?? IPAddress.Loopback,
            localPort,
            publicHost ?? (listenAddress ?? IPAddress.Loopback).ToString(),
            cancellationToken
        );

        return Task.FromResult(tunnel);
    }

    public static Task<Socks5ToHttpTunnel> StartAsync(
        string socksHost,
        int socksPort,
        string user,
        string pass,
        string listenHost = "127.0.0.1",
        int localPort = 0,
        string? publicHost = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!IPAddress.TryParse(listenHost, out var listenAddress))
        {
            throw new FormatException($"无效的监听地址: {listenHost}");
        }

        return StartAsync(
            new ProxyEndpoint(ProxyProtocol.Socks5, socksHost, socksPort, user, pass),
            listenAddress,
            localPort,
            publicHost,
            cancellationToken
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _server.Stop();
        _stopRegistration.Dispose();

        try
        {
            await _acceptLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Dispose();
        }
    }

    private async Task RunAcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var clientSocket = await _server.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = HandleClientAsync(clientSocket, _remoteProxy, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task HandleClientAsync(
        TcpClient client,
        ProxyEndpoint remoteProxy,
        CancellationToken token
    )
    {
        try
        {
            using (client)
            await using (var clientStream = client.GetStream())
            using (var upstreamClient = new TcpClient())
            {
                string request = await ReadHttpHeaderAsync(clientStream, token)
                    .ConfigureAwait(false);
                if (!TryParseConnectTarget(request, out var targetHost, out var targetPort))
                {
                    return;
                }

                await upstreamClient
                    .ConnectAsync(remoteProxy.Host, remoteProxy.Port, token)
                    .ConfigureAwait(false);
                await using var upstreamStream = upstreamClient.GetStream();

                if (remoteProxy.IsSocks5)
                {
                    await Socks5Protocol
                        .EstablishUpstreamConnectionAsync(
                            upstreamStream,
                            remoteProxy,
                            targetHost,
                            targetPort,
                            token
                        )
                        .ConfigureAwait(false);
                }
                else if (remoteProxy.IsHttp)
                {
                    await EstablishHttpProxyConnectionAsync(
                            upstreamStream,
                            remoteProxy,
                            targetHost,
                            targetPort,
                            token
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"不支持的上游代理协议: {remoteProxy.Protocol}"
                    );
                }

                var responseBytes = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 Connection Established\r\n\r\n"
                );
                await clientStream.WriteAsync(responseBytes, token).ConfigureAwait(false);

                var upstreamTask = clientStream.CopyToAsync(upstreamStream, token);
                var downstreamTask = upstreamStream.CopyToAsync(clientStream, token);
                await Task.WhenAny(upstreamTask, downstreamTask).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
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
            int bytesRead = await clientStream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new IOException("客户端在发送完整 HTTP 请求头前断开。");
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
            if (builder.Length > 16 * 1024)
            {
                throw new InvalidOperationException("HTTP 请求头过大，已拒绝处理。");
            }
        }

        return builder.ToString();
    }

    private static bool TryParseConnectTarget(string request, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        var firstLine = request.Split("\r\n", 2, StringSplitOptions.None)[0];
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (
            parts.Length < 2
            || !string.Equals(parts[0], "CONNECT", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        var separatorIndex = parts[1].LastIndexOf(':');
        if (separatorIndex <= 0)
        {
            return false;
        }

        host = parts[1][..separatorIndex];
        return int.TryParse(parts[1][(separatorIndex + 1)..], out port);
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

    private static string FormatHost(string host) =>
        host.Contains(':', StringComparison.Ordinal)
        && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
}