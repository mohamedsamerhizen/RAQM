using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.CashManagement;
using RAQM.Api.Entities.Accruals;
using RAQM.Api.Entities.FinancialClose;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.YearEnd;
using RAQM.Api.Entities.FixedAssets;
namespace RAQM.Api.Data;

public sealed class RaqmDbContext(DbContextOptions<RaqmDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyMember> CompanyMembers => Set<CompanyMember>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<LedgerAccount> Accounts => Set<LedgerAccount>();
    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();
    public DbSet<AccountingPeriod> Periods => Set<AccountingPeriod>();
    public DbSet<JournalEntry> Journals => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<OpeningBalanceBatch> OpeningBatches => Set<OpeningBalanceBatch>();
    public DbSet<AccountingAuditEvent> AccountingAudit => Set<AccountingAuditEvent>();
    public DbSet<TradingParty> TradingParties => Set<TradingParty>();
    public DbSet<TradingAccountMapping> TradingAccounts => Set<TradingAccountMapping>();
    public DbSet<TradingTaxCode> TradingTaxCodes => Set<TradingTaxCode>();
    public DbSet<TradingTaxRate> TradingTaxRates => Set<TradingTaxRate>();
    public DbSet<TradeDocument> TradeDocuments => Set<TradeDocument>();
    public DbSet<TradeDocumentLine> TradeLines => Set<TradeDocumentLine>();
    public DbSet<SettlementPayment> Payments => Set<SettlementPayment>();
    public DbSet<SettlementAllocation> Allocations => Set<SettlementAllocation>();
    public DbSet<BankProfile> Banks => Set<BankProfile>();
    public DbSet<TreasuryAccount> TreasuryAccounts => Set<TreasuryAccount>();
    public DbSet<BankStatement> BankStatements => Set<BankStatement>();
    public DbSet<BankStatementLine> BankStatementLines => Set<BankStatementLine>();
    public DbSet<ReconciliationSession> ReconciliationSessions => Set<ReconciliationSession>();
    public DbSet<BankMatch> BankMatches => Set<BankMatch>();
    public DbSet<BankMatchItem> BankMatchItems => Set<BankMatchItem>();
    public DbSet<TreasuryMovement> TreasuryMovements => Set<TreasuryMovement>();
    public DbSet<CashCount> CashCounts => Set<CashCount>();
    public DbSet<ReportDefinition> ReportDefinitions => Set<ReportDefinition>();
    public DbSet<ReportAccountMapping> ReportAccountMappings => Set<ReportAccountMapping>();
    public DbSet<RecognitionSchedule> RecognitionSchedules => Set<RecognitionSchedule>();
    public DbSet<RecognitionOccurrence> RecognitionOccurrences => Set<RecognitionOccurrence>();
    public DbSet<PeriodCloseSession> PeriodCloseSessions => Set<PeriodCloseSession>();
    public DbSet<PeriodCloseTask> PeriodCloseTasks => Set<PeriodCloseTask>();
    public DbSet<CloseException> CloseExceptions => Set<CloseException>();
    public DbSet<YearEndRecord> YearEndRecords => Set<YearEndRecord>();
    public DbSet<AssetCategory> AssetCategories => Set<AssetCategory>();
    public DbSet<AssetControlAccount> AssetControls => Set<AssetControlAccount>();
    public DbSet<FixedAsset> FixedAssets => Set<FixedAsset>();
    public DbSet<AssetComponent> AssetComponents => Set<AssetComponent>();
    public DbSet<AssetOperation> AssetOperations => Set<AssetOperation>();
    public DbSet<AssetMovement> AssetMovements => Set<AssetMovement>();
    public DbSet<AssetSourceClaim> AssetSourceClaims => Set<AssetSourceClaim>();
    public DbSet<Entities.Inventory.InventoryPolicy> InventoryPolicies => Set<Entities.Inventory.InventoryPolicy>();
    public DbSet<Entities.Inventory.InventoryAccount> InventoryAccounts => Set<Entities.Inventory.InventoryAccount>();
    public DbSet<Entities.Inventory.InventoryCategory> InventoryCategories => Set<Entities.Inventory.InventoryCategory>();
    public DbSet<Entities.Inventory.InventoryItem> InventoryItems => Set<Entities.Inventory.InventoryItem>();
    public DbSet<Entities.Inventory.Warehouse> Warehouses => Set<Entities.Inventory.Warehouse>();
    public DbSet<Entities.Inventory.ProcurementDocument> ProcurementDocuments => Set<Entities.Inventory.ProcurementDocument>();
    public DbSet<Entities.Inventory.ProcurementLine> ProcurementLines => Set<Entities.Inventory.ProcurementLine>();
    public DbSet<Entities.Inventory.ProcurementDiscrepancy> ProcurementDiscrepancies => Set<Entities.Inventory.ProcurementDiscrepancy>();
    public DbSet<Entities.Inventory.StockOperation> StockOperations => Set<Entities.Inventory.StockOperation>();
    public DbSet<Entities.Inventory.StockMovement> StockMovements => Set<Entities.Inventory.StockMovement>();
    public DbSet<Entities.Budgeting.FinancialBudget> FinancialBudgets => Set<Entities.Budgeting.FinancialBudget>();
    public DbSet<Entities.Budgeting.BudgetVersion> BudgetVersions => Set<Entities.Budgeting.BudgetVersion>();
    public DbSet<Entities.Budgeting.BudgetAllocation> BudgetAllocations => Set<Entities.Budgeting.BudgetAllocation>();
    public DbSet<Entities.Budgeting.BudgetPolicy> BudgetPolicies => Set<Entities.Budgeting.BudgetPolicy>();
    public DbSet<Entities.Budgeting.BudgetCommitment> BudgetCommitments => Set<Entities.Budgeting.BudgetCommitment>();
    public DbSet<Entities.Budgeting.BudgetOverride> BudgetOverrides => Set<Entities.Budgeting.BudgetOverride>();
    public DbSet<Entities.Forecasting.ForecastVersion> ForecastVersions => Set<Entities.Forecasting.ForecastVersion>();
    public DbSet<Entities.MultiCurrency.FxConfiguration> FxConfigurations => Set<Entities.MultiCurrency.FxConfiguration>();
    public DbSet<Entities.MultiCurrency.FxAccount> FxAccounts => Set<Entities.MultiCurrency.FxAccount>();
    public DbSet<Entities.MultiCurrency.FxCurrency> FxCurrencies => Set<Entities.MultiCurrency.FxCurrency>();
    public DbSet<Entities.MultiCurrency.FxRate> FxRates => Set<Entities.MultiCurrency.FxRate>();
    public DbSet<Entities.MultiCurrency.FxPosition> FxPositions => Set<Entities.MultiCurrency.FxPosition>();
    public DbSet<Entities.MultiCurrency.FxOperation> FxOperations => Set<Entities.MultiCurrency.FxOperation>();
    public DbSet<Entities.MultiCurrency.FxEffect> FxEffects => Set<Entities.MultiCurrency.FxEffect>();
    public DbSet<Entities.MultiCurrency.FxBankStatement> FxBankStatements => Set<Entities.MultiCurrency.FxBankStatement>();
    public DbSet<Entities.FinancialDimensions.DimensionDefinition> DimensionDefinitions => Set<Entities.FinancialDimensions.DimensionDefinition>();
    public DbSet<Entities.FinancialDimensions.DimensionValue> DimensionValues => Set<Entities.FinancialDimensions.DimensionValue>();
    public DbSet<Entities.FinancialDimensions.DimensionRule> DimensionRules => Set<Entities.FinancialDimensions.DimensionRule>();
    public DbSet<Entities.FinancialDimensions.DimensionBinding> DimensionBindings => Set<Entities.FinancialDimensions.DimensionBinding>();
    public DbSet<Entities.FinancialDimensions.PostedDimension> PostedDimensions => Set<Entities.FinancialDimensions.PostedDimension>();
    public DbSet<Entities.FinancialDimensions.DimensionProject> FinancialProjects => Set<Entities.FinancialDimensions.DimensionProject>();
    public DbSet<Entities.FinancialDimensions.DimensionProjectEvent> FinancialProjectEvents => Set<Entities.FinancialDimensions.DimensionProjectEvent>();
    public DbSet<Entities.FinancialDimensions.DimensionBudgetSlice> DimensionBudgetSlices => Set<Entities.FinancialDimensions.DimensionBudgetSlice>();
    public DbSet<Entities.FinancialDimensions.DimensionProjectForecast> ProjectFinancialForecasts => Set<Entities.FinancialDimensions.DimensionProjectForecast>();
    public DbSet<Entities.FinancialDimensions.DimensionAllocation> CostAllocationExecutions => Set<Entities.FinancialDimensions.DimensionAllocation>();
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HavePrecision(20, 4);
    }
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasPostgresExtension("btree_gist");
        foreach (var entity in builder.Model.GetEntityTypes())
            entity.SetSchema("identity");
        builder.ApplyConfigurationsFromAssembly(typeof(RaqmDbContext).Assembly);
    }
}

