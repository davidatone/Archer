using Archer.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Archer.Cli.Hosting;

/// <summary>
/// Builds and runs a single-process Orleans silo for the CLI invocation.
/// The host is started, the supplied delegate is run, and the host is gracefully shut down.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class CliHost
{
    public static async Task<int> RunAsync(
        Func<IServiceProvider, CancellationToken, Task<int>> action,
        Action<HostBuilderContext, IServiceCollection>? configure = null,
        CancellationToken cancellationToken = default)
    {
        using var lifetime = new CancellationTokenSource();
        using var registration = cancellationToken.Register(() => lifetime.Cancel());

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            lifetime.Cancel();
        };

        var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureArcher(configure);

        using var host = builder.Build();
        await host.StartAsync(lifetime.Token);
        try
        {
            return await action(host.Services, lifetime.Token);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}
