using RAQM.Api.Entities.TaxAccounting;

namespace RAQM.Api.Entities.RegulatoryDocuments;

public sealed class RegulatoryDocument : TaxRecord
{
    public Guid RegistrationId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid? OriginalDocumentId { get; set; }
    public int Version { get; set; } = 1;
    public string Schema { get; set; } = "";
    public string ContentJson { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
