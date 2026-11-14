using System.Data;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.FinancialDimensions;
using RAQM.Api.Services.FinancialReporting;
using static RAQM.Api.Helpers.FinancialDimensions.DimensionMath;
using static RAQM.Api.Services.FinancialDimensions.DimensionRegister;

namespace RAQM.Api.Services.ProjectAccounting;

public sealed record ProjectPosition(Guid ProjectId, Guid ValueId, DateOnly At, decimal Revenue, decimal Cost,
    decimal DirectCostOfSales, decimal OpenPurchaseCommitments)
{
    public decimal GrossMargin => Revenue - DirectCostOfSales;
    public decimal Contribution => Revenue - Cost;
}

public sealed class ProjectService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    DimensionRegister register, FinancialLedger ledger, TimeProvider clock)
{
    public Task<DimensionProject> Create(Guid actor, Guid org, Guid company, ProjectRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            if (input.Start.Year < 1900 || input.End < input.Start || input.Value.EffectiveDate > input.Start ||
                (input.CustomerId.HasValue && !await db.TradingParties.AnyAsync(x => x.CompanyId == company &&
                    x.Id == input.CustomerId && x.Kind == PartyKind.Customer && x.IsActive, token)))
                throw Bad("Provide ordered project dates and an eligible customer for customer-facing projects.");
            var value = await register.AddValue(scope, actor, input.Value, true, token);
            var row = Scoped(scope, new DimensionProject { ValueId = value.Id, OwnerId = actor, CustomerId = input.CustomerId,
                Start = input.Start, End = input.End, Description = AccountingRules.Text(input.Description, 1000, "Description"),
                Evidence = AccountingRules.Text(input.Evidence, 1000, "Project evidence") });
            db.FinancialProjects.Add(row);
            db.FinancialProjectEvents.Add(Scoped(scope, new DimensionProjectEvent { ProjectId = row.Id, State = ProjectState.Draft,
                Number = 1, Date = value.EffectiveDate, ActorId = actor, Reason = "Project prepared" }));
            access.Audit(scope, actor, row.Id, "DimensionProjectCreated");
            return row;
        }, ct);

    public Task<DimensionProjectEvent> Change(Guid actor, Guid org, Guid company, Guid id, ProjectAction input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var row = await Find(company, id, token);
            var prior = await db.FinancialProjectEvents.AsNoTracking().Where(x => x.CompanyId == company && x.ProjectId == id)
                .OrderByDescending(x => x.Number).FirstAsync(token);
            if (prior.Number != input.Version || input.Date < prior.Date || input.Date > Today(clock) ||
                (input.State == ProjectState.Active && input.Date < row.Start) ||
                (input.State == ProjectState.Approved && row.OwnerId == actor) ||
                !Transition(prior.State, input.State)) throw Conflict("Invalid project transition, effective date, actor or version.");
            if (input.State == ProjectState.Closed && await OpenPurchases(company, row.ValueId, token) > 0)
                throw Conflict("Resolve outstanding project purchase commitments before closure.");
            var item = Scoped(scope, new DimensionProjectEvent { ProjectId = id, ActorId = actor, Date = input.Date, State = input.State,
                Number = prior.Number + 1, Reason = AccountingRules.Text(input.Reason, 500, "Project decision") });
            db.FinancialProjectEvents.Add(item);
            access.Audit(scope, actor, id, $"DimensionProject:{input.State}", item.Reason);
            return item;
        }, ct);

    public static bool Transition(ProjectState from, ProjectState to) => (from, to) switch
    {
        (ProjectState.Draft, ProjectState.Approved) or (ProjectState.Approved, ProjectState.Active) or
        (ProjectState.Active, ProjectState.Suspended or ProjectState.Completed) or
        (ProjectState.Suspended, ProjectState.Active or ProjectState.Completed) or
        (ProjectState.Completed, ProjectState.Active or ProjectState.Closed) => true,
        _ => false
    };

    public async Task<object> Report(Guid actor, Guid org, Guid company, Guid id, DateOnly at, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var project = await Find(company, id, ct);
        var result = new
        {
            Currency = scope.BaseCurrency, Project = project, Financials = await Position(company, project, at, ct),
            History = await db.FinancialProjectEvents.AsNoTracking().Where(x => x.CompanyId == company && x.ProjectId == id && x.Date <= at)
                .OrderBy(x => x.Number).ToListAsync(ct),
            Forecasts = (await db.ProjectFinancialForecasts.AsNoTracking().Where(x => x.CompanyId == company && x.ProjectId == id && x.At <= at)
                .OrderByDescending(x => x.At).ThenBy(x => x.Id).ToListAsync(ct)).Select(x => new { Evidence = x, EstimatedTotalCost = x.PostedCost + x.CostToComplete,
                    EstimatedTotalRevenue = x.PostedRevenue + x.RevenueToComplete,
                    EstimatedMargin = x.PostedRevenue + x.RevenueToComplete - x.PostedCost - x.CostToComplete }).ToArray(),
            CommitmentsAreCurrent = true
        };
        await tx.CommitAsync(ct);
        return result;
    }

    public Task<DimensionProjectForecast> Forecast(Guid actor, Guid org, Guid company, Guid id, ProjectForecastRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var project = await Find(company, id, token);
            var units = CurrencyPolicy.Units(scope.BaseCurrency);
            CurrencyPolicy.Validate(input.CostToComplete, units);
            CurrencyPolicy.Validate(input.RevenueToComplete, units);
            var position = await Position(company, project, input.At, token);
            var row = Scoped(scope, new DimensionProjectForecast { ProjectId = id, ApprovedBy = actor, At = input.At,
                PostedCost = position.Cost, PostedRevenue = position.Revenue, CostToComplete = input.CostToComplete,
                RevenueToComplete = input.RevenueToComplete, Evidence = AccountingRules.Text(input.Evidence, 1000, "Forecast assumptions") });
            db.ProjectFinancialForecasts.Add(row);
            access.Audit(scope, actor, id, "DimensionProjectForecastRetained", row.Id.ToString());
            return row;
        }, ct);

    public async Task<PageResult<DimensionProject>> List(Guid actor, Guid org, Guid company, Guid? customer, string? search, int page, int size,
        CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        AccountingRules.Page(page, size);
        AccountingRules.OptionalText(search, 100);
        var query = from project in db.FinancialProjects.AsNoTracking() join value in db.DimensionValues on project.ValueId equals value.Id
            where project.CompanyId == company && value.CompanyId == company && (!customer.HasValue || project.CustomerId == customer) &&
                (string.IsNullOrWhiteSpace(search) || EF.Functions.ILike(value.Name, "%" + search + "%") || EF.Functions.ILike(value.Code, "%" + search + "%"))
            orderby value.Code, project.Id select project;
        return new(await query.Skip((page - 1) * size).Take(size).ToListAsync(ct), await query.CountAsync(ct), page, size);
    }

    private async Task<ProjectPosition> Position(Guid company, DimensionProject project, DateOnly at, CancellationToken ct)
    {
        if (at < project.Start || at > Today(clock)) throw Bad("Use an accounting cutoff from project start through today.");
        var accounts = (await ledger.Accounts(company, ct)).ToDictionary(x => x.Id);
        var mapping = await db.InventoryAccounts.Where(x => x.CompanyId == company && x.Role == InventoryRole.CostOfSales)
            .Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(ct);
        var rows = (await ledger.Lines(company, at, performance: true).Where(x => db.PostedDimensions.Any(tag =>
            tag.CompanyId == company && tag.JournalLineId == x.LineId && tag.ValueId == project.ValueId)).ToListAsync(ct))
            .Where(FinancialMath.OperatingHistory).ToArray();
        return new(project.Id, project.ValueId, at, -rows.Where(x => accounts[x.AccountId].Classification == AccountClass.Revenue).Sum(FinancialMath.Signed),
            rows.Where(x => accounts[x.AccountId].Classification == AccountClass.Expense).Sum(FinancialMath.Signed),
            rows.Where(x => x.AccountId == mapping).Sum(FinancialMath.Signed), await OpenPurchases(company, project.ValueId, ct));
    }

    internal async Task<decimal> OpenPurchases(Guid company, Guid value, CancellationToken ct)
    {
        var bindings = await db.DimensionBindings.AsNoTracking().Where(x => x.CompanyId == company && x.Source == DimensionSource.Procurement).ToListAsync(ct);
        var lines = bindings.GroupBy(x => x.SourceId).Where(x => Read(x.MaxBy(y => y.Revision)).SelectMany(y => y.Values).Any(y => y.ValueId == value))
            .Select(x => x.Key).ToArray();
        var orders = await (from line in db.ProcurementLines join order in db.ProcurementDocuments on line.DocumentId equals order.Id
            where line.CompanyId == company && order.CompanyId == company && lines.Contains(line.Id) &&
                (order.State == ProcurementState.Approved || order.State == ProcurementState.Confirmed) &&
                (order.IsOrder || !db.ProcurementDocuments.Any(x => x.CompanyId == company && x.RequisitionId == order.Id &&
                    (x.State == ProcurementState.Approved || x.State == ProcurementState.Confirmed)))
            select new { line.Id, line.NetAmount }).ToListAsync(ct);
        var activity = await db.StockOperations.AsNoTracking().Where(x => x.CompanyId == company && x.State == StockState.Posted &&
            x.OrderLineId.HasValue && lines.Contains(x.OrderLineId.Value)).ToListAsync(ct);
        return orders.Sum(order => Math.Max(0, order.NetAmount - activity.Where(x => x.OrderLineId == order.Id && x.Kind == StockKind.Receipt)
            .Sum(x => x.Accrual) + activity.Where(x => x.OrderLineId == order.Id && x.TradeDocumentId == null &&
                x.Kind is StockKind.ReceiptCorrection or StockKind.SupplierReturn).Sum(x => x.Accrual)));
    }

    private async Task<DimensionProject> Find(Guid company, Guid id, CancellationToken ct) =>
        await db.FinancialProjects.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw Missing("Financial project was not found.");
}
