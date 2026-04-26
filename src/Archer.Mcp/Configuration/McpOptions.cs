namespace Archer.Mcp.Configuration;

/// <summary>
/// Bound from <c>Archer:Mcp</c>. Defines where the MCP server YAML files live and
/// where credentials/keys are persisted on disk.
/// </summary>
public sealed class McpOptions
{
    public const string SectionName = "Archer:Mcp";

    /// <summary>
    /// Server-config YAML directories, scanned in order. Earlier directories take precedence
    /// for name collisions. Typical setup:
    /// <list type="bullet">
    ///   <item>Repo-level <c>mcp/</c> — committable, shared sidecar configs (no secrets).</item>
    ///   <item>User-level <c>~/.config/archer/mcp/</c> — user additions and overrides.</item>
    /// </list>
    /// Runtime-added servers via <c>AddOrUpdateAsync</c> are written to <see cref="UserConfigDirectory"/>.
    /// </summary>
    public IList<string> ServerDirectories { get; set; } = [];

    /// <summary>Where runtime-added server YAMLs land. If null, defaults to the last
    /// entry of <see cref="ServerDirectories"/>, which by convention is the user-level path.</summary>
    public string? UserConfigDirectory { get; set; }

    /// <summary>Path to the encrypted credentials blob. Defaults to <c>~/.config/archer/credentials.dat</c>.</summary>
    public string? CredentialsPath { get; set; }

    /// <summary>Path to the DataProtection key ring directory. Defaults to <c>~/.config/archer/dp-keys/</c>.</summary>
    public string? DataProtectionKeysDirectory { get; set; }
}
