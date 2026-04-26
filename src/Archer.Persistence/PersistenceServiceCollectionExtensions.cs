using Archer.Application.Agents;
using Archer.Application.Persistence;
using Archer.Domain.Time;
using Archer.Persistence.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Archer.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddArcherFilePersistence(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<FileAgentStateStoreOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<FileAgentStateStoreOptions>();
        if (configuration is not null)
        {
            optionsBuilder.Bind(configuration.GetSection(FileAgentStateStoreOptions.SectionName));
        }
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<IAgentStateStore, FileAgentStateStore>();
        services.AddSingleton<IAgentBlobStore, InMemoryAgentBlobStore>();
        services.TryAddSingletonClock();
        return services;
    }

    /// <summary>
    /// Register an <see cref="IAgentDefinitionRegistry"/> populated by scanning the given
    /// directories for *.yaml agent profiles. Built-in/embedded definitions can be added
    /// to the returned registry via further DI extensions.
    /// </summary>
    public static IServiceCollection AddArcherAgentDefinitions(
        this IServiceCollection services,
        IEnumerable<string> directories)
    {
        services.AddSingleton<IAgentDefinitionRegistry>(sp =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("AgentDefinitionRegistry");
            return AgentDefinitionRegistry.FromDirectories(directories, logger);
        });
        return services;
    }

    private static void TryAddSingletonClock(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(ISystemClock)))
        {
            services.AddSingleton<ISystemClock, SystemClock>();
        }
    }
}
