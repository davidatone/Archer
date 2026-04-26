using Archer.Actors;
using Archer.Events;
using Archer.Mcp;
using Archer.Model;
using Archer.Persistence;
using Archer.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Serialization;

namespace Archer.Host;

/// <summary>
/// Single-process host wiring. Boots a local Orleans silo, registers all Archer services,
/// and configures System.Text.Json serialization for Domain/Application types so that they
/// can cross grain boundaries without requiring [GenerateSerializer].
/// </summary>
public static class ArcherHostBuilder
{
    public static IHostBuilder ConfigureArcher(
        this IHostBuilder builder,
        Action<HostBuilderContext, IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Default to Development under a debugger when no environment was explicitly chosen.
        if (System.Diagnostics.Debugger.IsAttached
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        }

        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            var basePath = AppContext.BaseDirectory;
            config.SetBasePath(basePath);
            config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            config.AddJsonFile(
                $"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json",
                optional: true,
                reloadOnChange: false);
            config.AddEnvironmentVariables();
            config.AddEnvironmentVariables(prefix: "ARCHER_");
        });

        builder.UseOrleans((ctx, silo) =>
        {
            silo.UseLocalhostClustering();
            silo.Services.AddSerializer(b =>
            {
                b.AddJsonSerializer(IsArcherDomainType);
            });
        });

        builder.ConfigureServices((ctx, services) =>
        {
            BindAzureOpenAIFromEnv(ctx.Configuration);

            // Agent definitions: scan the working directory's `agents/` and (if running from
            // a build output) the project-relative copy. Both default to optional.
            var agentDirs = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "agents"),
                Path.Combine(AppContext.BaseDirectory, "agents"),
            };

            services
                .AddArcherFilePersistence(ctx.Configuration)
                .AddArcherAgentDefinitions(agentDirs)
                .AddArcherEvents()
                .AddArcherTools(ctx.Configuration)
                .AddArcherMcp(ctx.Configuration)
                .AddArcherAgentFrameworkModel(ctx.Configuration)
                .AddArcherActors(ctx.Configuration)
                .AddArcherTelemetry(ctx.Configuration);

            services.AddLogging(b =>
            {
                b.AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.IncludeScopes = false;
                    o.TimestampFormat = "HH:mm:ss ";
                });
                b.SetMinimumLevel(LogLevel.Information);
            });

            configureServices?.Invoke(ctx, services);
        });

        return builder;
    }

    /// <summary>Mirror common Azure OpenAI env vars into the AzureOpenAI:* config section.</summary>
    private static void BindAzureOpenAIFromEnv(IConfiguration config)
    {
        if (config is not IConfigurationRoot root)
        {
            return;
        }

        Mirror(root, "AZURE_OPENAI_ENDPOINT", "AzureOpenAI:Endpoint");
        Mirror(root, "AZURE_OPENAI_API_KEY", "AzureOpenAI:ApiKey");
        Mirror(root, "AZURE_OPENAI_DEPLOYMENT", "AzureOpenAI:DefaultDeployment");
        Mirror(root, "AZURE_OPENAI_API_VERSION", "AzureOpenAI:ApiVersion");

        static void Mirror(IConfigurationRoot root, string env, string key)
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(value))
            {
                root[key] ??= value;
            }
        }
    }

    private static bool IsArcherDomainType(Type t) =>
        t.Namespace is { } ns &&
        (ns.StartsWith("Archer.Domain", StringComparison.Ordinal) ||
         ns.StartsWith("Archer.Application", StringComparison.Ordinal));
}
