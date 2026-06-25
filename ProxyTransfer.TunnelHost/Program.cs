using Microsoft.Extensions.Options;
using ProxyTransfer.TunnelHost;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TunnelHostOptions>(
    builder.Configuration.GetSection(TunnelHostOptions.SectionName)
);
builder.Services.AddSingleton<TunnelDefinitionStore>();
builder.Services.AddSingleton<TunnelHostRegistry>();
builder.Services.AddHostedService<TunnelHostLifecycleService>();

var hostOptions =
    builder.Configuration.GetSection(TunnelHostOptions.SectionName).Get<TunnelHostOptions>()
    ?? new TunnelHostOptions();
builder.WebHost.UseUrls(hostOptions.ManagementUrl);

var app = builder.Build();

var authOptions = app.Services.GetRequiredService<IOptions<TunnelHostOptions>>().Value;
if (string.IsNullOrWhiteSpace(authOptions.ManagementApiKey))
{
    throw new InvalidOperationException(
        "缺少 TunnelHost:ManagementApiKey 配置，出于安全原因拒绝启动。"
    );
}

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

        if (!string.Equals(providedApiKey, authOptions.ManagementApiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "无效的 x-apikey。" });
            return;
        }

        await next();
    }
);

app.MapGet(
    "/api/host",
    (TunnelHostRegistry registry, IOptions<TunnelHostOptions> options) =>
        Results.Ok(registry.GetHostStatus(options.Value))
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

await app.RunAsync();
