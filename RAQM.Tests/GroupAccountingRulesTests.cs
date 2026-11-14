using RAQM.Api.DTOs.Consolidation;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Consolidation;
using RAQM.Api.Entities.CorporateGroups;
using RAQM.Api.Entities.Intercompany;
using RAQM.Api.Helpers;
using RAQM.Api.Services.Consolidation;
using RAQM.Api.Services.CorporateGroups;
using RAQM.Api.Services.CurrencyTranslation;
using RAQM.Api.Services.GroupReporting;
using RAQM.Api.Services.Intercompany;
using Xunit;

namespace RAQM.Tests;

public sealed class GroupAccountingRulesTests
{
    [Fact]
    public void ControlAndEconomicOwnershipAreIndependentAndHistoricallySelected()
    {
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        var grandchild = Guid.NewGuid();
        var january = new DateOnly(2025, 1, 1);
        var march = january.AddMonths(2);
        GroupOwnership[] rows =
        [
            new() { InvestorId = parent, InvesteeId = child, Ownership = 40, Voting = 30, Kind = InvestmentKind.Controlled, EffectiveOn = january },
            new() { InvestorId = child, InvesteeId = grandchild, Ownership = 75, Kind = InvestmentKind.Controlled, EffectiveOn = january },
            new() { InvestorId = parent, InvesteeId = child, Ownership = 50, Kind = InvestmentKind.Controlled, EffectiveOn = march }
        ];
        Assert.Equal(30m, CorporateGroupService.ResolveOwnership(parent, rows, january).Single(x => x.Edge.InvesteeId == grandchild).EconomicPercent);
        Assert.Equal(37.5m, CorporateGroupService.ResolveOwnership(parent, rows, march).Single(x => x.Edge.InvesteeId == grandchild).EconomicPercent);
        rows[2].InvestorId = grandchild;
        Assert.Throws<ApiException>(() => CorporateGroupService.ResolveOwnership(parent, rows, march));
    }

    [Fact]
    public void ServicePrincipalAndSettlementHaveIndependentExactJournalEffects()
    {
        var p = new IntercompanyPolicy { DueFromId = Guid.NewGuid(), DueToId = Guid.NewGuid(), RevenueId = Guid.NewGuid(),
            ExpenseId = Guid.NewGuid(), CreditorBankId = Guid.NewGuid(), DebtorBankId = Guid.NewGuid() };
        var service = IntercompanyService.BuildPlan(Guid.NewGuid(), p, PairKind.Service, 1000);
        Assert.Collection(service.Creditor, x => Assert.Equal((p.DueFromId, 1000m, 0m), (x.AccountId, x.Debit, x.Credit)),
            x => Assert.Equal((p.RevenueId, 0m, 1000m), (x.AccountId, x.Debit, x.Credit)));
        Assert.Collection(service.Debtor, x => Assert.Equal((p.ExpenseId, 1000m, 0m), (x.AccountId, x.Debit, x.Credit)),
            x => Assert.Equal((p.DueToId, 0m, 1000m), (x.AccountId, x.Debit, x.Credit)));
        var principal = IntercompanyService.BuildPlan(Guid.NewGuid(), p, PairKind.Principal, 50_000);
        Assert.Equal(50_000m, principal.Creditor.Single(x => x.AccountId == p.CreditorBankId).Credit);
        Assert.Equal(50_000m, principal.Debtor.Single(x => x.AccountId == p.DebtorBankId).Debit);
        var settlement = IntercompanyService.BuildPlan(Guid.NewGuid(), p, PairKind.Settlement, 300);
        Assert.Equal(300m, settlement.Creditor.Single(x => x.AccountId == p.DueFromId).Credit);
        Assert.Equal(300m, settlement.Debtor.Single(x => x.AccountId == p.DueToId).Debit);
        Assert.DoesNotContain(settlement.Creditor.Concat(settlement.Debtor), x => x.AccountId == p.RevenueId || x.AccountId == p.ExpenseId);
    }

