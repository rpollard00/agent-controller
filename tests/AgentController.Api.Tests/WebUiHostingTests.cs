using System.Net;
using AgentController.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentController.Api.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields."
)]
public sealed class WebUiHostingTests : IAsyncLifetime
{
    private const string SpaMarker = "agent-controller-spa-shell";

    private string _databasePath = null!;
    private string _webRootPath = null!;
    private WebUiHostingFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-hosting-{Guid.NewGuid():N}"
        );
        _webRootPath = Path.Combine(testRoot, "wwwroot");
        _databasePath = Path.Combine(testRoot, "hosting.db");

        Directory.CreateDirectory(Path.Combine(_webRootPath, "assets"));
        await File.WriteAllTextAsync(
            Path.Combine(_webRootPath, "index.html"),
            $"<!doctype html><html><body>{SpaMarker}</body></html>"
        );
        await File.WriteAllTextAsync(
            Path.Combine(_webRootPath, "assets", "app.css"),
            "body { color: white; }"
        );

        _factory = new WebUiHostingFactory(_databasePath, _webRootPath);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
        await database.Database.EnsureCreatedAsync();
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();

        var testRoot = Directory.GetParent(_webRootPath)?.FullName;
        if (testRoot is not null && Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ApiAndClientSideRoutes_Coexist()
    {
        using var apiResponse = await _client.GetAsync("/api/webui/repositories");
        Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);
        Assert.Equal("[]", await apiResponse.Content.ReadAsStringAsync());

        using var rootResponse = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
        Assert.Equal("AgentController API", await rootResponse.Content.ReadAsStringAsync());

        using var missingApiResponse = await _client.GetAsync("/api/not-a-controller-route");
        Assert.Equal(HttpStatusCode.NotFound, missingApiResponse.StatusCode);
        Assert.DoesNotContain(
            SpaMarker,
            await missingApiResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal
        );

        using var clientRouteResponse = await _client.GetAsync("/repositories/example/edit");
        Assert.Equal(HttpStatusCode.OK, clientRouteResponse.StatusCode);
        Assert.Equal("text/html", clientRouteResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            SpaMarker,
            await clientRouteResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task BuiltStaticAsset_IsServedWithoutSpaFallback()
    {
        using var response = await _client.GetAsync("/assets/app.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("body { color: white; }", await response.Content.ReadAsStringAsync());
    }

    private sealed class WebUiHostingFactory(string databasePath, string webRootPath)
        : SilentWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseWebRoot(webRootPath);
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["agentController:workerEnabled"] = "false",
                            ["workSource:provider"] = "LocalFake",
                            ["sourceControl:provider"] = "NoOp",
                            ["environmentProvider:provider"] = "NoOp",
                            ["runtime:provider"] = "NoOp",
                            ["feedback:enabled"] = "false",
                            ["feedback:provider"] = "None",
                        }
                    )
            );
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AgentControllerDbContext>();
                services.RemoveAll<DbContextOptions<AgentControllerDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AgentControllerDbContext>>();
                services.AddDbContext<AgentControllerDbContext>(options =>
                    options.UseSqlite($"Data Source={databasePath}")
                );
            });
        }
    }
}
