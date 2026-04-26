using Archer.Application.Telemetry;
using Archer.Host.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Archer.Host;

/// <summary>
/// Configures OpenTelemetry for Archer. Reads <c>Otel</c> from configuration; if no exporter
/// is configured the registration is a no-op (telemetry collected but not shipped). Supports:
/// <list type="bullet">
///   <item>Console exporter — useful for local diagnostics.</item>
///   <item>OTLP exporter — for Jaeger, Tempo, OTel Collector, Honeycomb, etc.</item>
/// </list>
/// Set <c>Otel:Endpoint</c> (e.g. <c>http://localhost:4317</c>) to enable OTLP.
/// </summary>
public static class OtelConfig
{
    public sealed class OtelOptions
    {
        public const string SectionName = "Otel";

        /// <summary>Service name reported to the collector. Defaults to "Archer".</summary>
        public string ServiceName { get; set; } = "Archer";

        /// <summary>OTLP endpoint URL. When set, OTLP exporter is enabled.</summary>
        public string? Endpoint { get; set; }

        /// <summary>OTLP protocol — "grpc" (default) or "httpprotobuf".</summary>
        public string Protocol { get; set; } = "grpc";

        /// <summary>Also log to the console (debug aid). Off by default.</summary>
        public bool ConsoleExporter { get; set; }
    }

    public static IServiceCollection AddArcherTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var opts = LoadOptions(configuration);
        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName: opts.ServiceName, serviceVersion: "0.1.0");

        services.AddOpenTelemetry()
            .WithTracing(tp => ConfigureTracing(tp, resource, opts))
            .WithMetrics(mp => ConfigureMetrics(mp, resource, opts));

        services.AddLogging(b => b.AddOpenTelemetry(o => ConfigureLogging(o, resource, opts)));

        return services;
    }

    /// <summary>Bind config + honour the standard OTLP env vars Aspire and the SDK inject.</summary>
    private static OtelOptions LoadOptions(IConfiguration configuration)
    {
        var opts = new OtelOptions();
        configuration.GetSection(OtelOptions.SectionName).Bind(opts);
        if (string.IsNullOrWhiteSpace(opts.Endpoint))
        {
            opts.Endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        }
        if (string.Equals(
                Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL"),
                "http/protobuf",
                StringComparison.OrdinalIgnoreCase))
        {
            opts.Protocol = "httpprotobuf";
        }
        return opts;
    }

    private static void ConfigureTracing(TracerProviderBuilder tp, ResourceBuilder resource, OtelOptions opts)
    {
        tp.SetResourceBuilder(resource);
        tp.AddSource(ArcherTelemetry.SourceName);
        tp.AddSource("Microsoft.Orleans.Runtime");
        tp.AddSource("Microsoft.Orleans.Application");
        tp.AddSource("Azure.*");
        if (opts.ConsoleExporter) tp.AddConsoleExporter();
        if (!string.IsNullOrWhiteSpace(opts.Endpoint))
        {
            tp.AddOtlpExporter(o => ConfigureOtlp(o, opts));
        }
    }

    private static void ConfigureMetrics(MeterProviderBuilder mp, ResourceBuilder resource, OtelOptions opts)
    {
        mp.SetResourceBuilder(resource);
        mp.AddMeter(ArcherTelemetry.MeterName);
        mp.AddRuntimeInstrumentation();
        if (opts.ConsoleExporter) mp.AddConsoleExporter();
        if (!string.IsNullOrWhiteSpace(opts.Endpoint))
        {
            mp.AddOtlpExporter(o => ConfigureOtlp(o, opts));
        }
    }

    private static void ConfigureLogging(OpenTelemetryLoggerOptions o, ResourceBuilder resource, OtelOptions opts)
    {
        o.SetResourceBuilder(resource);
        o.IncludeFormattedMessage = true;
        o.IncludeScopes = true;
        // Redact sensitive attributes BEFORE any exporter sees them.
        o.AddProcessor(new RedactingLogProcessor());
        if (opts.ConsoleExporter) o.AddConsoleExporter();
        if (!string.IsNullOrWhiteSpace(opts.Endpoint))
        {
            o.AddOtlpExporter(e => ConfigureOtlp(e, opts));
        }
    }

    private static void ConfigureOtlp(OpenTelemetry.Exporter.OtlpExporterOptions o, OtelOptions opts)
    {
        o.Endpoint = new Uri(opts.Endpoint!);
        o.Protocol = MapProtocol(opts.Protocol);
    }

    private static OpenTelemetry.Exporter.OtlpExportProtocol MapProtocol(string protocol) =>
        string.Equals(protocol, "httpprotobuf", StringComparison.OrdinalIgnoreCase)
            ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf
            : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
}
