using Microsoft.Extensions.Options;
using ProxyTransfer.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ApiKeyAuthOptions>(
    builder.Configuration.GetSection(ApiKeyAuthOptions.SectionName)
);
builder.Services.Configure<ProxyTunnelHostOptions>(builder.Configuration.GetSection("TunnelHost"));
builder.Services.AddSingleton<UpstreamPoolRegistry>();
builder.Services.AddHttpClient<TunnelHostApiClient>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<ProxyTunnelHostOptions>>().Value;
        client.BaseAddress = new Uri(options.ManagementUrl, UriKind.Absolute);
        var apiKey = string.IsNullOrWhiteSpace(options.ManagementApiKey)
            ? options.ApiKey
            : options.ManagementApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Add("x-apikey", apiKey);
        }
    }
);
builder.Services.AddSingleton<TunnelHostGateway>();
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

var apiKeyOptions = app.Services.GetRequiredService<IOptions<ApiKeyAuthOptions>>().Value;
if (string.IsNullOrWhiteSpace(apiKeyOptions.ApiKey))
{
    throw new InvalidOperationException("缺少 Auth:ApiKey 配置，无法启动 API 鉴权。");
}

if (string.IsNullOrWhiteSpace(hostOptions.ManagementUrl))
{
    throw new InvalidOperationException(
        "缺少 TunnelHost:ManagementUrl 配置，无法连接 TunnelHost。"
    );
}

if (
    string.IsNullOrWhiteSpace(hostOptions.ManagementApiKey)
    && string.IsNullOrWhiteSpace(hostOptions.ApiKey)
)
{
    throw new InvalidOperationException(
        "缺少 TunnelHost:ManagementApiKey 配置，无法调用 TunnelHost。"
    );
}

app.UseCors("frontend");
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(
    async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next();
            return;
        }

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await next();
            return;
        }

        if (!context.Request.Headers.TryGetValue("x-apikey", out var providedApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "缺少 x-apikey。" });
            return;
        }

        if (!string.Equals(providedApiKey, apiKeyOptions.ApiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "无效的 x-apikey。" });
            return;
        }

        await next();
    }
);

app.MapGet("/api/auth/validate", () => Results.Ok(new { valid = true }));

app.MapGet(
    "/api/tunnels",
    async (TunnelHostGateway gateway, CancellationToken cancellationToken) =>
        Results.Ok(await gateway.ListDirectAsync(cancellationToken).ConfigureAwait(false))
);

app.MapGet(
    "/api/batches",
    async (TunnelHostGateway gateway, CancellationToken cancellationToken) =>
        Results.Ok(await gateway.ListDirectBatchesAsync(cancellationToken).ConfigureAwait(false))
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
            var created = await gateway.ImportDirectAsync(request, cancellationToken);
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
            var created = await gateway.AddDirectAsync(request, cancellationToken);
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
            var started = await gateway.StartDirectAsync(id, request, cancellationToken);
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
        var stopped = await gateway.StopDirectAsync(id, cancellationToken);
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
            var tunnel = await gateway.GetDirectAsync(id, cancellationToken).ConfigureAwait(false);
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
    (string poolId, DeleteUpstreamPoolProxiesRequest request, UpstreamPoolRegistry registry) =>
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
            var created = await gateway.AddFixedAsync(request, cancellationToken);
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
            var started = await gateway.StartFixedAsync(id, cancellationToken);
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
        var stopped = await gateway.StopFixedAsync(id, cancellationToken);
        return stopped is null ? Results.NotFound() : Results.Ok(stopped);
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
                .TestFixedProxyAsync(fixedProxy, request, gateway.GetFixedAsync, cancellationToken)
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

app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "index.html"));
});

app.Run();
