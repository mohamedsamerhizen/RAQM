using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.Organizations;
namespace RAQM.Api.Data.Configurations.Organizations;

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b) { b.ToTable("Tenants", "organizations"); b.HasKey(x => x.Id); }
}
public sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> b)
    {
        b.ToTable("Organizations", "organizations"); b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.HasIndex(x => x.TenantId).IsUnique();
        b.Property(x => x.Name).HasMaxLength(160);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class OrganizationMemberConfiguration : IEntityTypeConfiguration<OrganizationMember>
{
    public void Configure(EntityTypeBuilder<OrganizationMember> b)
    {
        b.ToTable("Members", "organizations"); b.HasKey(x => new { x.OrganizationId, x.UserId });
        b.HasAlternateKey(x => new { x.TenantId, x.OrganizationId, x.UserId });
        b.HasOne<Organization>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrganizationId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
    }
}
public sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.ToTable("Companies", "organizations", t =>
        {
            t.HasCheckConstraint("CK_Company_Fiscal", "\"FiscalYearStartMonth\" BETWEEN 1 AND 12 AND \"FiscalYearStartDay\" BETWEEN 1 AND CASE WHEN \"FiscalYearStartMonth\" = 2 THEN 28 WHEN \"FiscalYearStartMonth\" IN (4,6,9,11) THEN 30 ELSE 31 END");
            t.HasCheckConstraint("CK_Company_Currency", "\"BaseCurrency\" ~ '^[A-Z]{3}$'");
        });
        b.HasKey(x => x.Id); b.HasAlternateKey(x => new { x.TenantId, x.OrganizationId, x.Id });
        b.HasIndex(x => new { x.OrganizationId, x.RegistrationNumber }).IsUnique();
        b.Property(x => x.Name).HasMaxLength(160); b.Property(x => x.RegistrationNumber).HasMaxLength(80);
        b.Property(x => x.BaseCurrency).HasMaxLength(3); b.Property(x => x.Version).IsConcurrencyToken();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrganizationId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class CompanyMemberConfiguration : IEntityTypeConfiguration<CompanyMember>
{
    public void Configure(EntityTypeBuilder<CompanyMember> b)
    {
        b.ToTable("CompanyMembers", "organizations"); b.HasKey(x => new { x.CompanyId, x.UserId });
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
        b.HasOne<Company>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrganizationId, x.CompanyId }).HasPrincipalKey(x => new { x.TenantId, x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrganizationMember>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrganizationId, x.UserId }).HasPrincipalKey(x => new { x.TenantId, x.OrganizationId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
    }
}


