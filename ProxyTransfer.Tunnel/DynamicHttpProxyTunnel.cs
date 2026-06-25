using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProxyTransfer.Tunnel;

public sealed class DynamicHttpProxyTunnel : IProxyTunnel
{
    private readonly IUpstreamRouter _upstreamRouter;
    private readonly TcpListener _server;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _stopRegistration;
    private readonly Task _acceptLoopTask;
    private string _lastRemoteProxyUri = "dynamic://unselected";
    private bool _disposed;

    private DynamicHttpProxyTunnel(
        IUpstreamRouter upstreamRouter,
        IPAddress listenAddress,
        int localPort,
        string publicHost,
        CancellationToken cancellationToken
    )
    {
        _upstreamRouter = upstreamRouter;
        ListenAddress = listenAddress;
        PublicHost = string.IsNullOrWhiteSpace(publicHost) ? listenAddress.ToString() : publicHost;

        _server = new TcpListener(listenAddress, localPort);

        if (cancellationToken.CanBeCanceled)
        {
            _stopRegistration = cancellationToken.Register(
                static state =>
                {
                    var tunnel = (DynamicHttpProxyTunnel)state!;
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

    public string RemoteProxyUri => _lastRemoteProxyUri;

    public static Task<DynamicHttpProxyTunnel> StartAsync(
        IUpstreamRouter upstreamRouter,
        IPAddress? listenAddress = null,
        int localPort = 0,
        string? publicHost = null,
        CancellationToken cancellationToken = default
    )
    {
        var address = listenAddress ?? IPAddress.Loopback;
        var tunnel = new DynamicHttpProxyTunnel(
            upstreamRouter,
            address,
            localPort,
            publicHost ?? address.ToString(),
            cancellationToken
        );

        return Task.FromResult(tunnel);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cts.CancelAsync();
        _server.Stop();
        await _stopRegistration.DisposeAsync();

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
                _ = HandleClientAsync(clientSocket, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            await using (var clientStream = client.GetStream())
            {
                var request = await ReadHttpHeaderAsync(clientStream, token).ConfigureAwait(false);
                if (!TryParseConnectTarget(request, out var targetHost, out var targetPort))
                {
                    await WriteBadGatewayAsync(clientStream, token).ConfigureAwait(false);
                    return;
                }

                Exception? lastException = null;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var lease = await _upstreamRouter
                        .SelectAsync(new ProxyConnectRequest(targetHost, targetPort), token)
                        .ConfigureAwait(false);

                    using var upstreamClient = new TcpClient();

                    try
                    {
                        await upstreamClient
                            .ConnectAsync(lease.Endpoint.Host, lease.Endpoint.Port, token)
                            .ConfigureAwait(false);
                        await using var upstreamStream = upstreamClient.GetStream();

                        _lastRemoteProxyUri = lease.Endpoint.ProxyUri;

                        await ProxyUpstreamConnector
                            .EstablishConnectionAsync(
                                upstreamStream,
                                lease.Endpoint,
                                targetHost,
                                targetPort,
                                token
                            )
                            .ConfigureAwait(false);

                        await _upstreamRouter
                            .ReportSuccessAsync(lease, token)
                            .ConfigureAwait(false);

                        var responseBytes = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 Connection Established\r\n\r\n"
                        );
                        await clientStream.WriteAsync(responseBytes, token).ConfigureAwait(false);

                        var upstreamTask = clientStream.CopyToAsync(upstreamStream, token);
                        var downstreamTask = upstreamStream.CopyToAsync(clientStream, token);
                        await Task.WhenAny(upstreamTask, downstreamTask).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex)
                        when (ex is IOException or SocketException or InvalidOperationException)
                    {
                        lastException = ex;
                        await _upstreamRouter
                            .ReportFailureAsync(lease, ex, token)
                            .ConfigureAwait(false);
                    }
                }

                if (lastException is not null)
                {
                    await WriteBadGatewayAsync(clientStream, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (InvalidOperationException) { }
    }

    private static async Task WriteBadGatewayAsync(
        NetworkStream clientStream,
        CancellationToken token
    )
    {
        var responseBytes = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n");
        await clientStream.WriteAsync(responseBytes, token).ConfigureAwait(false);
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

    private static string FormatHost(string host) =>
        host.Contains(':', StringComparison.Ordinal)
        && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
}
