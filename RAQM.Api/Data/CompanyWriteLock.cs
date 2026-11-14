using Microsoft.EntityFrameworkCore;
namespace RAQM.Api.Data;

// The caller owns the transaction. Acquire the tenant commercial gate before the company row.
public static class CompanyWriteLock
{
    public static async Task Acquire(RaqmDbContext db, Guid companyId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Company write lock requires a transaction.");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT saas.assert_write_v1((SELECT "TenantId" FROM organizations."Companies" WHERE "Id"={companyId}))
            """, ct);
        await db.Companies.FromSqlInterpolated(
            $"SELECT * FROM organizations.\"Companies\" WHERE \"Id\" = {companyId} FOR UPDATE").AsNoTracking().SingleAsync(ct);
    }
}
