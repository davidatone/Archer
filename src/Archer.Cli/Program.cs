using System.CommandLine;
using Archer.Cli.Commands;

var root = new RootCommand("Archer — actor-based, YAML-driven agent framework.")
{
    NewCommand.Build(),
    ResumeCommand.Build(name: "resume"),
    ResumeCommand.Build(name: "ask"),
    StatusCommand.Build(),
    EventsCommand.Build(),
    ListCommand.Build(),
    AgentsCommand.Build(),
    McpCommand.Build(),
};

InteractiveCommand.Attach(root);

return await root.InvokeAsync(args);
