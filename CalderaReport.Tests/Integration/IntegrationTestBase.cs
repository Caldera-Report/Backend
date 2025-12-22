using CalderaReport.Domain.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace CalderaReport.Tests.Integration;

public class IntegrationTestBase : IAsyncLifetime
{
    protected readonly PostgreSqlContainer _postgresContainer;
    protected readonly RedisContainer _redisContainer;
    protected IServiceProvider _serviceProvider = null!;
    protected AppDbContext _dbContext = null!;
    protected IConnectionMultiplexer _redis = null!;
    protected IDbContextFactory<AppDbContext> _contextFactory = null!;

    public IntegrationTestBase()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:15")
            .WithDatabase("testdb")
            .WithUsername("testuser")
            .WithPassword("testpass")
            .Build();

        _redisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        await _redisContainer.StartAsync();

        var services = new ServiceCollection();

        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseNpgsql(_postgresContainer.GetConnectionString()));
        
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        _redis = ConnectionMultiplexer.Connect(_redisContainer.GetConnectionString());
        services.AddSingleton<IConnectionMultiplexer>(_redis);

        _serviceProvider = services.BuildServiceProvider();
        _contextFactory = _serviceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        _dbContext = _contextFactory.CreateDbContext();

        await _dbContext.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dbContext != null)
            await _dbContext.DisposeAsync();
        if (_redis != null)
            await _redis.DisposeAsync();
        if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();
        await _postgresContainer.DisposeAsync();
        await _redisContainer.DisposeAsync();
    }
}
