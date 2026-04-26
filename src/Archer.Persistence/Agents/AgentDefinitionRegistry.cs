using System.Collections.Concurrent;
using Archer.Application.Agents;
using Archer.Domain.Agents;
using Microsoft.Extensions.Logging;

namespace Archer.Persistence.Agents;

/// <summary>
/// Reads every <c>*.yaml</c> file under the configured directories and serves them by id.
/// Hot-reloads via <see cref="FileSystemWatcher"/> — adding, editing, or deleting an agent
/// YAML at runtime updates the registry without restart. Threadsafe for many readers.
/// </summary>
public sealed class AgentDefinitionRegistry : IAgentDefinitionRegistry, IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);

    private readonly ILogger? _logger;
    private readonly IReadOnlyList<string> _directories;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _gate = new();

    /// <summary>Source-of-truth: id → (definition, originatingPath, dirIndex).</summary>
    private readonly ConcurrentDictionary<string, Entry> _byId = new(StringComparer.Ordinal);

    /// <summary>Atomic snapshot returned to readers; replaced on every mutation.</summary>
    private IReadOnlyList<AgentDefinition> _all = [];

    /// <summary>Pending reload tasks keyed by full path, used to debounce duplicate events.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);

    /// <summary>Fired (after the registry mutates) when a YAML is added, changed, or removed.</summary>
    public event Action? Changed;

    private AgentDefinitionRegistry(IEnumerable<string> directories, ILogger? logger)
    {
        _directories = [.. directories];
        _logger = logger;
    }

    public AgentDefinition? Get(string id) =>
        id is not null && _byId.TryGetValue(id, out var entry) ? entry.Definition : null;

    public IReadOnlyList<AgentDefinition> All => _all;

    /// <summary>Build a registry by scanning directories once, then keep watching them.</summary>
    public static AgentDefinitionRegistry FromDirectories(
        IEnumerable<string> directories,
        ILogger? logger = null)
    {
        var reg = new AgentDefinitionRegistry(directories, logger);
        reg.InitialScan();
        reg.StartWatching();
        return reg;
    }

    private void InitialScan()
    {
        for (var i = 0; i < _directories.Count; i++)
        {
            var dir = _directories[i];
            if (!Directory.Exists(dir))
            {
                _logger?.LogDebug("Agent definition directory not found, skipping: {Dir}", dir);
                continue;
            }
            foreach (var path in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly))
            {
                TryUpsertFile(path, i, fireChanged: false);
            }
        }
        RebuildSnapshot();
    }

    private void StartWatching()
    {
        for (var i = 0; i < _directories.Count; i++)
        {
            var dir = _directories[i];
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(dir, "*.yaml")
            {
                NotifyFilter = NotifyFilters.LastWrite
                              | NotifyFilters.FileName
                              | NotifyFilters.CreationTime
                              | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            var dirIndex = i;
            watcher.Created += (_, e) => DebouncedReload(e.FullPath, dirIndex);
            watcher.Changed += (_, e) => DebouncedReload(e.FullPath, dirIndex);
            watcher.Renamed += (_, e) =>
            {
                Remove(e.OldFullPath, dirIndex);
                DebouncedReload(e.FullPath, dirIndex);
            };
            watcher.Deleted += (_, e) => Remove(e.FullPath, dirIndex);
            _watchers.Add(watcher);
        }
    }

    private void DebouncedReload(string fullPath, int dirIndex)
    {
        // Editors emit a flurry of events when saving (rename + create + change). Coalesce them.
        var cts = new CancellationTokenSource();
        _pending.AddOrUpdate(fullPath, cts, (_, old) =>
        {
            old.Cancel();
            old.Dispose();
            return cts;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceWindow, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) return;
                _pending.TryRemove(fullPath, out _);
                if (File.Exists(fullPath))
                {
                    TryUpsertFile(fullPath, dirIndex, fireChanged: true);
                }
            }
            catch (TaskCanceledException) { /* superseded */ }
        });
    }

    private void TryUpsertFile(string fullPath, int dirIndex, bool fireChanged)
    {
        AgentDefinition def;
        try
        {
            def = YamlAgentDefinitionLoader.LoadFile(fullPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load agent definition from {Path}", fullPath);
            return;
        }

        lock (_gate)
        {
            // First-write-wins by directory order: a higher-priority (earlier) directory
            // already holding the id keeps its entry.
            if (_byId.TryGetValue(def.Id, out var existing))
            {
                var sameSource = string.Equals(existing.Path, fullPath, StringComparison.Ordinal);
                if (!sameSource && existing.DirIndex < dirIndex)
                {
                    _logger?.LogDebug(
                        "Ignoring {Path} for id '{Id}' — already provided by higher-priority {Existing}",
                        fullPath, def.Id, existing.Path);
                    return;
                }
            }

            _byId[def.Id] = new Entry(def, fullPath, dirIndex);
            RebuildSnapshot();
            _logger?.LogInformation(
                "{Verb} agent definition '{Id}' from {Path}",
                fireChanged ? "Reloaded" : "Loaded",
                def.Id, fullPath);
        }

        if (fireChanged)
        {
            Changed?.Invoke();
        }
    }

    private void Remove(string fullPath, int dirIndex)
    {
        bool removedAny;
        lock (_gate)
        {
            // Drop only if the entry still points at this path (don't yank an id that was
            // claimed by a higher-priority directory).
            var toRemove = _byId
                .Where(kv => string.Equals(kv.Value.Path, fullPath, StringComparison.Ordinal)
                          && kv.Value.DirIndex == dirIndex)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in toRemove)
            {
                _byId.TryRemove(id, out _);
                _logger?.LogInformation("Unloaded agent definition '{Id}' (file removed: {Path})", id, fullPath);
            }
            removedAny = toRemove.Count > 0;
            if (removedAny)
            {
                RebuildSnapshot();
            }
        }
        if (removedAny)
        {
            Changed?.Invoke();
        }
    }

    private void RebuildSnapshot()
    {
        _all = [.. _byId.Values.Select(e => e.Definition)];
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
        foreach (var cts in _pending.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _pending.Clear();
    }

    private readonly record struct Entry(AgentDefinition Definition, string Path, int DirIndex);
}
