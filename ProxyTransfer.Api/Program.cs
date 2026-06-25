using Microsoft.Extensions.Options;
using ProxyTransfer.Api;
using ProxyTransfer.Api.Endpoints;

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

app.MapAuthEndpoints();
app.MapTunnelEndpoints();
app.MapUpstreamPoolEndpoints();
app.MapFixedProxyEndpoints();
app.MapTestHistoryEndpoints();

app.Run();
