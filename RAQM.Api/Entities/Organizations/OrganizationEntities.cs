namespace RAQM.Api.Entities.Organizations;

public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedBy { get; set; }
}
public enum OrganizationRole { Owner, Member }
public enum CompanyRole { Manager, Reader }
public sealed class OrganizationMember
{
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public OrganizationRole Role { get; set; }
}
public sealed class Company
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string RegistrationNumber { get; set; } = "";
    public string BaseCurrency { get; set; } = "";
    public int FiscalYearStartMonth { get; set; } = 1;
    public int FiscalYearStartDay { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}
public sealed class CompanyMember
{
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public CompanyRole Role { get; set; }
}

