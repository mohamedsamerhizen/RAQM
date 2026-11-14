using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.DTOs.TaxAccounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.FinancialReporting;
using RAQM.Api.Services.TaxJurisdictions;
using static RAQM.Api.Services.TaxJurisdictions.TaxConfigurationService;

namespace RAQM.Api.Services.TaxReporting;

public sealed record TaxRegisterEntry(DateOnly Date, Guid SourceId, Guid JournalId, string Type, string Reference, string Currency,
    Guid AccountId, decimal Signed, decimal TaxableBase, Guid? RateId, bool Legacy, Guid? OriginalLineId, int? LineNumber = null,
    Guid? RuleId = null, TaxTreatment? Treatment = null);
public sealed record TaxReconciliationLine(TaxControlRole Role, Guid Account, decimal Opening, decimal SourceMovement, decimal LedgerMovement, decimal Closing,
    decimal Difference);
public sealed record TaxReportSnapshot(Guid RegistrationId, string Currency, DateOnly From, DateOnly To, TaxRegisterEntry[] Entries,
    TaxReconciliationLine[] Reconciliation, Guid[] Pending, Guid[] UnprofiledDocuments, Guid[] UnassignedTaxLines)
{
    public bool Agrees => Reconciliation.All(x => x.Difference == 0);
    public decimal OutputTax => -Movement(TaxControlRole.Output, "Invoice", "CustomerCredit", "ReverseCharge");
    public decimal RecoverableInput => Movement(TaxControlRole.Input, "Bill", "SupplierCredit", "ReverseCharge");
    public decimal NonrecoverableTax => Movement(TaxControlRole.Nonrecoverable, "ReverseCharge");
    public decimal Withheld => -Movement(TaxControlRole.Withholding, "Withhold");

    private decimal Movement(TaxControlRole role, params string[] types)
    {
        var account = Reconciliation.SingleOrDefault(x => x.Role == role)?.Account;
        return Entries.Where(x => x.AccountId == account && types.Contains(x.Type)).Sum(x => x.Signed);
    }
}

public sealed class TaxReportingService(RaqmDbContext db, FinancialAccess access, TaxConfigurationService configuration, FinancialLedger ledger, TimeProvider clock)
{
    public Task<TaxPeriod> Open(Guid actor, Guid org, Guid company, Guid id, TaxPeriodRequest input, CancellationToken ct) =>
        configuration.Admin(actor, org, company, async (scope, token) =>
        {
            await configuration.Registration(company, id, token, input.From);
            if (input.To < input.From || input.To.DayNumber - input.From.DayNumber > 731)
                throw Bad("Choose an ordered tax reporting period up to two years.");
            if (await db.Set<TaxPeriod>().AnyAsync(x => x.CompanyId == company && x.RegistrationId == id &&
                x.From <= input.To && x.To >= input.From, token)) throw Conflict("The registration already has a tax period covering these dates.");
            var period = Scoped(scope, new TaxPeriod { RegistrationId = id, From = input.From, To = input.To,
                Name = AccountingRules.Text(input.Name, 120, "Tax period name"), OpenedBy = actor });
            db.Add(period);
            access.Audit(scope, actor, period.Id, "TaxPeriodOpened");
            return period;
        }, ct);

    public async Task<object> Periods(Guid actor, Guid org, Guid company, Guid registration, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        await configuration.Registration(company, registration, ct);
        return await db.Set<TaxPeriod>().AsNoTracking().Where(x => x.CompanyId == company && x.RegistrationId == registration)
            .OrderByDescending(x => x.From).ToListAsync(ct);
    }