    [Fact]
    public void LoanAccrualUsesActualDaysAndCurrencyRounding()
    {
        var january = new DateOnly(2025, 1, 1);
        Assert.Equal(2500m, IntercompanyService.SimpleInterest(50_000m, 5m, january, january.AddDays(364), 2));
        Assert.Equal(68.49m, IntercompanyService.SimpleInterest(50_000m, 5m, january, january.AddDays(9), 2));
        Assert.Throws<ApiException>(() => IntercompanyService.SimpleInterest(0, 5, january, january, 2));
        Assert.Throws<ApiException>(() => IntercompanyService.SimpleInterest(100, 5, january, january.AddDays(366), 2));
    }

    [Fact]
    public void TranslationUsesClosingDailyAndHistoricalEquityRatesWithoutFallback()
    {
        var company = Guid.NewGuid();
        var equity = Guid.NewGuid();
        var posted = new DateOnly(2025, 1, 2);
        var cutoff = new DateOnly(2025, 1, 31);
        GroupTranslationRate Rate(DateOnly day, DateOnly effective, decimal rate, Guid? account = null) =>
            new() { MemberId = company, RateDate = day, EffectiveOn = effective, Rate = rate, EquityAccountId = account, ApprovedBy = Guid.NewGuid() };
        GroupTranslationRate[] rates =
        [
            Rate(cutoff, cutoff, 1.30m), Rate(posted, posted, 1.15m), Rate(posted, posted, 1.10m, equity),
            Rate(posted, cutoff.AddDays(1), 1.99m)
        ];
        var asset = GroupRateService.Select(rates, company, Guid.NewGuid(), AccountClass.Asset, posted, cutoff);
        var sales = GroupRateService.Select(rates, company, Guid.NewGuid(), AccountClass.Revenue, posted, cutoff);
        var capital = GroupRateService.Select(rates, company, equity, AccountClass.Equity, posted, cutoff);
        var translated = ConsolidationService.Translate(1000, asset.Rate, 2) + ConsolidationService.Translate(-200, sales.Rate, 2) +
            ConsolidationService.Translate(-800, capital.Rate, 2);
        Assert.Equal(190m, translated);
        Assert.Throws<ApiException>(() => GroupRateService.Select(rates, company, Guid.NewGuid(), AccountClass.Equity, posted, cutoff));
    }

    [Fact]
    public void EliminationRollupPreservesExternalRevenueAndKeepsIndependentLayers()
    {
        GroupAccount root = new() { Code = "R", Classification = AccountClass.Revenue };
        GroupAccount child = new() { Code = "R1", Classification = AccountClass.Revenue, ParentId = root.Id };
        GroupAccount expense = new() { Code = "E", Classification = AccountClass.Expense };
        GroupAccount cash = new() { Code = "C", Classification = AccountClass.Asset, Cash = true };
        GroupContribution Line(Guid account, decimal amount) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new DateOnly(2025, 1, 1), Guid.NewGuid(), account, amount, 1m, null, amount, false);
        var snapshot = new ConsolidationSnapshot("USD", "Common policy", [], [], [root, child, expense, cash],
            [Line(child.Id, -1200), Line(expense.Id, 1000), Line(cash.Id, 200)],
            [new("Elimination", "RECIP-1", child.Id, 1000, "Invoice"), new("Elimination", "RECIP-1", expense.Id, -1000, "Bill")], []);
        var report = GroupReportingService.Balances(snapshot);
        Assert.Equal(-200m, Assert.Single(report, x => x.Id == root.Id && x.Rollup).Final);
        Assert.Equal(0m, Assert.Single(report, x => x.Id == expense.Id).Final);
        Assert.Equal(0m, report.Where(x => !x.Rollup).Sum(x => x.Final));
        Assert.Equal(-1200m, Assert.Single(report, x => x.Id == child.Id).Translated);
    }
}
