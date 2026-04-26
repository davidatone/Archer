using System.CommandLine;
using Archer.Application.Agents;
using Archer.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Archer.Cli.Commands;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class AgentsCommand
{
    public static Command Build()
    {
        var cmd = new Command("agents", "List registered agent types loaded from agents/*.yaml.")
        {
            CommonOptions.StateDir,
        };

        cmd.SetHandler(async (string? state) =>
        {
            await CliHost.RunAsync(async (sp, ct) =>
            {
                await Task.CompletedTask;
                var registry = sp.GetRequiredService<IAgentDefinitionRegistry>();
                var defs = registry.All;
                if (defs.Count == 0)
                {
                    Console.WriteLine("(no agent definitions registered — drop a YAML in ./agents/)");
                    return 0;
                }

                Console.WriteLine($"{"ID",-20} {"DEPLOYMENT",-20} {"TOOLS",-30} DESCRIPTION");
                foreach (var d in defs.OrderBy(x => x.Id, StringComparer.Ordinal))
                {
                    var tools = string.Join(",", d.Tools);
                    var desc = d.Description.Replace('\n', ' ').Trim();
                    if (desc.Length > 60) desc = desc[..57] + "...";
                    Console.WriteLine($"{d.Id,-20} {d.Model.Deployment,-20} {Truncate(tools, 28),-30} {desc}");
                }
                return 0;
            }, configure: CommonOptions.StateDirOverride(state));
        }, CommonOptions.StateDir);

        return cmd;
    }

    private static string Truncate(string s, int len) =>
        s.Length <= len ? s : s[..(len - 1)] + "…";
}
