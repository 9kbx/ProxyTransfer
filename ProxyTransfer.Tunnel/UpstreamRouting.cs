namespace ProxyTransfer.Tunnel;

public sealed record ProxyConnectRequest(string TargetHost, int TargetPort);

public sealed record UpstreamLease(Guid UpstreamId, ProxyEndpoint Endpoint);

public interface IUpstreamRouter
{
    ValueTask<UpstreamLease> SelectAsync(
        ProxyConnectRequest request,
        CancellationToken cancellationToken
    );

    ValueTask ReportSuccessAsync(UpstreamLease lease, CancellationToken cancellationToken);

    ValueTask ReportFailureAsync(
        UpstreamLease lease,
        Exception exception,
        CancellationToken cancellationToken
    );
}
