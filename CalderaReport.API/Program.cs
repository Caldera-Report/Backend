using CalderaReport.API.Telemetry;
using CalderaReport.Clients;
using CalderaReport.Clients.Abstract;
using CalderaReport.Clients.Registries;
using CalderaReport.Domain.Configuration;
using CalderaReport.Domain.Data;
using CalderaReport.Domain.Serializers;
using CalderaReport.Services;
using CalderaReport.Services.Abstract;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

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
        var assembly = typeof(Program).Assembly.GetName();
        var serviceName = builder.Environment.ApplicationName ?? assembly.Name ?? "CalderaReport.API";
        var serviceVersion = assembly.Version?.ToString() ?? "unknown";
        resource.AddService(serviceName, serviceVersion, Environment.MachineName)
            .AddAttributes(new[]
            {
                new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName),
                new KeyValuePair<string, object>("host.name", Environment.MachineName)
            });
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

// Add services to the container.

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new Int64AsStringJsonConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddScoped<IPlayerService, PlayerService>();
builder.Services.AddScoped<IActivityService, ActivityService>();
builder.Services.AddScoped<ILeaderboardService, LeaderboardService>();
builder.Services.AddScoped<ICrawlerService, CrawlerService>();

builder.Services.AddPooledDbContextFactory<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSqlConnectionString"), npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorCodesToAdd: null);
    });
    options.EnableSensitiveDataLogging(builder.Environment.IsDevelopment());
}, poolSize: 64);


builder.Services.AddOptions<Destiny2Options>()
    .Configure<IConfiguration>((settings, configuration) =>
    {
        configuration.GetSection("Destiny2Api").Bind(settings);
    });

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("RedisConnectionString") ?? throw new InvalidOperationException("RedisConnectionString is not configured"))
);

builder.Services.AddHttpClient<IBungieClient, BungieClient>();

builder.Services.AddSingleton<RateLimiterRegistry>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

//app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
