using Archer.Tui;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Archer.Tui.Tests;

public class TuiFileLoggerProviderTests
{
    [Fact]
    public void Logger_below_warning_does_not_write()
    {
        var writer = new StringWriter();
        using var provider = new TuiFileLoggerProvider(writer);
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("info");
        logger.LogDebug("debug");

        writer.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Logger_at_warning_or_higher_writes_with_severity_letter()
    {
        var writer = new StringWriter();
        using var provider = new TuiFileLoggerProvider(writer);
        var logger = provider.CreateLogger("Test");

        logger.LogWarning("warn-message");
        logger.LogError("error-message");
        logger.LogCritical("crit-message");

        var output = writer.ToString();
        output.Should().Contain(" W ").And.Contain("warn-message");
        output.Should().Contain(" E ").And.Contain("error-message");
        output.Should().Contain(" C ").And.Contain("crit-message");
    }

    [Fact]
    public void Logger_writes_exception_on_a_separate_line()
    {
        var writer = new StringWriter();
        using var provider = new TuiFileLoggerProvider(writer);
        var logger = provider.CreateLogger("Test");

        logger.LogError(new InvalidOperationException("boom"), "during X");

        var output = writer.ToString();
        output.Should().Contain("during X");
        output.Should().Contain("InvalidOperationException");
        output.Should().Contain("boom");
    }

    [Fact]
    public void IsEnabled_only_returns_true_for_warning_and_above()
    {
        var writer = new StringWriter();
        using var provider = new TuiFileLoggerProvider(writer);
        var logger = provider.CreateLogger("Test");

        logger.IsEnabled(LogLevel.Trace).Should().BeFalse();
        logger.IsEnabled(LogLevel.Debug).Should().BeFalse();
        logger.IsEnabled(LogLevel.Information).Should().BeFalse();
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
        logger.IsEnabled(LogLevel.Error).Should().BeTrue();
        logger.IsEnabled(LogLevel.Critical).Should().BeTrue();
    }

    [Fact]
    public void BeginScope_returns_null()
    {
        var writer = new StringWriter();
        using var provider = new TuiFileLoggerProvider(writer);
        var logger = provider.CreateLogger("Test");
        logger.BeginScope("scope").Should().BeNull();
    }

    [Fact]
    public void Dispose_closes_underlying_writer()
    {
        var writer = new TrackingWriter();
        var provider = new TuiFileLoggerProvider(writer);
        provider.Dispose();
        writer.Disposed.Should().BeTrue();
    }

    private sealed class TrackingWriter : StringWriter
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }
}
