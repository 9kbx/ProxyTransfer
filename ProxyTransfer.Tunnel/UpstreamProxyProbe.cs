using System.Net.Sockets;

namespace ProxyTransfer.Tunnel;

public static class UpstreamProxyProbe
{
    public static async Task ProbeAsync(
        ProxyEndpoint remoteProxy,
        string targetHost,
        int targetPort,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var upstreamClient = new TcpClient();
        await upstreamClient
            .ConnectAsync(remoteProxy.Host, remoteProxy.Port, timeoutCts.Token)
            .ConfigureAwait(false);
        await using var upstreamStream = upstreamClient.GetStream();

        await ProxyUpstreamConnector
            .EstablishConnectionAsync(
                upstreamStream,
                remoteProxy,
                targetHost,
                targetPort,
                timeoutCts.Token
            )
            .ConfigureAwait(false);
    }
}
