namespace ProxyTransfer.Api.Endpoints;

internal static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/port-range",
            async (TunnelHostGateway gateway, CancellationToken cancellationToken) =>
            {
                var portRange = await gateway
                    .GetPortRangeAsync(cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(
                    new PortRangeResponse(portRange.StartPort, portRange.EndPort, portRange.Message)
                );
            }
        );
    }
}
