using System.Net;
using System.Net.Sockets;

namespace ProxyTransfer.Tunnel;

public sealed class Socks5ToSocks5Tunnel : IProxyTunnel
{
    private readonly ProxyEndpoint _remoteProxy;
    private readonly TcpListener _server;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _stopRegistration;
    private readonly Task _acceptLoopTask;
    private bool _disposed;

    private Socks5ToSocks5Tunnel(
        ProxyEndpoint remoteProxy,
        IPAddress listenAddress,
        int localPort,
        string publicHost,
        CancellationToken cancellationToken
    )
    {
        if (!remoteProxy.IsSocks5)
        {
            throw new ArgumentException(
                "Socks5ToSocks5Tunnel 仅支持 SOCKS5 上游代理。",
                nameof(remoteProxy)
            );
        }

        _remoteProxy = remoteProxy;
        ListenAddress = listenAddress;
        PublicHost = string.IsNullOrWhiteSpace(publicHost) ? listenAddress.ToString() : publicHost;

        _server = new TcpListener(listenAddress, localPort);

        if (cancellationToken.CanBeCanceled)
        {
            _stopRegistration = cancellationToken.Register(
                static state =>
                {
                    var tunnel = (Socks5ToSocks5Tunnel)state!;
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

    public string RemoteProxyUri => _remoteProxy.ProxyUri;

    public static Task<Socks5ToSocks5Tunnel> StartAsync(
        ProxyEndpoint remoteProxy,
        IPAddress? listenAddress = null,
        int localPort = 0,
        string? publicHost = null,
        CancellationToken cancellationToken = default
    )
    {
        var tunnel = new Socks5ToSocks5Tunnel(
            remoteProxy,
            listenAddress ?? IPAddress.Loopback,
            localPort,
            publicHost ?? (listenAddress ?? IPAddress.Loopback).ToString(),
            cancellationToken
        );

        return Task.FromResult(tunnel);
    }

    public static Task<Socks5ToSocks5Tunnel> StartAsync(
        string socksHost,
        int socksPort,
        string? user = null,
        string? pass = null,
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
                if (
                    !await Socks5Protocol
                        .NegotiateNoAuthAsync(clientStream, token)
                        .ConfigureAwait(false)
                )
                {
                    return;
                }

                (string targetHost, int targetPort) = await Socks5Protocol
                    .ReadConnectRequestAsync(clientStream, token)
                    .ConfigureAwait(false);

                await upstreamClient
                    .ConnectAsync(remoteProxy.Host, remoteProxy.Port, token)
                    .ConfigureAwait(false);
                await using var upstreamStream = upstreamClient.GetStream();

                try
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
                catch
                {
                    await Socks5Protocol
                        .WriteFailureResponseAsync(clientStream, 0x05, token)
                        .ConfigureAwait(false);
                    throw;
                }

                await Socks5Protocol
                    .WriteSuccessResponseAsync(clientStream, token)
                    .ConfigureAwait(false);

                var upstreamTask = clientStream.CopyToAsync(upstreamStream, token);
                var downstreamTask = upstreamStream.CopyToAsync(clientStream, token);
                await Task.WhenAny(upstreamTask, downstreamTask).ConfigureAwait(false);
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
