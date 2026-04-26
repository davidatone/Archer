using Archer.Application.Mcp;
using Archer.Mcp.Client;
using Archer.Mcp.Configuration;
using Archer.Mcp.Credentials;
using Archer.Mcp.Registry;
using Archer.Mcp.Tools;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Archer.Mcp;

public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Wire up the MCP server registry and credential store. Reads configuration from the
    /// <c>Archer:Mcp</c> section. Defaults that mirror agent loading:
    /// <list type="bullet">
    ///   <item><c>ServerDirectories</c> falls back to <c>./mcp/</c> + <c>~/.config/archer/mcp/</c>.</item>
    ///   <item><c>UserConfigDirectory</c> defaults to <c>~/.config/archer/mcp/</c>.</item>
    ///   <item><c>CredentialsPath</c> defaults to <c>~/.config/archer/credentials.dat</c>.</item>
    ///   <item><c>DataProtectionKeysDirectory</c> defaults to <c>~/.config/archer/dp-keys/</c>.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddArcherMcp(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<McpOptions>(configuration.GetSection(McpOptions.SectionName));
        services.PostConfigure<McpOptions>(opts =>
        {
            var home = GetUserConfigRoot();

            if (opts.ServerDirectories.Count == 0)
            {
                // Same fallback chain as agents/: working dir, build output, then user config.
                opts.ServerDirectories.Add(Path.Combine(Environment.CurrentDirectory, "mcp"));
                opts.ServerDirectories.Add(Path.Combine(AppContext.BaseDirectory, "mcp"));
                opts.ServerDirectories.Add(Path.Combine(home, "mcp"));
            }
            opts.UserConfigDirectory ??= Path.Combine(home, "mcp");
            opts.CredentialsPath ??= Path.Combine(home, "credentials.dat");
            opts.DataProtectionKeysDirectory ??= Path.Combine(home, "dp-keys");
        });

        services.AddDataProtection()
            .SetApplicationName("archer")
            .PersistKeysToFileSystem(
                new DirectoryInfo(ResolveKeysDirectory(configuration)));

        services.AddSingleton<IMcpServerRegistry>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpOptions>>().Value;
            var logger = sp.GetService<ILogger<McpServerRegistry>>();
            return McpServerRegistry.FromDirectories(
                opts.ServerDirectories,
                opts.UserConfigDirectory!,
                logger);
        });

        services.AddSingleton<ICredentialStore>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpOptions>>().Value;
            var dp = sp.GetRequiredService<IDataProtectionProvider>();
            var logger = sp.GetService<ILogger<EncryptedFileCredentialStore>>();
            return new EncryptedFileCredentialStore(opts.CredentialsPath!, dp, logger);
        });

        services.AddSingleton<IMcpClientPool, McpClientPool>();
        services.AddSingleton<McpToolSource>();
        services.AddHostedService(sp => sp.GetRequiredService<McpToolSource>());

        return services;
    }

    /// <summary>
    /// Resolve the DataProtection keys directory eagerly so it can be passed to
    /// AddDataProtection's builder. Mirrors the same fallback PostConfigure applies.
    /// </summary>
    private static string ResolveKeysDirectory(IConfiguration configuration)
    {
        var configured = configuration[$"{McpOptions.SectionName}:DataProtectionKeysDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        return Path.Combine(GetUserConfigRoot(), "dp-keys");
    }

    private static string GetUserConfigRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "archer");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "archer");
    }
}
