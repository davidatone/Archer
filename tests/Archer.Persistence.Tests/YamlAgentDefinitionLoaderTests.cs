using Archer.Domain.Agents;
using Archer.Persistence.Agents;
using FluentAssertions;

namespace Archer.Persistence.Tests;

public class YamlAgentDefinitionLoaderTests
{
    [Fact]
    public void Load_minimal_definition()
    {
        const string yaml = """
            id: code-scout
            instructions: Investigate the repo.
            tools:
              - list_files
              - grep
            model:
              deployment: gpt-x
            """;

        var def = YamlAgentDefinitionLoader.Load(new StringReader(yaml));

        def.Id.Should().Be("code-scout");
        def.Instructions.Should().Be("Investigate the repo.");
        def.Tools.Should().BeEquivalentTo(new[] { "list_files", "grep" });
        def.Model.Deployment.Should().Be("gpt-x");
        def.Model.MaxCompletionTokens.Should().Be(16384);
        def.Interruption.Should().Be(InterruptionMode.Hard);
    }

    [Fact]
    public void Load_full_definition_with_context_and_reasoning()
    {
        const string yaml = """
            id: full-agent
            description: Full coverage test agent.
            instructions: Do all the things.
            tools: [a, b]
            model:
              deployment: gpt-5.3
              apiVersion: 2025-04-01-preview
              contextWindow: 200000
              maxCompletionTokens: 65536
              reasoning:
                effort: high
                summary: detailed
            context:
              recentMessageWindow: 25%
              pinFirstMessage: false
            interruption: hard
            """;

        var def = YamlAgentDefinitionLoader.Load(new StringReader(yaml));

        def.Description.Should().Be("Full coverage test agent.");
        def.Model.ApiVersion.Should().Be("2025-04-01-preview");
        def.Model.ContextWindowTokens.Should().Be(200000);
        def.Model.MaxCompletionTokens.Should().Be(65536);
        def.Model.Reasoning!.Effort.Should().Be(ReasoningEffort.High);
        def.Model.Reasoning.Summary.Should().Be(ReasoningSummary.Detailed);
        def.Context.RecentMessageWindow.Fraction.Should().BeApproximately(0.25, 0.001);
        def.Context.PinFirstMessage.Should().BeFalse();
    }

    [Fact]
    public void Load_throws_for_missing_id()
    {
        const string yaml = """
            instructions: x
            tools: [a]
            model: { deployment: gpt }
            """;

        var act = () => YamlAgentDefinitionLoader.Load(new StringReader(yaml));
        act.Should().Throw<InvalidDataException>().WithMessage("*'id'*");
    }

    [Fact]
    public void Load_throws_for_missing_instructions()
    {
        const string yaml = """
            id: x
            tools: [a]
            model: { deployment: gpt }
            """;
        var act = () => YamlAgentDefinitionLoader.Load(new StringReader(yaml));
        act.Should().Throw<InvalidDataException>().WithMessage("*'instructions'*");
    }

    [Fact]
    public void Load_throws_for_missing_tools_list()
    {
        const string yaml = """
            id: x
            instructions: y
            model: { deployment: gpt }
            """;
        var act = () => YamlAgentDefinitionLoader.Load(new StringReader(yaml));
        act.Should().Throw<InvalidDataException>().WithMessage("*'tools'*");
    }

    [Fact]
    public void Load_throws_for_missing_model()
    {
        const string yaml = """
            id: x
            instructions: y
            tools: [a]
            """;
        var act = () => YamlAgentDefinitionLoader.Load(new StringReader(yaml));
        act.Should().Throw<InvalidDataException>().WithMessage("*'model'*");
    }

    [Fact]
    public void Load_throws_on_empty_document()
    {
        var act = () => YamlAgentDefinitionLoader.Load(new StringReader(""));
        act.Should().Throw<InvalidDataException>().WithMessage("*empty*");
    }

    [Fact]
    public void LoadFile_round_trips_through_disk()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
                id: from-disk
                instructions: hi
                tools: [a]
                model: { deployment: gpt }
                """);
            var def = YamlAgentDefinitionLoader.LoadFile(tmp);
            def.Id.Should().Be("from-disk");
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
