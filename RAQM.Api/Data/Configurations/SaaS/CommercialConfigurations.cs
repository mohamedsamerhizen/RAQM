using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.SaaS;

namespace RAQM.Api.Data.Configurations.SaaS;

internal static class CommercialMapping
{
    public static void Tenant<T>(EntityTypeBuilder<T> b, string table) where T : TenantCommercialRecord
    {
        b.ToTable(table, "saas");
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }

    public static void Link<T,U>(EntityTypeBuilder<T> b, string field) where T : TenantCommercialRecord where U : TenantCommercialRecord =>
        b.HasOne<U>().WithMany().HasForeignKey("TenantId", field).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
}

public sealed class CommercialPlanConfiguration : IEntityTypeConfiguration<CommercialPlan>
{
    public void Configure(EntityTypeBuilder<CommercialPlan> b)
    {
        b.ToTable("Plans", "saas");
        b.HasIndex(x => new { x.Code, x.Version }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
        b.ToTable(t => t.HasCheckConstraint("CK_Plan_Limits", """ "Version">0 AND "Price">=0 AND "ExtraSeatPrice">=0 AND "Interval" IN(0,1) AND "CompanyLimit">0 AND "SeatLimit">0 AND "IncludedSeats" BETWEEN 0 AND "SeatLimit" AND "WarehouseLimit">=0 AND "IntegrationLimit">=0 AND "WebhookLimit">=0 AND "TrialDays" BETWEEN 0 AND 90 AND "GraceDays" BETWEEN 1 AND 30 """));
    }
}

public sealed class CommercialAccountConfiguration : IEntityTypeConfiguration<CommercialAccount>
{
    public void Configure(EntityTypeBuilder<CommercialAccount> b)
    {
        CommercialMapping.Tenant(b, "Accounts");
        b.HasIndex(x => x.TenantId).IsUnique();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrganizationId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Company>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrganizationId, x.FirstCompanyId }).HasPrincipalKey(x => new { x.TenantId, x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PortabilityExport>().WithMany().HasForeignKey(x => new { x.TenantId, x.RetainedExportId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_Commercial_Account", """ "State" BETWEEN 0 AND 2 """));
    }
}

public sealed class CommercialSubscriptionConfiguration : IEntityTypeConfiguration<CommercialSubscription>
{
    public void Configure(EntityTypeBuilder<CommercialSubscription> b)
    {
        CommercialMapping.Tenant(b, "Subscriptions");
        b.HasIndex(x => x.TenantId).IsUnique();
        b.HasIndex(x => x.End);
        b.HasOne<CommercialPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CommercialPlan>().WithMany().HasForeignKey(x => x.NextPlanId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_Subscription_Window", """ "Start"<"End" AND "Cycle">=0 """));
    }
}

public sealed class ProvisioningReceiptConfiguration : IEntityTypeConfiguration<ProvisioningReceipt>
{
    public void Configure(EntityTypeBuilder<ProvisioningReceipt> b)
    {
        CommercialMapping.Tenant(b, "Provisioning");
        b.Property(x => x.RequestKey).HasMaxLength(100);
        b.HasIndex(x => new { x.RequesterId, x.RequestKey }).IsUnique();
        b.HasIndex(x => x.RequesterId).HasFilter(""" "Trial" """).IsUnique();
    }
}

public sealed class CommercialInvoiceConfiguration : IEntityTypeConfiguration<CommercialInvoice>
{
    public void Configure(EntityTypeBuilder<CommercialInvoice> b)
    {
        CommercialMapping.Tenant(b, "Invoices");
        CommercialMapping.Link<CommercialInvoice, CommercialSubscription>(b, nameof(CommercialInvoice.SubscriptionId));
        b.HasOne<CommercialPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.RequestKey).HasMaxLength(100);
        b.Property(x => x.Lines).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.RequestKey }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_Commercial_Invoice", """ "From"<"To" AND "Total">=0 AND "IssuedAt"<="DueAt" """));
    }
}

public sealed class CommercialMovementConfiguration : IEntityTypeConfiguration<CommercialMovement>
{
    public void Configure(EntityTypeBuilder<CommercialMovement> b)
    {
        CommercialMapping.Tenant(b, "Movements");
        CommercialMapping.Link<CommercialMovement, CommercialInvoice>(b, nameof(CommercialMovement.InvoiceId));
        CommercialMapping.Link<CommercialMovement, CommercialMovement>(b, nameof(CommercialMovement.BasisId));
        b.Property(x => x.Reference).HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.Reference }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_Commercial_Movement", """ "Kind" BETWEEN 0 AND 2 AND "Amount">0 AND ("Kind"=1)=("BasisId" IS NOT NULL) AND ("VerifiedAt" IS NULL)=("VerifiedBy" IS NULL) """));
    }
}

public sealed class CommercialEventConfiguration : IEntityTypeConfiguration<CommercialEvent>
{
    public void Configure(EntityTypeBuilder<CommercialEvent> b)
    {
        CommercialMapping.Tenant(b, "Events");
        b.HasIndex(x => new { x.TenantId, x.At });
    }
}

public sealed class PortabilityExportConfiguration : IEntityTypeConfiguration<PortabilityExport>
{
    public void Configure(EntityTypeBuilder<PortabilityExport> b)
    {
        CommercialMapping.Tenant(b, "Exports");
        b.HasIndex(x => new { x.TenantId, x.ExpiresAt });
    }
}

public sealed class CommercialCaseConfiguration : IEntityTypeConfiguration<CommercialCase>
{
    public void Configure(EntityTypeBuilder<CommercialCase> b)
    {
        CommercialMapping.Tenant(b, "Cases");
        b.HasIndex(x => new { x.TenantId, x.OpenedAt });
        b.HasOne<PortabilityExport>().WithMany().HasForeignKey(x => new { x.TenantId, x.ExportId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
