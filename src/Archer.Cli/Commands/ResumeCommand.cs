using System.CommandLine;
using Archer.Actors.Contracts;
using Archer.Application.Events;
using Archer.Cli.Hosting;
using Archer.Domain.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace Archer.Cli.Commands;

internal static class ResumeCommand
{
    public static Command Build(string name)
    {
        var agentArg = new Argument<string>("agent-id", "Agent id (scout_XXXXXXXXXXXX).");
        var promptOption = new Option<string?>("--prompt", "User prompt to send.")
        {
            IsRequired = false,
        };
        promptOption.AddAlias("-p");

        var inlinePrompt = new Argument<string?>("inline-prompt")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Prompt as a positional argument (alternative to --prompt).",
        };

        var cmd = new Command(name, name == "ask"
            ? "Send a new message to an existing agent (alias for resume)."
            : "Resume an existing agent with a new user message.")
        {
            agentArg, promptOption, inlinePrompt, CommonOptions.StateDir,
        };

        cmd.SetHandler(async (string agentId, string? promptOpt, string? inline, string? state) =>
        {
            var prompt = promptOpt ?? inline;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                Console.Error.WriteLine("Provide a prompt via --prompt or as a positional argument.");
                Environment.ExitCode = 2;
                return;
            }
            if (!AgentId.IsValid(agentId))
            {
                Console.Error.WriteLine($"Invalid agent id: {agentId}");
                Environment.ExitCode = 2;
                return;
            }

            await CliHost.RunAsync(async (sp, ct) =>
            {
                var grainFactory = sp.GetRequiredService<IGrainFactory>();
                var sink = sp.GetRequiredService<IAgentEventSink>();
                var grain = grainFactory.GetGrain<IArcherAgentGrain>(agentId);

                var subscribeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pump = NewCommand.PumpEventsAsync(sink, agentId, subscribeCts.Token);
                await grain.AddUserMessageAsync(new UserMessageInput(prompt));
                await NewCommand.WaitForTurnEndAsync(pump, subscribeCts);
                return 0;
            }, configure: CommonOptions.StateDirOverride(state));
        }, agentArg, promptOption, inlinePrompt, CommonOptions.StateDir);

        return cmd;
    }
}
