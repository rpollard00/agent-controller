using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AgentController.ServiceDefaults;

/// <summary>
/// Shared Aspire conventions for OpenTelemetry, health checks,
/// service discovery, and HTTP resilience.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Registers OpenTelemetry tracing/metrics/logging, service discovery,
    /// default health checks, and HTTP resilience policies.
    ///
    /// Call once during host construction in every project that should
    /// participate in the Aspire dashboard (API, migrations runner, etc.).
    /// </summary>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default for all outbound HttpClient calls.
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default for all outbound HttpClient calls.
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict resilience to specific URL schemes:
        // http.AddStandardResilienceHandler().AddServiceDiscovery();

        return builder;
    }

    /// <summary>
    /// Adds default liveness and startup health checks registered
    /// by AddServiceDefaults.
    /// </summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services
            .AddHealthChecks()
            // Add a default liveness check to ensure the app is responsive.
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        return builder;
    }

    /// <summary>
    /// Configures OpenTelemetry tracing, metrics, and logging exporters.
    /// Sends telemetry to the Aspire dashboard via OTLP when the standard
    /// OTEL_EXPORTER_OTLP_ENDPOINT environment variable is set.
    /// </summary>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                if (builder.Environment.IsDevelopment())
                {
                    // Show all spans in development for easy debugging.
                    tracing.SetSampler(new AlwaysOnSampler());
                }

                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    /// <summary>
    /// Adds the OTLP exporter when the standard OTEL_EXPORTER_OTLP_ENDPOINT
    /// environment variable is present. No exporter is added when the variable
    /// is missing, so telemetry is a no-op in environments without an OTLP
    /// collector or the Aspire dashboard.
    /// </summary>
    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Maps the default Aspire health-check endpoints (/health and /alive)
    /// on the web application. Only call this from web API projects;
    /// non-HTTP hosts (e.g. migration runner) should skip this step.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // The /health endpoint returns 200 when all health checks pass.
        // The /alive endpoint returns 200 when the app is live (liveness only).
        app.MapHealthChecks("/health");

        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
        });

        return app;
    }
}
