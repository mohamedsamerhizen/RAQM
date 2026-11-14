using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Entities.InternalControls;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Services.AuditEvidence;
using RAQM.Api.Services.FinancialReporting;
using RAQM.Api.Services.Governance;
using RAQM.Api.Services.Inventory;
using static RAQM.Api.Helpers.Governance.GovernanceRules;
using static RAQM.Api.Services.Governance.GovernanceAdministration;

namespace RAQM.Api.Services.InternalControls;

public sealed record ControlResult(ControlOutcome Outcome, string Summary, int Population, Guid[] SourceIds, string? Fingerprint = null,
    Guid[]? PopulationSample = null, object? Measurements = null);

public sealed class ControlService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions, GovernanceAdministration admin,
    GovernanceGate gate, GovernanceAudit audit, ITradingReportService trading, InventoryReporting inventory, FinancialLedger ledger, TimeProvider clock)
{
    private const int PopulationLimit = 5000;

    public async Task<Company> Auditor(Guid actor, Guid org, Guid company, bool write, CancellationToken ct)
    {
        var scope = await access.Resolve(actor, org, company, write, false, ct);
        try { return await access.Resolve(actor, org, company, write, true, ct); }
        catch (ApiException e) when (e.Status == 403)
        {
            if ((await gate.Grants(company, actor, FinancialDuty.AuditWorkspace, gate.Now, ct)).Any(x => x.Permission == GovernancePermission.Audit))
                return scope;
            throw Missing();
        }
    }

    public Task<InternalControl> Define(Guid actor, Guid org, Guid company, InternalControlInput input, CancellationToken ct) =>
        admin.Admin(actor, org, company, async (scope, token) =>
        {
            if (!Enum.IsDefined(input.Check) || input.EffectiveOn.Year < 1900 || !await gate.CanWrite(scope, input.OwnerId, token))
                throw Bad("Use an eligible control owner, evaluator and effective date.");
            if (input.RiskId is { } risk && !await db.Set<GovernanceWork>().AnyAsync(x => x.CompanyId == company &&
                x.Id == risk && x.Kind == GovernanceWorkKind.Risk, token)) throw Missing();
            var code = AccountingRules.Text(input.Code, 40, "Control code").ToUpperInvariant();
            var version = (await db.Set<InternalControl>().Where(x => x.CompanyId == company && x.Code == code).MaxAsync(x => (int?)x.Version, token) ?? 0) + 1;
            var row = Scoped(scope, new InternalControl { Code = code, Version = version,
                Objective = AccountingRules.Text(input.Objective, 500, "Control objective"), Process = AccountingRules.Text(input.Process, 100, "Business process"),
                Frequency = AccountingRules.Text(input.Frequency, 100, "Control frequency"), EvidenceRequired = AccountingRules.Text(input.EvidenceRequired, 500, "Evidence required"),
                Preventive = input.Preventive, CloseBlocking = input.CloseBlocking, Check = input.Check, RiskId = input.RiskId,
                OwnerId = input.OwnerId, CreatedBy = actor, EffectiveOn = input.EffectiveOn });
            db.Add(row);
            await audit.Append(scope, actor, new("controls", "ControlPrepared", row.Id, "Control definition prepared", Related: row.RiskId), token);
            return row;
        }, ct);

    public Task<InternalControl> Approve(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct) =>
        admin.Admin(actor, org, company, async (scope, token) =>
        {
            var row = await db.Set<InternalControl>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing();
            if (row.ApprovedBy == actor) return row;
            if (row.ApprovedBy.HasValue || row.CreatedBy == actor || await db.Set<InternalControl>().AnyAsync(x => x.CompanyId == company &&
                x.Code == row.Code && x.ApprovedBy != null && (x.EffectiveOn >= row.EffectiveOn || x.Version >= row.Version), token))
                throw Conflict("Use a prospective control version and independent approval.");
            row.ApprovedBy = actor;
            await audit.Append(scope, actor, new("controls", "ControlApproved", row.Id, "Control definition independently approved"), token);
            return row;
        }, ct);

    public Task<ControlEvaluation> Evaluate(Guid actor, Guid org, Guid company, Guid id, EvaluateControlInput input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await Auditor(actor, org, company, true, token);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            if (input.From.Year < 1900 || input.Cutoff < input.From || input.Cutoff > today || input.Cutoff.DayNumber - input.From.DayNumber > 366)
                throw Bad("Use a historical testing window up to 367 days.");
            var definition = await db.Set<InternalControl>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id &&
                x.ApprovedBy != null && x.EffectiveOn <= input.Cutoff, token) ?? throw Missing();
            if (await db.Set<InternalControl>().AnyAsync(x => x.CompanyId == company && x.Code == definition.Code &&
                x.ApprovedBy != null && x.EffectiveOn <= input.Cutoff && x.Version > definition.Version, token))
                throw Conflict("Select the current control version for the cutoff.");
            if (input.EngagementId is { } engagement && !await db.Set<GovernanceWork>().AnyAsync(x => x.CompanyId == company &&
                x.Id == engagement && x.Kind == GovernanceWorkKind.Engagement && x.State == GovernanceWorkState.InProgress, token))
                throw Conflict("Attach a test to an in-progress engagement.");
            var method = AccountingRules.Text(input.Method, 400, "Test procedure and selection method");
            var result = await Execute(scope, actor, definition.Check, input, token);
            var row = Scoped(scope, new ControlEvaluation { ControlId = id, EngagementId = input.EngagementId, TesterId = actor,
                From = input.From, Cutoff = input.Cutoff, At = AccountingRules.StoredUtcInstant(gate.Now), Method = method,
                ResultJson = JsonSerializer.Serialize(result), Outcome = result.Outcome });
            db.Add(row);
            await audit.Append(scope, actor, new("controls", "ControlEvaluated", row.Id, result.Summary, Related: id, Next: result.Outcome.ToString()), token);
            if (result.Outcome == ControlOutcome.Fail)
            {
                var existing = await db.Set<GovernanceWork>().AnyAsync(x => x.CompanyId == company && x.Kind == GovernanceWorkKind.Finding &&
                    x.ControlId == id && x.State != GovernanceWorkState.Closed, token);
                if (!existing)
                {
                    var finding = Scoped(scope, new GovernanceWork { Kind = GovernanceWorkKind.Finding, State = GovernanceWorkState.InProgress,
                        ControlId = id, RiskId = definition.RiskId, EvaluationId = row.Id, CreatedBy = actor, OwnerId = definition.OwnerId,
                        ParentId = input.EngagementId, Severity = definition.CloseBlocking ? 20 : 9, DueOn = today.AddDays(30),
                        Title = $"{definition.Code} control failure",
                        DetailsJson = JsonSerializer.Serialize(new { Category = definition.Check.ToString(), result.Summary, result.SourceIds }) });
                    db.Add(finding);
                    await audit.Append(scope, actor, new("audit", "FindingOpened", finding.Id, "Automated control failure requires investigation",
                        Related: row.Id), token);
                }
            }
            return row;
        }, ct);

    public Task<ControlEvaluation> Review(Guid actor, Guid org, Guid company, Guid id, ReviewControlInput input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await Auditor(actor, org, company, true, token);
            var row = await db.Set<ControlEvaluation>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing();
            if (row.ReviewerId.HasValue || row.TesterId == actor || !Enum.IsDefined(input.Conclusion))
                throw Conflict("A different auditor must review the test.");
            if (input.Conclusion == ControlOutcome.Pass && row.Outcome != ControlOutcome.Pass)
            {
                var manual = await db.Set<InternalControl>().AnyAsync(x => x.CompanyId == company && x.Id == row.ControlId && x.Check == ControlCheck.Manual, token);
                if (!manual || !await db.Set<EvidenceReference>().AnyAsync(x => x.CompanyId == company && x.EvaluationId == id, token))
                    throw Conflict("Incomplete automated evidence cannot be certified as passing. Manual review requires linked evidence.");
            }
            row.ReviewerId = actor;
            row.Conclusion = input.Conclusion;
            row.ReviewEvidence = AccountingRules.Text(input.Evidence, 1000, "Independent review evidence");
            await audit.Append(scope, actor, new("controls", "ControlReviewed", row.Id, "Independent test conclusion recorded",
                Related: row.ControlId, Prior: row.Outcome.ToString(), Next: row.Conclusion.ToString()), token);
            return row;
        }, ct);

    private async Task<ControlResult> Execute(Company scope, Guid actor, ControlCheck check, EvaluateControlInput input, CancellationToken ct)
    {
        var company = scope.Id;
        var start = input.From;
        var cutoff = input.Cutoff;
        switch (check)
        {
            case ControlCheck.JournalBalance:
                var journals = await db.Journals.AsNoTracking().Where(x => x.CompanyId == company && x.State == JournalState.Posted &&
                    x.PostingDate >= start && x.PostingDate <= cutoff && x.SourceModule != FinancialMath.YearEndSource).OrderBy(x => x.Id).Select(x =>
                    new { x.Id, Count = x.Lines.Count, Balance = x.Lines.Sum(y => y.Debit - y.Credit) }).Take(PopulationLimit + 1).ToListAsync(ct);
                return Population(journals.Count, journals.Where(x => x.Count < 2 || x.Balance != 0).Select(x => x.Id),
                    "Posted journals tested against original debit and credit lines.", TradeRules.Hash(journals)) with
                    { PopulationSample = journals.Take(100).Select(x => x.Id).ToArray() };
            case ControlCheck.TradingReconciliation:
                if (!await db.TradingParties.AnyAsync(x => x.CompanyId == company, ct)) return Incomplete("No trading population is configured.");
                if (await db.TradeDocuments.Where(x => x.CompanyId == company).Take(PopulationLimit + 1).CountAsync(ct) > PopulationLimit ||
                    await db.Payments.Where(x => x.CompanyId == company).Take(PopulationLimit + 1).CountAsync(ct) > PopulationLimit ||
                    await db.Allocations.Where(x => x.CompanyId == company).Take(PopulationLimit + 1).CountAsync(ct) > PopulationLimit)
                    return Incomplete("Trading population exceeds the supported bound.");
                try
                {
                    var report = await trading.Reconcile(actor, scope.OrganizationId, company, cutoff, ct);
                    return new(report.Agrees ? ControlOutcome.Pass : ControlOutcome.Fail, "AR/AP and payment conservation tested against the GL.",
                        report.Controls.Count, [], TradeRules.Hash(report), Measurements: report);
                }
                catch (ApiException e) when (e.Status is 400 or 409) { return Incomplete("Required trading controls are unavailable."); }
            case ControlCheck.BankCoverage:
                var banks = await db.TreasuryAccounts.AsNoTracking().Where(x => x.CompanyId == company && x.Kind == TreasuryAccountKind.Bank && x.Active)
                    .OrderBy(x => x.Id).Take(PopulationLimit + 1).ToListAsync(ct);
                if (banks.Count is 0 or > PopulationLimit) return Incomplete("No bounded active bank population is available.");
                var spans = await (from statement in db.BankStatements join session in db.ReconciliationSessions on statement.Id equals session.StatementId
                    where statement.CompanyId == company && session.CompanyId == company && session.State == ReconciliationState.Finalized &&
                        statement.End >= start && statement.Start <= cutoff
                    orderby statement.Start, statement.Id select new { statement.BankAccountId, statement.Start, statement.End })
                    .Take(PopulationLimit + 1).ToListAsync(ct);
                if (spans.Count > PopulationLimit) return Incomplete("Finalized statement population exceeds the supported bound.");
                var gaps = new List<Guid>();
                foreach (var bank in banks)
                {
                    var through = start.AddDays(-1);
                    foreach (var span in spans.Where(x => x.BankAccountId == bank.Id))
                        if (span.Start.DayNumber <= through.DayNumber + 1 && span.End > through) through = span.End;
                    if (through < cutoff) gaps.Add(bank.Id);
                }
                return Population(banks.Count, gaps, "Continuous finalized bank statement coverage tested.", TradeRules.Hash(new
                    { Banks = banks.Select(x => x.Id), Spans = spans })) with { PopulationSample = banks.Take(100).Select(x => x.Id).ToArray() };
            case ControlCheck.InventoryReadiness:
                var period = await db.Periods.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.EndDate == cutoff && x.StartDate == start, ct);
                if (period is null || !await db.InventoryPolicies.AnyAsync(x => x.CompanyId == company, ct))
                    return Incomplete("An aligned inventory fiscal period and policy are required.");
                var stockCount = await db.StockMovements.Where(x => x.CompanyId == company && x.Date <= cutoff).Take(PopulationLimit + 1).CountAsync(ct);
                if (stockCount == 0) return Incomplete("There is no posted inventory movement population.");
                if (stockCount > PopulationLimit ||
                    await ledger.Lines(company, cutoff).Take(PopulationLimit + 1).CountAsync(ct) > PopulationLimit)
                    return Incomplete("Inventory or ledger population exceeds the supported bound.");
                var inventoryEvidence = await inventory.Check(company, period, ct);
                var issues = inventoryEvidence.Findings.Where(x => x.Severity != FindingSeverity.Information).ToArray();
                return new(issues.Length == 0 ? ControlOutcome.Pass : ControlOutcome.Fail, "Inventory financial close controls evaluated.",
                    stockCount, issues.Select(x => x.SourceId).OfType<Guid>().Distinct().ToArray(), inventoryEvidence.Fingerprint);
            case ControlCheck.ApprovalEvidence:
                var lower = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                var upper = new DateTimeOffset(cutoff.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                var actions = await db.Set<GovernanceEvent>().AsNoTracking().Where(x => x.CompanyId == company &&
                    x.RecordedAt >= lower && x.RecordedAt < upper && (x.Type == "GovernanceDecision" || x.Type == "GovernanceDenied"))
                    .OrderBy(x => x.Sequence).Take(PopulationLimit + 1).ToListAsync(ct);
                return Population(actions.Count, actions.Where(x => x.Outcome == GovernanceOutcome.MonitorFinding).Select(x => x.Id),
                    "Retained governance decisions and denied attempts tested.", TradeRules.Hash(actions.Select(x => x.Hash)));
            case ControlCheck.ExpiredAccess:
                if (cutoff != DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
                    return Incomplete("Emergency review status is evaluated at the current cutoff only.");
                var end = new DateTimeOffset(cutoff.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                var now = gate.Now < end ? gate.Now : end;
                var due = await db.Set<GovernanceGrant>().AsNoTracking().Where(x => x.CompanyId == company && x.Emergency &&
                    x.ApprovedAt != null && (x.ExpiresAt <= now || x.RevokedAt <= now)).OrderBy(x => x.Id).Take(PopulationLimit + 1).ToListAsync(ct);
                var ids = due.Where(x => x.ReviewedBy == null).Select(x => x.Id).ToArray();
                var unreviewed = await db.Set<GovernanceEvent>().Where(x => x.CompanyId == company && x.Type == "EmergencyGrantUsed" &&
                    x.RelatedId != null && ids.Contains(x.RelatedId.Value)).Select(x => x.RelatedId!.Value).Distinct().ToArrayAsync(ct);
                return Population(due.Count, unreviewed, "Ended emergency grant usage checked for independent review.",
                    TradeRules.Hash(due.Select(x => new { x.Id, x.ReviewedBy, x.RevokedAt, x.ExpiresAt })));
            default:
                return Incomplete(AccountingRules.Text(input.ManualObservation, 1000, "Manual observation"));
        }
    }

    private static ControlResult Population(int count, IEnumerable<Guid> ids, string summary, string fingerprint)
    {
        if (count == 0 || count > PopulationLimit) return Incomplete("The available source population is empty or exceeds the supported bound.");
        var findings = ids.Distinct().Take(100).ToArray();
        return new(findings.Length == 0 ? ControlOutcome.Pass : ControlOutcome.Fail, summary, count, findings, fingerprint);
    }

    private static ControlResult Incomplete(string summary) => new(ControlOutcome.Indeterminate, summary, 0, []);

    internal async Task<(CloseFinding[] Findings, string Fingerprint)> Close(Company scope, Guid actor, AccountingPeriod period, CancellationToken ct)
    {
        var company = scope.Id;
        var definitions = (await db.Set<InternalControl>().AsNoTracking().Where(x => x.CompanyId == company &&
            x.ApprovedBy != null && x.EffectiveOn <= period.EndDate).ToListAsync(ct))
            .GroupBy(x => x.Code).Select(x => x.MaxBy(y => y.Version)!).Where(x => x.CloseBlocking).OrderBy(x => x.Id).ToArray();
        if (definitions.Length == 0) return ([], "");
        var ids = definitions.Select(x => x.Id).ToArray();
        var tests = (await db.Set<ControlEvaluation>().AsNoTracking().Where(x => x.CompanyId == company && ids.Contains(x.ControlId) &&
            x.From == period.StartDate && x.Cutoff == period.EndDate).ToListAsync(ct)).GroupBy(x => x.ControlId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.At).ThenByDescending(y => y.Id).First());
        var issues = await db.Set<GovernanceWork>().AsNoTracking().Where(x => x.CompanyId == company && x.Kind == GovernanceWorkKind.Finding &&
            x.ControlId.HasValue && ids.Contains(x.ControlId.Value) && x.State != GovernanceWorkState.Closed && x.Severity >= 16)
            .OrderBy(x => x.Id).ToListAsync(ct);
        var findings = issues.Select(x => new CloseFinding("governance-finding", FindingSeverity.Blocking, "An enabled control has an open material finding.", x.Id)).ToList();
        foreach (var control in definitions)
        {
            if (!tests.TryGetValue(control.Id, out var test) || test.Conclusion != ControlOutcome.Pass)
            {
                findings.Add(new("governance-control", FindingSeverity.Blocking, $"Control {control.Code} requires a reviewed passing result for the exact period.", control.Id));
                continue;
            }
            if (control.Check == ControlCheck.Manual) continue;
            var retained = JsonSerializer.Deserialize<ControlResult>(test.ResultJson)!;
            var current = await Execute(scope, actor, control.Check, new(period.StartDate, period.EndDate, null, "Close verification", null), ct);
            if (current.Outcome != ControlOutcome.Pass || retained.Fingerprint != current.Fingerprint)
                findings.Add(new("governance-control-stale", FindingSeverity.Blocking, $"Control {control.Code} source evidence changed after review.", control.Id));
        }
        return (findings.ToArray(), TradeRules.Hash(new { Controls = ids, Tests = tests.Values.OrderBy(x => x.Id).Select(x => new { x.Id, x.Conclusion }),
            Issues = issues.Select(x => new { x.Id, x.Version }) }));
    }
}