    public async Task<TaxReportSnapshot> Report(Guid actor, Guid org, Guid company, Guid registration, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var result = await Capture(scope, registration, from, to, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<object> Compare(Guid actor, Guid org, Guid company, Guid registration, DateOnly from, DateOnly to,
        DateOnly priorFrom, DateOnly priorTo, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var current = await Capture(scope, registration, from, to, ct);
        var previous = await Capture(scope, registration, priorFrom, priorTo, ct);
        await tx.CommitAsync(ct);
        return new { Current = current, Previous = previous };
    }

    public Task<TaxReturnVersion> Prepare(Guid actor, Guid org, Guid company, Guid registration, Guid periodId, TaxReturnRequest input, CancellationToken ct) =>
        configuration.Admin(actor, org, company, async (scope, token) =>
        {
            var period = await Period(company, registration, periodId, token);
            var previous = await Versions(company, registration, periodId, token);
            if (previous.FirstOrDefault(x => x.State == TaxEvidenceState.Approved)?.Id != input.SupersedesId)
                throw Conflict("An amendment must name the current approved return.");
            var snapshot = await Capture(scope, registration, period.From, period.To, token);
            var row = Scoped(scope, new TaxReturnVersion { RegistrationId = registration, PeriodId = period.Id,
                SupersedesId = input.SupersedesId, Version = (previous.FirstOrDefault()?.Version ?? 0) + 1,
                SourceHash = TradeRules.Hash(snapshot), SnapshotJson = JsonSerializer.Serialize(snapshot),
                Evidence = AccountingRules.Text(input.Evidence, 1000, "Return preparation evidence"), PreparedBy = actor });
            db.Add(row);
            access.Audit(scope, actor, row.Id, "TaxReturnPrepared", row.Evidence);
            return row;
        }, ct);

    public Task<TaxReturnVersion> Approve(Guid actor, Guid org, Guid company, Guid registration, Guid periodId, Guid id, CancellationToken ct) =>
        configuration.Admin(actor, org, company, async (scope, token) =>
        {
            var period = await Period(company, registration, periodId, token);
            var row = await db.Set<TaxReturnVersion>().SingleOrDefaultAsync(x => x.CompanyId == company && x.RegistrationId == registration &&
                x.PeriodId == periodId && x.Id == id, token) ?? throw Missing();
            if (row.ApprovedBy == actor) return row;
            Independent(row.PreparedBy, row.ApprovedBy, actor);
            var prior = (await Versions(company, registration, periodId, token)).FirstOrDefault(x => x.State == TaxEvidenceState.Approved);
            if (prior?.Id != row.SupersedesId) throw Conflict("The approved tax return changed. Prepare another version.");
            var snapshot = await Capture(scope, registration, period.From, period.To, token);
            if (!snapshot.Agrees || snapshot.Pending.Length > 0 || snapshot.UnprofiledDocuments.Length > 0 || snapshot.UnassignedTaxLines.Length > 0)
                throw Conflict("Investigate control differences, pending work and unassigned evidence before approving.");
            if (TradeRules.Hash(snapshot) != row.SourceHash) throw Conflict("Source evidence changed. Prepare a new tax return version.");
            row.State = TaxEvidenceState.Approved;
            row.ApprovedBy = actor;
            row.ApprovedAt = AccountingRules.StoredUtcInstant(clock.GetUtcNow());
            access.Audit(scope, actor, id, "TaxReturnApproved");
            return row;
        }, ct);

    public async Task<IReadOnlyList<TaxReturnVersion>> History(Guid actor, Guid org, Guid company, Guid registration, Guid period, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        await Period(company, registration, period, ct);
        return await Versions(company, registration, period, ct);
    }

    public async Task<string> Export(Guid actor, Guid org, Guid company, Guid registration, Guid period, Guid id, CancellationToken ct)
    {
        var rows = await History(actor, org, company, registration, period, ct);
        var row = rows.SingleOrDefault(x => x.Id == id && x.State == TaxEvidenceState.Approved) ?? throw Missing();
        var snapshot = JsonSerializer.Deserialize<TaxReportSnapshot>(row.SnapshotJson)!;
        var text = new StringBuilder("date,type,reference,source,journal,currency,account,signed,taxable_base,rate,legacy\r\n");
        foreach (var x in snapshot.Entries)
            text.Append(CultureInfo.InvariantCulture, $"{x.Date:yyyy-MM-dd},{x.Type},{Cell(x.Reference)},{x.SourceId},{x.JournalId},{x.Currency},{x.AccountId},{x.Signed},{x.TaxableBase},{x.RateId},{x.Legacy}\r\n");
        return text.ToString();
    }

    public async Task<object> Foreign(Guid actor, Guid org, Guid company, DateOnly from, DateOnly to, int page, int size, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        Dates(from, to);
        AccountingRules.Page(page, size);
        var operations = await db.FxOperations.AsNoTracking().Where(x => x.CompanyId == company && x.State == FxState.Posted &&
            x.Date >= from && x.Date <= to && (x.Kind == FxKind.Invoice || x.Kind == FxKind.Bill || x.Kind == FxKind.Credit))
            .OrderBy(x => x.Date).ThenBy(x => x.Sequence).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return operations.Select(x => new { x.Id, x.Kind, x.Date, x.JournalId, x.OriginalId, Plan = JsonSerializer.Deserialize<FxPlan>(x.PlanJson!) }).ToArray();
    }

    internal async Task<TaxReportSnapshot> Capture(Company company, Guid id, DateOnly from, DateOnly to, CancellationToken ct)
    {
        Dates(from, to);
        var registration = await configuration.Registration(company.Id, id, ct, from);
        if (registration.Currency != company.BaseCurrency) throw Conflict("This return workflow requires reporting currency to match functional currency.");
        var controls = await db.Set<TaxControl>().AsNoTracking().Where(x => x.CompanyId == company.Id && x.RegistrationId == id)
            .OrderBy(x => x.Role).ToListAsync(ct);
        if (controls.Count == 0) throw Conflict("Register tax controls before preparing reconciliation.");
        var ids = controls.Select(x => x.AccountId).ToArray();
        var domestic = await (from line in db.TradeLines.AsNoTracking()
            join document in db.TradeDocuments.AsNoTracking() on line.DocumentId equals document.Id
            where line.CompanyId == company.Id && document.CompanyId == company.Id && document.State == TradeState.Posted &&
                document.PostingDate <= to && document.JournalId != null
            let source = document.OriginalDocumentId ?? document.Id
            let profile = db.Set<CommercialTaxEvidence>().Where(e => e.CompanyId == company.Id && e.DocumentId == source)
                .Select(e => (Guid?)e.RegistrationId).FirstOrDefault()
            where (profile == id || (line.TaxAccountId.HasValue && ids.Contains(line.TaxAccountId.Value))) &&
                document.Kind != TradeKind.OpeningReceivable && document.Kind != TradeKind.OpeningPayable
            let originalNumber = db.TradeLines.Where(x => x.CompanyId == company.Id && x.Id == line.OriginalLineId)
                .Select(x => (int?)x.Number).FirstOrDefault()
            select new { line.Id, line.Number, line.TaxAccountId, line.TaxAmount, line.NetAmount, line.TaxRateId, line.OriginalLineId,
                document.Kind, Document = document.Id, document.JournalId, document.PostingDate, DocumentNumber = document.Number, Profile = profile,
                Basis = source, QuoteLine = originalNumber ?? line.Number }).ToListAsync(ct);
        var sources = domestic.Where(x => x.Profile == id).Select(x => x.Basis).Distinct().ToArray();
        var evidence = await db.Set<CommercialTaxEvidence>().AsNoTracking().Where(x => x.CompanyId == company.Id &&
            x.RegistrationId == id && sources.Contains(x.DocumentId)).ToDictionaryAsync(x => x.DocumentId, ct);
        var quotes = evidence.ToDictionary(x => x.Key, x => JsonSerializer.Deserialize<TaxQuote>(x.Value.SnapshotJson)!);
        var entries = domestic.Select(x =>
        {
            var credit = TradeRules.IsCredit(x.Kind) ? -1 : 1;
            var sale = TradeRules.Side(x.Kind) == PartyKind.Customer;
            var component = quotes.TryGetValue(x.Basis, out var quote) ? quote.Lines.Single(y => y.Number == x.QuoteLine).Components.Single() : null;
            return new TaxRegisterEntry(x.PostingDate!.Value, x.Document, x.JournalId!.Value, x.Kind.ToString(), x.DocumentNumber ?? "",
                company.BaseCurrency, x.TaxAccountId ?? Guid.Empty, x.TaxAmount * credit * (sale ? -1 : 1), x.NetAmount * credit, x.TaxRateId,
                x.Profile != id, x.OriginalLineId, x.Number, component?.RuleId, component?.Treatment);
        }).ToList();
        var operations = await db.Set<TaxOperation>().AsNoTracking().Where(x => x.CompanyId == company.Id && x.RegistrationId == id &&
            x.Date <= to).OrderBy(x => x.Date).ThenBy(x => x.Id).ToListAsync(ct);
        var byId = operations.ToDictionary(x => x.Id);
        foreach (var op in operations.Where(x => x.State == TaxEvidenceState.Posted))
        {
            var plan = JsonSerializer.Deserialize<TaxPostingPlan>(op.PlanJson!)!;
            foreach (var line in plan.Lines.Where(x => ids.Contains(x.AccountId)))
                entries.Add(new(op.Date, op.Id, op.JournalId!.Value, op.Kind == TaxOperationKind.Correction && op.BasisId is { } original &&
                    byId.TryGetValue(original, out var originalOp) ? originalOp.Kind.ToString() : op.Kind.ToString(), op.Reference, company.BaseCurrency,
                    line.AccountId, line.Debit - line.Credit, plan.TaxableBase, null, false, null));
            var paymentId = op.PaymentId ?? (op.Kind == TaxOperationKind.Correction && op.BasisId.HasValue &&
                byId.TryGetValue(op.BasisId.Value, out var basis) ? basis.PaymentId : null);
            if (paymentId is { } payment)
            {
                var pay = await db.Payments.AsNoTracking().SingleAsync(x => x.CompanyId == company.Id && x.Id == payment, ct);
                var correcting = op.Kind == TaxOperationKind.Correction;
                var settlementJournal = correcting ? pay.ReversalJournalId!.Value : pay.JournalId!.Value;
                entries.Add(new(op.Date, op.Id, settlementJournal, "WithholdingSettlement", op.Reference, company.BaseCurrency,
                    pay.CashAccountId, correcting ? pay.Amount : -pay.Amount, 0, null, false, null));
            }
        }
        var gl = await ledger.Lines(company.Id, to, accounts: ids).ToListAsync(ct);
        var nominal = controls.Where(x => x.Role is TaxControlRole.Nonrecoverable or TaxControlRole.CurrentExpense or TaxControlRole.DeferredExpense)
            .Select(x => x.AccountId).ToHashSet();
        entries.AddRange(gl.Where(x => x.Source == FinancialMath.YearEndSource && nominal.Contains(x.AccountId)).Select(x =>
            new TaxRegisterEntry(x.Date, x.JournalId, x.JournalId, "FiscalClosing", x.Reference ?? "", company.BaseCurrency, x.AccountId,
                x.Debit - x.Credit, 0, null, false, null, x.Number)));
        var lines = controls.Select(x =>
        {
            var opening = gl.Where(g => g.AccountId == x.AccountId && g.Date < from).Sum(g => g.Debit - g.Credit);
            var movement = gl.Where(g => g.AccountId == x.AccountId && g.Date >= from).Sum(g => g.Debit - g.Credit);
            var source = entries.Where(e => e.AccountId == x.AccountId && e.Date >= from).Sum(e => e.Signed);
            var sourceClosing = entries.Where(e => e.AccountId == x.AccountId).Sum(e => e.Signed);
            return new TaxReconciliationLine(x.Role, x.AccountId, opening, source, movement, opening + movement, opening + movement - sourceClosing);
        }).ToArray();
        return new(id, company.BaseCurrency, from, to, entries.Where(x => x.Date >= from).OrderBy(x => x.Date).ThenBy(x => x.SourceId)
                .ThenBy(x => x.LineNumber).ThenBy(x => x.JournalId).ThenBy(x => x.AccountId).ToArray(), lines,
            operations.Where(x => x.State is TaxEvidenceState.Approved or TaxEvidenceState.Draft).Select(x => x.Id).Order().ToArray(),
            domestic.Where(x => x.PostingDate >= from && x.Profile != id && x.TaxAmount != 0).Select(x => x.Document).Distinct().Order().ToArray(),
            domestic.Where(x => x.PostingDate >= from && x.Profile == id && x.TaxAmount != 0 && !ids.Contains(x.TaxAccountId!.Value))
                .Select(x => x.Id).Distinct().Order().ToArray());
    }

    private async Task<TaxPeriod> Period(Guid company, Guid registration, Guid id, CancellationToken ct) =>
        await db.Set<TaxPeriod>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.RegistrationId == registration && x.Id == id, ct)
            ?? throw Missing();

    private Task<List<TaxReturnVersion>> Versions(Guid company, Guid registration, Guid period, CancellationToken ct) =>
        db.Set<TaxReturnVersion>().AsNoTracking().Where(x => x.CompanyId == company && x.RegistrationId == registration && x.PeriodId == period)
            .OrderByDescending(x => x.Version).ToListAsync(ct);

    private void Dates(DateOnly from, DateOnly to)
    {
        if (from.Year < 1900 || to < from || to > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            throw Bad("Use an ordered historical reporting window.");
    }

    private static string Cell(string text)
    {
        text = text.TrimStart();
        if (text.Length > 0 && "=+-@".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
