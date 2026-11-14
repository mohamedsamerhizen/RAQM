using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.MultiCurrency;

namespace RAQM.Api.DTOs.MultiCurrency;

public sealed record FxConfigurationRequest(int SpotFallbackDays, bool RequireCloseRevaluation, IReadOnlyDictionary<FxAccountRole, Guid> Accounts);
public sealed record EnableCurrencyRequest(string Code);
public sealed record FxRateRequest(Guid CurrencyId, FxRateType Type, DateOnly Date, decimal ExchangeRate, string Source, string Evidence, Guid? ReplacesId = null);
public sealed record FxAction(Guid Version, string Reason, bool AllowBackdated = false);
public sealed record FxBankRequest(Guid CurrencyId, Guid LedgerAccountId, string Code);
public sealed record FxBankStatementRequest(DateOnly Cutoff, decimal StatementForeign, string Evidence);

public sealed record FxRequest(string Key, FxKind Kind, DateOnly Date, string Evidence, Guid? CurrencyId = null, Guid? PartyId = null,
    Guid? PositionId = null, Guid? OriginalId = null, Guid? BankPositionId = null, Guid? FunctionalCashAccountId = null,
    decimal ForeignAmount = 0, DateOnly? DueDate = null, string? Reference = null, IReadOnlyList<TradeLineRequest>? Lines = null,
    Guid? AdvancePositionId = null, decimal AppliedAdvance = 0, decimal BaseFee = 0, Guid? FeeAccountId = null,
    bool NonRefundableAdvance = false);

public sealed record FxMovement(Guid PositionId, Guid? RateId, decimal ForeignDelta, decimal BaseDelta);
public sealed record FxCommercialLine(int Number, string Description, Guid AccountId, Guid? TaxRateId, Guid? TaxAccountId,
    decimal ForeignNet, decimal ForeignTax, decimal ForeignDiscount, decimal BaseNet, decimal BaseTax);
public sealed record FxValuation(Guid PositionId, Guid RateId, decimal ForeignAmount, decimal PriorBase, decimal ClosingBase);
public sealed record FxPlan(IReadOnlyList<JournalDraftLine> Lines, IReadOnlyList<FxMovement> Movements,
    IReadOnlyList<FxCommercialLine>? Commercial = null, IReadOnlyList<FxValuation>? Valuations = null,
    Guid? AppliedRateId = null, decimal ForeignAmount = 0, decimal BaseAmount = 0,
    decimal Realized = 0, decimal Unrealized = 0, decimal AdvanceBasis = 0);
public sealed record FxBalance(Guid PositionId, decimal ForeignAmount, decimal BaseAmount);
