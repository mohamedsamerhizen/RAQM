using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.Trading;
public sealed class TradingSetupService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions, TradingPolicy policy, GovernanceGate? governance = null) : ITradingSetupService
{
    public Task<IReadOnlyList<ControlAccountResponse>> Configure(Guid actor, Guid org, Guid company, ControlAccountRequest r, CancellationToken ct) => transactions.Execute<IReadOnlyList<ControlAccountResponse>>(actor, org, company, async (c, t) =>
    {
        await access.Resolve(actor, org, company, true, true, t);
        CurrencyPolicy.Units(c.BaseCurrency);
        var reason = AccountingRules.Text(r.Reason, 500, "Configuration reason");
        if (r.CashAccounts is null || r.CashAccounts.Count == 0 || r.CashAccounts.Count > 50)
            throw new ApiException(400, "Configure 1..50 cash accounts.", "trading_configuration");
        var roles = new Dictionary<AccountRole, Guid>
        {
            {
                AccountRole.Receivable,
                r.Receivable
            },
            {
                AccountRole.Payable,
                r.Payable
            },
            {
                AccountRole.Revenue,
                r.Revenue
            },
            {
                AccountRole.Expense,
                r.Expense
            },
            {
                AccountRole.TaxOutput,
                r.TaxOutput
            },
            {
                AccountRole.TaxInput,
                r.TaxInput
            },
            {
                AccountRole.CustomerAdvance,
                r.CustomerAdvance
            },
            {
                AccountRole.SupplierAdvance,
                r.SupplierAdvance
            },
            {
                AccountRole.OpeningEquity,
                r.OpeningEquity
            }
        };
        var ids = roles.Values.Concat(r.CashAccounts).ToArray();
        if (ids.Distinct().Count() != ids.Length)
            throw new ApiException(400, "Control, default, advance, tax, equity and cash accounts must be distinct.", "trading_configuration");
        if (await db.AssetControls.AnyAsync(x => x.CompanyId == company && ids.Contains(x.AccountId), t) ||
            await db.InventoryAccounts.AnyAsync(x => x.CompanyId == company && ids.Contains(x.AccountId), t))
            throw new ApiException(400, "Fixed asset control accounts cannot be used for trading control mappings.", "asset_mapping");
        var registered = await db.TreasuryAccounts.AsNoTracking().Where(x => x.CompanyId == company).ToListAsync(t);
        if (registered.Any(x => roles.Values.Contains(x.LedgerAccountId) ||
            (x.Kind == Entities.Banking.TreasuryAccountKind.PettyCash && r.CashAccounts.Contains(x.LedgerAccountId))))
            throw new ApiException(400, "Treasury accounts must retain their dedicated accounting roles.", "treasury_mapping");
        var cashAccounts = r.CashAccounts.Concat(registered.Where(x => x.Kind != Entities.Banking.TreasuryAccountKind.PettyCash)
            .Select(x => x.LedgerAccountId)).Distinct().ToArray();
        foreach (var(role, id)in roles)
        {
            var a = await policy.Account(company, id, t);
            var expected = role switch
            {
                AccountRole.Receivable or AccountRole.TaxInput or AccountRole.SupplierAdvance => AccountClass.Asset,
                AccountRole.Payable or AccountRole.TaxOutput or AccountRole.CustomerAdvance => AccountClass.Liability,
                AccountRole.Revenue => AccountClass.Revenue,
                AccountRole.Expense => AccountClass.Expense,
                _ => AccountClass.Equity
            };
            if (a.Classification != expected)
                throw new ApiException(400, $"{role} requires {expected} classification.", "trading_configuration");
        }

        foreach (var id in r.CashAccounts)
            if ((await policy.Account(company, id, t)).Classification != AccountClass.Asset)
                throw new ApiException(400, "Cash/bank mappings require asset accounts.", "cash_account");
        if (await db.TradingParties.AnyAsync(x => x.CompanyId == company, t) || await db.TradingTaxCodes.AnyAsync(x => x.CompanyId == company, t))
            throw new ApiException(409, "Trading mappings freeze when counterparties or tax codes exist.", "trading_configuration_locked");
        var protectedIds = roles.Where(x => Protected(x.Key)).Select(x => x.Value).ToArray();
        var legacy = await db.JournalLines.AnyAsync(l => l.CompanyId == company && protectedIds.Contains(l.AccountId) && db.Journals.Any(j => j.Id == l.JournalId && j.State == JournalState.Posted), t);
        if (legacy && !r.AcknowledgeLegacyActivity)
            throw new ApiException(409, "Existing protected-account activity requires explicit legacy acknowledgement; discrepancies will remain visible.", "legacy_control_activity");
        await db.TradingAccounts.Where(x => x.CompanyId == company).ExecuteDeleteAsync(t);
        var mappings = roles.Select(x => new TradingAccountMapping { TenantId = c.TenantId, OrganizationId = org, CompanyId = company, AccountId = x.Value, Role = x.Key, ActorId = actor, Reason = reason }).Concat(cashAccounts.Select(id => new TradingAccountMapping { TenantId = c.TenantId, OrganizationId = org, CompanyId = company, AccountId = id, Role = AccountRole.Cash, ActorId = actor, Reason = reason })).ToList();
        db.TradingAccounts.AddRange(mappings);
        access.Audit(c, actor, company, "TradingConfigured", reason);
        await db.SaveChangesAsync(t);
        return mappings.Select(x => new ControlAccountResponse(x.AccountId, x.Role, x.ActivatedAt)).ToList();
    }, ct);
    public static bool Protected(AccountRole role) => role is AccountRole.Receivable or AccountRole.Payable or AccountRole.TaxOutput or AccountRole.TaxInput or AccountRole.CustomerAdvance or AccountRole.SupplierAdvance;
    public async Task<IReadOnlyList<ControlAccountResponse>> Configuration(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.TradingAccounts.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Role).Select(x => new ControlAccountResponse(x.AccountId, x.Role, x.ActivatedAt)).ToListAsync(ct);
    }

    private async Task ApplyParty(TradingParty party, PartyRequest r, CancellationToken ct)
    {
        if (r is null || !Enum.IsDefined(r.Kind))
            throw new ApiException(400, "Invalid party kind.", "party_kind");
        var map = await policy.RequiredMappings(party.CompanyId, ct);
        if (r.ControlAccountId != map[r.Kind == PartyKind.Customer ? AccountRole.Receivable : AccountRole.Payable])
            throw new ApiException(400, "Assign the configured counterparty control account.", "control_account");
        var account = await policy.Account(party.CompanyId, r.DefaultAccountId, ct);
        if ((r.Kind == PartyKind.Customer && account.Classification != AccountClass.Revenue) || (r.Kind == PartyKind.Supplier && account.Classification is not (AccountClass.Expense or AccountClass.Asset)) || await db.TradingAccounts.AnyAsync(x => x.CompanyId == party.CompanyId && x.AccountId == account.Id && x.Role != AccountRole.Revenue && x.Role != AccountRole.Expense, ct))
            throw new ApiException(400, "Use ordinary revenue or expense/non-inventory asset mappings.", "line_account");
        if (party.Kind != r.Kind && await db.TradingParties.AnyAsync(x => x.Id == party.Id, ct))
            throw new ApiException(409, "Counterparty kind is immutable.", "party_kind");
        if (r.PaymentTermsDays is < 0 or > 365)
            throw new ApiException(400, "Payment terms must be 0..365 days.", "payment_terms");
        if (r.CreditLimit is { } limit)
            CurrencyPolicy.Validate(limit, CurrencyPolicy.Units(party.Currency));
        if (r.Email is not null && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(r.Email))
            throw new ApiException(400, "Invalid email.", "party_contact");
        AccountingRules.OptionalText(r.Email, 254);
        AccountingRules.OptionalText(r.Phone, 80);
        AccountingRules.OptionalText(r.BillingAddress, 1000);
        AccountingRules.OptionalText(r.TaxIdentifier, 80);
        party.Kind = r.Kind;
        party.Code = AccountingRules.Text(r.Code, 40, "Code").ToUpperInvariant();
        party.Name = AccountingRules.Text(r.Name, 160, "Name");
        party.Email = r.Email;
        party.Phone = r.Phone;
        party.BillingAddress = r.BillingAddress;
        party.TaxIdentifier = r.TaxIdentifier;
        party.PaymentTermsDays = r.PaymentTermsDays;
        party.CreditLimit = r.CreditLimit;
        party.ControlAccountId = r.ControlAccountId;
        party.DefaultAccountId = r.DefaultAccountId;
        party.IsActive = r.IsActive;
        party.Version = Guid.NewGuid();
        party.UpdatedAt = DateTimeOffset.UtcNow;
        if (await db.TradingParties.AnyAsync(x => x.CompanyId == party.CompanyId && x.Kind == party.Kind && x.Code == party.Code && x.Id != party.Id, ct))
            throw new ApiException(409, "Party code already exists in this company and kind.", "party_duplicate");
    }

    public Task<PartyResponse> CreateParty(Guid actor, Guid org, Guid company, PartyRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        var p = new TradingParty
        {
            TenantId = c.TenantId,
            OrganizationId = org,
            CompanyId = company,
            Kind = r.Kind,
            Currency = c.BaseCurrency,
            CreatedBy = actor,
            UpdatedBy = actor
        };
        await ApplyParty(p, r, t);
        db.TradingParties.Add(p);
        access.Audit(c, actor, p.Id, "PartyCreated");
        await db.SaveChangesAsync(t);
        return View(p);
    }, ct);
    public Task<PartyResponse> UpdateParty(Guid actor, Guid org, Guid company, Guid id, UpdatePartyRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        var p = await db.TradingParties.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, t) ?? throw new ApiException(404, "Party not found.", "party_not_found");
        if (p.Version != r.Version)
            throw new ApiException(409, "Party version changed.", "version_conflict");
        var previousFinancial = (p.DefaultAccountId, p.ControlAccountId, p.PaymentTermsDays, p.TaxIdentifier, p.BillingAddress, p.IsActive);
        await ApplyParty(p, r.Party, t);
        if (governance is not null && p.Kind == PartyKind.Supplier && previousFinancial !=
            (p.DefaultAccountId, p.ControlAccountId, p.PaymentTermsDays, p.TaxIdentifier, p.BillingAddress, p.IsActive))
            await governance.ObserveSupplierChange(c, actor, id, t);
        p.UpdatedBy = actor;
        access.Audit(c, actor, id, "PartyUpdated");
        await db.SaveChangesAsync(t);
        return View(p);
    }, ct);
    public async Task<PartyResponse> GetParty(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return View(await db.TradingParties.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw new ApiException(404, "Party not found.", "party_not_found"));
    }

    public async Task<PageResult<PartyResponse>> Parties(Guid actor, Guid org, Guid company, PartyKind? kind, string? search, bool? active, int page, int pageSize, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        AccountingRules.Page(page, pageSize);
        AccountingRules.OptionalText(search, 100);
        if (kind.HasValue && !Enum.IsDefined(kind.Value))
            throw new ApiException(400, "Invalid party kind.", "party_kind");
        var q = db.TradingParties.AsNoTracking().Where(x => x.CompanyId == company);
        if (kind.HasValue)
            q = q.Where(x => x.Kind == kind);
        if (active.HasValue)
            q = q.Where(x => x.IsActive == active);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(x => EF.Functions.ILike(x.Code, "%" + search + "%") || EF.Functions.ILike(x.Name, "%" + search + "%"));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.Code).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(rows.Select(View).ToList(), total, page, pageSize);
    }

    public static PartyResponse View(TradingParty p) => new(p.Id, p.Kind, p.Code, p.Name, p.Email, p.Phone, p.BillingAddress, p.TaxIdentifier, p.PaymentTermsDays, p.CreditLimit, p.Currency, p.ControlAccountId, p.DefaultAccountId, p.IsActive, p.Version);
    public Task<TaxCodeResponse> CreateTaxCode(Guid actor, Guid org, Guid company, TaxCodeRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        await access.Resolve(actor, org, company, true, true, t);
        var map = await policy.RequiredMappings(company, t);
        if (r.InputAccountId != map[AccountRole.TaxInput] || r.OutputAccountId != map[AccountRole.TaxOutput])
            throw new ApiException(400, "Use configured input/output tax accounts.", "tax_account");
        var code = AccountingRules.Text(r.Code, 40, "Code").ToUpperInvariant();
        if (await db.TradingTaxCodes.AnyAsync(x => x.CompanyId == company && x.Code == code, t))
            throw new ApiException(409, "Tax code exists.", "tax_duplicate");
        var tax = new TradingTaxCode
        {
            TenantId = c.TenantId,
            OrganizationId = org,
            CompanyId = company,
            Code = code,
            Name = AccountingRules.Text(r.Name, 160, "Name"),
            InputAccountId = r.InputAccountId,
            OutputAccountId = r.OutputAccountId,
            IsActive = r.IsActive,
            CreatedBy = actor
        };
        db.TradingTaxCodes.Add(tax);
        access.Audit(c, actor, tax.Id, "TaxCodeCreated");
        await db.SaveChangesAsync(t);
        return TaxView(tax);
    }, ct);
    public Task<TaxCodeResponse> UpdateTaxCode(Guid actor, Guid org, Guid company, Guid id, TaxCodeRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        await access.Resolve(actor, org, company, true, true, t);
        var tax = await db.TradingTaxCodes.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, t) ?? throw new ApiException(404, "Tax code not found.", "tax_not_found");
        if (tax.Code != r.Code?.Trim().ToUpperInvariant() || tax.InputAccountId != r.InputAccountId || tax.OutputAccountId != r.OutputAccountId)
            throw new ApiException(409, "Tax code identity/accounts are immutable; create a new code.", "tax_immutable");
        tax.Name = AccountingRules.Text(r.Name, 160, "Name");
        tax.IsActive = r.IsActive;
        access.Audit(c, actor, id, "TaxCodeUpdated");
        await db.SaveChangesAsync(t);
        return TaxView(tax);
    }, ct);
    public Task<TaxRateResponse> AddTaxRate(Guid actor, Guid org, Guid company, Guid code, TaxRateRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        await access.Resolve(actor, org, company, true, true, t);
        TradeRules.Rate(r.RatePercent);
        if (r.EffectiveFrom.Year < 1900 || r.EffectiveTo < r.EffectiveFrom)
            throw new ApiException(400, "Invalid tax effective dates.", "tax_dates");
        if (!await db.TradingTaxCodes.AnyAsync(x => x.CompanyId == company && x.Id == code && x.IsActive, t))
            throw new ApiException(404, "Active tax code not found.", "tax_not_found");
        if (await db.TradingTaxRates.AnyAsync(x => x.CompanyId == company && x.TaxCodeId == code && x.EffectiveFrom <= r.EffectiveTo && x.EffectiveTo >= r.EffectiveFrom, t))
            throw new ApiException(409, "Tax rate effective windows cannot overlap.", "tax_overlap");
        var version = (await db.TradingTaxRates.Where(x => x.TaxCodeId == code).MaxAsync(x => (int? )x.Version, t) ?? 0) + 1;
        var rate = new TradingTaxRate
        {
            TenantId = c.TenantId,
            OrganizationId = org,
            CompanyId = company,
            TaxCodeId = code,
            Version = version,
            EffectiveFrom = r.EffectiveFrom,
            EffectiveTo = r.EffectiveTo,
            RatePercent = r.RatePercent,
            CreatedBy = actor
        };
        db.TradingTaxRates.Add(rate);
        access.Audit(c, actor, rate.Id, "TaxRateCreated");
        await db.SaveChangesAsync(t);
        return RateView(rate);
    }, ct);
    public async Task<IReadOnlyList<TaxCodeResponse>> TaxCodes(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return (await db.TradingTaxCodes.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct)).Select(TaxView).ToList();
    }

    public async Task<IReadOnlyList<TaxRateResponse>> TaxRates(Guid actor, Guid org, Guid company, Guid code, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return (await db.TradingTaxRates.AsNoTracking().Where(x => x.CompanyId == company && x.TaxCodeId == code).OrderBy(x => x.EffectiveFrom).ToListAsync(ct)).Select(RateView).ToList();
    }

    private static TaxCodeResponse TaxView(TradingTaxCode x) => new(x.Id, x.Code, x.Name, x.OutputAccountId, x.InputAccountId, x.IsActive);
    private static TaxRateResponse RateView(TradingTaxRate x) => new(x.Id, x.TaxCodeId, x.Version, x.EffectiveFrom, x.EffectiveTo, x.RatePercent);
}
