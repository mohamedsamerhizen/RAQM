using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace RAQM.Api.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RaqmDbContext>
{
    public RaqmDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<RaqmDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Database")
            ?? "Host=localhost;Database=raqm;Username=raqm").Options);
}

