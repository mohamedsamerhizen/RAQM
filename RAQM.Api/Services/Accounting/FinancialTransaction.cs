using RAQM.Api.Data;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Accounting;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace RAQM.Api.Services.Accounting;
// A scoped coordinator is the sole commit owner. Joined failures poison the outer operation,
// even if a trusted caller catches the exception. No independently committed nested transaction.
public sealed class FinancialTransaction(RaqmDbContext db, FinancialAccess access, ILogger<FinancialTransaction>? logger = null) : IFinancialTransaction
{
    private (Guid Actor, Guid Org)? owner;
    private IReadOnlyDictionary<Guid, Company>? contexts;
    private bool failed;
    public async Task<T> Execute<T>(Guid actor, Guid org, Guid company, Func<Company, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        if (owner is { } current)
        {
            if (current != (actor, org) || !contexts!.TryGetValue(company, out var context))
            {
                failed = true;
                throw new ApiException(409, "Cannot join a different actor/company transaction.", "transaction_scope");
            }

            try
            {
                return await operation(context, ct);
            }
            catch
            {
                failed = true;
                throw;
            }
        }

        return await ExecuteCompanies(actor, org, [company], (companies, token) => operation(companies[company], token), ct);
    }

    public async Task<T> ExecuteCompanies<T>(Guid actor, Guid org, IReadOnlyCollection<Guid> companies,
        Func<IReadOnlyDictionary<Guid, Company>, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        if (owner is not null || db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Uncoordinated ambient financial transactions are not supported.");
        if (companies.Count == 0)
            throw new ArgumentException("At least one company is required.", nameof(companies));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var trace = Activity.Current?.TraceId.ToHexString();
            if (trace is not null)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('raqm.trace_id', {trace}, true)", ct);
            var locked = new Dictionary<Guid, Company>();
            foreach (var company in companies.Distinct().Order())
                locked.Add(company, await access.Lock(actor, org, company, false, ct));
            contexts = locked;
            owner = (actor, org);
            failed = false;
            var result = await operation(locked, ct);
            if (failed)
                throw new ApiException(409, "A joined financial operation failed; the entire transaction must roll back.", "transaction_failed");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            try
            {
                // PostgreSQL can already have ended the transaction when a deferred constraint fails at COMMIT.
                if (tx.GetDbTransaction().Connection is not null)
                    await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackError)
            {
                logger?.LogWarning("Financial transaction rollback could not be confirmed ({ErrorType})", rollbackError.GetType().Name);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }

            throw;
        }
        finally
        {
            owner = null;
            contexts = null;
            failed = false;
        }
    }
}
