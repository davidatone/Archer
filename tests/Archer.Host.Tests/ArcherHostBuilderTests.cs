using Archer.Host;
using FluentAssertions;

namespace Archer.Host.Tests;

public class ArcherHostBuilderTests
{
    [Fact]
    public void ConfigureArcher_throws_for_null_builder()
    {
        Action act = () => ArcherHostBuilder.ConfigureArcher(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
