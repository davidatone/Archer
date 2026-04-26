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
internal static class NewCommand
{
    public static Command Build()
    {
        var repo = new Option<DirectoryInfo>("--repo")
        {
            Description = "Path to the repository to investigate.",
            IsRequired = true,
        };
        repo.AddAlias("-r");

        var prompt = new Option<string>("--prompt")
        {
            Description = "Initial user prompt for the agent.",
            IsRequired = true,
        };
        prompt.AddAlias("-p");

        var agentIdOption = new Option<string?>("--agent-id", "Optional explicit agent instance id.");
        var agentTypeOption = new Option<string>("--agent", () => "code-scout", "Agent type from agents/*.yaml (e.g. code-scout).");
        var modelOption = new Option<string?>("--model", "Override the Azure OpenAI deployment for this agent.");

        var cmd = new Command("new", "Create a new agent instance of the given type and run its first turn.")
        {
            repo, prompt, agentIdOption, agentTypeOption, modelOption, CommonOptions.StateDir,
        };

        cmd.SetHandler(async (DirectoryInfo r, string p, string? id, string agentType, string? m, string? state) =>
        {
            await CliHost.RunAsync(async (sp, ct) =>
            {
                var registry = sp.GetRequiredService<Application.Agents.IAgentDefinitionRegistry>();
                if (registry.Get(agentType) is null)
                {
                    await Console.Error.WriteLineAsync($"Unknown agent type '{agentType}'. Run `archer agents` to list available types.").ConfigureAwait(false);
                    return 2;
                }

                var agentId = id ?? AgentId.New();
                if (!AgentId.IsValid(agentId))
                {
                    await Console.Error.WriteLineAsync($"Invalid agent id: {agentId}").ConfigureAwait(false);
                    return 2;
                }

                var grainFactory = sp.GetRequiredService<IGrainFactory>();
                var sink = sp.GetRequiredService<IAgentEventSink>();
                var grain = grainFactory.GetGrain<IArcherAgentGrain>(agentId);

                await Console.Out.WriteLineAsync($"Agent type: {agentType}").ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"Agent id:   {agentId}").ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"Repo:       {r.FullName}").ConfigureAwait(false);
                await Console.Out.WriteLineAsync().ConfigureAwait(false);

                var subscribeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pumpTask = PumpEventsAsync(sink, agentId, subscribeCts.Token);

                await grain.InitializeAsync(new NewAgentRequest(r.FullName, p, AgentDefinitionId: agentType, ModelDeployment: m));
                await WaitForTurnEndAsync(pumpTask, subscribeCts);
                return 0;
            }, configure: CommonOptions.StateDirOverride(state));
        }, repo, prompt, agentIdOption, agentTypeOption, modelOption, CommonOptions.StateDir);

        return cmd;
    }

    internal static async Task PumpEventsAsync(
        IAgentEventSink sink,
        string agentId,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in sink.SubscribeAsync(agentId, cancellationToken))
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

    internal static async Task WaitForTurnEndAsync(Task pumpTask, CancellationTokenSource cts)
    {
        // Give the pump a generous timeout to receive a TurnCompleted/TurnFailed.
        var completed = await Task.WhenAny(pumpTask, Task.Delay(TimeSpan.FromMinutes(10)));
        await cts.CancelAsync().ConfigureAwait(false);
        if (completed != pumpTask)
        {
            await Console.Error.WriteLineAsync("[cli] event pump timeout").ConfigureAwait(false);
        }
    }
}
