namespace ProxyTransfer.TunnelHost;

public sealed class TunnelHostLifecycleService : IHostedService
{
    private readonly TunnelHostRegistry _registry;

    public TunnelHostLifecycleService(TunnelHostRegistry registry)
    {
        _registry = registry;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _registry.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _registry.ShutdownAsync();
    }
}
