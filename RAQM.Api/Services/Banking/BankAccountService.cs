using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Banking;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.Banking;

public sealed class BankAccountService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions) : IBankAccountService
{
    public Task<BankProfile> CreateBank(Guid actor, Guid org, Guid company, BankProfileRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var code = AccountingRules.Text(request.Code, 40, "Bank code").ToUpperInvariant();
            var swift = request.Swift?.Trim().ToUpperInvariant();
            if (swift is not null && !Regex.IsMatch(swift, @"\A[A-Z]{6}[A-Z0-9]{2}([A-Z0-9]{3})?\z"))
                throw new ApiException(400, "Invalid SWIFT/BIC format.", "bank_swift");
            if (await db.Banks.AnyAsync(x => x.CompanyId == company && x.Code == code, token))
                throw new ApiException(409, "Bank code already exists.", "bank_code");
            var bank = new BankProfile { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId,
                Code = code, Name = AccountingRules.Text(request.Name, 160, "Bank name"), Swift = swift };
            db.Banks.Add(bank);
            access.Audit(scope, actor, bank.Id, "BankProfileCreated");
            return bank;
        }, ct);

    public Task<BankProfile> ChangeBank(Guid actor, Guid org, Guid company, Guid id, AccountChange request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var bank = await db.Banks.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token)
                ?? throw new ApiException(404, "Bank profile not found.", "bank_profile");
            CheckVersion(bank.Version, request.Version);
            if (!request.Active && await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == company && x.BankProfileId == id && x.Active, token))
                throw new ApiException(409, "Deactivate active accounts before deactivating the bank profile.", "bank_profile_active");
            bank.Name = AccountingRules.Text(request.Name, 160, "Bank name");
            bank.Active = request.Active;
            bank.Version = Guid.NewGuid();
            access.Audit(scope, actor, id, "BankProfileChanged", AccountingRules.Text(request.Reason, 500, "Reason"));
            return bank;
        }, ct);

    public async Task<IReadOnlyList<BankProfile>> Banks(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.Banks.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct);
    }

    public Task<TreasuryAccountResponse> Register(Guid actor, Guid org, Guid company, TreasuryAccountRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            CurrencyPolicy.Units(scope.BaseCurrency);
            if (!Enum.IsDefined(request.Kind))
                throw new ApiException(400, "Invalid treasury account type.", "treasury_kind");
            var code = AccountingRules.Text(request.Code, 40, "Account code").ToUpperInvariant();
            var ledger = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == request.LedgerAccountId, token);
            if (ledger is not { IsActive: true, IsPosting: true, IsContra: false, Classification: AccountClass.Asset })
                throw new ApiException(400, "Map to a company asset account enabled for posting.", "treasury_ledger");
            if (await db.AssetControls.AnyAsync(x => x.CompanyId == company && x.AccountId == ledger.Id, token) ||
                await db.InventoryAccounts.AnyAsync(x => x.CompanyId == company && x.AccountId == ledger.Id, token))
                throw new ApiException(400, "This GL is dedicated to fixed asset control.", "treasury_ledger");
            if (await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == company && (x.Code == code || x.LedgerAccountId == ledger.Id), token))
                throw new ApiException(409, "Treasury code or GL mapping already exists.", "treasury_duplicate");
            var mappedRole = await db.TradingAccounts.Where(x => x.CompanyId == company && x.AccountId == ledger.Id)
                .Select(x => (AccountRole?)x.Role).SingleOrDefaultAsync(token);
            if (mappedRole is not (null or AccountRole.Cash) || (request.Kind == TreasuryAccountKind.PettyCash && mappedRole.HasValue))
                throw new ApiException(400, "This GL is reserved by the trading setup.", "treasury_ledger");
            if (!request.AcknowledgeExistingLedger && await (from line in db.JournalLines join journal in db.Journals on line.JournalId equals journal.Id
                where line.CompanyId == company && line.AccountId == ledger.Id && journal.State == JournalState.Posted select line.Id).AnyAsync(token))
                throw new ApiException(409, "Acknowledge the existing GL activity before registering this account.", "treasury_existing");
            var account = new TreasuryAccount { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, Kind = request.Kind,
                Code = code, Name = AccountingRules.Text(request.Name, 160, "Account name"), LedgerAccountId = ledger.Id, Currency = scope.BaseCurrency,
                BankProfileId = request.BankProfileId, CustodianId = request.CustodianId };
            if (request.Kind == TreasuryAccountKind.Bank)
            {
                await ValidateBankProfile(company, request.BankProfileId, token);
                var identifier = NormalizeIdentifier(request.AccountIdentifier, false);
                var iban = NormalizeIdentifier(request.Iban, true);
                if (iban is null && identifier is null)
                    throw new ApiException(400, "Supply an account identifier or IBAN.", "bank_identifier");
                account.IdentifierDigest = Digest(identifier);
                account.IbanDigest = Digest(iban);
                account.AccountMask = Mask(identifier);
                account.IbanMask = Mask(iban);
                if (await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == company &&
                    ((account.IdentifierDigest != null && x.BankProfileId == account.BankProfileId && x.IdentifierDigest == account.IdentifierDigest) ||
                    (account.IbanDigest != null && x.IbanDigest == account.IbanDigest)), token))
                    throw new ApiException(409, "This bank identifier is already registered.", "bank_identifier");
            }
            else if (request.BankProfileId.HasValue || request.AccountIdentifier is not null || request.Iban is not null)
                throw new ApiException(400, "Bank fields apply only to bank accounts.", "bank_identifier");
            await ValidateCustodian(scope, account.Kind, request.CustodianId, token);

            if (account.Kind != TreasuryAccountKind.PettyCash && mappedRole is null)
                db.TradingAccounts.Add(new() { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, AccountId = ledger.Id,
                    Role = AccountRole.Cash, ActorId = actor, Reason = "Registered treasury cash or bank account" });
            db.TreasuryAccounts.Add(account);
            access.Audit(scope, actor, account.Id, "TreasuryAccountRegistered", request.AcknowledgeExistingLedger ? "Existing GL history acknowledged" : null);
            return View(account);
        }, ct);

    public Task<TreasuryAccountResponse> Change(Guid actor, Guid org, Guid company, Guid id, AccountChange request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var account = await db.TreasuryAccounts.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token)
                ?? throw new ApiException(404, "Treasury account not found.", "treasury_account");
            CheckVersion(account.Version, request.Version);
            if (account.Kind == TreasuryAccountKind.Bank && request.Active)
                await ValidateBankProfile(company, account.BankProfileId, token);
            var custodian = request.CustodianId ?? account.CustodianId;
            await ValidateCustodian(scope, account.Kind, custodian, token);
            account.CustodianId = custodian;
            account.Name = AccountingRules.Text(request.Name, 160, "Account name");
            account.Active = request.Active;
            account.Version = Guid.NewGuid();
            access.Audit(scope, actor, id, "TreasuryAccountChanged", AccountingRules.Text(request.Reason, 500, "Reason"));
            return View(account);
        }, ct);

    private async Task ValidateBankProfile(Guid company, Guid? id, CancellationToken ct)
    {
        if (!await db.Banks.AnyAsync(x => x.CompanyId == company && x.Id == id && x.Active, ct))
            throw new ApiException(400, "An active bank profile in this company is required.", "bank_profile");
    }

    private async Task ValidateCustodian(Company scope, TreasuryAccountKind kind, Guid? id, CancellationToken ct)
    {
        if (kind != TreasuryAccountKind.PettyCash)
        {
            if (id.HasValue)
                throw new ApiException(400, "Custodians apply to petty cash accounts.", "custodian");
            return;
        }
        if (!id.HasValue || !await db.OrganizationMembers.AnyAsync(x => x.OrganizationId == scope.OrganizationId && x.UserId == id &&
            (x.Role == OrganizationRole.Owner || db.CompanyMembers.Any(m => m.CompanyId == scope.Id && m.UserId == id && m.Role == CompanyRole.Manager)), ct))
            throw new ApiException(400, "Select a current company financial writer as custodian.", "custodian");
    }

    public async Task<PageResult<TreasuryAccountResponse>> Search(Guid actor, Guid org, Guid company, TreasuryAccountKind? kind, bool? active,
        string? search, int page, int size, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        AccountingRules.Page(page, size);
        AccountingRules.OptionalText(search, 100);
        var query = db.TreasuryAccounts.AsNoTracking().Where(x => x.CompanyId == company);
        if (kind.HasValue) query = query.Where(x => x.Kind == kind);
        if (active.HasValue) query = query.Where(x => x.Active == active);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => EF.Functions.ILike(x.Code, $"%{search}%") || EF.Functions.ILike(x.Name, $"%{search}%"));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Code).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new(rows.Select(View).ToList(), total, page, size);
    }

    public async Task<IReadOnlyList<AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.AccountingAudit.AsNoTracking().Where(x => x.CompanyId == company && x.EntityId == id).OrderBy(x => x.At)
            .Select(x => new AuditResponse(x.Id, x.EntityId, x.Action, x.ActorId, x.At, x.Reason)).ToListAsync(ct);
    }

    internal static void CheckVersion(Guid stored, Guid supplied)
    {
        if (stored != supplied) throw new ApiException(409, "The resource changed. Reload and retry.", "version_conflict");
    }

    internal static TreasuryAccountResponse View(TreasuryAccount account) => new(account.Id, account.Kind, account.Code, account.Name,
        account.Currency, account.LedgerAccountId, account.BankProfileId, account.AccountMask, account.IbanMask, account.CustodianId, account.Active, account.Version);

    private static string? Mask(string? value) => value is null ? null : "****" + value[^4..];
    private static string? Digest(string? value) => value is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? NormalizeIdentifier(string? input, bool iban)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        if (input.Length > 120)
            throw new ApiException(400, "Bank identifier is too long.", "bank_identifier");
        var value = string.Concat(input.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        if (!Regex.IsMatch(value, iban ? @"\A[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\z" : @"\A[A-Z0-9]{4,64}\z"))
            throw new ApiException(400, "Invalid bank identifier format.", "bank_identifier");
        if (!iban) return value;
        var remainder = 0;
        foreach (var ch in value[4..] + value[..4])
            remainder = char.IsAsciiDigit(ch) ? (remainder * 10 + ch - '0') % 97 : (remainder * 100 + ch - 'A' + 10) % 97;
        if (remainder != 1)
            throw new ApiException(400, "Invalid IBAN checksum.", "bank_iban");
        return value;
    }
}
