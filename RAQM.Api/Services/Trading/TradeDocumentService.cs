using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Inventory;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.MultiCurrency;

namespace RAQM.Api.Services.Trading;
public sealed class TradeDocumentService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions, IPostingEngine engine,
    TradingPolicy policy, ISettlementService settlements, FxQueries? fx = null) : ITradeDocumentService
{
    private async Task<TradeDocument> Find(Guid company, Guid id, bool tracking, CancellationToken ct)
    {
        IQueryable<TradeDocument> q = db.TradeDocuments.Where(x => x.CompanyId == company && x.Id == id).Include(x => x.Lines);
        return await (tracking ? q : q.AsNoTracking()).SingleOrDefaultAsync(ct) ?? throw new ApiException(404, "Document not found.", "document_not_found");
    }

    private async Task<(decimal Quantity, decimal Net, decimal Tax, decimal Discount)> UsedCredit(Guid line, Guid exclude, CancellationToken ct)
    {
        var rows = await (
            from l in db.TradeLines
            join d in db.TradeDocuments on l.DocumentId equals d.Id
            where l.OriginalLineId == line && d.Id != exclude && d.State == TradeState.Posted
            select new
            {
                l.Quantity,
                l.NetAmount,
                l.TaxAmount,
                l.DiscountAmount
            }

        ).ToListAsync(ct);
        return (rows.Sum(x => x.Quantity), rows.Sum(x => x.NetAmount), rows.Sum(x => x.TaxAmount), rows.Sum(x => x.DiscountAmount));
    }

    private async Task LineAccount(PartyKind side, LedgerAccount a, CancellationToken ct)
    {
        if ((side == PartyKind.Customer && a.Classification != AccountClass.Revenue) || (side == PartyKind.Supplier && a.Classification is not (AccountClass.Expense or AccountClass.Asset)) || await db.TradingAccounts.AnyAsync(x => x.CompanyId == a.CompanyId && x.AccountId == a.Id && x.Role != AccountRole.Revenue && x.Role != AccountRole.Expense, ct))
            throw new ApiException(400, "Use revenue or suitable non-inventory expense/asset categories, not protected/cash controls.", "line_account");
    }

    private async Task Apply(Company c, Guid actor, TradeDocument d, TradeDocumentRequest r, CancellationToken ct)
    {
        if (r is null || !Enum.IsDefined(r.Kind) || r.Lines is null || r.Lines.Count is < 1 or > 300 || r.Lines.Any(x => x is null))
            throw new ApiException(400, "Choose a document kind and 1..300 valid lines.", "document_lines");
        var party = await db.TradingParties.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == c.Id && x.Id == r.PartyId, ct) ?? throw new ApiException(404, "Party not found.", "party_not_found");
        if (party.Kind != TradeRules.Side(r.Kind) || (!party.IsActive && !TradeRules.IsCredit(r.Kind)))
            throw new ApiException(400, "Party kind/status does not permit this document.", "document_party");
        if (r.DocumentDate.Year is < 1900 or > 9998)
            throw new ApiException(400, "Document date must be 1900..9998.", "document_date");
        var map = await policy.RequiredMappings(c.Id, ct);
        if (party.Currency != c.BaseCurrency || party.ControlAccountId != map[party.Kind == PartyKind.Customer ? AccountRole.Receivable : AccountRole.Payable])
            throw new ApiException(409, "Party currency/control mapping changed.", "control_account");
        var due = r.DueDate ?? r.DocumentDate.AddDays(party.PaymentTermsDays);
        if (due < r.DocumentDate || r.ScheduledPaymentDate < r.DocumentDate)
            throw new ApiException(400, "Due/scheduled dates cannot precede document date.", "document_dates");
        AccountingRules.OptionalText(r.ExternalReference, 160);
        var supplierKey = r.Kind == TradeKind.Bill ? AccountingRules.Text(r.ExternalReference, 160, "Supplier invoice reference").ToUpperInvariant() : null;
        if (supplierKey is not null && await db.TradeDocuments.AnyAsync(x => x.CompanyId == c.Id && x.PartyId == party.Id && x.Kind == TradeKind.Bill && x.State != TradeState.Cancelled && x.SupplierReferenceKey == supplierKey && x.Id != d.Id, ct))
            throw new ApiException(409, "Supplier invoice reference already exists.", "supplier_invoice_duplicate");
        if (TradeRules.IsOpening(r.Kind) && await db.TradeDocuments.AnyAsync(x => x.CompanyId == c.Id && x.PartyId == party.Id && x.Kind == r.Kind && x.State != TradeState.Cancelled && x.Id != d.Id, ct))
            throw new ApiException(409, "Party opening document already exists.", "opening_duplicate");
        TradeDocument? original = null;
        if (TradeRules.IsCredit(r.Kind))
        {
            if (!r.OriginalDocumentId.HasValue)
                throw new ApiException(400, "Credit notes require an original document.", "credit_original");
            original = await Find(c.Id, r.OriginalDocumentId.Value, false, ct);
            if (original.State != TradeState.Posted || original.PartyId != party.Id || TradeRules.IsCredit(original.Kind) || TradeRules.Side(original.Kind) != party.Kind)
                throw new ApiException(400, "Credit original must be the same party's posted invoice/bill.", "credit_original");
            if (r.Lines.Select(x => x.OriginalLineId).Distinct().Count() != r.Lines.Count)
                throw new ApiException(400, "Use one credit line per original line.", "credit_lines");
        }
        else if (r.OriginalDocumentId.HasValue)
            throw new ApiException(400, "Only credits reference an original document.", "credit_original");
        d.PartyId = party.Id;
        d.Kind = r.Kind;
        d.OriginalDocumentId = r.OriginalDocumentId;
        d.ControlAccountId = party.ControlAccountId;
        d.Currency = c.BaseCurrency;
        d.MinorUnits = CurrencyPolicy.Units(c.BaseCurrency);
        d.PartyName = party.Name;
        d.PartyTaxIdentifier = party.TaxIdentifier;
        d.BillingAddress = party.BillingAddress;
        d.DocumentDate = r.DocumentDate;
        d.DueDate = due;
        d.ScheduledPaymentDate = r.ScheduledPaymentDate;
        d.Description = AccountingRules.Text(r.Description, 1000, "Description");
        d.ExternalReference = r.ExternalReference;
        d.SupplierReferenceKey = supplierKey;
        var lines = new List<TradeDocumentLine>();
        foreach (var input in r.Lines)
        {
            var l = new TradeDocumentLine
            {
                TenantId = c.TenantId,
                OrganizationId = c.OrganizationId,
                CompanyId = c.Id,
                DocumentId = d.Id,
                Number = lines.Count + 1,
                Description = AccountingRules.Text(input.Description, 500, "Line description")
            };
            CalculatedLine amount;
            if (input.CancelUnissuedStock && r.Kind != TradeKind.CustomerCredit)
                throw new ApiException(400, "Use a customer credit to cancel unissued stock.", "stock_cancellation");
            if (input.StockItemId.HasValue && r.Kind != TradeKind.Invoice)
                throw new ApiException(400, "Stock intent is set on the original sales invoice.", "stock_intent");
            if (r.Kind == TradeKind.Invoice)
            {
                l.StockSalesFactor = 0;
                if (input.StockItemId is { } itemId)
                {
                    var item = await db.InventoryItems.AsNoTracking().SingleOrDefaultAsync(x =>
                        x.CompanyId == c.Id && x.Id == itemId && x.Kind == ItemKind.Stocked && x.IsActive, ct)
                        ?? throw new ApiException(400, "Select an active stocked item in this company.", "stock_intent");
                    StockValuation.Quantity(input.Quantity * item.SalesFactor);
                    l.StockItemId = item.Id;
                    l.StockSalesFactor = item.SalesFactor;
                }
            }
            if (original is not null)
            {
                var old = original.Lines.SingleOrDefault(x => x.Id == input.OriginalLineId) ?? throw new ApiException(400, "Credit line is outside its original document.", "credit_line");
                if ((input.AccountId.HasValue && input.AccountId != old.AccountId) || (input.TaxRateId.HasValue && input.TaxRateId != old.TaxRateId))
                    throw new ApiException(400, "Credit account/tax comes from the original immutable line.", "credit_line");
                if (input.CancelUnissuedStock && old.StockItemId is null)
                    throw new ApiException(400, "Cancellation requires a stocked sales invoice line.", "stock_cancellation");
                if (input.CancelUnissuedStock)
                    StockValuation.Quantity(input.Quantity * old.StockSalesFactor!.Value);
                l.CancelUnissuedStock = input.CancelUnissuedStock;
                var used = await UsedCredit(old.Id, d.Id, ct);
                amount = TradeRules.Credit(old, input.Quantity, used.Quantity, used.Net, used.Tax, used.Discount, c.BaseCurrency);
                l.OriginalLineId = old.Id;
                l.AccountId = old.AccountId;
                l.Quantity = input.Quantity;
                l.UnitPrice = old.UnitPrice;
                l.DiscountPercent = old.DiscountPercent;
                l.TaxRateId = old.TaxRateId;
                l.TaxAccountId = old.TaxAccountId;
                l.TaxPercent = old.TaxPercent;
                l.TaxInclusive = old.TaxInclusive;
            }
            else
            {
                l.AccountId = TradeRules.IsOpening(r.Kind) ? map[AccountRole.OpeningEquity] : input.AccountId ?? party.DefaultAccountId;
                if (input.OriginalLineId.HasValue)
                    throw new ApiException(400, "Original line is only valid for credits.", "credit_line");
                if (TradeRules.IsOpening(r.Kind) && (input.TaxRateId.HasValue || input.DiscountPercent != 0))
                    throw new ApiException(400, "Opening documents use equity without tax/discount.", "opening_document");
                var account = await policy.Account(c.Id, l.AccountId, ct);
                if (!TradeRules.IsOpening(r.Kind))
                    await LineAccount(party.Kind, account, ct);
                l.Quantity = input.Quantity;
                l.UnitPrice = input.UnitPrice;
                l.DiscountPercent = input.DiscountPercent;
                l.TaxInclusive = input.TaxInclusive;
                if (input.TaxRateId is { } taxId)
                {
                    var rate = await db.TradingTaxRates.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == c.Id && x.Id == taxId && x.EffectiveFrom <= r.DocumentDate && x.EffectiveTo >= r.DocumentDate, ct) ?? throw new ApiException(400, "Tax version is not effective for document date.", "tax_effective");
                    var code = await db.TradingTaxCodes.AsNoTracking().SingleAsync(x => x.CompanyId == c.Id && x.Id == rate.TaxCodeId, ct);
                    if (!code.IsActive)
                        throw new ApiException(400, "Tax code is inactive.", "tax_inactive");
                    l.TaxRateId = rate.Id;
                    l.TaxPercent = rate.RatePercent;
                    l.TaxAccountId = party.Kind == PartyKind.Customer ? code.OutputAccountId : code.InputAccountId;
                }
                else if (input.TaxInclusive)
                    throw new ApiException(400, "Inclusive pricing requires a tax version.", "tax_required");
                amount = TradeRules.Calculate(l.Quantity, l.UnitPrice, l.DiscountPercent, l.TaxPercent, l.TaxInclusive, c.BaseCurrency);
            }

            l.DiscountAmount = amount.Discount;
            l.NetAmount = amount.Net;
            l.TaxAmount = amount.Tax;
            l.Total = amount.Total;
            lines.Add(l);
        }

        d.Lines = lines;
        d.NetTotal = lines.Sum(x => x.NetAmount);
        d.TaxTotal = lines.Sum(x => x.TaxAmount);
        d.Total = lines.Sum(x => x.Total);
        TradingPolicy.Positive(d.Total, c.BaseCurrency);
        d.UpdatedBy = actor;
        d.UpdatedAt = DateTimeOffset.UtcNow;
        d.Version = Guid.NewGuid();
    }

    private async Task Ready(Company c, TradeDocument d, CancellationToken ct, DateOnly? postingDate = null)
    {
        if (d.Kind == TradeKind.SupplierCredit && await db.Set<TaxOperation>().AnyAsync(x => x.CompanyId == c.Id &&
            x.BillId == d.OriginalDocumentId && x.Kind == TaxOperationKind.ReverseCharge && x.State == TaxEvidenceState.Posted &&
            !db.Set<TaxOperation>().Any(r => r.CompanyId == c.Id && r.BasisId == x.Id && r.Kind == TaxOperationKind.Correction &&
                r.State == TaxEvidenceState.Posted && (!postingDate.HasValue || r.Date <= postingDate.Value)), ct))
            throw new ApiException(409, "Correct the original self-assessment before approving or posting its supplier credit.", "tax_correction_required");
        var party = await db.TradingParties.AsNoTracking().SingleAsync(x => x.CompanyId == c.Id && x.Id == d.PartyId, ct);
        if (!party.IsActive && !TradeRules.IsCredit(d.Kind))
            throw new ApiException(409, "Party is inactive.", "party_inactive");
        if (d.Currency != c.BaseCurrency)
            throw new ApiException(409, "Document currency differs from company.", "currency_mismatch");
        foreach (var line in d.Lines)
        {
            if (TradeRules.IsCredit(d.Kind))
            {
                var original = await db.TradeLines.AsNoTracking().SingleAsync(x => x.CompanyId == c.Id && x.Id == line.OriginalLineId, ct);
                var used = await UsedCredit(original.Id, d.Id, ct);
                var expected = TradeRules.Credit(original, line.Quantity, used.Quantity, used.Net, used.Tax, used.Discount, c.BaseCurrency);
                if (expected.Net != line.NetAmount || expected.Tax != line.TaxAmount || expected.Discount != line.DiscountAmount)
                    throw new ApiException(409, "Credit residual changed; reopen and recalculate draft.", "credit_reprice");
            }
            else if (!TradeRules.IsOpening(d.Kind))
            {
                await LineAccount(party.Kind, await policy.Account(c.Id, line.AccountId, ct), ct);
                if (line.TaxRateId.HasValue && !await (
                    from rate in db.TradingTaxRates
                    join code in db.TradingTaxCodes on rate.TaxCodeId equals code.Id
                    where rate.CompanyId == c.Id && rate.Id == line.TaxRateId && code.IsActive
                    select rate.Id).AnyAsync(ct))
                    throw new ApiException(409, "Tax code is inactive.", "tax_inactive");
            }
        }

        AccountingRules.Lines(TradeRules.DocumentPosting(d), d.Currency, true);
    }

    public Task<TradeDocumentResponse> Create(Guid actor, Guid org, Guid company, TradeDocumentRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        var d = new TradeDocument
        {
            TenantId = c.TenantId,
            OrganizationId = org,
            CompanyId = company,
            CreatedBy = actor
        };
        await Apply(c, actor, d, r, t);
        db.TradeDocuments.Add(d);
        access.Audit(c, actor, d.Id, "TradeDraftCreated");
        await db.SaveChangesAsync(t);
        return View(d, await TradingSnapshot.Load(db, company, t));
    }, ct);
    public Task<TradeDocumentResponse> Update(Guid actor, Guid org, Guid company, Guid id, UpdateTradeDocumentRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        var d = await Find(company, id, true, t);
        if (await db.Set<CommercialTaxEvidence>().AnyAsync(x => x.CompanyId == company && x.DocumentId == id, t))
            throw new ApiException(409, "Cancel and recreate a document with frozen tax quote evidence.", "tax_quote_frozen");
        if (d.State != TradeState.Draft)
            throw new ApiException(409, "Only draft documents can be edited.", "document_immutable");
        if (d.Version != r.Version)
            throw new ApiException(409, "Document version changed.", "version_conflict");
        if (r.Document is null || r.Document.Kind != d.Kind || r.Document.PartyId != d.PartyId || r.Document.OriginalDocumentId != d.OriginalDocumentId)
            throw new ApiException(409, "Document party/kind/original identity cannot change.", "document_identity");
        db.TradeLines.RemoveRange(d.Lines);
        await db.SaveChangesAsync(t);
        await Apply(c, actor, d, r.Document, t);
        var replacementLines = d.Lines;
        d.Lines = [];
        // The database validates line tax snapshots using the persisted draft date.
        await db.SaveChangesAsync(t);
        d.Lines = replacementLines;
        db.TradeLines.AddRange(replacementLines);
        access.Audit(c, actor, id, "TradeDraftUpdated");
        await db.SaveChangesAsync(t);
        return View(d, await TradingSnapshot.Load(db, company, t));
    }, ct);
    public Task<TradeDocumentResponse> Transition(Guid actor, Guid org, Guid company, Guid id, string action, DocumentTransitionRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        var d = await Find(company, id, true, t);
        var reason = AccountingRules.Text(r.Reason, 500, "Reason");
        if (d.Version != r.Version)
            throw new ApiException(409, "Document version changed.", "version_conflict");
        if (action == "approve" && d.State == TradeState.Draft)
        {
            if (d.Lines.Any(x => x.CancelUnissuedStock))
            {
                await access.Resolve(actor, org, company, true, true, t);
                if (actor == d.CreatedBy || actor == d.UpdatedBy)
                    throw new ApiException(409, "Unissued stock cancellation requires independent Owner approval.", "stock_cancellation_approval");
            }
            await Ready(c, d, t);
            d.State = TradeState.Approved;
            d.ApprovedBy = actor;
            d.ApprovedAt = DateTimeOffset.UtcNow;
        }
        else if (action == "reopen" && d.State == TradeState.Approved)
        {
            d.State = TradeState.Draft;
            d.ApprovedBy = null;
            d.ApprovedAt = null;
        }
        else if (action == "cancel" && d.State is TradeState.Draft or TradeState.Approved)
            d.State = TradeState.Cancelled;
        else
            throw new ApiException(409, "Invalid transition; posted documents require credit notes.", "document_transition");
        d.UpdatedBy = actor;
        d.UpdatedAt = DateTimeOffset.UtcNow;
        d.Version = Guid.NewGuid();
        access.Audit(c, actor, id, $"Trade{action}", reason);
        await db.SaveChangesAsync(t);
        return View(d, await TradingSnapshot.Load(db, company, t));
    }, ct);
    public Task<TradeDocumentResponse> Post(Guid actor, Guid org, Guid company, Guid id, PostTradeDocumentRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        var d = await Find(company, id, true, t);
        var key = AccountingRules.Text(r.IdempotencyKey, 64, "Idempotency key");
        var hash = TradeRules.Hash(new { r.PostingDate, r.AllocateCreditToOriginal, Funding = r.ApplyFunding ?? [] });
        if (d.State == TradeState.Posted)
        {
            if (d.PostKey != key || d.PostRequestHash != hash)
                throw new ApiException(409, "Document already posted with different intent.", "document_posted");
            return View(d, await TradingSnapshot.Load(db, company, t));
        }

        if (d.State != TradeState.Approved || d.Version != r.Version)
            throw new ApiException(409, "Post an approved, current document version.", "document_approval");
        await Ready(c, d, t, r.PostingDate);
        if (d.OriginalDocumentId is { } originalId && await db.TradeDocuments.AnyAsync(x => x.CompanyId == company && x.Id == originalId && x.PostingDate > r.PostingDate, t))
            throw new ApiException(400, "Credit posting cannot precede its original document posting.", "credit_date");
        var sequence = (await db.TradeDocuments.Where(x => x.CompanyId == company && x.Kind == d.Kind && x.State == TradeState.Posted).MaxAsync(x => x.Sequence, t) ?? 0) + 1;
        var number = $"{TradeRules.Prefix(d.Kind)}-{sequence:D8}";
        var receipt = await engine.PostAsync(actor, new(new(c.TenantId, org, company), TradeRules.Module(d.Kind), d.Id, TradeRules.Key("Document", d.Id, key), r.PostingDate, c.BaseCurrency, TradeRules.DocumentPosting(d), null, d.DocumentDate, d.Description, number, r.AllowBackdated, r.Reason), t);
        d.State = TradeState.Posted;
        d.JournalId = receipt.JournalId;
        d.PostingDate = r.PostingDate;
        d.Sequence = sequence;
        d.Number = number;
        d.PostKey = key;
        d.PostRequestHash = hash;
        d.PostedBy = actor;
        d.PostedAt = receipt.PostedAt;
        d.UpdatedBy = actor;
        d.UpdatedAt = receipt.PostedAt;
        d.Version = Guid.NewGuid();
        access.Audit(c, actor, id, "TradeDocumentPosted", r.Reason);
        await db.SaveChangesAsync(t);
        if (TradeRules.IsCredit(d.Kind) && r.AllocateCreditToOriginal)
        {
            var s = await TradingSnapshot.Load(db, company, t);
            var original = s.Documents.Single(x => x.Id == d.OriginalDocumentId);
            var amount = Math.Min(d.Total, s.Outstanding(original, DateOnly.MaxValue));
            if (amount > 0)
                await settlements.Allocate(actor, org, company, new(FundingKind.CreditNote, d.Id, original.Id, amount, r.PostingDate, $"credit-{d.Id:N}", r.AllowBackdated, r.Reason), t);
        }

        if (r.ApplyFunding is { } funding)
        {
            if (TradeRules.IsCredit(d.Kind) || funding.Count > 100 || funding.Any(x => x is null))
                throw new ApiException(400, "Only debit documents accept up to 100 funding instructions.", "allocation_targets");
            for (var i = 0; i < funding.Count; i++)
                await settlements.Allocate(actor, org, company, new(funding[i].Kind, funding[i].FundingId, d.Id, funding[i].Amount, r.PostingDate, $"document-{d.Id:N}-{i}", r.AllowBackdated, r.Reason), t);
        }

        if (TradeRules.Side(d.Kind) == PartyKind.Customer && !TradeRules.IsCredit(d.Kind))
        {
            var party = await db.TradingParties.AsNoTracking().SingleAsync(x => x.Id == d.PartyId && x.CompanyId == company, t);
            var s = await TradingSnapshot.Load(db, company, t);
            if (party.CreditLimit is { } limit && Math.Max(s.ControlBalance(party.Id, DateOnly.MaxValue), 0) +
                await (fx ?? new FxQueries(db)).CustomerExposure(company, party.Id, t) > limit)
                throw new ApiException(409, "Customer credit limit exceeded; apply funding atomically or obtain an approved limit.", "credit_limit");
        }

        return View(d, await TradingSnapshot.Load(db, company, t));
    }, ct);
    public async Task<TradeDocumentResponse> Get(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return View(await Find(company, id, false, ct), await TradingSnapshot.Load(db, company, ct));
    }

    public async Task<PageResult<TradeDocumentResponse>> Search(Guid actor, Guid org, Guid company, Guid? party, TradeKind? kind, TradeState? state, DateOnly? dueBefore, string? search, int page, int pageSize, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        AccountingRules.Page(page, pageSize);
        AccountingRules.OptionalText(search, 100);
        if ((kind.HasValue && !Enum.IsDefined(kind.Value)) || (state.HasValue && !Enum.IsDefined(state.Value)))
            throw new ApiException(400, "Invalid document filters.", "document_filter");
        var q = db.TradeDocuments.AsNoTracking().Where(x => x.CompanyId == company);
        if (party.HasValue)
            q = q.Where(x => x.PartyId == party);
        if (kind.HasValue)
            q = q.Where(x => x.Kind == kind);
        if (state.HasValue)
            q = q.Where(x => x.State == state);
        if (dueBefore.HasValue)
            q = q.Where(x => x.DueDate <= dueBefore);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(x => EF.Functions.ILike(x.Description, "%" + search + "%") || (x.Number != null && x.Number == search) || (x.ExternalReference != null && EF.Functions.ILike(x.ExternalReference, "%" + search + "%")));
        var count = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.DocumentDate).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).Include(x => x.Lines).ToListAsync(ct);
        var snapshot = await TradingSnapshot.Load(db, company, ct);
        return new(rows.Select(d => View(d, snapshot)).ToList(), count, page, pageSize);
    }

    public static TradeDocumentResponse View(TradeDocument d, TradingSnapshot snapshot)
    {
        var outstanding = d.State == TradeState.Posted ? snapshot.Outstanding(d, DateOnly.MaxValue) : 0;
        var allocated = d.State == TradeState.Posted ? TradeRules.IsCredit(d.Kind) ? snapshot.Allocations.Where(x => x.CreditDocumentId == d.Id).Sum(x => TradeRules.AllocationSign(x) * x.Amount) : snapshot.AppliedTo(d.Id, DateOnly.MaxValue) : 0;
        var status = d.State != TradeState.Posted ? SettlementStatus.Unsettled : outstanding == 0 ? SettlementStatus.Settled : outstanding < d.Total ? SettlementStatus.Partial : SettlementStatus.Unsettled;
        return new(d.Id, d.PartyId, d.Kind, d.State, status, d.Number, d.DocumentDate, d.DueDate, d.ScheduledPaymentDate, d.PostingDate, d.Currency, d.PartyName, d.PartyTaxIdentifier, d.BillingAddress, d.Description, d.ExternalReference, d.OriginalDocumentId, d.NetTotal, d.TaxTotal, d.Total, allocated, outstanding, d.JournalId, d.Version, d.CreatedBy, d.ApprovedBy, d.PostedBy, d.CreatedAt, d.PostedAt, d.Lines.OrderBy(x => x.Number).Select(x => new TradeLineResponse(x.Id, x.Number, x.Description, x.AccountId, x.OriginalLineId, x.Quantity, x.UnitPrice, x.DiscountPercent, x.DiscountAmount, x.TaxRateId, x.TaxPercent, x.TaxInclusive, x.NetAmount, x.TaxAmount, x.Total, x.CancelUnissuedStock)).ToList());
    }
}
