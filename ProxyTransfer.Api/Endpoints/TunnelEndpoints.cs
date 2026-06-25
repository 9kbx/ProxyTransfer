namespace ProxyTransfer.Api.Endpoints;

internal static class TunnelEndpoints
{
    public static void MapTunnelEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/tunnels",
            async (TunnelHostGateway gateway, CancellationToken cancellationToken) =>
                Results.Ok(await gateway.ListDirectAsync(cancellationToken).ConfigureAwait(false))
        );

        app.MapGet(
            "/api/batches",
            async (TunnelHostGateway gateway, CancellationToken cancellationToken) =>
                Results.Ok(
                    await gateway.ListDirectBatchesAsync(cancellationToken).ConfigureAwait(false)
                )
        );

        app.MapPost(
            "/api/tunnels/import",
            async (
                ImportProxiesRequest request,
                TunnelHostGateway gateway,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    var created = await gateway
                        .ImportDirectAsync(request, cancellationToken)
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
            "/api/tunnels",
            async (
                AddProxyRequest request,
                TunnelHostGateway gateway,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    var created = await gateway
                        .AddDirectAsync(request, cancellationToken)
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
            "/api/tunnels/{id:guid}/start",
            async (
                Guid id,
                StartProxyRequest? request,
                TunnelHostGateway gateway,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    var started = await gateway
                        .StartDirectAsync(id, request, cancellationToken)
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
            "/api/tunnels/{id:guid}/stop",
            async (Guid id, TunnelHostGateway gateway, CancellationToken cancellationToken) =>
            {
                var stopped = await gateway
                    .StopDirectAsync(id, cancellationToken)
                    .ConfigureAwait(false);
                return stopped is null ? Results.NotFound() : Results.Ok(stopped);
            }
        );

        app.MapPost(
            "/api/tunnels/{id:guid}/test",
            async (
                Guid id,
                TunnelHostGateway gateway,
                ProxyTestService tester,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    var tunnel = await gateway
                        .GetDirectAsync(id, cancellationToken)
                        .ConfigureAwait(false);
                    if (tunnel is null)
                    {
                        return Results.NotFound();
                    }

                    var result = await tester
                        .TestTunnelAsync(tunnel, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(result);
                }
                catch (Exception ex)
                    when (ex
                            is InvalidOperationException
                                or HttpRequestException
                                or ArgumentException
                    )
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );

        app.MapPost(
            "/api/tunnels/test-batch",
            async (
                BatchTunnelTestRequest request,
                TunnelHostGateway gateway,
                ProxyTestService tester,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(request.BatchId))
                    {
                        return Results.BadRequest(new { message = "批次号不能为空。" });
                    }

                    var candidates = (
                        await gateway.ListDirectAsync(cancellationToken).ConfigureAwait(false)
                    )
                        .Where(item =>
                            string.Equals(
                                item.BatchId,
                                request.BatchId,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        .ToArray();

                    if (candidates.Length == 0)
                    {
                        return Results.NotFound();
                    }

                    var targets = request.RunningOnly
                        ? candidates.Where(item => item.Status == "Running").ToArray()
                        : candidates;

                    var items = new List<BatchTunnelTestItemResponse>(targets.Length);
                    var successCount = 0;

                    foreach (var tunnel in targets)
                    {
                        try
                        {
                            var result = await tester
                                .TestTunnelAsync(tunnel, cancellationToken)
                                .ConfigureAwait(false);
                            successCount++;
                            items.Add(
                                new BatchTunnelTestItemResponse(
                                    tunnel.Id,
                                    tunnel.RemoteProxyDisplay,
                                    tunnel.ForwardedProxy,
                                    tunnel.Status,
                                    true,
                                    null,
                                    result.RunId,
                                    result.CompletedAt
                                )
                            );
                        }
                        catch (Exception ex)
                        {
                            items.Add(
                                new BatchTunnelTestItemResponse(
                                    tunnel.Id,
                                    tunnel.RemoteProxyDisplay,
                                    tunnel.ForwardedProxy,
                                    tunnel.Status,
                                    false,
                                    ex.Message,
                                    null,
                                    null
                                )
                            );
                        }
                    }

                    var response = new BatchTunnelTestResponse(
                        request.BatchId,
                        candidates.Length,
                        targets.Length,
                        successCount,
                        items.Count - successCount,
                        items
                    );

                    return Results.Ok(response);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );

        app.MapPost(
            "/api/tunnels/stop-batch",
            async (
                StopBatchRequest request,
                TunnelHostGateway gateway,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    var count = await gateway
                        .StopBatchAsync(request.BatchId, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(new { request.BatchId, stoppedCount = count });
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );
    }
}
