using System.CommandLine;
using Archer.Actors.Contracts;
using Archer.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace Archer.Cli.Commands;

internal static class StatusCommand
{
    public static Command Build()
    {
        var agentArg = new Argument<string>("agent-id");

        var cmd = new Command("status", "Print agent state summary.")
        {
            agentArg, CommonOptions.StateDir,
        };

        cmd.SetHandler(async (string agentId, string? state) =>
        {
            await CliHost.RunAsync(async (sp, ct) =>
            {
                var factory = sp.GetRequiredService<IGrainFactory>();
                var grain = factory.GetGrain<IArcherAgentGrain>(agentId);
                var snap = await grain.GetSnapshotAsync();
                if (snap is null)
                {
                    Console.Error.WriteLine($"Agent {agentId} has not been initialized.");
                    return 2;
                }

                Console.WriteLine($"Agent:           {snap.AgentId}");
                Console.WriteLine($"Repo:            {snap.RepoRoot}");
                Console.WriteLine($"Created:         {snap.CreatedAtUtc:O}");
                Console.WriteLine($"Updated:         {snap.UpdatedAtUtc:O}");
                Console.WriteLine($"Messages:        {snap.Messages.Count}");
                Console.WriteLine($"Latest seq:      {snap.LatestMessageSeq}");
                Console.WriteLine($"Active turn:     {(snap.ActiveTurnId is { } id ? id.ToString() : "(none)")}");
                Console.WriteLine($"Model:           {snap.ModelDeployment ?? "(default)"}");
                Console.WriteLine();
                Console.WriteLine($"Recent summaries ({snap.Summaries.Count}):");
                foreach (var s in snap.Summaries.TakeLast(3))
                {
                    Console.WriteLine($"  - {s.Content}");
                }
                return 0;
            }, configure: CommonOptions.StateDirOverride(state));
        }, agentArg, CommonOptions.StateDir);

        return cmd;
    }
}
