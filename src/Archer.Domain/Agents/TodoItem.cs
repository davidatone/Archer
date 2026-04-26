namespace Archer.Domain.Agents;

public sealed class TodoItem
{
    public required string Id { get; init; }
    public required string Title { get; set; }
    public string? Notes { get; set; }
    public required TodoStatus Status { get; set; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; set; }

    public static string NewId() => "todo_" + Guid.NewGuid().ToString("N")[..8];
}
