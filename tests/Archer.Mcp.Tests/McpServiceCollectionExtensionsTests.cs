using Archer.Application.Mcp;
using Archer.Mcp;
using Archer.Mcp.Client;
using Archer.Mcp.Configuration;
using Archer.Mcp.Tools;
using Archer.Persistence;
using Archer.Tools;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Archer.Mcp.Tests;

public class McpServiceCollectionExtensionsTests
{
    private static IConfiguration MakeConfig(string keysDir, string credsPath, string serverDir) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{McpOptions.SectionName}:DataProtectionKeysDirectory"] = keysDir,
            [$"{McpOptions.SectionName}:CredentialsPath"] = credsPath,
            [$"{McpOptions.SectionName}:UserConfigDirectory"] = serverDir,
            [$"{McpOptions.SectionName}:ServerDirectories:0"] = serverDir,
        }).Build();

    [Fact]
    public void AddArcherMcp_throws_for_null_arguments()
    {
        Action a1 = () => McpServiceCollectionExtensions.AddArcherMcp(null!, MakeConfig("/k", "/c", "/s"));
        a1.Should().Throw<ArgumentNullException>();

        Action a2 = () => McpServiceCollectionExtensions.AddArcherMcp(new ServiceCollection(), null!);
        a2.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task AddArcherMcp_registers_registry_credentials_pool_and_tool_source()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "archer-mcp-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(NullLoggerFactory.Instance);
            services.AddLogging();
            services.AddArcherFilePersistence();
            services.AddArcherTools();
            services.AddArcherMcp(MakeConfig(
                Path.Combine(tmp, "keys"),
                Path.Combine(tmp, "creds.dat"),
                Path.Combine(tmp, "servers")));
            await using var sp = services.BuildServiceProvider();

            sp.GetRequiredService<IMcpServerRegistry>().Should().NotBeNull();
            sp.GetRequiredService<ICredentialStore>().Should().NotBeNull();
            sp.GetRequiredService<IMcpClientPool>().Should().NotBeNull();
            sp.GetRequiredService<McpToolSource>().Should().NotBeNull();

            var opts = sp.GetRequiredService<IOptions<McpOptions>>().Value;
            opts.UserConfigDirectory.Should().NotBeNullOrEmpty();
            opts.CredentialsPath.Should().NotBeNullOrEmpty();
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* swallow */ }
        }
    }

    [Fact]
    public async Task AddArcherMcp_with_no_overrides_falls_back_to_user_config_directory()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddArcherFilePersistence();
        services.AddArcherTools();
        services.AddArcherMcp(new ConfigurationBuilder().Build());
        await using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<McpOptions>>().Value;
        opts.ServerDirectories.Should().NotBeEmpty();
        opts.UserConfigDirectory.Should().NotBeNullOrEmpty();
        opts.CredentialsPath.Should().NotBeNullOrEmpty();
        opts.DataProtectionKeysDirectory.Should().NotBeNullOrEmpty();
    }
}
