using RAQM.Api.Services.RegulatoryDocuments;

namespace RAQM.Api.Interfaces.RegulatoryDocuments;

public interface ITaxInvoiceSchema
{
    string Identifier { get; }
    void Validate(StructuredTaxInvoice invoice);
}
