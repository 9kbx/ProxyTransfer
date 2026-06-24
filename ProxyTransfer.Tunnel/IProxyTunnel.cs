using System.Net;

namespace ProxyTransfer.Tunnel;

public interface IProxyTunnel : IAsyncDisposable
{
    IPAddress ListenAddress { get; }

    string PublicHost { get; }

    int LocalPort { get; }

    string LocalProxyUri { get; }

    string RemoteProxyUri { get; }
}