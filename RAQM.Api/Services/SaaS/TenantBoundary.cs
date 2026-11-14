using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;

namespace RAQM.Api.Services.SaaS;

public static class TenantBoundary
{
    public static async Task<bool> CanOperate(RaqmDbContext db, Guid tenant, CancellationToken ct)
    {
        var result = await db.Database.SqlQuery<bool>($"""SELECT saas.can_operate_v1({tenant}) AS "Value" """).ToListAsync(ct);
        return result.Single();
    }
}
