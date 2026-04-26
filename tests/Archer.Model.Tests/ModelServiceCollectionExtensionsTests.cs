using Archer.Application.Model;
using Archer.Application.Tools;
using Archer.Domain.Tools;
using Archer.Model;
using Archer.Model.AgentFramework;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archer.Model.Tests;

public class ModelServiceCollectionExtensionsTests
{
    [Fact]
    public void AddArcherAgentFrameworkModel_registers_factories_and_runners()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolRegistry, EmptyToolRegistry>();
        services.AddArcherAgentFrameworkModel();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IChatClientFactory>().Should().BeOfType<AzureOpenAIChatClientFactory>();
        sp.GetRequiredService<IAgentContextBuilder>().Should().BeOfType<AgentContextBuilder>();
        sp.GetRequiredService<IModelTurnRunner>().Should().BeOfType<AgentFrameworkModelTurnRunner>();
    }

    [Fact]
    public void AddArcherAgentFrameworkModel_binds_configuration_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com",
                ["AzureOpenAI:DefaultDeployment"] = "gpt-x",
                ["AzureOpenAI:UseV1Surface"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolRegistry, EmptyToolRegistry>();
        services.AddArcherAgentFrameworkModel(config);
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
        opts.Endpoint.Should().Be("https://example.openai.azure.com");
        opts.DefaultDeployment.Should().Be("gpt-x");
        opts.UseV1Surface.Should().BeFalse();
    }

    [Fact]
    public void AddArcherAgentFrameworkModel_invokes_configure_callback()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolRegistry, EmptyToolRegistry>();
        services.AddArcherAgentFrameworkModel(configure: o => o.Endpoint = "https://x");
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value.Endpoint.Should().Be("https://x");
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools => [];
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public bool TryGet(string name, out ITool tool) { tool = null!; return false; }
        public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public void Add(ITool tool) { }
        public bool Remove(string name) => false;
        public event Action<ToolRegistryChange>? Changed { add { } remove { } }
    }
}
