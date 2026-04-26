using Archer.Domain.Agents;
using YamlDotNet.RepresentationModel;

namespace Archer.Persistence.Agents;

/// <summary>
/// Parses an <see cref="AgentDefinition"/> from a YAML document. The loader is intentionally
/// hand-rolled (no auto-binding) so the schema mismatches we'd otherwise hit on YamlDotNet's
/// strict deserializer surface as actionable errors at the field that's wrong.
/// </summary>
public static class YamlAgentDefinitionLoader
{
    public static AgentDefinition LoadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var reader = new StreamReader(path);
        return Load(reader, path);
    }

    public static AgentDefinition Load(TextReader reader, string sourceLabel = "<yaml>")
    {
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0)
        {
            throw new InvalidDataException($"{sourceLabel}: empty YAML document.");
        }

        if (yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException($"{sourceLabel}: top-level node must be a mapping.");
        }

        return new AgentDefinition
        {
            Id = RequireString(root, "id"),
            Description = TryGetString(root, "description") ?? string.Empty,
            Instructions = RequireString(root, "instructions"),
            Tools = RequireStringList(root, "tools"),
            Model = ParseModel(RequireMapping(root, "model")),
            Context = TryGetMapping(root, "context") is { } ctx ? ParseContext(ctx) : new ContextProfile(),
            Interruption = TryGetString(root, "interruption") is { } i
                ? Enum.Parse<InterruptionMode>(i, ignoreCase: true)
                : InterruptionMode.Hard,
        };
    }

    private static ModelProfile ParseModel(YamlMappingNode node) => new()
    {
        Deployment = RequireString(node, "deployment"),
        ApiVersion = TryGetString(node, "apiVersion"),
        ContextWindowTokens = TryGetInt(node, "contextWindow"),
        MaxCompletionTokens = TryGetInt(node, "maxCompletionTokens") ?? 16384,
        Reasoning = TryGetMapping(node, "reasoning") is { } r ? ParseReasoning(r) : null,
    };

    private static ReasoningProfile ParseReasoning(YamlMappingNode node) => new()
    {
        Effort = TryGetString(node, "effort") is { } e
            ? Enum.Parse<ReasoningEffort>(e, ignoreCase: true)
            : ReasoningEffort.Medium,
        Summary = TryGetString(node, "summary") is { } s
            ? Enum.Parse<ReasoningSummary>(s, ignoreCase: true)
            : ReasoningSummary.Auto,
    };

    private static ContextProfile ParseContext(YamlMappingNode node) => new()
    {
        RecentMessageWindow = TryGetString(node, "recentMessageWindow") is { } w
            ? Percentage.Parse(w)
            : Percentage.Of(30),
        PinFirstMessage = TryGetBool(node, "pinFirstMessage") ?? true,
    };

    // ---- mapping helpers ---------------------------------------------------------------

    private static string RequireString(YamlMappingNode node, string key) =>
        TryGetString(node, key) ?? throw new InvalidDataException($"Missing required field '{key}'.");

    private static string? TryGetString(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s
            ? s.Value
            : null;

    private static int? TryGetInt(YamlMappingNode node, string key)
        => TryGetString(node, key) is { } s && int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i)
            ? i
            : null;

    private static bool? TryGetBool(YamlMappingNode node, string key)
        => TryGetString(node, key) is { } s && bool.TryParse(s, out var b)
            ? b
            : null;

    private static YamlMappingNode RequireMapping(YamlMappingNode node, string key)
        => TryGetMapping(node, key) ?? throw new InvalidDataException($"Missing required mapping '{key}'.");

    private static YamlMappingNode? TryGetMapping(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v as YamlMappingNode : null;

    private static IReadOnlyList<string> RequireStringList(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var v) || v is not YamlSequenceNode seq)
        {
            throw new InvalidDataException($"Missing required list '{key}'.");
        }
        return [.. seq.OfType<YamlScalarNode>().Select(s => s.Value!).Where(s => !string.IsNullOrWhiteSpace(s))];
    }
}
