using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.Entities.FinancialReporting;

public abstract class FinancialEvidence : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
}

public enum StatementGroup
{
    CurrentAsset, NoncurrentAsset, CurrentLiability, NoncurrentLiability,
    Capital, RetainedEarnings, Distributions, OtherEquity,
    OperatingRevenue, CostOfSales, OperatingExpense, OtherIncome, OtherExpense
}

public enum CashGroup { Operating, Investing, Financing }

public sealed class ReportDefinition : FinancialEvidence
{
    public int Revision { get; set; }
    public int MappingCount { get; set; }
    public Guid? RetainedEarningsId { get; set; }
    public string CashRulesJson { get; set; } = "[]";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Reason { get; set; } = "";
    public List<ReportAccountMapping> Mappings { get; set; } = [];
}

public sealed class ReportAccountMapping : FinancialEvidence
{
    public Guid DefinitionId { get; set; }
    public Guid AccountId { get; set; }
    public StatementGroup Group { get; set; }
}
