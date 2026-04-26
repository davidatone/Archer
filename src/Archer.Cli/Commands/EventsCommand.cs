using System.CommandLine;
using Archer.Application.Events;
using Archer.Cli.Hosting;
using Archer.Cli.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Archer.Cli.Commands;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class EventsCommand
{
    public static Command Build()
    {
        var agentArg = new Argument<string>("agent-id");
        var follow = new Option<bool>("--follow", "Keep streaming events until cancelled.");
        follow.AddAlias("-f");

        var cmd = new Command("events", "Stream events for an agent.")
        {
            agentArg, follow, CommonOptions.StateDir,
        };

        cmd.SetHandler(async (string agentId, bool follow, string? state) =>
        {
            await CliHost.RunAsync(async (sp, ct) =>
            {
                var sink = sp.GetRequiredService<IAgentEventSink>();
                await foreach (var evt in sink.SubscribeAsync(agentId, ct))
                {
                    EventRenderer.Render(evt, Console.Out);
                    if (!follow && evt is Archer.Domain.Events.TurnCompletedEvent
                        or Archer.Domain.Events.TurnFailedEvent)
                    {
                        return 0;
                    }
                }
                return 0;
            }, configure: CommonOptions.StateDirOverride(state));
        }, agentArg, follow, CommonOptions.StateDir);

        return cmd;
    }
}
