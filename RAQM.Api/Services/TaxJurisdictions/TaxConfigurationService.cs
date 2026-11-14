using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.TaxAccounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Intercompany;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.TaxJurisdictions;

public sealed class TaxConfigurationService(RaqmDbContext db, IFinancialTransaction transaction, FinancialAccess access)
{
    internal static ApiException Missing() => new(404, "Tax evidence was not found.", "tax_evidence_missing");
    internal static ApiException Bad(string detail) => new(400, detail, "tax_input");
    internal static ApiException Conflict(string detail) => new(409, detail, "tax_conflict");
    internal static string Code(string value) => AccountingRules.Text(value, 40, "Tax classification").ToUpperInvariant();

    public Task<TaxRegistration> Register(Guid actor, Guid org, Guid company, TaxRegistrationRequest input, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var country = Code(input.Country);
            var residence = Code(input.Residence);
            if (country.Length != 2 || residence.Length != 2 || country.Concat(residence).Any(x => x is < 'A' or > 'Z') || input.EffectiveOn.Year < 1900)
                throw Bad("Use two-letter country and residence identifiers and an effective date.");
            _ = CurrencyPolicy.Units(input.Currency);
            var code = Code(input.Code);
            if (await db.Set<TaxRegistration>().AnyAsync(x => x.CompanyId == company && x.Code == code, token))
                throw Conflict("Tax registration code is already in use.");
            var row = Scoped(scope, new TaxRegistration { Code = code, Country = country,
                Region = input.Region is null ? null : Code(input.Region), Residence = residence, Currency = input.Currency,
                Authority = AccountingRules.Text(input.Authority, 160, "Tax authority"),
                Number = AccountingRules.Text(input.Number, 100, "Tax registration number"), EffectiveOn = input.EffectiveOn,
                Evidence = AccountingRules.Text(input.Evidence, 1000, "Registration evidence"), PreparedBy = actor });
            db.Add(row);
            access.Audit(scope, actor, row.Id, "TaxRegistrationPrepared");
            return row;
        }, ct);

    public Task<TaxRegistration> ApproveRegistration(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var row = await db.Set<TaxRegistration>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing();
            if (row.ApprovedBy == actor) return row;
            Independent(row.PreparedBy, row.ApprovedBy, actor);
            row.ApprovedBy = actor;
            access.Audit(scope, actor, id, "TaxRegistrationApproved");
            return row;
        }, ct);

    public Task<TaxControl> Control(Guid actor, Guid org, Guid company, Guid id, TaxControlRequest input, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            await Registration(company, id, token);
            var classification = input.Role switch
            {
                TaxControlRole.Input or TaxControlRole.Prepaid or TaxControlRole.DeferredAsset or TaxControlRole.Clearing => AccountClass.Asset,
                TaxControlRole.Output or TaxControlRole.Withholding or TaxControlRole.CurrentPayable or TaxControlRole.DeferredLiability => AccountClass.Liability,
                TaxControlRole.Nonrecoverable or TaxControlRole.CurrentExpense or TaxControlRole.DeferredExpense => AccountClass.Expense,
                _ => throw Bad("Unknown tax control role.")
            };
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == input.AccountId &&
                x.IsPosting && x.IsActive && !x.IsContra && x.Classification == classification, token) ?? throw Bad("Use an eligible account of the required classification.");
            var existing = await db.Set<TaxControl>().AnyAsync(x => x.CompanyId == company && (x.AccountId == account.Id ||
                x.RegistrationId == id && x.Role == input.Role), token);
            if (existing) throw Conflict("Tax controls are dedicated and immutable.");
            var trading = await db.TradingAccounts.AsNoTracking().Where(x => x.CompanyId == company && x.AccountId == account.Id).ToListAsync(token);
            if (input.Role == TaxControlRole.Clearing && (!trading.Any(x => x.Role == AccountRole.Cash) ||
                await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == company && x.LedgerAccountId == account.Id, token)))
                throw Conflict("Withholding clearing requires a dedicated trading cash account without treasury registration.");
            if (await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == company && x.LedgerAccountId == account.Id, token) ||
                await db.Set<IntercompanyPolicy>().AnyAsync(x => x.DueFromId == account.Id || x.DueToId == account.Id, token))
                throw Conflict("Tax controls must be separate from treasury and reciprocal controls.");
            if (trading.Any(x => !(input.Role == TaxControlRole.Clearing && x.Role == AccountRole.Cash ||
                input.Role == TaxControlRole.Output && x.Role == AccountRole.TaxOutput || input.Role == TaxControlRole.Input && x.Role == AccountRole.TaxInput)))
                throw Conflict("The selected account already serves an incompatible trading purpose.");
            if (input.Role is not (TaxControlRole.Input or TaxControlRole.Output) &&
                await db.JournalLines.AnyAsync(x => x.CompanyId == company && x.AccountId == account.Id, token))
                throw Conflict("A new protected control requires an unused account.");
            if (await db.InventoryAccounts.AnyAsync(x => x.CompanyId == company && x.AccountId == account.Id, token) ||
                await db.FxAccounts.AnyAsync(x => x.CompanyId == company && x.AccountId == account.Id, token) ||
                await db.AssetControls.AnyAsync(x => x.CompanyId == company && x.AccountId == account.Id, token))
                throw Conflict("Use a separate account from inventory, fixed asset and FX controls.");
            var row = Scoped(scope, new TaxControl { RegistrationId = id, Role = input.Role, AccountId = account.Id, ActorId = actor });
            db.Add(row);
            access.Audit(scope, actor, row.Id, "TaxControlRegistered", AccountingRules.Text(input.Evidence, 500, "Control evidence"));
            return row;
        }, ct);

    public Task<TaxRule> Rule(Guid actor, Guid org, Guid company, Guid id, TaxRuleRequest input, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var registration = await Registration(company, id, token);
            if (!Enum.IsDefined(input.Side) || !Enum.IsDefined(input.Treatment) || input.Order is < 1 or > 20 ||
                input.EffectiveOn < registration.EffectiveOn || input.EndsOn < input.EffectiveOn ||
                (input.Treatment is TaxTreatment.Standard or TaxTreatment.ZeroRated) != input.RateId.HasValue)
                throw Bad("The rate, treatment, ordering or effective window is invalid.");
            if (input.PartyId is { } party && !await db.TradingParties.AnyAsync(x => x.CompanyId == company && x.Id == party && x.Kind == input.Side, token))
                throw Missing();
            if (input.RateId is { } rateId)
            {
                var rate = await db.TradingTaxRates.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == rateId, token) ?? throw Missing();
                if (rate.EffectiveFrom > input.EffectiveOn || rate.EffectiveTo < input.EndsOn || (rate.RatePercent == 0) != (input.Treatment == TaxTreatment.ZeroRated))
                    throw Bad("The legacy tax rate must cover the rule window and match its treatment.");
            }
            var row = Scoped(scope, new TaxRule { RegistrationId = id, Group = Code(input.Group),
                ItemClass = input.ItemClass is null ? null : Code(input.ItemClass), Side = input.Side, PartyId = input.PartyId, Order = input.Order,
                Treatment = input.Treatment, RateId = input.RateId, EffectiveOn = input.EffectiveOn, EndsOn = input.EndsOn, PreparedBy = actor,
                Evidence = AccountingRules.Text(input.Evidence, 1000, "Rule evidence") });
            db.Add(row);
            access.Audit(scope, actor, row.Id, "TaxRulePrepared", row.Group);
            return row;
        }, ct);

    public Task<TaxRule> ApproveRule(Guid actor, Guid org, Guid company, Guid id, Guid rule, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var row = await db.Set<TaxRule>().SingleOrDefaultAsync(x => x.CompanyId == company && x.RegistrationId == id && x.Id == rule, token) ?? throw Missing();
            if (row.ApprovedBy == actor) return row;
            Independent(row.PreparedBy, row.ApprovedBy, actor);
            if (await db.Set<TaxRule>().AnyAsync(x => x.CompanyId == company && x.RegistrationId == id && x.ApprovedBy != null &&
                x.Group == row.Group && x.Side == row.Side && x.Order == row.Order && x.ItemClass == row.ItemClass && x.PartyId == row.PartyId &&
                x.EffectiveOn <= row.EndsOn && x.EndsOn >= row.EffectiveOn, token))
                throw Conflict("An approved equivalent rule overlaps these dates.");
            row.ApprovedBy = actor;
            access.Audit(scope, actor, row.Id, "TaxRuleApproved", row.Group);
            return row;
        }, ct);

    public async Task<object> List(Guid actor, Guid org, Guid company, Guid? registration, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return new
        {
            Registrations = await db.Set<TaxRegistration>().AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct),
            Controls = await db.Set<TaxControl>().AsNoTracking().Where(x => x.CompanyId == company && (registration == null || x.RegistrationId == registration))
                .OrderBy(x => x.Role).ToListAsync(ct),
            Rules = await db.Set<TaxRule>().AsNoTracking().Where(x => x.CompanyId == company && (registration == null || x.RegistrationId == registration))
                .OrderBy(x => x.Group).ThenBy(x => x.EffectiveOn).ThenBy(x => x.Id).ToListAsync(ct)
        };
    }

    internal async Task<TaxRegistration> Registration(Guid company, Guid id, CancellationToken ct, DateOnly? date = null)
    {
        var row = await db.Set<TaxRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw Missing();
        if (!row.ApprovedBy.HasValue || date.HasValue && date.Value < row.EffectiveOn)
            throw Conflict("The tax registration is not approved and effective for this operation.");
        return row;
    }

    internal async Task<T> Admin<T>(Guid actor, Guid org, Guid company, Func<Company, CancellationToken, Task<T>> action, CancellationToken ct) =>
        await transaction.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            return await action(scope, token);
        }, ct);

    internal static void Independent(Guid preparer, Guid? approved, Guid actor)
    {
        if (approved.HasValue || preparer == actor) throw Conflict("A different owner must approve draft evidence.");
    }

    internal static T Scoped<T>(Company company, T row) where T : TaxRecord
    {
        row.TenantId = company.TenantId;
        row.OrganizationId = company.OrganizationId;
        row.CompanyId = company.Id;
        return row;
    }
}
