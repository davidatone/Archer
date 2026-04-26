using System.Globalization;
using System.Text.Json;
using Archer.Application.Persistence;
using Archer.Domain.Agents;
using Archer.Domain.Events;
using Archer.Domain.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archer.Persistence;

public sealed class FileAgentStateStore : IAgentStateStore
{
    private readonly FileAgentStateStoreOptions _options;
    private readonly ILogger<FileAgentStateStore> _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public FileAgentStateStore(
        IOptions<FileAgentStateStoreOptions> options,
        ILogger<FileAgentStateStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string AgentDirectory(string agentId)
    {
        if (!AgentId.IsValid(agentId))
        {
            throw new ArgumentException($"Invalid agent id '{agentId}'.", nameof(agentId));
        }

        return Path.Combine(Path.GetFullPath(_options.StateDirectory), "agents", agentId);
    }

    public async Task<AgentState?> LoadAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(AgentDirectory(agentId), "state.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<AgentState>(
            stream,
            JsonOptions.Default,
            cancellationToken);

        if (state is null)
        {
            _logger.LogWarning("State file at {Path} deserialized to null.", path);
        }

        return state;
    }

    public async Task SaveAsync(AgentState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var dir = AgentDirectory(state.AgentId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "state.json");
        var tmp = path + ".tmp";

        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions.Default, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public Task<bool> ExistsAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(AgentDirectory(agentId), "state.json");
        return Task.FromResult(File.Exists(path));
    }

    public async Task AppendEventAsync(string agentId, AgentEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var dir = AgentDirectory(agentId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "events.ndjson");

        var json = JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions.Compact);

        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(json);
            await writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveToolResultAsync(
        string agentId,
        Guid turnId,
        int index,
        ToolResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var dir = Path.Combine(AgentDirectory(agentId), "tool-results");
        Directory.CreateDirectory(dir);
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"{turnId:N}-{index:D3}-{Sanitize(result.ToolName)}.json");
        var path = Path.Combine(dir, fileName);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, result, JsonOptions.Default, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListAgentsAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetFullPath(_options.StateDirectory), "agents");
        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var ids = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => name is not null && AgentId.IsValid(name))
            .Select(name => name!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    private static string Sanitize(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            buffer[i] = char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_';
        }
        return new string(buffer);
    }
}
