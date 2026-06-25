using Microsoft.Extensions.Options;
using ProxyTransfer.TunnelHost;
using ProxyTransfer.TunnelHost.Endpoints;

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

app.MapHostEndpoints();

await app.RunAsync();
