using System.Net;
using System.Net.Sockets;

namespace ProxyTransfer.Tunnel;

public sealed class DynamicSocks5ProxyTunnel : IProxyTunnel
{
    private readonly IUpstreamRouter _upstreamRouter;
    private readonly TcpListener _server;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _stopRegistration;
    private readonly Task _acceptLoopTask;
    private string _lastRemoteProxyUri = "dynamic://unselected";
    private bool _disposed;

    private DynamicSocks5ProxyTunnel(
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
                    var tunnel = (DynamicSocks5ProxyTunnel)state!;
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

    public string LocalProxyUri => $"socks5://{FormatHost(PublicHost)}:{LocalPort}";

    public string RemoteProxyUri => _lastRemoteProxyUri;

    public static Task<DynamicSocks5ProxyTunnel> StartAsync(
        IUpstreamRouter upstreamRouter,
        IPAddress? listenAddress = null,
        int localPort = 0,
        string? publicHost = null,
        CancellationToken cancellationToken = default
    )
    {
        var address = listenAddress ?? IPAddress.Loopback;
        var tunnel = new DynamicSocks5ProxyTunnel(
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
                if (
                    !await Socks5Protocol
                        .NegotiateNoAuthAsync(clientStream, token)
                        .ConfigureAwait(false)
                )
                {
                    return;
                }

                var (targetHost, targetPort) = await Socks5Protocol
                    .ReadConnectRequestAsync(clientStream, token)
                    .ConfigureAwait(false);

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

                        await Socks5Protocol
                            .WriteSuccessResponseAsync(clientStream, token)
                            .ConfigureAwait(false);

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
                    await Socks5Protocol
                        .WriteFailureResponseAsync(clientStream, 0x05, token)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (InvalidOperationException) { }
    }

    private static string FormatHost(string host) =>
        host.Contains(':', StringComparison.Ordinal)
        && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
}
