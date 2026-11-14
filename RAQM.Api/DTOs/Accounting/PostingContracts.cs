namespace RAQM.Api.DTOs.Accounting;
// In-process contract. Scope is independently resolved from actor membership by the engine.
public sealed record PostingScope(Guid TenantId, Guid OrganizationId, Guid CompanyId);
public sealed record JournalDraftLine(Guid AccountId, decimal Debit, decimal Credit, string? Description);
public sealed record PostingRequest(PostingScope Scope, string SourceModule, Guid SourceDocumentId, string IdempotencyKey, DateOnly AccountingDate, string Currency, IReadOnlyList<JournalDraftLine> Lines, Guid? ReversesJournalId, DateOnly DocumentDate, string Description, string? Reference = null, bool AllowBackdated = false, string? Reason = null, Guid? DraftId = null, Guid? ExpectedVersion = null);
public sealed record PostingReceipt(Guid JournalId, string JournalNumber, DateTimeOffset PostedAt);
