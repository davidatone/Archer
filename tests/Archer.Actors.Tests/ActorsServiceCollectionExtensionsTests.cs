using Archer.Actors;
using Archer.Actors.Grains;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Archer.Actors.Tests;

public class ActorsServiceCollectionExtensionsTests
{
    [Fact]
    public void AddArcherActors_registers_options_with_defaults()
    {
        var services = new ServiceCollection();
        services.AddArcherActors();
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<IOptions<TurnWorkerOptions>>().Value;
        opts.MaxIterations.Should().Be(9999);
    }

    [Fact]
    public void AddArcherActors_binds_configuration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TurnWorker:MaxIterations"] = "5",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddArcherActors(config);
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IOptions<TurnWorkerOptions>>().Value.MaxIterations.Should().Be(5);
    }

    [Fact]
    public void AddArcherActors_invokes_configure_callback()
    {
        var services = new ServiceCollection();
        services.AddArcherActors(configure: o => o.MaxIterations = 3);
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IOptions<TurnWorkerOptions>>().Value.MaxIterations.Should().Be(3);
    }
}
