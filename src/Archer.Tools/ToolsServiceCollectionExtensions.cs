using Archer.Application.Tools;
using Archer.Tools.Safety;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Archer.Tools;

public static class ToolsServiceCollectionExtensions
{
    public static IServiceCollection AddArcherTools(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<ToolOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<ToolOptions>();
        if (configuration is not null)
        {
            optionsBuilder.Bind(configuration.GetSection(ToolOptions.SectionName));
        }
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<IRepoPathResolver, RepoPathResolver>();

        services.AddSingleton<ITool, ListFilesTool>();
        services.AddSingleton<ITool, GrepTool>();
        services.AddSingleton<ITool, SearchPatternTool>();
        services.AddSingleton<ITool, TodoListTool>();

        services.AddSingleton<IToolRegistry, ToolRegistry>();
        return services;
    }
}
