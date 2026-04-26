using Archer.Application.Model;
using Archer.Model.AgentFramework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Archer.Model;

public static class ModelServiceCollectionExtensions
{
    public static IServiceCollection AddArcherAgentFrameworkModel(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<AzureOpenAIOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<AzureOpenAIOptions>();
        if (configuration is not null)
        {
            optionsBuilder.Bind(configuration.GetSection(AzureOpenAIOptions.SectionName));
        }
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<AzureOpenAIChatClientFactory>();
        services.AddSingleton<IChatClientFactory>(sp => sp.GetRequiredService<AzureOpenAIChatClientFactory>());
        services.AddSingleton<IAgentContextBuilder, AgentContextBuilder>();
        services.AddSingleton<IModelTurnRunner, AgentFrameworkModelTurnRunner>();
        return services;
    }
}
