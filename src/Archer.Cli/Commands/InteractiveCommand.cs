using System.CommandLine;
using Archer.Actors.Contracts;
using Archer.Application.Events;
using Archer.Cli.Hosting;
using Archer.Cli.Rendering;
using Archer.Domain.Agents;
using Archer.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace Archer.Cli.Commands;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class InteractiveCommand
{
    public static Option<DirectoryInfo> RepoOption { get; } = new("--repo", () => new DirectoryInfo(Directory.GetCurrentDirectory()))
    {
        Description = "Repository to investigate (defaults to current directory).",
    };

    public static Option<string?> AgentIdOption { get; } = new("--agent-id", "Resume an existing agent instead of creating a new one.");
    public static Option<string?> ModelOption { get; } = new("--model", "Override the deployment for new agents.");

    public static void Attach(RootCommand root)
    {
        root.AddGlobalOption(RepoOption);
        root.AddGlobalOption(AgentIdOption);
        root.AddGlobalOption(ModelOption);
        root.AddGlobalOption(CommonOptions.StateDir);

        root.SetHandler(async (DirectoryInfo repo, string? agentId, string? model, string? stateDir) =>
        {
            await CliHost.RunAsync(async (sp, ct) => await RunReplAsync(sp, repo, agentId, model, ct),
                configure: CommonOptions.StateDirOverride(stateDir));
        }, RepoOption, AgentIdOption, ModelOption, CommonOptions.StateDir);
    }

    private static async Task<int> RunReplAsync(
        IServiceProvider sp,
        DirectoryInfo repo,
        string? agentIdArg,
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var grainFactory = sp.GetRequiredService<IGrainFactory>();
        var sink = sp.GetRequiredService<IAgentEventSink>();

        var (resumeExit, activeAgentId) = await TryResumeAgentAsync(agentIdArg);
        if (resumeExit is { } exit) return exit;

        await PrintBannerAsync(repo);
        await ReplLoopAsync(grainFactory, sink, repo, modelOverride, activeAgentId, cancellationToken);
        await Console.Out.WriteLineAsync("Bye.").ConfigureAwait(false);
        return 0;
    }

    private static async Task<(int? Exit, string? AgentId)> TryResumeAgentAsync(string? agentIdArg)
    {
        var resume = ResolveResumedAgentId(agentIdArg);
        if (resume.ExitCode is { } exit)
        {
            await Console.Error.WriteLineAsync(resume.ErrorMessage!).ConfigureAwait(false);
            return (exit, null);
        }
        if (resume.AgentId is not null)
        {
            await Console.Out.WriteLineAsync($"Resuming agent {resume.AgentId}.").ConfigureAwait(false);
        }
        return (null, resume.AgentId);
    }

    private static async Task ReplLoopAsync(
        IGrainFactory grainFactory, IAgentEventSink sink,
        DirectoryInfo repo, string? modelOverride, string? activeAgentId,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await ReadOnePromptAsync(activeAgentId, ct);
            if (line is null) break;
            if (line.Length == 0) continue;

            var (newAgentId, exit) = await ProcessReplLineAsync(
                line, grainFactory, sink, repo, modelOverride, activeAgentId, ct);
            activeAgentId = newAgentId;
            if (exit) break;
        }
    }

    private static async Task<string?> ReadOnePromptAsync(string? activeAgentId, CancellationToken ct)
    {
        await Console.Out.WriteAsync(activeAgentId is null ? "> " : $"{activeAgentId.AsSpan(6, 6)}> ").ConfigureAwait(false);
        var line = await ReadLineAsync(ct);
        return line?.Trim();
    }

    private static async Task<(string? ActiveAgentId, bool Exit)> ProcessReplLineAsync(
        string line, IGrainFactory grainFactory, IAgentEventSink sink,
        DirectoryInfo repo, string? modelOverride, string? activeAgentId,
        CancellationToken ct)
    {
        if (line.StartsWith('/'))
        {
            if (await HandleSlashAsync(line, repo.FullName, activeAgentId, sink, grainFactory, ct) is { } newId)
            {
                activeAgentId = newId;
            }
            return (activeAgentId, line is "/exit" or "/quit");
        }
        var updated = await DispatchUserMessageAsync(grainFactory, sink, activeAgentId, line, repo, modelOverride, ct);
        return (updated, false);
    }

    private static (int? ExitCode, string? AgentId, string? ErrorMessage) ResolveResumedAgentId(string? agentIdArg)
    {
        if (string.IsNullOrWhiteSpace(agentIdArg)) return (null, null, null);
        if (!AgentId.IsValid(agentIdArg))
        {
            return (2, null, $"Invalid agent id: {agentIdArg}");
        }
        return (null, agentIdArg, null);
    }

    private static async Task PrintBannerAsync(DirectoryInfo repo)
    {
        await Console.Out.WriteLineAsync("Archer — interactive mode.").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Repo: {repo.FullName}").ConfigureAwait(false);
        await Console.Out.WriteLineAsync("Type a prompt and press Enter. Slash commands: /help /status /new <prompt> /interrupt [reason] /clear /exit").ConfigureAwait(false);
        await Console.Out.WriteLineAsync().ConfigureAwait(false);
    }

    private static async Task<string> DispatchUserMessageAsync(
        IGrainFactory grainFactory, IAgentEventSink sink,
        string? activeAgentId, string line, DirectoryInfo repo, string? modelOverride,
        CancellationToken cancellationToken)
    {
        if (activeAgentId is null)
        {
            var newId = AgentId.New();
            var grain = grainFactory.GetGrain<IArcherAgentGrain>(newId);
            await Console.Out.WriteLineAsync($"[agent] new {newId}").ConfigureAwait(false);
            await DriveTurnAsync(sink, newId,
                () => grain.InitializeAsync(new NewAgentRequest(repo.FullName, line, ModelDeployment: modelOverride)),
                cancellationToken);
            return newId;
        }
        var existing = grainFactory.GetGrain<IArcherAgentGrain>(activeAgentId);
        await DriveTurnAsync(sink, activeAgentId,
            () => existing.AddUserMessageAsync(new UserMessageInput(line)),
            cancellationToken);
        return activeAgentId;
    }

    private static async Task<string?> HandleSlashAsync(
        string line,
        string repoRoot,
        string? activeAgentId,
        IAgentEventSink sink,
        IGrainFactory grainFactory,
        CancellationToken ct)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1] : null;

        switch (cmd)
        {
            case "/help":
                Console.WriteLine("""
                    /help                — show this help
                    /status              — show agent snapshot
                    /new <prompt>        — abandon current and start a new agent with prompt
                    /interrupt [reason]  — supersede the active turn
                    /clear               — clear the screen
                    /exit, /quit         — leave
                    """);
                return null;

            case "/status":
                if (activeAgentId is null)
                {
                    await Console.Out.WriteLineAsync("No active agent yet.").ConfigureAwait(false);
                    return null;
                }
                var snap = await grainFactory.GetGrain<IArcherAgentGrain>(activeAgentId).GetSnapshotAsync();
                if (snap is null)
                {
                    await Console.Out.WriteLineAsync("Agent not initialized yet — send a prompt first.").ConfigureAwait(false);
                    return null;
                }
                await Console.Out.WriteLineAsync($"agent={snap.AgentId} messages={snap.Messages.Count} active-turn={(snap.ActiveTurnId is { } t ? t.ToString() : "(none)")}").ConfigureAwait(false);
                return null;

            case "/new":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    await Console.Out.WriteLineAsync("Usage: /new <prompt>").ConfigureAwait(false);
                    return null;
                }
                var newId = AgentId.New();
                await Console.Out.WriteLineAsync($"[agent] new {newId}").ConfigureAwait(false);
                var grain = grainFactory.GetGrain<IArcherAgentGrain>(newId);
                await DriveTurnAsync(sink, newId,
                    () => grain.InitializeAsync(new NewAgentRequest(repoRoot, rest)),
                    ct);
                return newId;

            case "/interrupt":
                if (activeAgentId is null)
                {
                    await Console.Out.WriteLineAsync("No active agent.").ConfigureAwait(false);
                    return null;
                }
                await grainFactory.GetGrain<IArcherAgentGrain>(activeAgentId)
                    .InterruptAsync(new InterruptRequest(rest ?? "User interrupt."));
                await Console.Out.WriteLineAsync("[agent] interrupt requested").ConfigureAwait(false);
                return null;

            case "/clear":
                Console.Clear();
                return null;

            case "/exit":
            case "/quit":
                return null;

            default:
                await Console.Out.WriteLineAsync($"Unknown command: {cmd}. Try /help.").ConfigureAwait(false);
                return null;
        }
    }

    private static async Task DriveTurnAsync(
        IAgentEventSink sink,
        string agentId,
        Func<Task> startTurn,
        CancellationToken ct)
    {
        using var subscribeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpUntilTurnEndAsync(sink, agentId, subscribeCts.Token);
        try
        {
            await startTurn();
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[error] {ex.Message}").ConfigureAwait(false);
            await subscribeCts.CancelAsync().ConfigureAwait(false);
            return;
        }

        var winner = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromMinutes(15), subscribeCts.Token));
        await subscribeCts.CancelAsync().ConfigureAwait(false);
        if (winner != pump)
        {
            await Console.Error.WriteLineAsync("[cli] turn timeout").ConfigureAwait(false);
        }
    }

    private static async Task PumpUntilTurnEndAsync(IAgentEventSink sink, string agentId, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in sink.SubscribeAsync(agentId, ct))
            {
                EventRenderer.Render(evt, Console.Out);
                if (evt is TurnCompletedEvent or TurnFailedEvent)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on user-initiated cancellation
        }
    }

    private static async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        // Console.In is synchronous; offload to thread pool so Ctrl+C still cancels.
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = cancellationToken.Register(() => tcs.TrySetResult(null));
        _ = Task.Run(() =>
        {
            try { tcs.TrySetResult(Console.In.ReadLine()); }
            catch (Exception) { tcs.TrySetResult(null); }
        }, CancellationToken.None);
        return await tcs.Task.ConfigureAwait(false);
    }
}
