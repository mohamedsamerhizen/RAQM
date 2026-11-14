using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.TaxAccounting;
using RAQM.Api.Entities.RegulatoryDocuments;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.RegulatoryDocuments;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.TaxJurisdictions;
using static RAQM.Api.Services.TaxJurisdictions.TaxConfigurationService;

namespace RAQM.Api.Services.RegulatoryDocuments;

public sealed record StructuredTaxLine(int Number, Guid LineId, Guid? OriginalLineId, string Description, decimal Net,
    decimal Tax, decimal Total, Guid? RateId, TaxTreatment Treatment, Guid RuleId);
public sealed record StructuredTaxInvoice(Guid SourceId, Guid JournalId, string Number, DateOnly IssueDate, string Currency,
    string Seller, string SellerTaxNumber, string SellerCountry, string Buyer, string? BuyerTaxNumber, Guid? OriginalDocumentId,
    string? OriginalNumber, decimal Net, decimal Tax, decimal Total, StructuredTaxLine[] Lines);

public sealed class RaqmInvoiceSchema : ITaxInvoiceSchema
{
    public string Identifier => "raqm.tax-invoice.v1";
    public void Validate(StructuredTaxInvoice invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice.Number) || string.IsNullOrWhiteSpace(invoice.Seller) ||
            string.IsNullOrWhiteSpace(invoice.SellerTaxNumber) || string.IsNullOrWhiteSpace(invoice.Buyer) ||
            invoice.OriginalDocumentId.HasValue != !string.IsNullOrWhiteSpace(invoice.OriginalNumber) || invoice.Lines.Length == 0 ||
            invoice.Lines.Select(x => x.Number).Distinct().Count() != invoice.Lines.Length ||
            invoice.Lines.Any(x => x.Net < 0 || x.Tax < 0 || x.Net + x.Tax != x.Total || x.RuleId == Guid.Empty) ||
            invoice.Net != invoice.Lines.Sum(x => x.Net) || invoice.Tax != invoice.Lines.Sum(x => x.Tax) ||
            invoice.Total != invoice.Lines.Sum(x => x.Total))
            throw Conflict("The structured invoice lacks required identity, reference or monetary conservation.");
        var units = CurrencyPolicy.Units(invoice.Currency);
        CurrencyPolicy.Validate(invoice.Total, units);
    }
}

public sealed class TaxInvoiceService(RaqmDbContext db, IFinancialTransaction transaction, FinancialAccess access, ITaxInvoiceSchema schema, TimeProvider clock)
{
    public Task<RegulatoryDocument> Create(Guid actor, Guid org, Guid company, Guid documentId, CancellationToken ct) =>
        transaction.Execute(actor, org, company, async (scope, token) =>
        {
            var prior = await db.Set<RegulatoryDocument>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.DocumentId == documentId && x.Version == 1, token);
            if (prior is not null) return prior;
            var document = await db.TradeDocuments.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.Id == documentId && x.State == TradeState.Posted && (x.Kind == TradeKind.Invoice || x.Kind == TradeKind.CustomerCredit), token) ?? throw Missing();
            var basisId = document.OriginalDocumentId ?? document.Id;
            var evidence = await db.Set<CommercialTaxEvidence>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.DocumentId == basisId, token) ?? throw Conflict("The posted document lacks registration and tax rule evidence.");
            var profile = await db.Set<TaxRegistration>().AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == evidence.RegistrationId, token);
            var basis = document.Id == basisId ? document : await db.TradeDocuments.AsNoTracking().Include(x => x.Lines)
                .SingleAsync(x => x.CompanyId == company && x.Id == basisId, token);
            var quote = JsonSerializer.Deserialize<TaxQuote>(evidence.SnapshotJson)!;
            var originals = basis.Lines.ToDictionary(x => x.Id, x => x.Number);
            var lines = document.Lines.OrderBy(x => x.Number).Select(x =>
            {
                var number = x.OriginalLineId is { } source ? originals[source] : x.Number;
                var applied = quote.Lines.Single(q => q.Number == number).Components.Single();
                return new StructuredTaxLine(x.Number, x.Id, x.OriginalLineId, x.Description, x.NetAmount, x.TaxAmount,
                    x.Total, x.TaxRateId, applied.Treatment, applied.RuleId);
            }).ToArray();
            var invoice = new StructuredTaxInvoice(document.Id, document.JournalId!.Value, document.Number!, document.DocumentDate, document.Currency,
                quote.SellerName, profile.Number, profile.Country, document.PartyName, document.PartyTaxIdentifier, document.OriginalDocumentId,
                document.OriginalDocumentId.HasValue ? basis.Number : null, document.NetTotal, document.TaxTotal, document.Total, lines);
            schema.Validate(invoice);
            var row = Scoped(scope, new RegulatoryDocument { RegistrationId = profile.Id, DocumentId = document.Id,
                OriginalDocumentId = document.OriginalDocumentId, Schema = schema.Identifier, ContentJson = JsonSerializer.Serialize(invoice),
                ContentHash = TradeRules.Hash(invoice), CreatedBy = actor, CreatedAt = AccountingRules.StoredUtcInstant(clock.GetUtcNow()) });
            db.Add(row);
            access.Audit(scope, actor, row.Id, "StructuredTaxInvoiceIssued");
            return row;
        }, ct);

    public async Task<RegulatoryDocument> Get(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.Set<RegulatoryDocument>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw Missing();
    }
}
