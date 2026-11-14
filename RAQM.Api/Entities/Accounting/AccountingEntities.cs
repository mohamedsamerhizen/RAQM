namespace RAQM.Api.Entities.Accounting;
public enum AccountClass
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense
}

public enum BalanceSide
{
    Debit,
    Credit
}

public enum PeriodState
{
    Open,
    Closed,
    Locked
}

public enum JournalState
{
    Draft,
    Posted
}

public interface ICompanyFinancialEntity
{
    Guid Id { get; set; }

    Guid TenantId { get; set; }

    Guid OrganizationId { get; set; }

    Guid CompanyId { get; set; }
}

public sealed class LedgerAccount : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public AccountClass Classification { get; set; }
    public BalanceSide NormalBalance { get; set; }
    public bool IsContra { get; set; }
    public Guid? ParentId { get; set; }
    public bool IsPosting { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FiscalYear : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AccountingPeriod : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid FiscalYearId { get; set; }
    public int Number { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public PeriodState State { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class JournalEntry : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public JournalState State { get; set; }
    public string Description { get; set; } = "";
    public string? Reference { get; set; }
    public DateOnly PostingDate { get; set; }
    public DateOnly DocumentDate { get; set; }
    public string Currency { get; set; } = "";
    public int CurrencyPolicyVersion { get; set; } = 1;
    public int MinorUnits { get; set; }
    public Guid? PeriodId { get; set; }
    public long? Sequence { get; set; }
    public string? Number { get; set; }
    public string SourceModule { get; set; } = "";
    public Guid SourceDocumentId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? PayloadHash { get; set; }
    public Guid? ReversesJournalId { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public Guid? PostedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PostedAt { get; set; }
    public string? PostingReason { get; set; }
    public List<JournalLine> Lines { get; set; } = [];
}

public sealed class JournalLine : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid JournalId { get; set; }
    public int Number { get; set; }
    public Guid AccountId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Description { get; set; }
}

public sealed class OpeningBalanceBatch : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public string BatchKey { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid JournalId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// EntityId deliberately has no FK: draft-deletion evidence must survive its source.
public sealed class AccountingAuditEvent : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid EntityId { get; set; }
    public string Action { get; set; } = "";
    public string? Reason { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
