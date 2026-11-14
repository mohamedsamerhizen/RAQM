using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.Entities.Banking;

public enum TreasuryAccountKind { Bank, Cash, PettyCash }
public enum ReconciliationState { Matching, Review, Finalized }

public abstract class BankEvidence : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
}

public sealed class BankProfile : BankEvidence
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Swift { get; set; }
    public bool Active { get; set; } = true;
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class TreasuryAccount : BankEvidence
{
    public TreasuryAccountKind Kind { get; set; }
    public Guid? BankProfileId { get; set; }
    public Guid LedgerAccountId { get; set; }
    public Guid? CustodianId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Currency { get; set; } = "";
    public string? AccountMask { get; set; }
    public string? IbanMask { get; set; }
    public string? IdentifierDigest { get; set; }
    public string? IbanDigest { get; set; }
    public bool Active { get; set; } = true;
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class BankStatement : BankEvidence
{
    public Guid BankAccountId { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public decimal Opening { get; set; }
    public decimal Closing { get; set; }
    public string ExternalId { get; set; } = "";
    public string Digest { get; set; } = "";
    public string RequestKey { get; set; } = "";
    public int LineCount { get; set; }
    public Guid ImportedBy { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<BankStatementLine> Lines { get; set; } = [];
}

public sealed class BankStatementLine : BankEvidence
{
    public Guid StatementId { get; set; }
    public Guid BankAccountId { get; set; }
    public int Row { get; set; }
    public int Sequence { get; set; }
    public DateOnly BookingDate { get; set; }
    public DateOnly? ValueDate { get; set; }
    public decimal Amount { get; set; }
    public string Reference { get; set; } = "";
    public string Description { get; set; } = "";
    public string OriginalFields { get; set; } = "";
    public string? StableExternalId { get; set; }
}

public sealed class ReconciliationSession : BankEvidence
{
    public Guid StatementId { get; set; }
    public Guid BankAccountId { get; set; }
    public ReconciliationState State { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid? FinalizedBy { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public decimal? FinalizedBook { get; set; }
    public decimal? FinalizedDeposits { get; set; }
    public decimal? FinalizedWithdrawals { get; set; }
}

public sealed class BankMatch : BankEvidence
{
    public Guid SessionId { get; set; }
    public Guid StatementLineId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public Guid ConfirmedBy { get; set; }
    public DateTimeOffset ConfirmedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ReleasedBy { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
    public List<BankMatchItem> Items { get; set; } = [];
}

public sealed class BankMatchItem : BankEvidence
{
    public Guid MatchId { get; set; }
    public Guid JournalLineId { get; set; }
    public decimal Amount { get; set; }
    public bool Released { get; set; }
}
