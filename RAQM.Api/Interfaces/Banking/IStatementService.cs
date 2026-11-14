using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.Entities.Banking;

namespace RAQM.Api.Interfaces.Banking;

public interface IStatementService
{
    Task<StatementPreview> Preview(Guid actor, Guid org, Guid company, Guid bank, StatementRequest request, CancellationToken ct);
    Task<StatementSummary> Import(Guid actor, Guid org, Guid company, Guid bank, StatementRequest request, CancellationToken ct);
    Task<PageResult<StatementSummary>> List(Guid actor, Guid org, Guid company, Guid bank, int page, int size, CancellationToken ct);
    Task<IReadOnlyList<BankStatementLine>> Lines(Guid actor, Guid org, Guid company, Guid bank, Guid statement, CancellationToken ct);
}
