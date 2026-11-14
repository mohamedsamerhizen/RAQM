using RAQM.Api.Entities.Organizations;
namespace RAQM.Api.DTOs.Organizations;

public sealed record OrganizationResponse(Guid Id, Guid TenantId, string Name, DateTimeOffset CreatedAt, Guid CreatedBy)
{
    public static OrganizationResponse From(Organization value) =>
        new(value.Id, value.TenantId, value.Name, value.CreatedAt, value.CreatedBy);
}

public sealed record CompanyResponse(
    Guid Id, Guid TenantId, Guid OrganizationId, string Name, string RegistrationNumber,
    string BaseCurrency, int FiscalYearStartMonth, int FiscalYearStartDay,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid CreatedBy, Guid UpdatedBy, Guid Version)
{
    public static CompanyResponse From(Company value) =>
        new(value.Id, value.TenantId, value.OrganizationId, value.Name, value.RegistrationNumber,
            value.BaseCurrency, value.FiscalYearStartMonth, value.FiscalYearStartDay,
            value.CreatedAt, value.UpdatedAt, value.CreatedBy, value.UpdatedBy, value.Version);
}
