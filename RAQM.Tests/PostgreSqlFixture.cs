using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;
using RAQM.Api.Data;
namespace RAQM.Tests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public ApiFactory Factory { get; private set; } = null!;
    public string ConnectionString => postgres.GetConnectionString();
    public async Task InitializeAsync()
    {
        // Missing Docker is a real failure, never a silent skip or an in-memory substitution.
        await postgres.StartAsync();
        Factory = new ApiFactory(postgres.GetConnectionString());
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().Database.MigrateAsync();
    }
    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        await postgres.DisposeAsync();
    }
}
[CollectionDefinition("PostgreSQL")]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture> { }



