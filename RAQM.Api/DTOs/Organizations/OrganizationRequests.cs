using RAQM.Api.Entities.Organizations;
namespace RAQM.Api.DTOs.Organizations;
public sealed record CreateOrganizationRequest(string Name);
public sealed record CompanyRequest(string Name, string RegistrationNumber, string BaseCurrency, int FiscalYearStartMonth, int FiscalYearStartDay);
public sealed record UpdateCompanyRequest(string Name, string BaseCurrency, int FiscalYearStartMonth, int FiscalYearStartDay, Guid Version);
public sealed record AddMemberRequest(Guid UserId);
public sealed record GrantCompanyRequest(Guid UserId, CompanyRole Role);

