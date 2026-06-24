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

app.Run();
