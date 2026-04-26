using Archer.Application.Agents;
using Archer.Application.Persistence;
using Archer.Domain.Time;
using Archer.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Archer.Persistence.Tests;

public class PersistenceServiceCollectionExtensionsTests
{
    [Fact]
    public void AddArcherFilePersistence_registers_state_store_blob_store_and_clock()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArcherFilePersistence();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IAgentStateStore>().Should().BeOfType<FileAgentStateStore>();
        sp.GetRequiredService<IAgentBlobStore>().Should().BeOfType<InMemoryAgentBlobStore>();
        sp.GetRequiredService<ISystemClock>().Should().BeOfType<SystemClock>();
    }

    [Fact]
    public void AddArcherFilePersistence_does_not_overwrite_pre_registered_clock()
    {
        var services = new ServiceCollection();
        var customClock = new FakeClock();
        services.AddSingleton<ISystemClock>(customClock);
        services.AddArcherFilePersistence();
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISystemClock>().Should().BeSameAs(customClock);
    }

    [Fact]
    public void AddArcherFilePersistence_binds_configuration_options()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:StateDirectory"] = "/tmp/archer-test",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddArcherFilePersistence(config);
        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileAgentStateStoreOptions>>().Value;
        opts.StateDirectory.Should().Be("/tmp/archer-test");
    }

    [Fact]
    public void AddArcherFilePersistence_invokes_configure_callback()
    {
        var services = new ServiceCollection();
        services.AddArcherFilePersistence(configure: o => o.StateDirectory = "/x");
        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileAgentStateStoreOptions>>().Value;
        opts.StateDirectory.Should().Be("/x");
    }

    [Fact]
    public void AddArcherAgentDefinitions_registers_a_resolvable_registry()
    {
        var services = new ServiceCollection();
        services.AddArcherAgentDefinitions(Array.Empty<string>());
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IAgentDefinitionRegistry>().Should().NotBeNull();
    }

    private sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
