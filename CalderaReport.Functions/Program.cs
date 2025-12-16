using CalderaReport.Functions.Services.Abstract;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using System.Diagnostics;
using System.Text.Json;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var cfg = builder.Configuration;
var baseEndpoint = cfg["OpenTelemetry:Endpoint"]?.TrimEnd('/');
if (!string.IsNullOrWhiteSpace(baseEndpoint))
{
    var tracesEndpoint = new Uri($"{baseEndpoint}/v1/traces");
    var metricsEndpoint = new Uri($"{baseEndpoint}/v1/metrics");
    var logsEndpoint = new Uri($"{baseEndpoint}/v1/logs");
    var samplingRatio = Math.Clamp(cfg.GetValue<double?>("OpenTelemetry:TraceSamplingRatio") ?? 1.0, 0.0001, 1.0);

    Action<ResourceBuilder> configureResource = resource =>
    {
        var assembly = typeof(Destiny2Service).Assembly.GetName();
        var serviceName = builder.Environment.ApplicationName ?? assembly.Name ?? "API";
        var serviceVersion = assembly.Version?.ToString() ?? "unknown";
        resource.AddService(serviceName, serviceVersion, Environment.MachineName)
            .AddAttributes(new[] { new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName) });
    };

    Action<BatchExportProcessorOptions<Activity>> configureBatch = batch =>
    {
        batch.MaxQueueSize = 2048;
        batch.ScheduledDelayMilliseconds = 5000;
        batch.ExporterTimeoutMilliseconds = 30000;
        batch.MaxExportBatchSize = 512;
    };

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(configureResource)
        .WithMetrics(metrics =>
        {
            metrics.AddRuntimeInstrumentation();
            metrics.AddHttpClientInstrumentation();
            metrics.AddAspNetCoreInstrumentation();
            metrics.AddNpgsqlInstrumentation();
            metrics.AddOtlpExporter(opts =>
            {
                opts.Endpoint = metricsEndpoint;
                opts.Protocol = OtlpExportProtocol.HttpProtobuf;
            });
        })
        .WithTracing(tracing =>
        {
            tracing.AddHttpClientInstrumentation();
            tracing.AddAspNetCoreInstrumentation();
            tracing.AddNpgsql();
            tracing.AddSource(APITelemetry.ActivitySourceName);
            tracing.SetSampler(new TraceIdRatioBasedSampler(samplingRatio));
            tracing.AddOtlpExporter(opts =>
            {
                opts.Endpoint = tracesEndpoint;
                opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                configureBatch(opts.BatchExportProcessorOptions);
            });
        });

    builder.Logging.AddOpenTelemetry(options =>
    {
        options.IncludeScopes = true;
        options.ParseStateValues = true;
        options.IncludeFormattedMessage = true;
        var resourceBuilder = ResourceBuilder.CreateDefault();
        configureResource(resourceBuilder);
        options.SetResourceBuilder(resourceBuilder);
        options.AddOtlpExporter(opts =>
        {
            opts.Endpoint = logsEndpoint;
            opts.Protocol = OtlpExportProtocol.HttpProtobuf;
        });
    });

    builder.Logging.AddFilter<OpenTelemetryLoggerProvider>(filter => builder.Environment.IsDevelopment() ? filter >= LogLevel.Information : filter >= LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.Extensions.Logging.Console", builder.Environment.IsDevelopment() ? LogLevel.Debug : LogLevel.Information);
}


builder.Services.AddDbContextPool<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSqlConnectionString"), npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorCodesToAdd: null);
    });

    options.EnableServiceProviderCaching();
    options.EnableSensitiveDataLogging(builder.Environment.IsDevelopment());
}, poolSize: 64);



builder.Services.AddHttpClient<IDestiny2ApiClient, Destiny2ApiClient>();
builder.Services.AddHttpClient<IManifestClient, ManifestClient>();
builder.Services.AddScoped<IDestiny2Service, Destiny2Service>();
builder.Services.AddScoped<IQueryService, QueryService>();

builder.Services.AddSingleton(sp =>
{
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    json.Converters.Add(new Int64AsStringJsonConverter());
    return json;
});

builder.Build().Run();
