using CalderaReport.API.Telemetry;
using CalderaReport.Clients;
using CalderaReport.Clients.Abstract;
using CalderaReport.Clients.Registries;
using CalderaReport.Domain.Configuration;
using CalderaReport.Domain.Data;
using CalderaReport.Domain.Serializers;
using CalderaReport.Services;
using CalderaReport.Services.Abstract;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Dashboard.BasicAuthorization;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.HttpOverrides;
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

builder.Services.AddHangfire(config => config.UsePostgreSqlStorage(c => c.UseNpgsqlConnection(builder.Configuration.GetConnectionString("PostgreSqlConnectionString"))));

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = Environment.ProcessorCount;
    options.Queues = new[] { "default", "leaderboards-api" };
});

var corsAllowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? Array.Empty<string>();

if (corsAllowedOrigins.Length == 0)
{
    var csvAllowedOrigins = builder.Configuration["Cors:AllowedOrigins"];
    if (!string.IsNullOrWhiteSpace(csvAllowedOrigins))
    {
        corsAllowedOrigins = csvAllowedOrigins
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("DefaultCors", policy =>
    {
        if (corsAllowedOrigins.Length > 0)
        {
            policy.WithOrigins(corsAllowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
        else if (builder.Environment.IsDevelopment())
        {
            policy.AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

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

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto
});

//app.UseHttpsRedirection();


var hangfireDashboardUsername = builder.Configuration["Hangfire:Dashboard:Username"];
var hangfireDashboardPassword = builder.Configuration["Hangfire:Dashboard:Password"];
if (string.IsNullOrWhiteSpace(hangfireDashboardUsername) || string.IsNullOrWhiteSpace(hangfireDashboardPassword))
{
    throw new InvalidOperationException("Hangfire dashboard credentials are not configured. Set Hangfire:Dashboard:Username and Hangfire:Dashboard:Password.");
}

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[]
    {
        new BasicAuthAuthorizationFilter(new BasicAuthAuthorizationFilterOptions
        {
            RequireSsl = !app.Environment.IsDevelopment(),
            SslRedirect = !app.Environment.IsDevelopment(),
            LoginCaseSensitive = true,
            Users = new[]
            {
                new BasicAuthAuthorizationUser
                {
                    Login = hangfireDashboardUsername,
                    PasswordClear = hangfireDashboardPassword
                }
            }
        })
    }
});

using (var scope = app.Services.CreateScope())
{
    RecurringJob.AddOrUpdate<ICrawlerService>(
        "PlayerCrawlerJob",
        s => s.LoadCrawler(),
        Cron.Daily);
}

app.UseCors("DefaultCors");
app.UseAuthorization();

app.MapControllers();

app.Run();
