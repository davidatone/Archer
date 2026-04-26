using Archer.Actors.Grains;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Archer.Actors;

public static class ActorsServiceCollectionExtensions
{
    public static IServiceCollection AddArcherActors(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<TurnWorkerOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<TurnWorkerOptions>();
        if (configuration is not null)
        {
            optionsBuilder.Bind(configuration.GetSection(TurnWorkerOptions.SectionName));
        }
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        return services;
    }
}
