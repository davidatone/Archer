using Archer.Application.Tools;
using Archer.Persistence;
using Archer.Tools;
using Archer.Tools.Safety;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Archer.Tools.Tests;

public class ToolsServiceCollectionExtensionsTests
{
    [Fact]
    public void AddArcherTools_registers_tools_and_registry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArcherFilePersistence(); // TodoListTool needs IAgentBlobStore + ISystemClock
        services.AddArcherTools();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IRepoPathResolver>().Should().BeOfType<RepoPathResolver>();
        sp.GetRequiredService<IToolRegistry>().Should().NotBeNull();
        sp.GetServices<ITool>().Should().HaveCount(4);
    }

    [Fact]
    public void AddArcherTools_binds_configuration_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tools:MaxFileBytes"] = "999",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddArcherTools(config);
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ToolOptions>>().Value;
        opts.MaxFileBytes.Should().Be(999);
    }

    [Fact]
    public void AddArcherTools_invokes_configure_action()
    {
        var services = new ServiceCollection();
        services.AddArcherTools(configure: o => o.MaxFileBytes = 7);
        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ToolOptions>>().Value;
        opts.MaxFileBytes.Should().Be(7);
    }
}
