namespace ProxyTransfer.Api.Endpoints;

internal static class UpstreamPoolEndpoints
{
    public static void MapUpstreamPoolEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/upstream-pools",
            (UpstreamPoolRegistry registry) => Results.Ok(registry.ListPools())
        );

        app.MapGet(
            "/api/upstream-pools/{poolId}",
            (string poolId, UpstreamPoolRegistry registry) =>
            {
                try
                {
                    return Results.Ok(registry.GetPool(poolId));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );

        app.MapPost(
            "/api/upstream-pools/import",
            (ImportUpstreamPoolRequest request, UpstreamPoolRegistry registry) =>
            {
                try
                {
                    return Results.Ok(registry.Import(request));
                }
                catch (Exception ex)
                    when (ex is FormatException or InvalidOperationException or ArgumentException)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );

        app.MapPost(
            "/api/upstream-pools/{poolId}/append",
            (string poolId, UpdateUpstreamPoolRequest request, UpstreamPoolRegistry registry) =>
            {
                try
                {
                    return Results.Ok(registry.Append(poolId, request));
                }
                catch (Exception ex)
                    when (ex is FormatException or InvalidOperationException or ArgumentException)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );

        app.MapPost(
            "/api/upstream-pools/{poolId}/delete",
            (
                string poolId,
                DeleteUpstreamPoolProxiesRequest request,
                UpstreamPoolRegistry registry
            ) =>
            {
                try
                {
                    return Results.Ok(registry.DeleteProxies(poolId, request));
                }
                catch (Exception ex)
                    when (ex is FormatException or InvalidOperationException or ArgumentException)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );

        app.MapPost(
            "/api/upstream-pools/{poolId}/test",
            async (
                string poolId,
                UpstreamPoolTestRequest? request,
                UpstreamPoolRegistry registry,
                ProxyTestService tester,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    var pool = registry.GetPool(poolId);
                    var targetIds = request
                        ?.UpstreamIds?.Where(static id => id != Guid.Empty)
                        .Distinct()
                        .ToArray();
                    var candidates =
                        targetIds is { Length: > 0 }
                            ? pool.Items.Where(item => targetIds.Contains(item.Id)).ToArray()
                        : request?.UpstreamId is Guid upstreamId
                            ? pool.Items.Where(item => item.Id == upstreamId).ToArray()
                        : pool.Items.ToArray();

                    if (candidates.Length == 0)
                    {
                        return Results.NotFound();
                    }

                    var items = new List<UpstreamProxyTestItemResponse>(candidates.Length);
                    foreach (var upstream in candidates)
                    {
                        var endpoint = registry.GetEndpoint(poolId, upstream.Id);
                        var result = await tester
                            .TestUpstreamProxyAsync(
                                upstream.Id,
                                upstream.ProxyDisplay,
                                endpoint,
                                request?.TestProvider,
                                cancellationToken
                            )
                            .ConfigureAwait(false);

                        if (result.Success)
                        {
                            registry.MarkSuccess(poolId, upstream.Id);
                        }
                        else
                        {
                            registry.MarkFailure(
                                poolId,
                                upstream.Id,
                                new InvalidOperationException(
                                    result.ErrorMessage ?? "上游代理测试失败。"
                                )
                            );
                        }

                        items.Add(result);
                    }

                    var response = tester.RememberUpstreamPoolTest(
                        new UpstreamPoolTestResponse(
                            Guid.NewGuid(),
                            DateTimeOffset.Now,
                            poolId,
                            candidates.Length,
                            items.Count(static item => item.Success),
                            items.Count(static item => !item.Success),
                            items
                        )
                    );

                    return Results.Ok(response);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );

        app.MapGet(
            "/api/upstream-pool-test-history",
            (string? poolId, ProxyTestService tester) =>
                Results.Ok(tester.GetUpstreamPoolHistory(poolId))
        );

        app.MapDelete(
            "/api/upstream-pool-test-history/{runId:guid}",
            (Guid runId, ProxyTestService tester) =>
            {
                var deleted = tester.DeleteUpstreamPoolTestRun(runId);
                return deleted ? Results.Ok(new { runId, deleted = true }) : Results.NotFound();
            }
        );

        app.MapPost(
            "/api/upstream-pool-test-history/clear",
            (string poolId, ProxyTestService tester) =>
            {
                try
                {
                    var removedCount = tester.ClearUpstreamPoolHistory(poolId);
                    return Results.Ok(new { poolId, removedCount });
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            }
        );
    }
}
