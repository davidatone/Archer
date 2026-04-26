using System.Collections.Concurrent;
using Archer.Application.Mcp;
using Archer.Domain.Mcp;
using Archer.Mcp.Yaml;
using Microsoft.Extensions.Logging;

namespace Archer.Mcp.Registry;

/// <summary>
/// Reads every <c>*.yaml</c> file under the configured directories and serves them by name.
/// Hot-reloads via <see cref="FileSystemWatcher"/>. Supports runtime add/remove via
/// <see cref="AddOrUpdateAsync"/> and <see cref="RemoveAsync"/>, which persist to the
/// user-level configuration directory. Threadsafe for many readers.
/// </summary>
public sealed class McpServerRegistry : IMcpServerRegistry, IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);

    private readonly ILogger? _logger;
    private readonly IReadOnlyList<string> _directories;
    private readonly string _userConfigDirectory;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _gate = new();

    private readonly ConcurrentDictionary<string, Entry> _byName = new(StringComparer.Ordinal);
    private IReadOnlyList<McpServerConfig> _all = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);

    public event Action<McpRegistryChange>? Changed;

    private McpServerRegistry(IEnumerable<string> directories, string userConfigDirectory, ILogger? logger)
    {
        _directories = [.. directories];
        _userConfigDirectory = userConfigDirectory;
        _logger = logger;
    }

    public McpServerConfig? Get(string name) =>
        name is not null && _byName.TryGetValue(name, out var entry) ? entry.Config : null;

    public IReadOnlyList<McpServerConfig> All => _all;

    public static McpServerRegistry FromDirectories(
        IEnumerable<string> directories,
        string userConfigDirectory,
        ILogger? logger = null)
    {
        var reg = new McpServerRegistry(directories, userConfigDirectory, logger);
        Directory.CreateDirectory(userConfigDirectory);
        reg.InitialScan();
        reg.StartWatching();
        return reg;
    }

    public async Task AddOrUpdateAsync(McpServerConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        Directory.CreateDirectory(_userConfigDirectory);
        var path = Path.Combine(_userConfigDirectory, config.Name + ".yaml");
        var yaml = YamlMcpConfigLoader.Serialize(config);

        // Atomic temp+rename so the watcher only sees a complete file.
        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tmp, yaml, cancellationToken).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);

        // Apply synchronously instead of waiting for the watcher — callers expect the registry
        // to reflect the write before the task completes.
        var dirIndex = FindDirectoryIndex(path);
        TryUpsertFile(path, dirIndex, fireChanged: true);
    }

    public Task<bool> RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_byName.TryGetValue(name, out var existing))
        {
            return Task.FromResult(false);
        }

        // Only delete from the user-level directory. Repo-level YAMLs are project config.
        var userPath = Path.Combine(_userConfigDirectory, name + ".yaml");
        var pathToDelete = string.Equals(existing.Path, userPath, StringComparison.Ordinal)
            ? userPath
            : null;

        if (pathToDelete is null)
        {
            _logger?.LogWarning(
                "Refusing to remove server '{Name}' — its YAML lives outside the user config directory ({Path}).",
                name, existing.Path);
            return Task.FromResult(false);
        }

        if (File.Exists(pathToDelete))
        {
            File.Delete(pathToDelete);
        }
        Remove(pathToDelete, existing.DirIndex);
        return Task.FromResult(true);
    }

    private int FindDirectoryIndex(string fullPath)
    {
        for (var i = 0; i < _directories.Count; i++)
        {
            if (PathStartsWith(fullPath, _directories[i]))
            {
                return i;
            }
        }
        // Fall back to "last directory" — caller writes to user config which should be configured.
        return _directories.Count - 1;
    }

    private static bool PathStartsWith(string path, string dir)
    {
        try
        {
            var fullDir = Path.GetFullPath(dir);
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullDir, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private void InitialScan()
    {
        for (var i = 0; i < _directories.Count; i++)
        {
            var dir = _directories[i];
            if (!Directory.Exists(dir))
            {
                _logger?.LogDebug("MCP server directory not found, skipping: {Dir}", dir);
                continue;
            }
            foreach (var path in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly))
            {
                TryUpsertFile(path, i, fireChanged: false);
            }
        }
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
                if (_pending.TryRemove(fullPath, out var winner))
                {
                    // Dispose this CTS when its task wins the debounce — superseded CTSes are
                    // disposed inside AddOrUpdate above. Without this, every quiet file change
                    // leaks one CTS handle.
                    winner.Dispose();
                }
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
        McpServerConfig config;
        try
        {
            config = YamlMcpConfigLoader.LoadFile(fullPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load MCP server config from {Path}", fullPath);
            return;
        }

        bool isUpdate;
        lock (_gate)
        {
            if (_byName.TryGetValue(config.Name, out var existing))
            {
                var sameSource = string.Equals(existing.Path, fullPath, StringComparison.Ordinal);
                if (!sameSource && existing.DirIndex < dirIndex)
                {
                    _logger?.LogDebug(
                        "Ignoring {Path} for MCP server '{Name}' — already provided by higher-priority {Existing}.",
                        fullPath, config.Name, existing.Path);
                    return;
                }
                isUpdate = true;
            }
            else
            {
                isUpdate = false;
            }

            _byName[config.Name] = new Entry(config, fullPath, dirIndex);
            RebuildSnapshot();
            _logger?.LogInformation(
                "{Verb} MCP server '{Name}' from {Path}",
                fireChanged ? "Reloaded" : "Loaded",
                config.Name, fullPath);
        }

        if (fireChanged)
        {
            Changed?.Invoke(new McpRegistryChange(
                isUpdate ? McpRegistryChangeKind.Updated : McpRegistryChangeKind.Added,
                config));
        }
    }

    private void Remove(string fullPath, int dirIndex)
    {
        var removed = new List<McpServerConfig>();
        lock (_gate)
        {
            var toRemove = _byName
                .Where(kv => string.Equals(kv.Value.Path, fullPath, StringComparison.Ordinal)
                          && kv.Value.DirIndex == dirIndex)
                .Select(kv => (kv.Key, kv.Value.Config))
                .ToList();
            foreach (var (name, config) in toRemove)
            {
                if (_byName.TryRemove(name, out _))
                {
                    removed.Add(config);
                    _logger?.LogInformation("Unloaded MCP server '{Name}' (file removed: {Path})", name, fullPath);
                }
            }
            if (removed.Count > 0)
            {
                RebuildSnapshot();
            }
        }
        foreach (var config in removed)
        {
            Changed?.Invoke(new McpRegistryChange(McpRegistryChangeKind.Removed, config));
        }
    }

    private void RebuildSnapshot()
    {
        _all = [.. _byName.Values.Select(e => e.Config)];
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

    private readonly record struct Entry(McpServerConfig Config, string Path, int DirIndex);
}
