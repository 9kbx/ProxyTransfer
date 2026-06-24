using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProxyTransfer.Tunnel;

public sealed class Socks5ProxyTunnel : IAsyncDisposable
{
    private readonly ProxyEndpoint _remoteProxy;
    private readonly TcpListener _server;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _stopRegistration;
    private readonly Task _acceptLoopTask;
    private bool _disposed;

    private Socks5ProxyTunnel(
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
                    var tunnel = (Socks5ProxyTunnel)state!;
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

    public static Task<Socks5ProxyTunnel> StartAsync(
        ProxyEndpoint remoteProxy,
        IPAddress? listenAddress = null,
        int localPort = 0,
        string? publicHost = null,
        CancellationToken cancellationToken = default
    )
    {
        var tunnel = new Socks5ProxyTunnel(
            remoteProxy,
            listenAddress ?? IPAddress.Loopback,
            localPort,
            publicHost ?? (listenAddress ?? IPAddress.Loopback).ToString(),
            cancellationToken
        );

        return Task.FromResult(tunnel);
    }

    public static Task<Socks5ProxyTunnel> StartAsync(
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
                    await EstablishSocks5ConnectionAsync(
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

    private static async Task EstablishSocks5ConnectionAsync(
        NetworkStream upstreamStream,
        ProxyEndpoint remoteProxy,
        string targetHost,
        int targetPort,
        CancellationToken token
    )
    {
        await upstreamStream
            .WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x02 }, token)
            .ConfigureAwait(false);

        var handshakeResponse = new byte[2];
        await upstreamStream.ReadExactlyAsync(handshakeResponse, token).ConfigureAwait(false);

        if (handshakeResponse[0] != 0x05)
        {
            throw new InvalidOperationException("SOCKS5 握手版本不正确。");
        }

        if (handshakeResponse[1] == 0x02)
        {
            var userBytes = Encoding.UTF8.GetBytes(remoteProxy.UserName ?? string.Empty);
            var passwordBytes = Encoding.UTF8.GetBytes(remoteProxy.Password ?? string.Empty);
            var authRequest = new byte[3 + userBytes.Length + passwordBytes.Length];
            authRequest[0] = 0x01;
            authRequest[1] = (byte)userBytes.Length;
            Array.Copy(userBytes, 0, authRequest, 2, userBytes.Length);
            authRequest[2 + userBytes.Length] = (byte)passwordBytes.Length;
            Array.Copy(passwordBytes, 0, authRequest, 3 + userBytes.Length, passwordBytes.Length);

            await upstreamStream.WriteAsync(authRequest, token).ConfigureAwait(false);

            var authResponse = new byte[2];
            await upstreamStream.ReadExactlyAsync(authResponse, token).ConfigureAwait(false);
            if (authResponse[1] != 0x00)
            {
                throw new InvalidOperationException("SOCKS5 用户名/密码认证失败。");
            }
        }
        else if (handshakeResponse[1] != 0x00)
        {
            throw new InvalidOperationException(
                $"SOCKS5 服务端返回了不支持的认证方式: 0x{handshakeResponse[1]:X2}"
            );
        }

        await SendConnectCommandAsync(upstreamStream, targetHost, targetPort, token)
            .ConfigureAwait(false);
        await ValidateConnectResponseAsync(upstreamStream, token).ConfigureAwait(false);
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

    private static async Task SendConnectCommandAsync(
        NetworkStream socksStream,
        string targetHost,
        int targetPort,
        CancellationToken token
    )
    {
        if (targetPort is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPort), "目标端口超出有效范围。");
        }

        var hostBytes = Encoding.ASCII.GetBytes(targetHost);
        var command = new byte[7 + hostBytes.Length];
        command[0] = 0x05;
        command[1] = 0x01;
        command[2] = 0x00;
        command[3] = 0x03;
        command[4] = (byte)hostBytes.Length;
        Array.Copy(hostBytes, 0, command, 5, hostBytes.Length);
        command[5 + hostBytes.Length] = (byte)(targetPort >> 8);
        command[6 + hostBytes.Length] = (byte)targetPort;

        await socksStream.WriteAsync(command, token).ConfigureAwait(false);
    }

    private static async Task ValidateConnectResponseAsync(
        NetworkStream socksStream,
        CancellationToken token
    )
    {
        var fixedResponse = new byte[4];
        await socksStream.ReadExactlyAsync(fixedResponse, token).ConfigureAwait(false);

        if (fixedResponse[1] != 0x00)
        {
            throw new InvalidOperationException(
                $"SOCKS5 CONNECT 失败，错误码: 0x{fixedResponse[1]:X2}"
            );
        }

        int addressLength = fixedResponse[3] switch
        {
            0x01 => 4,
            0x03 => await ReadAddressLengthAsync(socksStream, token).ConfigureAwait(false),
            0x04 => 16,
            _ => throw new InvalidOperationException(
                $"未知的 SOCKS5 地址类型: 0x{fixedResponse[3]:X2}"
            ),
        };

        var trailingBytes = new byte[addressLength + 2];
        await socksStream.ReadExactlyAsync(trailingBytes, token).ConfigureAwait(false);
    }

    private static async Task<int> ReadAddressLengthAsync(
        NetworkStream socksStream,
        CancellationToken token
    )
    {
        var lengthBuffer = new byte[1];
        await socksStream.ReadExactlyAsync(lengthBuffer, token).ConfigureAwait(false);
        return lengthBuffer[0];
    }

    private static string FormatHost(string host) =>
        host.Contains(':', StringComparison.Ordinal)
        && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
}
