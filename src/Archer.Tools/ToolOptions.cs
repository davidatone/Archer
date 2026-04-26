namespace Archer.Tools;

public sealed class ToolOptions
{
    public const string SectionName = "Tools";

    /// <summary>Default exclude globs applied unless overridden by a tool call.</summary>
    public string[] DefaultExcludeGlobs { get; set; } =
    [
        ".git/**",
        "bin/**",
        "obj/**",
        "node_modules/**",
        "dist/**",
        "build/**",
        "coverage/**",
        "*.min.js",
        "*.lock",
        "*.png",
        "*.jpg",
        "*.jpeg",
        "*.gif",
        "*.ico",
        "*.pdf",
        "*.zip",
    ];

    public int MaxFileBytes { get; set; } = 1_048_576; // 1 MB
    public int MaxToolResultBytes { get; set; } = 256_000;
    public bool FollowSymlinks { get; set; }

    /// <summary>
    /// Path-prefix bonuses applied by <c>search_pattern</c>'s ranking. A file whose
    /// repo-relative path starts with one of these prefixes gets the matching score (1.0
    /// is full bonus, 0.6 default for unmatched paths). Useful for biasing toward source
    /// directories regardless of language: e.g. ["src/", "lib/", "app/"].
    /// </summary>
    public string[] PreferredPathPrefixes { get; set; } = ["src/", "lib/", "app/"];
}
