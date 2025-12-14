using CalderaReport.Crawler.Clients;
using CalderaReport.Crawler.Clients.Abstract;
using CalderaReport.Crawler.Registries;
using CalderaReport.Crawler.Services;
using Domain.Configuration;
using Domain.Data;
using Domain.DTO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Threading.Channels;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

ConfigureOpenTelemetryLogs(builder);

void ConfigureOpenTelemetryLogs(HostApplicationBuilder b)
{
    var cfg = b.Configuration;
    var baseEndpoint = cfg["OpenTelemetry:Endpoint"]?.TrimEnd('/');

    if (string.IsNullOrWhiteSpace(baseEndpoint))
    {
        b.Logging.AddConsole();
        return;
    }

    var logsEndpoint = new Uri($"{baseEndpoint}/v1/logs");
    string? headers = cfg["OpenTelemetry:Headers"];

    var serviceName = b.Environment.ApplicationName ?? "Crawler";
    var serviceVersion = typeof(PlayerCrawler).Assembly.GetName().Version?.ToString() ?? "unknown";

    b.Logging.AddOpenTelemetry(options =>
    {
        options.IncludeScopes = true;
        options.ParseStateValues = true;
        options.IncludeFormattedMessage = true;

        options.SetResourceBuilder(ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceVersion, Environment.MachineName)
            .AddAttributes(new[]
            {
                new KeyValuePair<string, object>("deployment.environment", b.Environment.EnvironmentName),
            }));

        options.AddOtlpExporter(opts =>
        {
            opts.Endpoint = logsEndpoint;
            opts.Protocol = OtlpExportProtocol.HttpProtobuf;
            if (!string.IsNullOrWhiteSpace(headers))
                opts.Headers = headers;
        });
    });

    b.Logging.AddFilter<OpenTelemetryLoggerProvider>(filter =>
    {
        return filter >= LogLevel.Warning;
    });

    b.Logging.AddFilter("Microsoft.Extensions.Logging.Console",
        b.Environment.IsDevelopment() ? LogLevel.Debug : LogLevel.Information);
}

builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("RedisConnectionString") ?? throw new InvalidOperationException("Redis connection string is not configured"))
);
builder.Services.AddHttpClient();
builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSqlConnectionString"), npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorCodesToAdd: null);
    });
    options.EnableSensitiveDataLogging(true);
});
// Provide scoped AppDbContext via factory for components that expect the context directly.
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

builder.Services.AddOptions<Destiny2Options>()
    .Configure<IConfiguration>((settings, configuration) =>
    {
        configuration.GetSection("Destiny2Api").Bind(settings);
    });

builder.Services.AddSingleton<RateLimiterRegistry>();
builder.Services.AddHttpClient<IDestiny2ApiClient, Destiny2ApiClient>();
builder.Services.AddMemoryCache();

// Background services
builder.Services.AddHostedService(sp =>
    new PlayerCrawler(
        sp.GetRequiredService<IDestiny2ApiClient>(),
        sp.GetRequiredService<ILogger<PlayerCrawler>>(),
        sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
        sp.GetRequiredService<IMemoryCache>(),
        sp.GetRequiredService<IConnectionMultiplexer>()));

builder.Services.AddHostedService(sp =>
    new ActivityReportCrawler(
        sp.GetRequiredService<ILogger<ActivityReportCrawler>>(),
        sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
        sp.GetRequiredService<IDestiny2ApiClient>(),
        sp.GetRequiredService<IMemoryCache>(),
        sp.GetRequiredService<IConnectionMultiplexer>()));

var host = builder.Build();

await host.RunAsync();

