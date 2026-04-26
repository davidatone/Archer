namespace Archer.Domain.Agents;

/// <summary>
/// A reusable, declarative agent profile loaded from YAML. Defines what the agent does
/// (instructions), what it can use (tools), how it talks to the model, and how it manages
/// context. Multiple instances of an agent can be spawned from the same definition.
/// </summary>
public sealed record AgentDefinition
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required ModelProfile Model { get; init; }
    public required string Instructions { get; init; }
    public required IReadOnlyList<string> Tools { get; init; }
    public required ContextProfile Context { get; init; }
    public InterruptionMode Interruption { get; init; } = InterruptionMode.Hard;
}

public sealed record ModelProfile
{
    public required string Deployment { get; init; }
    public string? ApiVersion { get; init; }

    /// <summary>Model's input-context budget in tokens (model-specific). Used to size
    /// percentage-based context strategies; null means use a sensible default.</summary>
    public int? ContextWindowTokens { get; init; }

    public int MaxCompletionTokens { get; init; } = 16384;

    public ReasoningProfile? Reasoning { get; init; }
}

public sealed record ReasoningProfile
{
    public ReasoningEffort Effort { get; init; } = ReasoningEffort.Medium;
    public ReasoningSummary Summary { get; init; } = ReasoningSummary.Auto;
}

public enum ReasoningEffort
{
    None,
    Minimal,
    Low,
    Medium,
    High,
}

public enum ReasoningSummary
{
    Auto,
    Concise,
    Detailed,
}

public sealed record ContextProfile
{
    /// <summary>
    /// Budget for recent-message inclusion expressed as a percentage of
    /// <see cref="ModelProfile.ContextWindowTokens"/>. Rounded up to the next message boundary.
    /// E.g. "30%" of a 200k-token window ≈ 60k tokens of recent messages.
    /// </summary>
    public Percentage RecentMessageWindow { get; init; } = Percentage.Of(30);

    /// <summary>Always include the first user message even if it falls outside the recent window.</summary>
    public bool PinFirstMessage { get; init; } = true;
}

/// <summary>
/// How a new user message handles an in-flight turn. Only <see cref="Hard"/> is implemented;
/// other policies will be added when there's a use case.
/// </summary>
public enum InterruptionMode
{
    Hard,
}

/// <summary>
/// A 0-100 percentage, immutable and round-trip-stable. Stored as a normalized decimal
/// so YAML can express either "30%" or "0.30".
/// </summary>
public readonly record struct Percentage
{
    /// <summary>The fraction in [0, 1].</summary>
    public double Fraction { get; init; }

    private Percentage(double fraction) => Fraction = fraction;

    public static Percentage Of(double percent)
    {
        if (percent < 0 || percent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), "Must be between 0 and 100.");
        }
        return new Percentage(percent / 100.0);
    }

    public int RoundUp(int total) => (int)Math.Ceiling(Fraction * total);

    public override string ToString() => $"{Fraction * 100:0.##}%";

    public static Percentage Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var trimmed = s.Trim();
        if (trimmed.EndsWith('%'))
        {
            return Of(double.Parse(trimmed[..^1], System.Globalization.CultureInfo.InvariantCulture));
        }
        // Decimal fraction, e.g. "0.30"
        var f = double.Parse(trimmed, System.Globalization.CultureInfo.InvariantCulture);
        return f <= 1.0 ? new Percentage(f) : Of(f);
    }
}
