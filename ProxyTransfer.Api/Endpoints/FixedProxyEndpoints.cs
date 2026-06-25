namespace ProxyTransfer.Api.Endpoints;

internal static class FixedProxyEndpoints
{
    public static void MapFixedProxyEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/fixed-proxies",
            async (TunnelHostGateway gateway, CancellationToken cancellationToken) =>
                Results.Ok(await gateway.ListFixedAsync(cancellationToken).ConfigureAwait(false))
        );

        app.MapPost(
            "/api/fixed-proxies",
            async (
                FixedProxyRequest request,
                TunnelHostGateway gateway,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    var created = await gateway
                        .AddFixedAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(created);
                }
                catch (Exception ex)
                    when (ex is FormatException or InvalidOperationException or ArgumentException)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );

        app.MapPost(
            "/api/fixed-proxies/{id:guid}/start",
            async (Guid id, TunnelHostGateway gateway, CancellationToken cancellationToken) =>
            {
                try
                {
                    var started = await gateway
                        .StartFixedAsync(id, cancellationToken)
                        .ConfigureAwait(false);
                    return started is null ? Results.NotFound() : Results.Ok(started);
                }
                catch (Exception ex)
                    when (ex is FormatException or InvalidOperationException or ArgumentException)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );

        app.MapPost(
            "/api/fixed-proxies/{id:guid}/stop",
            async (Guid id, TunnelHostGateway gateway, CancellationToken cancellationToken) =>
            {
                var stopped = await gateway
                    .StopFixedAsync(id, cancellationToken)
                    .ConfigureAwait(false);
                return stopped is null ? Results.NotFound() : Results.Ok(stopped);
            }
        );

        app.MapDelete(
            "/api/fixed-proxies/{id:guid}",
            async (Guid id, TunnelHostGateway gateway, CancellationToken cancellationToken) =>
            {
                var deleted = await gateway
                    .DeleteFixedAsync(id, cancellationToken)
                    .ConfigureAwait(false);
                return deleted ? Results.Ok(new { id, deleted = true }) : Results.NotFound();
            }
        );

        app.MapPost(
            "/api/fixed-proxies/{id:guid}/test",
            async (
                Guid id,
                ProxyTestRequest? request,
                TunnelHostGateway gateway,
                ProxyTestService tester,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    var fixedProxy = await gateway
                        .GetFixedAsync(id, cancellationToken)
                        .ConfigureAwait(false);
                    if (fixedProxy is null)
                    {
                        return Results.NotFound();
                    }

                    var result = await tester
                        .TestFixedProxyAsync(
                            fixedProxy,
                            request,
                            gateway.GetFixedAsync,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return Results.Ok(result);
                }
                catch (Exception ex)
                    when (ex
                            is InvalidOperationException
                                or HttpRequestException
                                or ArgumentException
                                or ArgumentOutOfRangeException
                    )
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );
    }
}
