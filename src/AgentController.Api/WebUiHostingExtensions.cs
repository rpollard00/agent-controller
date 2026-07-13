namespace AgentController.Api;

/// <summary>
/// Configures production static-asset hosting and client-side route fallback for the web UI.
/// </summary>
public static class WebUiHostingExtensions
{
    public static WebApplication MapWebUiHosting(this WebApplication app)
    {
        // Hashed Vite assets are served directly. Existing endpoint routes still take
        // precedence over both fallback endpoints.
        app.UseStaticFiles();

        // Unknown API paths must remain HTTP 404 responses rather than receiving the SPA.
        app.MapFallback("/api/{**path}", () => Results.NotFound());
        app.MapFallbackToFile("index.html");

        return app;
    }
}
