using System.CommandLine;
using Archer.Application.Persistence;
using Archer.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Archer.Cli.Commands;

internal static class ListCommand
{
    public static Command Build()
    {
        var cmd = new Command("list", "List known agent ids.")
        {
            CommonOptions.StateDir,
        };

        cmd.SetHandler(async (string? state) =>
        {
            await CliHost.RunAsync(async (sp, ct) =>
            {
                var store = sp.GetRequiredService<IAgentStateStore>();
                var ids = await store.ListAgentsAsync(ct);
                if (ids.Count == 0)
                {
                    Console.WriteLine("(no agents found)");
                    return 0;
                }
                foreach (var id in ids)
                {
                    Console.WriteLine(id);
                }
                return 0;
            }, configure: CommonOptions.StateDirOverride(state));
        }, CommonOptions.StateDir);

        return cmd;
    }
}
