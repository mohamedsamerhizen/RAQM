using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Banking;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Banking;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.Banking;

public sealed class StatementService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    BankingPolicy policy, TimeProvider clock) : IStatementService
{
    public async Task<StatementPreview> Preview(Guid actor, Guid org, Guid company, Guid bank, StatementRequest request, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, true, false, ct);
        var account = await policy.Bank(company, bank, ct, active: false);
        var preview = StatementCsv.Parse(request, account.Currency);
        return await Validate(company, bank, request, preview, ct);
    }

    public Task<StatementSummary> Import(Guid actor, Guid org, Guid company, Guid bank, StatementRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var account = await policy.Bank(company, bank, token, active: false);
            var key = AccountingRules.Text(request.IdempotencyKey, 64, "Import key");
            var external = AccountingRules.Text(request.ExternalId, 160, "Statement identifier");
            var preview = StatementCsv.Parse(request, account.Currency);
            var prior = await db.BankStatements.AsNoTracking().Include(x => x.Lines)
                .SingleOrDefaultAsync(x => x.CompanyId == company && x.BankAccountId == bank && x.RequestKey == key, token);
            if (prior is not null)
            {
                if (preview.Errors.Count != 0 || prior.Digest != preview.Digest || prior.ExternalId != external)
                    throw new ApiException(409, "The import key has a different payload.", "import_key");
                return View(prior, prior.Lines.Count);
            }
            preview = await Validate(company, bank, request, preview, token);
            if (preview.Errors.Count != 0)
                throw new ApiException(400, $"Statement validation failed at row {preview.Errors[0].Row}. {preview.Errors[0].Message}", "statement_invalid");
            var statement = new BankStatement { CompanyId = company, TenantId = scope.TenantId, OrganizationId = org,
                BankAccountId = bank, Start = request.Start, End = request.End, Opening = request.Opening, Closing = request.Closing,
                ExternalId = external, Digest = preview.Digest, RequestKey = key, ImportedBy = actor, LineCount = preview.Rows.Count };
            statement.Lines = preview.Rows.Select((x, i) => new BankStatementLine
            {
                CompanyId = company, TenantId = scope.TenantId, OrganizationId = org, BankAccountId = bank, StatementId = statement.Id,
                Row = x.Row, Sequence = i + 1, BookingDate = x.BookingDate, ValueDate = x.ValueDate, Amount = x.Amount, Reference = x.Reference,
                Description = x.Description, StableExternalId = x.StableId, OriginalFields = x.OriginalFields
            }).ToList();
            db.BankStatements.Add(statement);
            access.Audit(scope, actor, statement.Id, "BankStatementImported", $"Rows: {statement.Lines.Count}");
            return View(statement, statement.Lines.Count);
        }, ct);

    private async Task<StatementPreview> Validate(Guid company, Guid bank, StatementRequest request, StatementPreview preview, CancellationToken ct)
    {
        var errors = preview.Errors.ToList();
        var warnings = preview.Warnings.ToList();
        if (string.IsNullOrWhiteSpace(request.ExternalId) || request.ExternalId.Trim().Length > 160)
            errors.Add(new(0, "A statement identifier of up to 160 characters is required."));
        if (request.End > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            errors.Add(new(0, "The statement end date is in the future."));
        var prior = await db.BankStatements.AsNoTracking().Where(x => x.CompanyId == company && x.BankAccountId == bank)
            .OrderByDescending(x => x.End).FirstOrDefaultAsync(ct);
        if (prior is not null && (request.Start.DayNumber != prior.End.DayNumber + 1 || request.Opening != prior.Closing))
            errors.Add(new(0, "Statement dates and opening balance must continue the last imported statement."));
        var external = request.ExternalId?.Trim();
        if (await db.BankStatements.AnyAsync(x => x.CompanyId == company && x.BankAccountId == bank &&
            (x.ExternalId == external || x.Digest == preview.Digest), ct))
            errors.Add(new(0, "This statement identifier or content has already been imported."));
        var stable = preview.Rows.Where(x => x.StableId != null).Select(x => x.StableId!).ToArray();
        var duplicates = stable.Length == 0 ? [] : await db.BankStatementLines.Where(x => x.CompanyId == company && x.BankAccountId == bank &&
            x.StableExternalId != null && stable.Contains(x.StableExternalId)).Select(x => x.StableExternalId!).ToListAsync(ct);
        var used = duplicates.ToHashSet(StringComparer.Ordinal);
        errors.AddRange(preview.Rows.Where(x => x.StableId != null && used.Contains(x.StableId))
            .Select(x => new ImportIssue(x.Row, "Stable bank transaction ID has already been imported.")));
        return preview with { Errors = errors, Warnings = warnings };
    }

    public async Task<PageResult<StatementSummary>> List(Guid actor, Guid org, Guid company, Guid bank, int page, int size, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        await policy.Bank(company, bank, ct, active: false);
        AccountingRules.Page(page, size);
        var query = db.BankStatements.AsNoTracking().Where(x => x.CompanyId == company && x.BankAccountId == bank);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.End).Skip((page - 1) * size).Take(size)
            .Select(x => new StatementSummary(x.Id, x.BankAccountId, x.Start, x.End, x.Opening, x.Closing, x.ExternalId, x.LineCount, x.ImportedAt)).ToListAsync(ct);
        return new(rows, total, page, size);
    }

    public async Task<IReadOnlyList<BankStatementLine>> Lines(Guid actor, Guid org, Guid company, Guid bank, Guid statement, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        if (!await db.BankStatements.AnyAsync(x => x.CompanyId == company && x.BankAccountId == bank && x.Id == statement, ct))
            throw new ApiException(404, "Statement not found.", "statement_missing");
        return await db.BankStatementLines.AsNoTracking().Where(x => x.CompanyId == company && x.StatementId == statement).OrderBy(x => x.Row).ToListAsync(ct);
    }

    private static StatementSummary View(BankStatement statement, int count) =>
        new(statement.Id, statement.BankAccountId, statement.Start, statement.End, statement.Opening, statement.Closing, statement.ExternalId, count, statement.ImportedAt);
}
