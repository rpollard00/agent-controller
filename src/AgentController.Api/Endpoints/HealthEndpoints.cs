namespace AgentController.Api.Endpoints;

/// <summary>
/// Health and root endpoint group: GET / and GET /health.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => "AgentController API");

        app.MapGet(
            "/health",
            () => Results.Ok(new { Status = "Healthy", Timestamp = DateTimeOffset.UtcNow })
        );

        return app;
    }
}
