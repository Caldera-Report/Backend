using CalderaReport.Crawler.Frontend.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var redisConnectionString = builder.Configuration.GetConnectionString("RedisConnectionString")
    ?? throw new InvalidOperationException("Redis connection string is not configured.");
var postgresConnectionString = builder.Configuration.GetConnectionString("PostgreSqlConnectionString")
    ?? throw new InvalidOperationException("PostgreSQL connection string is not configured.");

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(postgresConnectionString));
builder.Services.AddSingleton<ICrawlerStatusProvider, CrawlerStatusProvider>();
builder.Services.AddSingleton<ICrawlerTriggerService, CrawlerTriggerService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
else
    app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
