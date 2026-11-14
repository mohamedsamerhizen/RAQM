using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.TaxAccounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.FinancialReporting;
using RAQM.Api.Services.IncomeTax;
using RAQM.Api.Services.TaxJurisdictions;
using RAQM.Api.Services.WithholdingTax;
using static RAQM.Api.Services.TaxJurisdictions.TaxConfigurationService;

namespace RAQM.Api.Services.TaxAccounting;

public sealed class TaxOperationService(RaqmDbContext db, TaxConfigurationService configuration, IPostingEngine engine,
    ISettlementService settlement, FinancialLedger ledger, FinancialAccess access, TimeProvider clock, GovernanceGate? governance = null)
{
    internal const string Source = "Tax.Operation";
    internal static string Key(Guid id) => $"Tax:{id:N}";
    internal static string PaymentKey(Guid id) => $"tax-ap-{id:N}";
    internal static string ReversePaymentKey(Guid id) => $"tax-ap-reverse-{id:N}";

    public Task<TaxOperation> Prepare(Guid actor, Guid org, Guid company, Guid registration, TaxOperationRequest input, CancellationToken ct) =>
        configuration.Admin(actor, org, company, async (scope, token) =>
        {
            var profile = await configuration.Registration(company, registration, token, input.Date);
            var reference = CodeReference(input.Reference);
            var evidence = AccountingRules.Text(input.Evidence, 500, "Tax operation evidence");
            if (!Enum.IsDefined(input.Kind) || profile.Currency != scope.BaseCurrency || input.Date > Today() ||
                input.Date.Year < 1900 || input.BasisId.HasValue != (input.Kind == TaxOperationKind.Correction) ||
                input.BillId.HasValue != (input.Kind is TaxOperationKind.Withhold or TaxOperationKind.ReverseCharge))
                throw Bad("Use a supported operation, source and functional-currency date.");
            input = input with { Reference = reference, Evidence = evidence };
            var prior = await db.Set<TaxOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.RegistrationId == registration && x.Reference == reference, token);
            if (prior is not null)
            {
                if (TradeRules.Hash(JsonSerializer.Deserialize<TaxOperationRequest>(prior.InputsJson)!) != TradeRules.Hash(input))
                    throw Conflict("The reference belongs to different tax intent.");
                return prior;
            }
            var row = Scoped(scope, new TaxOperation { RegistrationId = registration, PeriodId = input.PeriodId,
                BillId = input.BillId, BasisId = input.BasisId, Kind = input.Kind, Date = input.Date, Reference = reference,
                Evidence = evidence, InputsJson = JsonSerializer.Serialize(input), PreparedBy = actor });
            var plan = await Plan(scope, row, input, actor, token);
            row.Amount = plan.Assessed;
            row.PlanJson = JsonSerializer.Serialize(plan with { Actor = Guid.Empty });
            db.Add(row);
            access.Audit(scope, actor, row.Id, "TaxOperationPrepared", row.Evidence);
            return row;
        }, ct);

    public Task<TaxOperation> Change(Guid actor, Guid org, Guid company, Guid registration, Guid id, string action, CancellationToken ct) =>
        configuration.Admin(actor, org, company, async (scope, token) =>
        {
            var row = await db.Set<TaxOperation>().SingleOrDefaultAsync(x => x.CompanyId == company && x.RegistrationId == registration &&
                x.Id == id, token) ?? throw Missing();
            if (action == "post" && row.State == TaxEvidenceState.Posted || action == "approve" && row.ApprovedBy == actor ||
                action == "cancel" && row.State == TaxEvidenceState.Cancelled) return row;
            if (action == "cancel" && row.State is TaxEvidenceState.Draft or TaxEvidenceState.Approved)
            {
                row.State = TaxEvidenceState.Cancelled;
                access.Audit(scope, actor, id, "TaxOperationCancelled");
                return row;
            }
            var input = JsonSerializer.Deserialize<TaxOperationRequest>(row.InputsJson)!;
            var plan = await Plan(scope, row, input, actor, token);
            var prepared = JsonSerializer.Deserialize<TaxPostingPlan>(row.PlanJson!)!;
            if (TradeRules.Hash(plan with { Actor = Guid.Empty }) != TradeRules.Hash(prepared with { Actor = Guid.Empty }))
                throw Conflict("Tax inputs or balances changed. Prepare a fresh operation.");
            if (action == "approve" && row.State == TaxEvidenceState.Draft)
            {
                Independent(row.PreparedBy, row.ApprovedBy, actor);
                if (governance is not null && row.Kind is TaxOperationKind.CurrentProvision or TaxOperationKind.DeferredAsset or TaxOperationKind.DeferredLiability)
                    await governance.Enforce(scope, actor, FinancialDuty.TaxProvisionApproval, id, token);
                row.ApprovedBy = actor;
                row.State = TaxEvidenceState.Approved;
                access.Audit(scope, actor, id, "TaxOperationApproved");
                if (row.Kind == TaxOperationKind.Withhold && input.PriorOrdinarySettlementsIneligible)
                    access.Audit(scope, actor, id, "TaxPriorSettlementIneligible", input.PolicyEvidence);
                return row;
            }
            if (action != "post" || row.State != TaxEvidenceState.Approved)
                throw Conflict("An independently approved tax operation is required.");
            row.State = TaxEvidenceState.Posting;
            row.PostingActor = actor;
            await db.SaveChangesAsync(token);

            Guid? paymentId = null;
            if (row.Kind == TaxOperationKind.Withhold)
            {
                var bill = await Bill(company, row.BillId!.Value, row.Date, token);
                var clearing = plan.Lines[0].AccountId;
                var pay = await settlement.ReceiveOrPay(actor, org, company, new(bill.PartyId, clearing, input.Amount,
                    row.Date, row.Reference, PaymentKey(row.Id), false, true, row.Evidence, [new(bill.Id, input.Amount)]), token);
                if (pay.ControlAmount != input.Amount || pay.AdvanceAmount != 0 || pay.Allocated != input.Amount)
                    throw Conflict("The withholding payment must settle only the approved bill obligation.");
                paymentId = pay.Id;
            }
            else if (row.Kind == TaxOperationKind.Correction)
            {
                var basis = await db.Set<TaxOperation>().AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == row.BasisId, token);
                if (basis.PaymentId is { } payment)
                    await settlement.ReversePayment(actor, org, company, payment, new(row.Date, ReversePaymentKey(row.Id), row.Evidence, true), token);
            }

            var receipt = await engine.PostAsync(actor, new(new(scope.TenantId, org, company), plan.ReversesJournalId.HasValue ? "Reversal" : Source,
                plan.ReversesJournalId ?? row.Id, Key(row.Id), row.Date, scope.BaseCurrency, plan.Lines, plan.ReversesJournalId,
                row.Date, $"Tax {row.Kind}", row.Reference, true, row.Evidence), token);
            row.PaymentId = paymentId;
            row.JournalId = receipt.JournalId;
            row.State = TaxEvidenceState.Posted;
            access.Audit(scope, actor, id, "TaxOperationPosted", row.Evidence);
            return row;
        }, ct);

    public async Task<IReadOnlyList<TaxOperation>> List(Guid actor, Guid org, Guid company, Guid registration, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        await access.Resolve(actor, org, company, false, false, ct);
        await configuration.Registration(company, registration, ct);
        return await db.Set<TaxOperation>().AsNoTracking().Where(x => x.CompanyId == company && x.RegistrationId == registration)
            .OrderByDescending(x => x.Date).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
    }

    public async Task<DeferredMeasurement> DeferredPreview(Guid actor, Guid org, Guid company, Guid registration,
        DeferredPreviewRequest input, CancellationToken ct)
    {
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var profile = await configuration.Registration(company, registration, ct);
        if (profile.Currency != scope.BaseCurrency) throw Conflict("Use a functional-currency tax registration.");
        return IncomeTaxMeasurement.Deferred(input.CarryingValue, input.TaxBase, input.UnderlyingLiability, input.Rate,
            input.RecognizableBasis, CurrencyPolicy.Units(scope.BaseCurrency));
    }

    public async Task<object> Certificate(Guid actor, Guid org, Guid company, Guid registration, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        var profile = await configuration.Registration(company, registration, ct);
        var row = await db.Set<TaxOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
            x.RegistrationId == registration && x.Id == id && x.Kind == TaxOperationKind.Withhold && x.State == TaxEvidenceState.Posted, ct)
            ?? throw Missing();
        var bill = await Bill(company, row.BillId!.Value, row.Date, ct);
        var input = JsonSerializer.Deserialize<TaxOperationRequest>(row.InputsJson)!;
        var correctedBy = await db.Set<TaxOperation>().AsNoTracking().Where(x => x.CompanyId == company && x.BasisId == id &&
            x.Kind == TaxOperationKind.Correction && x.State == TaxEvidenceState.Posted).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        return new { Schema = "raqm.withholding-evidence.v1", row.Id, row.Reference, row.Date, RegistrationNumber = profile.Number,
            Supplier = bill.PartyName, SupplierTaxNumber = bill.PartyTaxIdentifier, BillNumber = bill.Number, bill.Currency,
            Gross = input.Amount, Withheld = row.Amount, Cash = input.Amount - row.Amount, input.Rate,
            row.JournalId, row.PaymentId, row.ApprovedBy, CorrectedBy = correctedBy };
    }

    private async Task<TaxPostingPlan> Plan(Company scope, TaxOperation row, TaxOperationRequest input, Guid actor, CancellationToken ct)
    {
        var company = scope.Id;
        var controls = await db.Set<TaxControl>().AsNoTracking().Where(x => x.CompanyId == company && x.RegistrationId == row.RegistrationId)
            .ToDictionaryAsync(x => x.Role, x => x.AccountId, ct);
        var units = CurrencyPolicy.Units(scope.BaseCurrency);
        var date = row.Date;
        Guid Get(TaxControlRole role) => controls.GetValueOrDefault(role) is var id && id != Guid.Empty ? id : throw Conflict($"Missing {role} control.");
        TaxPostingPlan Result(JournalDraftLine[] lines, decimal taxable, decimal assessed, decimal recoverable = 0, decimal nonrecoverable = 0,
            decimal unrecognized = 0, Guid? reverses = null, Guid[]? priorOrdinaryAllocations = null)
        {
            if (assessed < 0 || assessed == 0 && row.Kind != TaxOperationKind.Withhold &&
                !(row.Kind == TaxOperationKind.Correction && reverses.HasValue))
                throw Conflict("This operation has no supported positive financial effect.");
            AccountingRules.Lines(lines, scope.BaseCurrency, true);
            return new(actor, lines, reverses, taxable, assessed, recoverable, nonrecoverable, unrecognized,
                priorOrdinaryAllocations);
        }
        static JournalDraftLine Debit(Guid account, decimal amount) => new(account, amount, 0, null);
        static JournalDraftLine Credit(Guid account, decimal amount) => new(account, 0, amount, null);
        var period = input.PeriodId is { } periodId ? await db.Set<TaxPeriod>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
            x.RegistrationId == row.RegistrationId && x.Id == periodId, ct) ?? throw Missing() : null;
        if (row.Kind is TaxOperationKind.CurrentProvision or TaxOperationKind.DeferredAsset or TaxOperationKind.DeferredLiability)
        {
            if (period is null || date < period.From || date > period.To)
                throw Conflict("The provision must use an explicit tax period containing its accounting date.");
            _ = AccountingRules.Text(input.PolicyEvidence, 1000, "Approved tax basis and rate evidence");
            if (await Live(company, row.RegistrationId, row.Kind, x => x.PeriodId == period.Id, ct) is { Count: > 0 })
                throw Conflict("A live provision of this class already exists for the period. Correct it before replacing.");
        }
        if (row.Kind == TaxOperationKind.CurrentProvision)
        {
            var current = IncomeTaxMeasurement.Current(input.Base, input.AccountingAdjustments, input.Rate, units);
            return Result([Debit(Get(TaxControlRole.CurrentExpense), current), Credit(Get(TaxControlRole.CurrentPayable), current)],
                Math.Max(0, input.Base + input.AccountingAdjustments), current);
        }
        if (row.Kind is TaxOperationKind.DeferredAsset or TaxOperationKind.DeferredLiability)
        {
            if (!input.ExpectedReversal.HasValue || input.ExpectedReversal <= date)
                throw Bad("Deferred tax needs an expected reversal date and reviewed assumptions.");
            var m = IncomeTaxMeasurement.Deferred(input.CarryingValue, input.TaxBase, input.UnderlyingLiability, input.Rate, input.RecognizableBasis, units);
            return row.Kind == TaxOperationKind.DeferredAsset
                ? Result([Debit(Get(TaxControlRole.DeferredAsset), m.Asset), Credit(Get(TaxControlRole.DeferredExpense), m.Asset)],
                    m.Deductible, m.Asset, unrecognized: m.Unrecognized)
                : Result([Debit(Get(TaxControlRole.DeferredExpense), m.Liability), Credit(Get(TaxControlRole.DeferredLiability), m.Liability)],
                    m.Taxable, m.Liability);
        }
        if (row.Kind == TaxOperationKind.Correction)
        {
            var basis = await db.Set<TaxOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.RegistrationId == row.RegistrationId && x.Id == row.BasisId && x.State == TaxEvidenceState.Posted &&
                x.Date <= date && x.Kind != TaxOperationKind.Correction, ct) ?? throw Conflict("A retained posted original is required.");
            if (await db.Set<TaxOperation>().AnyAsync(x => x.CompanyId == company && x.BasisId == basis.Id &&
                x.State == TaxEvidenceState.Posted && x.Kind == TaxOperationKind.Correction, ct)) throw Conflict("The original tax operation is already corrected.");
            if (basis.Kind == TaxOperationKind.Withhold)
            {
                var withholding = await Live(company, row.RegistrationId, basis.Kind, x => x.BillId == basis.BillId, ct);
                var payments = withholding.Select(x => x.PaymentId).OfType<Guid>().ToArray();
                var newest = await db.Payments.Where(x => x.CompanyId == company && payments.Contains(x.Id))
                    .OrderByDescending(x => x.Sequence).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
                if (newest != basis.PaymentId) throw Conflict("Correct withholding installments in reverse posting order.");
            }
            var reversed = await db.JournalLines.AsNoTracking().Where(x => x.CompanyId == company && x.JournalId == basis.JournalId)
                .OrderBy(x => x.Number).Select(x => new JournalDraftLine(x.AccountId, x.Credit, x.Debit, x.Description)).ToArrayAsync(ct);
            foreach (var line in reversed.Where(x => controls.ContainsValue(x.AccountId)))
            {
                var role = controls.Single(x => x.Value == line.AccountId).Key;
                if (role is TaxControlRole.CurrentPayable or TaxControlRole.Output or TaxControlRole.Withholding or TaxControlRole.DeferredLiability && line.Debit > 0)
                    await Exposure(company, line.AccountId, date, line.Debit, false, ct);
                if (role is TaxControlRole.Prepaid or TaxControlRole.Input or TaxControlRole.DeferredAsset && line.Credit > 0)
                    await Exposure(company, line.AccountId, date, line.Credit, true, ct);
            }
            return Result(reversed, 0, basis.Amount, reverses: basis.JournalId);
        }
        if (row.Kind == TaxOperationKind.ReverseCharge)
        {
            var bill = await Bill(company, row.BillId!.Value, date, ct);
            _ = AccountingRules.Text(input.PolicyEvidence, 1000, "Jurisdiction-of-supply and recovery evidence");
            TradeRules.Rate(input.Rate);
            TradeRules.Rate(input.RecoveryPercent);
            if (bill.TaxTotal != 0 || await db.TradeDocuments.AnyAsync(x => x.CompanyId == company && x.OriginalDocumentId == bill.Id &&
                x.State == TradeState.Posted, ct) || (await Live(company, null, row.Kind, x => x.BillId == bill.Id, ct)).Count > 0)
                throw Conflict("Reverse charge requires an untaxed uncredited bill without another live self-assessment.");
            if (input.RecoveryPercent != 100)
            {
                var accounts = bill.Lines.Select(x => x.AccountId).ToArray();
                if (await db.Accounts.AnyAsync(x => x.CompanyId == company && accounts.Contains(x.Id) && x.Classification != AccountClass.Expense, ct) ||
                    await db.StockOperations.AnyAsync(x => x.CompanyId == company && x.TradeDocumentId == bill.Id && x.State != StockState.Cancelled, ct) ||
                    await (from claim in db.AssetSourceClaims join line in db.JournalLines on claim.JournalLineId equals line.Id
                           where claim.CompanyId == company && line.CompanyId == company && line.JournalId == bill.JournalId
                           select claim.Id).AnyAsync(ct))
                    throw Conflict("Nonrecoverable inventory or asset acquisition tax requires an integrated cost policy.");
            }
            var assessed = IncomeTaxMeasurement.Measure(bill.NetTotal, input.Rate, units);
            var recoverable = IncomeTaxMeasurement.Measure(assessed, input.RecoveryPercent, units);
            var nonrecoverable = assessed - recoverable;
            var lines = new List<JournalDraftLine>();
            if (recoverable > 0) lines.Add(Debit(Get(TaxControlRole.Input), recoverable));
            if (nonrecoverable > 0) lines.Add(Debit(Get(TaxControlRole.Nonrecoverable), nonrecoverable));
            lines.Add(Credit(Get(TaxControlRole.Output), assessed));
            return Result(lines.ToArray(), bill.NetTotal, assessed, recoverable, nonrecoverable);
        }
        if (row.Kind == TaxOperationKind.Withhold)
        {
            var bill = await Bill(company, row.BillId!.Value, date, ct);
            _ = AccountingRules.Text(input.PolicyEvidence, 1000, "Supplier eligibility, rate and withholding evidence");
            if (bill.TaxTotal != 0) throw Conflict("This payment-stage policy requires a bill with no gross-up or other tax component.");
            var history = await Live(company, null, row.Kind, x => x.BillId == bill.Id, ct);
            if (history.Any(x => x.Date > date || x.RegistrationId != row.RegistrationId ||
                JsonSerializer.Deserialize<TaxOperationRequest>(x.InputsJson)!.Rate != input.Rate ||
                JsonSerializer.Deserialize<TaxOperationRequest>(x.InputsJson)!.Threshold != input.Threshold))
                throw Conflict("Withholding installments must retain the initial approved policy and chronological order.");
            var already = history.Sum(x => JsonSerializer.Deserialize<TaxOperationRequest>(x.InputsJson)!.Amount);
            var linkedPayments = history.Select(x => x.PaymentId).OfType<Guid>().ToArray();
            var ordinary = await db.Allocations.AsNoTracking().Where(x => x.CompanyId == company && x.TargetDocumentId == bill.Id &&
                x.State == SettlementState.Posted && x.ReversesAllocationId == null && x.PaymentId != null &&
                !linkedPayments.Contains(x.PaymentId.Value) &&
                !db.Allocations.Any(reversal => reversal.CompanyId == company && reversal.ReversesAllocationId == x.Id &&
                    reversal.State == SettlementState.Posted)).ToListAsync(ct);
            if (ordinary.Any(x => x.EffectiveDate > date))
                throw Conflict("Withholding cannot precede a later ordinary bill settlement.");
            if (ordinary.Count > 0 && !input.PriorOrdinarySettlementsIneligible)
                throw Conflict("Prior ordinary bill settlements require explicit ineligibility attestation or coordinated reversal.");
            if (ordinary.Count == 0 && input.PriorOrdinarySettlementsIneligible)
                throw Bad("No prior ordinary bill settlement requires ineligibility attestation.");
            var w = WithholdingMeasurement.Payment(input.Amount, already, bill.NetTotal, input.Rate, input.Threshold, units);
            var bank = await Bank(company, input.BankAccountId, ct);
            var lines = new List<JournalDraftLine> { Debit(Get(TaxControlRole.Clearing), w.Gross) };
            if (w.Withheld > 0) lines.Add(Credit(Get(TaxControlRole.Withholding), w.Withheld));
            if (w.Cash > 0) lines.Add(Credit(bank, w.Cash));
            return Result(lines.ToArray(), w.Gross, w.Withheld,
                priorOrdinaryAllocations: ordinary.Count == 0 ? null : ordinary.Select(x => x.Id).Order().ToArray());
        }

        CurrencyPolicy.Validate(input.Amount, units);
        if (row.Kind == TaxOperationKind.ApplyPrepaid)
        {
            var prepaid = Get(TaxControlRole.Prepaid);
            var payable = Get(TaxControlRole.CurrentPayable);
            await Exposure(company, prepaid, date, input.Amount, true, ct);
            await Exposure(company, payable, date, input.Amount, false, ct);
            return Result([Debit(payable, input.Amount), Credit(prepaid, input.Amount)], 0, input.Amount);
        }
        var cash = await Bank(company, input.BankAccountId, ct);
        if (row.Kind == TaxOperationKind.Installment)
            return Result([Debit(Get(TaxControlRole.Prepaid), input.Amount), Credit(cash, input.Amount)], 0, input.Amount);
        if (row.Kind == TaxOperationKind.Pay && input.Control is TaxControlRole.Output or TaxControlRole.Withholding or TaxControlRole.CurrentPayable)
        {
            var liability = Get(input.Control.Value);
            await Exposure(company, liability, date, input.Amount, false, ct);
            return Result([Debit(liability, input.Amount), Credit(cash, input.Amount)], 0, input.Amount);
        }
        if (row.Kind == TaxOperationKind.Refund && input.Control is TaxControlRole.Input or TaxControlRole.Prepaid)
        {
            var asset = Get(input.Control.Value);
            await Exposure(company, asset, date, input.Amount, true, ct);
            return Result([Debit(cash, input.Amount), Credit(asset, input.Amount)], 0, input.Amount);
        }
        throw Bad("Choose an eligible tax liability payment or asset refund.");
    }

    private async Task Exposure(Guid company, Guid account, DateOnly date, decimal amount, bool debit, CancellationToken ct)
    {
        var entries = await ledger.Lines(company, DateOnly.MaxValue, accounts: [account]).ToListAsync(ct);
        var direction = debit ? 1 : -1;
        var balance = entries.Where(x => x.Date <= date).Sum(x => direction * (x.Debit - x.Credit));
        if (balance < amount) throw Conflict("The tax operation exceeds available control exposure.");
        foreach (var day in entries.Where(x => x.Date > date).GroupBy(x => x.Date))
        {
            balance += day.Sum(x => direction * (x.Debit - x.Credit));
            if (balance < amount) throw Conflict("A backdated tax operation would overdraw later control evidence.");
        }
    }

    private async Task<List<TaxOperation>> Live(Guid company, Guid? registration, TaxOperationKind kind,
        System.Linq.Expressions.Expression<Func<TaxOperation, bool>> predicate, CancellationToken ct) =>
        await db.Set<TaxOperation>().AsNoTracking().Where(x => x.CompanyId == company &&
            (registration == null || x.RegistrationId == registration) && x.State == TaxEvidenceState.Posted && x.Kind == kind &&
            !db.Set<TaxOperation>().Any(r => r.CompanyId == company && r.BasisId == x.Id && r.Kind == TaxOperationKind.Correction &&
                r.State == TaxEvidenceState.Posted)).Where(predicate).ToListAsync(ct);

    private async Task<TradeDocument> Bill(Guid company, Guid id, DateOnly date, CancellationToken ct) =>
        await db.TradeDocuments.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id &&
            x.Kind == TradeKind.Bill && x.State == TradeState.Posted && x.PostingDate <= date, ct) ?? throw Conflict("A posted supplier bill is required.");

    private async Task<Guid> Bank(Guid company, Guid? id, CancellationToken ct) =>
        await db.TreasuryAccounts.Where(x => x.CompanyId == company && x.Id == id && x.Kind == TreasuryAccountKind.Bank && x.Active)
            .Select(x => (Guid?)x.LedgerAccountId).SingleOrDefaultAsync(ct) ?? throw Conflict("An active registered bank account is required.");

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    private static string CodeReference(string value) => AccountingRules.Text(value, 100, "Tax reference").ToUpperInvariant();
}
