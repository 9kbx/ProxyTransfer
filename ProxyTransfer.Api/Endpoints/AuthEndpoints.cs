namespace ProxyTransfer.Api.Endpoints;

internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/auth/validate", () => Results.Ok(new { valid = true }));
    }
}
