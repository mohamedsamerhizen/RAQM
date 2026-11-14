using RAQM.Api.Entities.Organizations;
namespace RAQM.Api.Interfaces.Accounting;

public interface IFinancialTransaction
{
    Task<T> Execute<T>(Guid actor,Guid organization,Guid company,
        Func<Company,CancellationToken,Task<T>> operation,CancellationToken ct);
}
