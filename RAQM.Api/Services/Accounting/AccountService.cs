using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;

namespace RAQM.Api.Services.Accounting;
public sealed class AccountService(RaqmDbContext db, FinancialAccess access) : IAccountService
{
    public async Task<CurrencyPolicyResponse> Policy(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return new(CurrencyPolicy.Version, CurrencyPolicy.Supported);
    }

    private async Task Apply(LedgerAccount account, AccountRequest r, Guid actor, CancellationToken ct)
    {
        if (r is null || !Enum.IsDefined(r.Classification))
            throw new ApiException(400, "Invalid account classification.", "account_class");
        var code = AccountingRules.Text(r.Code, 32, "Code").ToUpperInvariant();
        if (!Regex.IsMatch(code, @"^[A-Z0-9][A-Z0-9._-]{0,31}$", RegexOptions.CultureInvariant))
            throw new ApiException(400, "Account code may contain letters, digits, dot, underscore and dash.", "account_code");
        var accounts = await db.Accounts.AsNoTracking().Where(x => x.CompanyId == account.CompanyId).ToListAsync(ct);
        if (accounts.Any(x => x.Id != account.Id && x.Code == code))
            throw new ApiException(409, "Account code already exists in this company.", "account_duplicate");
        AccountingRules.Hierarchy(account.Id, r.ParentId, accounts.ToDictionary(x => x.Id, x => x.ParentId));
        if (r.ParentId is { } id)
        {
            var parent = accounts.Single(x => x.Id == id);
            if (parent.IsPosting || !parent.IsActive || parent.Classification != r.Classification)
                throw new ApiException(400, "Parent must be active, non-posting and of the same classification.", "account_parent");
        }

        var children = accounts.Where(x => x.ParentId == account.Id).ToList();
        if((await db.TradingAccounts.AnyAsync(x=>x.CompanyId==account.CompanyId&&x.AccountId==account.Id,ct) ||
            await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == account.CompanyId && x.LedgerAccountId == account.Id, ct) ||
            await db.AssetControls.AnyAsync(x => x.CompanyId == account.CompanyId && x.AccountId == account.Id, ct) ||
            await db.AssetCategories.AnyAsync(x => x.CompanyId == account.CompanyId &&
                (x.DepreciationExpenseId == account.Id || x.ImpairmentLossId == account.Id || x.GainId == account.Id || x.LossId == account.Id), ct)) &&
            (account.Classification!=r.Classification||account.IsContra!=r.IsContra||account.IsPosting!=r.IsPosting))
            throw new ApiException(409,"Mapped account classification/posting semantics are protected.","mapped_account_locked");
        if ((account.Classification != r.Classification || account.IsContra != r.IsContra || account.IsPosting != r.IsPosting || !r.IsActive) &&
            await db.InventoryAccounts.AnyAsync(x => x.CompanyId == account.CompanyId && x.AccountId == account.Id, ct))
            throw new ApiException(409, "Mapped inventory accounts must retain their posting semantics and remain active.", "mapped_account_locked");
        if ((account.Classification != r.Classification || account.IsContra != r.IsContra || account.IsPosting != r.IsPosting) &&
            (await db.BudgetAllocations.AnyAsync(x => x.CompanyId == account.CompanyId && x.AccountId == account.Id, ct) ||
             await db.BudgetPolicies.AnyAsync(x => x.CompanyId == account.CompanyId && x.AccountId == account.Id, ct)))
            throw new ApiException(409, "Budgeted accounts must retain their posting semantics.", "mapped_account_locked");
        if ((account.Classification != r.Classification || account.IsContra != r.IsContra || account.IsPosting != r.IsPosting || !r.IsActive) &&
            (await db.FxAccounts.AnyAsync(x => x.CompanyId == account.CompanyId && x.AccountId == account.Id, ct) ||
             await db.FxPositions.AnyAsync(x => x.CompanyId == account.CompanyId && x.AccountId == account.Id &&
                x.Kind == Entities.MultiCurrency.FxPositionKind.Bank, ct)))
            throw new ApiException(409, "FX accounts must retain active posting semantics.", "mapped_account_locked");
        if ((r.IsPosting && children.Count > 0) || (!r.IsActive && children.Any(x => x.IsActive)) || children.Any(x => x.Classification != r.Classification))
            throw new ApiException(400, "Account settings conflict with its children.", "account_children");
        var subtree = AccountingRules.Descendants(account.Id, accounts.ToDictionary(x => x.Id, x => x.ParentId)).ToArray();
        var used = await db.JournalLines.AnyAsync(l => l.CompanyId == account.CompanyId && subtree.Contains(l.AccountId) && db.Journals.Any(j => j.Id == l.JournalId && j.State == JournalState.Posted), ct);
        if (used && (account.Code != code || account.Classification != r.Classification || account.IsContra != r.IsContra || account.IsPosting != r.IsPosting || account.ParentId != r.ParentId))
            throw new ApiException(409, "Structural account settings are frozen after posting.", "account_used");
        account.Code = code;
        account.Name = AccountingRules.Text(r.Name, 160, "Name");
        AccountingRules.OptionalText(r.Description, 1000);
        account.Description = r.Description;
        account.Classification = r.Classification;
        account.IsContra = r.IsContra;
        account.NormalBalance = AccountingRules.Normal(r.Classification, r.IsContra);
        account.ParentId = r.ParentId;
        account.IsActive = r.IsActive;
        account.IsPosting = r.IsPosting;
        account.UpdatedBy = actor;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        account.Version = Guid.NewGuid();
    }

    public async Task<AccountResponse> Create(Guid actor, Guid org, Guid company, AccountRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await access.Lock(actor, org, company, false, ct);
        CurrencyPolicy.Units(c.BaseCurrency);
        var account = new LedgerAccount
        {
            TenantId = c.TenantId,
            OrganizationId = org,
            CompanyId = company,
            CreatedBy = actor
        };
        await Apply(account, r, actor, ct);
        db.Accounts.Add(account);
        access.Audit(c, actor, account.Id, "AccountCreated");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AccountResponse.From(account);
    }

    public async Task<AccountResponse> Update(Guid actor, Guid org, Guid company, Guid id, UpdateAccountRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await access.Lock(actor, org, company, false, ct);
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw new ApiException(404, "Account not found.", "account_not_found");
        if (account.Version != r.Version)
            throw new ApiException(409, "Account version changed.", "version_conflict");
        await Apply(account, r.Settings, actor, ct);
        access.Audit(c, actor, id, "AccountUpdated");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return AccountResponse.From(account);
    }

    public async Task<PageResult<AccountResponse>> Search(Guid actor, Guid org, Guid company, string? search, AccountClass? classification, bool? active, bool? posting, int page, int pageSize, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        AccountingRules.Page(page, pageSize);
        if (classification.HasValue && !Enum.IsDefined(classification.Value))
            throw new ApiException(400, "Invalid account classification.", "account_class");
        AccountingRules.OptionalText(search, 100);
        var q = db.Accounts.AsNoTracking().Where(x => x.CompanyId == company);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(x => EF.Functions.ILike(x.Code, "%" + search.Trim() + "%") || EF.Functions.ILike(x.Name, "%" + search.Trim() + "%"));
        if (classification.HasValue)
            q = q.Where(x => x.Classification == classification);
        if (active.HasValue)
            q = q.Where(x => x.IsActive == active);
        if (posting.HasValue)
            q = q.Where(x => x.IsPosting == posting);
        var count = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.Code).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(rows.Select(AccountResponse.From).ToList(), count, page, pageSize);
    }

    public async Task<AccountResponse> Get(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return AccountResponse.From(await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw new ApiException(404, "Account not found.", "account_not_found"));
    }

    public async Task<IReadOnlyList<AccountResponse>> Initialize(Guid actor, Guid org, Guid company, string template, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await access.Lock(actor, org, company, false, ct);
        CurrencyPolicy.Units(c.BaseCurrency);
        if (template != "basic-v1")
            throw new ApiException(400, "Unknown account template.", "account_template");
        if (await db.Accounts.AnyAsync(x => x.CompanyId == company, ct))
            throw new ApiException(409, "Initialization requires an empty chart.", "chart_exists");
        var result = new List<LedgerAccount>();
        var items = new[]
        {
            ("1000", "Assets", AccountClass.Asset, "1100", "Cash"),
            ("2000", "Liabilities", AccountClass.Liability, "2100", "Accounts payable"),
            ("3000", "Equity", AccountClass.Equity, "3100", "Opening equity"),
            ("4000", "Revenue", AccountClass.Revenue, "4100", "Sales revenue"),
            ("5000", "Expenses", AccountClass.Expense, "5100", "Operating expenses")
        };
        foreach (var(code, name, type, childCode, childName)in items)
        {
            var parent = new LedgerAccount
            {
                TenantId = c.TenantId,
                OrganizationId = org,
                CompanyId = company,
                Code = code,
                Name = name,
                Classification = type,
                NormalBalance = AccountingRules.Normal(type, false),
                CreatedBy = actor,
                UpdatedBy = actor
            };
            var child = new LedgerAccount
            {
                TenantId = c.TenantId,
                OrganizationId = org,
                CompanyId = company,
                Code = childCode,
                Name = childName,
                Classification = type,
                NormalBalance = parent.NormalBalance,
                IsPosting = true,
                ParentId = parent.Id,
                CreatedBy = actor,
                UpdatedBy = actor
            };
            result.Add(parent);
            result.Add(child);
        }

        db.Accounts.AddRange(result);
        access.Audit(c, actor, company, "ChartInitialized", "basic-v1");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result.Select(AccountResponse.From).ToList();
    }
}
