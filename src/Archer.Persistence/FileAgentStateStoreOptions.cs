namespace Archer.Persistence;

public sealed class FileAgentStateStoreOptions
{
    public const string SectionName = "Persistence";

    /// <summary>Root directory for agent state. Defaults to ".archer".</summary>
    public string StateDirectory { get; set; } = ".archer";
}
