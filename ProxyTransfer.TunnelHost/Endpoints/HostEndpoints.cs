using Microsoft.Extensions.Options;

namespace ProxyTransfer.TunnelHost.Endpoints;

internal static class HostEndpoints
{
    public static void MapHostEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/host",
            (TunnelHostRegistry registry, IOptions<TunnelHostOptions> options) =>
                Results.Ok(registry.GetHostStatus(options.Value))
        );

        app.MapGet(
            "/api/host/port-range",
            (IOptions<TunnelHostOptions> options) =>
            {
                var opts = options.Value;
                if (!opts.ListenPortRangeStart.HasValue || !opts.ListenPortRangeEnd.HasValue)
                {
                    return Results.Ok(
                        new TunnelHostPortRangeResponse(
                            null,
                            null,
                            "未配置端口范围，系统将使用操作系统随机端口。"
                        )
                    );
                }

                return Results.Ok(
                    new TunnelHostPortRangeResponse(
                        opts.ListenPortRangeStart.Value,
                        opts.ListenPortRangeEnd.Value,
                        null
                    )
                );
            }
        );

        app.MapGet("/api/instances", (TunnelHostRegistry registry) => Results.Ok(registry.List()));

        app.MapGet(
            "/api/instances/{id:guid}",
            (Guid id, TunnelHostRegistry registry) =>
            {
                var instance = registry.Get(id);
                return instance is null ? Results.NotFound() : Results.Ok(instance);
            }
        );

        app.MapPost(
            "/api/direct",
            async (
                CreateDirectTunnelRequest request,
                TunnelHostRegistry registry,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    var created = await registry
                        .CreateDirectAsync(request, cancellationToken)
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
            "/api/pools",
            async (
                CreatePoolTunnelRequest request,
                TunnelHostRegistry registry,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    var created = await registry
                        .CreatePoolAsync(request, cancellationToken)
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
            "/api/instances/{id:guid}/start",
            async (
                Guid id,
                StartTunnelRequest? request,
                TunnelHostRegistry registry,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    var started = await registry
                        .StartAsync(id, request, cancellationToken)
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
            "/api/instances/{id:guid}/stop",
            async (Guid id, TunnelHostRegistry registry) =>
            {
                var stopped = await registry.StopAsync(id).ConfigureAwait(false);
                return stopped is null ? Results.NotFound() : Results.Ok(stopped);
            }
        );

        app.MapDelete(
            "/api/instances/{id:guid}",
            async (Guid id, TunnelHostRegistry registry) =>
            {
                var deleted = await registry.DeleteAsync(id).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
        );
    }
}
