using Archer.Host;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Archer.Host.Tests;

public class OtelConfigTests
{
    private static IConfiguration MakeConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void AddArcherTelemetry_with_no_exporter_registers_logger_factory()
    {
        var services = new ServiceCollection();
        services.AddArcherTelemetry(MakeConfig([]));
        using var sp = services.BuildServiceProvider();
        sp.GetService<ILoggerFactory>().Should().NotBeNull();
    }

    [Fact]
    public void AddArcherTelemetry_with_console_exporter_builds()
    {
        var services = new ServiceCollection();
        services.AddArcherTelemetry(MakeConfig(new Dictionary<string, string?>
        {
            ["Otel:ConsoleExporter"] = "true",
            ["Otel:ServiceName"] = "TestService",
        }));
        using var sp = services.BuildServiceProvider();
        sp.GetService<ILoggerFactory>().Should().NotBeNull();
    }

    [Fact]
    public void AddArcherTelemetry_with_grpc_otlp_endpoint_builds()
    {
        var services = new ServiceCollection();
        services.AddArcherTelemetry(MakeConfig(new Dictionary<string, string?>
        {
            ["Otel:Endpoint"] = "http://localhost:4317",
            ["Otel:Protocol"] = "grpc",
        }));
        using var sp = services.BuildServiceProvider();
        sp.GetService<ILoggerFactory>().Should().NotBeNull();
    }

    [Fact]
    public void AddArcherTelemetry_with_httpprotobuf_otlp_endpoint_builds()
    {
        var services = new ServiceCollection();
        services.AddArcherTelemetry(MakeConfig(new Dictionary<string, string?>
        {
            ["Otel:Endpoint"] = "http://localhost:4318",
            ["Otel:Protocol"] = "httpprotobuf",
        }));
        using var sp = services.BuildServiceProvider();
        sp.GetService<ILoggerFactory>().Should().NotBeNull();
    }

    [Fact]
    public void OtelOptions_defaults_are_sensible()
    {
        var opts = new OtelConfig.OtelOptions();
        opts.ServiceName.Should().Be("Archer");
        opts.Protocol.Should().Be("grpc");
        opts.ConsoleExporter.Should().BeFalse();
        opts.Endpoint.Should().BeNull();
    }

    [Fact]
    public void AddArcherTelemetry_falls_back_to_OTEL_EXPORTER_OTLP_ENDPOINT_env()
    {
        const string varName = "OTEL_EXPORTER_OTLP_ENDPOINT";
        var prev = Environment.GetEnvironmentVariable(varName);
        try
        {
            Environment.SetEnvironmentVariable(varName, "http://localhost:9999");
            var services = new ServiceCollection();
            services.AddArcherTelemetry(MakeConfig([]));
            using var sp = services.BuildServiceProvider();
            sp.GetService<ILoggerFactory>().Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, prev);
        }
    }

    [Fact]
    public void AddArcherTelemetry_honours_OTEL_EXPORTER_OTLP_PROTOCOL_http_protobuf()
    {
        const string varName = "OTEL_EXPORTER_OTLP_PROTOCOL";
        var prev = Environment.GetEnvironmentVariable(varName);
        try
        {
            Environment.SetEnvironmentVariable(varName, "http/protobuf");
            var services = new ServiceCollection();
            services.AddArcherTelemetry(MakeConfig(new Dictionary<string, string?>
            {
                ["Otel:Endpoint"] = "http://localhost:4318",
            }));
            using var sp = services.BuildServiceProvider();
            sp.GetService<ILoggerFactory>().Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, prev);
        }
    }
}
