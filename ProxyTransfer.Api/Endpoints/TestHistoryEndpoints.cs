namespace ProxyTransfer.Api.Endpoints;

internal static class TestHistoryEndpoints
{
    public static void MapTestHistoryEndpoints(this WebApplication app)
    {
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

            await context.Response.SendFileAsync(
                Path.Combine(app.Environment.WebRootPath, "index.html")
            );
        });
    }
}
