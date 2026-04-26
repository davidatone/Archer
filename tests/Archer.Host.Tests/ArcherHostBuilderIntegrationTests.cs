using Archer.Application.Agents;
using Archer.Application.Events;
using Archer.Application.Persistence;
using Archer.Application.Tools;
using Archer.Domain.Time;
using Archer.Host;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Archer.Host.Tests;

/// <summary>
/// Drives <see cref="ArcherHostBuilder.ConfigureArcher"/> end-to-end: builds a real host,
/// resolves the major Archer services, then shuts down. Verifies the wiring chain (Persistence
/// → Events → Tools → MCP → Model → Actors → Telemetry) holds together as configured by the
/// builder.
/// </summary>
public class ArcherHostBuilderIntegrationTests
{
    [Fact]
    public async Task ConfigureArcher_builds_a_host_that_resolves_all_major_services()
    {
        var prevEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var prevApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        // Provide a placeholder so the Azure factory doesn't try to authenticate at startup.
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "test-key");
        try
        {
            var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .ConfigureArcher();
            using var host = hostBuilder.Build();

            // Resolution alone touches the registration pipeline that the builder set up.
            host.Services.GetRequiredService<IAgentStateStore>().Should().NotBeNull();
            host.Services.GetRequiredService<IAgentBlobStore>().Should().NotBeNull();
            host.Services.GetRequiredService<IAgentDefinitionRegistry>().Should().NotBeNull();
            host.Services.GetRequiredService<IAgentEventSink>().Should().NotBeNull();
            host.Services.GetRequiredService<IToolRegistry>().Should().NotBeNull();
            host.Services.GetRequiredService<ISystemClock>().Should().NotBeNull();

            // Light-weight start/stop to exercise the lifecycle hooks (Orleans + hosted services).
            // We use a short cancellation so this stays fast even if a hosted service hangs.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StartAsync(cts.Token);
            await host.StopAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", prevEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", prevApiKey);
        }
    }

    [Fact]
    public void ConfigureArcher_accepts_a_user_supplied_service_callback()
    {
        var sentinelCalled = false;
        var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureArcher((_, services) =>
            {
                sentinelCalled = true;
                services.AddSingleton(new Sentinel());
            });
        using var host = hostBuilder.Build();
        sentinelCalled.Should().BeTrue();
        host.Services.GetService<Sentinel>().Should().NotBeNull();
    }

    private sealed class Sentinel { }
}
