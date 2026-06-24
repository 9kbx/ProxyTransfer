using Microsoft.Extensions.Options;
using ProxyTransfer.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ProxyTunnelHostOptions>(builder.Configuration.GetSection("TunnelHost"));
builder.Services.AddSingleton(static serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<ProxyTunnelHostOptions>>().Value;
    return new ProxyTunnelRegistry(options);
});
builder.Services.AddSingleton(static serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<ProxyTunnelHostOptions>>().Value;
    return new UpstreamPoolRegistry(options);
});
builder.Services.AddSingleton(static serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<ProxyTunnelHostOptions>>().Value;
    var upstreamPools = serviceProvider.GetRequiredService<UpstreamPoolRegistry>();
    return new FixedProxyRegistry(options, upstreamPools);
});
builder.Services.AddSingleton<ProxyTestService>();
builder.Services.AddHostedService<UpstreamProbeService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "frontend",
        policy =>
        {
            var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
            if (origins is { Length: > 0 })
            {
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
                return;
            }

            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        }
    );
});

var hostOptions =
    builder.Configuration.GetSection("TunnelHost").Get<ProxyTunnelHostOptions>()
    ?? new ProxyTunnelHostOptions();
builder.WebHost.UseUrls(hostOptions.ApiUrl);

var app = builder.Build();

app.UseCors("frontend");

app.MapGet("/", () => Results.Redirect("/api/tunnels"));

app.MapGet("/api/tunnels", (ProxyTunnelRegistry registry) => Results.Ok(registry.List()));

app.MapGet("/api/batches", (ProxyTunnelRegistry registry) => Results.Ok(registry.ListBatches()));

app.MapPost(
    "/api/tunnels/import",
    async (
        ImportProxiesRequest request,
        ProxyTunnelRegistry registry,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            var created = await registry.ImportAsync(request, cancellationToken);
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
        ProxyTunnelRegistry registry,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            var created = await registry.AddAsync(request, cancellationToken);
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
        ProxyTunnelRegistry registry,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            var started = await registry.StartAsync(id, request, cancellationToken);
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
    async (Guid id, ProxyTunnelRegistry registry) =>
    {
        var stopped = await registry.StopAsync(id);
        return stopped is null ? Results.NotFound() : Results.Ok(stopped);
    }
);

app.MapPost(
    "/api/tunnels/{id:guid}/test",
    async (
        Guid id,
        ProxyTunnelRegistry registry,
        ProxyTestService tester,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            var tunnel = registry.Get(id);
            if (tunnel is null)
            {
                return Results.NotFound();
            }

            var result = await tester.TestTunnelAsync(tunnel, cancellationToken);
            return Results.Ok(result);
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }
);

app.MapPost(
    "/api/tunnels/test-batch",
    async (
        BatchTunnelTestRequest request,
        ProxyTunnelRegistry registry,
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

            var candidates = registry
                .List()
                .Where(item =>
                    string.Equals(item.BatchId, request.BatchId, StringComparison.OrdinalIgnoreCase)
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
    async (StopBatchRequest request, ProxyTunnelRegistry registry) =>
    {
        try
        {
            var count = await registry.StopBatchAsync(request.BatchId);
            return Results.Ok(new { request.BatchId, stoppedCount = count });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }
);

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
                var result = await tester
                    .TestUpstreamProxyAsync(upstream, cancellationToken)
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
                        new InvalidOperationException(result.ErrorMessage ?? "上游代理测试失败。")
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
    (string? poolId, ProxyTestService tester) => Results.Ok(tester.GetUpstreamPoolHistory(poolId))
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

app.MapGet("/api/fixed-proxies", (FixedProxyRegistry registry) => Results.Ok(registry.List()));

app.MapPost(
    "/api/fixed-proxies",
    async (
        FixedProxyRequest request,
        FixedProxyRegistry registry,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            var created = await registry.AddAsync(request, cancellationToken);
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
    async (Guid id, FixedProxyRegistry registry, CancellationToken cancellationToken) =>
    {
        try
        {
            var started = await registry.StartAsync(id, cancellationToken);
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
    async (Guid id, FixedProxyRegistry registry) =>
    {
        var stopped = await registry.StopAsync(id);
        return stopped is null ? Results.NotFound() : Results.Ok(stopped);
    }
);

app.MapPost(
    "/api/fixed-proxies/{id:guid}/test",
    async (
        Guid id,
        ProxyTestRequest? request,
        FixedProxyRegistry registry,
        ProxyTestService tester,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            var fixedProxy = registry.Get(id);
            if (fixedProxy is null)
            {
                return Results.NotFound();
            }

            var result = await tester
                .TestFixedProxyAsync(fixedProxy, request, registry.Get, cancellationToken)
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

app.MapGet(
    "/api/test-history",
    (string? mode, Guid? resourceId, ProxyTestService tester) =>
        Results.Ok(tester.GetHistory(mode, resourceId))
);

app.Run();
