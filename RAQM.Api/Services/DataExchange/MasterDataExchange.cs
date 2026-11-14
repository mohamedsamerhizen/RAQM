using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Integrations;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Integrations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.ApiAccess;
using RAQM.Api.Services.Integrations;
using static RAQM.Api.Services.Integrations.IntegrationAdministration;

namespace RAQM.Api.Services.DataExchange;

public interface IMasterDataAdapter
{
    ImportPreview Parse(ImportRequest input);
}

public sealed class CustomerCsvAdapter : IMasterDataAdapter
{
    public ImportPreview Parse(ImportRequest input)
    {
        if (input.Version != 1 || string.IsNullOrWhiteSpace(input.Csv) || Encoding.UTF8.GetByteCount(input.Csv) > 64 * 1024)
            throw Invalid("Use schema version 1 and a CSV payload no larger than 64 KiB.");
        using var parser = new TextFieldParser(new StringReader(input.Csv))
        {
            Delimiters = [","], HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true
        };
        var rows = new List<ImportRow>();
        var errors = new List<string>();
        var external = new HashSet<string>(StringComparer.Ordinal);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (parser.ReadFields() is not { } header || !header.SequenceEqual(new[] { "external_id", "code", "name" }))
                return new([], ["Use the exact header external_id,code,name."]);
            var count = 0;
            while (!parser.EndOfData)
            {
                if (++count > 100) return new([], ["The import limit is 100 rows."]);
                var line = (int)parser.LineNumber;
                var fields = parser.ReadFields();
                if (fields is not { Length: 3 } || fields[0].Length is < 1 or > 80 || fields[1].Length is < 1 or > 40 ||
                    fields[2].Length is < 1 or > 120 || fields.Any(x => x.Any(char.IsControl)))
                {
                    errors.Add($"Line {line}: invalid field count, characters or lengths.");
                    continue;
                }
                var id = fields[0].ToUpperInvariant();
                var code = fields[1].ToUpperInvariant();
                if (!external.Add(id) || !codes.Add(code)) errors.Add($"Line {line}: duplicate identifier or code.");
                else rows.Add(new(line, id, code, fields[2]));
            }
        }
        catch (MalformedLineException) { errors.Add($"Line {parser.ErrorLineNumber}: malformed CSV."); }
        if (rows.Count == 0 && errors.Count == 0) errors.Add("Include at least one data row.");
        return new(rows.ToArray(), errors.ToArray());
    }
}

public sealed class MasterDataExchange(RaqmDbContext db, IMasterDataAdapter adapter, IntegrationAdministration admin,
    FinancialAccess access, ITradingSetupService trading, TimeProvider clock)
{
    public async Task<object> Receive(MachineIdentity actor, Guid org, Guid company, string key, ImportRequest input, CancellationToken ct)
    {
        key = AccountingRules.Text(key, 100, "External request ID");
        await actor.Require(db, org, company, "master.import", clock.GetUtcNow(), ct);
        var hash = TradeRules.Hash(input);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await CompanyWriteLock.Acquire(db, company, ct);
        var app = await actor.Require(db, org, company, "master.import", clock.GetUtcNow(), ct);
        var row = await db.Set<InboxBatch>().SingleOrDefaultAsync(x => x.CompanyId == company && x.ApplicationId == actor.Application && x.ExternalId == key, ct);
        if (row is not null && row.Fingerprint != hash) throw Conflict("The external ID already belongs to a different payload.");
        if (row is null)
        {
            var preview = adapter.Parse(input);
            if (preview.Errors.Length > 0) return new { Accepted = false, Preview = preview };
            row = new InboxBatch { TenantId = app.TenantId, OrganizationId = org, CompanyId = company,
                ApplicationId = app.Id, ExternalId = key, Fingerprint = hash, Input = JsonSerializer.Serialize(preview.Rows),
                ReceivedAt = clock.GetUtcNow() };
            db.Add(row);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new { Accepted = true, row.Id, row.State, row.Result };
    }

    public async Task<object> Inspect(Guid actor, Guid org, Guid company, Guid batch, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, true, ct);
        var row = await Batch(company, batch, ct);
        return new { row.Id, row.ApplicationId, row.ExternalId, row.State, row.ApprovedBy, row.ReceivedAt,
            Rows = JsonSerializer.Deserialize<ImportRow[]>(row.Input), row.Result };
    }

    public Task<object> Change(Guid actor, Guid org, Guid company, Guid batch, string action, CancellationToken ct) =>
        admin.Admin<object>(actor, org, company, async (scope, token) =>
        {
            var row = await Batch(company, batch, token);
            if (action == "execute" && row.State == InboxState.Processed) return new { row.Id, row.State, row.Result };
            var app = await admin.Application(company, row.ApplicationId, token);
            if (action == "cancel" && row.State is InboxState.Received or InboxState.Approved) row.State = InboxState.Cancelled;
            else if (app.State != ApplicationState.Active) throw Conflict("The source application is inactive.");
            else if (action == "approve" && row.State == InboxState.Received && actor != app.OwnerId)
            {
                row.ApprovedBy = actor;
                row.State = InboxState.Approved;
            }
            else if (action == "execute" && row.State == InboxState.Approved && row.ApprovedBy != actor)
            {
                await access.Resolve(row.ApprovedBy!.Value, org, company, true, true, token);
                var rows = JsonSerializer.Deserialize<ImportRow[]>(row.Input)!;
                var keys = rows.Select(x => x.ExternalId).ToArray();
                var codes = rows.Select(x => x.Code).ToArray();
                if (await db.Set<ImportedMaster>().AnyAsync(x => x.CompanyId == company && x.ApplicationId == app.Id && keys.Contains(x.ExternalId), token) ||
                    await db.TradingParties.AnyAsync(x => x.CompanyId == company && x.Kind == PartyKind.Customer && codes.Contains(x.Code), token))
                    throw Conflict("An external row identity or customer code already exists. The batch was not applied.");
                var accounts = await db.TradingAccounts.Where(x => x.CompanyId == company &&
                    (x.Role == AccountRole.Receivable || x.Role == AccountRole.Revenue)).ToDictionaryAsync(x => x.Role, x => x.AccountId, token);
                if (!accounts.TryGetValue(AccountRole.Receivable, out var ar) || !accounts.TryGetValue(AccountRole.Revenue, out var income))
                    throw Conflict("Configure the existing trading accounts first.");
                var result = new List<object>();
                foreach (var input in rows)
                {
                    var party = await trading.CreateParty(actor, org, company, new PartyRequest(PartyKind.Customer, input.Code, input.Name,
                        null, null, null, null, 0, null, ar, income), token);
                    db.Add(Scoped(scope, new ImportedMaster { ApplicationId = app.Id, BatchId = row.Id, ExternalId = input.ExternalId, PartyId = party.Id }));
                    result.Add(new { input.Line, input.ExternalId, party.Id });
                }
                row.Result = JsonSerializer.Serialize(result);
                row.State = InboxState.Processed;
            }
            else throw Conflict("Invalid or non-independent import transition.");
            await admin.Audit(scope, actor, row.Id, "Import:" + action, token);
            return new { row.Id, row.State, row.Result };
        }, ct);

    private async Task<InboxBatch> Batch(Guid company, Guid id, CancellationToken ct) =>
        await db.Set<InboxBatch>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw Missing();
}
