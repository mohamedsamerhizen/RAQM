using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Consolidation;
using RAQM.Api.Entities.CorporateGroups;
using RAQM.Api.Entities.Intercompany;
using RAQM.Api.Entities.Organizations;

namespace RAQM.Api.Data.Configurations.CorporateGroups;

internal static class GroupConfiguration
{
    public static void Scope<T>(EntityTypeBuilder<T> b, string table) where T : class, ICompanyFinancialEntity
    {
        b.ToTable(table, "groups");
        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        Company(b, nameof(ICompanyFinancialEntity.CompanyId));
    }

    public static void Evidence<T>(EntityTypeBuilder<T> b, string table) where T : class, IGroupEvidence
    {
        Scope(b, table);
        b.HasAlternateKey(x => new { x.GroupId, x.Id });
        b.HasOne<CorporateGroup>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.GroupId }).HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }

    public static void Company<T>(EntityTypeBuilder<T> b, string company) where T : class =>
        b.HasOne<Company>().WithMany().HasForeignKey("TenantId", "OrganizationId", company)
            .HasPrincipalKey(nameof(Entities.Organizations.Company.TenantId), nameof(Entities.Organizations.Company.OrganizationId), "Id")
            .OnDelete(DeleteBehavior.Restrict);

    public static void Ledger<T, TTarget>(EntityTypeBuilder<T> b, string company, string key)
        where T : class where TTarget : class, ICompanyFinancialEntity =>
        b.HasOne<TTarget>().WithMany().HasForeignKey(company, key).HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

    public static void Group<T, TTarget>(EntityTypeBuilder<T> b, string key)
        where T : class, IGroupEvidence where TTarget : class, IGroupEvidence =>
        b.HasOne<TTarget>().WithMany().HasForeignKey("GroupId", key).HasPrincipalKey(x => new { x.GroupId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
}

public sealed class CorporateGroupConfiguration : IEntityTypeConfiguration<CorporateGroup>
{
    public void Configure(EntityTypeBuilder<CorporateGroup> b)
    {
        GroupConfiguration.Scope(b, "CorporateGroups");
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.ToTable(t => t.HasCheckConstraint("CK_Group_Dates", """ "RetiredOn" IS NULL OR "RetiredOn" > "StartsOn" """));
    }
}

public sealed class GroupOwnershipConfiguration : IEntityTypeConfiguration<GroupOwnership>
{
    public void Configure(EntityTypeBuilder<GroupOwnership> b)
    {
        GroupConfiguration.Evidence(b, "GroupOwnerships");
        GroupConfiguration.Company(b, nameof(GroupOwnership.InvestorId));
        GroupConfiguration.Company(b, nameof(GroupOwnership.InvesteeId));
        b.HasIndex(x => new { x.GroupId, x.InvesteeId, x.EffectiveOn }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_Ownership_Values",
            """ "InvestorId" <> "InvesteeId" AND "Ownership" BETWEEN 0 AND 100 AND "Voting" BETWEEN 0 AND 100 AND "Kind" BETWEEN 0 AND 4 """));
    }
}

public sealed class GroupAccountConfiguration : IEntityTypeConfiguration<GroupAccount>
{
    public void Configure(EntityTypeBuilder<GroupAccount> b)
    {
        GroupConfiguration.Evidence(b, "GroupAccounts");
        GroupConfiguration.Group<GroupAccount, GroupAccount>(b, nameof(GroupAccount.ParentId));
        b.HasIndex(x => new { x.GroupId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
    }
}

public sealed class GroupMappingConfiguration : IEntityTypeConfiguration<GroupMapping>
{
    public void Configure(EntityTypeBuilder<GroupMapping> b)
    {
        GroupConfiguration.Evidence(b, "GroupMappings");
        GroupConfiguration.Company(b, nameof(GroupMapping.MemberId));
        GroupConfiguration.Ledger<GroupMapping, LedgerAccount>(b, nameof(GroupMapping.MemberId), nameof(GroupMapping.LedgerAccountId));
        GroupConfiguration.Group<GroupMapping, GroupAccount>(b, nameof(GroupMapping.GroupAccountId));
        b.HasIndex(x => new { x.GroupId, x.MemberId, x.LedgerAccountId, x.EffectiveOn }).IsUnique();
    }
}

public sealed class IntercompanyPolicyConfiguration : IEntityTypeConfiguration<IntercompanyPolicy>
{
    public void Configure(EntityTypeBuilder<IntercompanyPolicy> b)
    {
        GroupConfiguration.Evidence(b, "IntercompanyPolicies");
        GroupConfiguration.Company(b, nameof(IntercompanyPolicy.CreditorId));
        GroupConfiguration.Company(b, nameof(IntercompanyPolicy.DebtorId));
        foreach (var key in new[] { nameof(IntercompanyPolicy.DueFromId), nameof(IntercompanyPolicy.RevenueId), nameof(IntercompanyPolicy.CreditorBankId) })
            GroupConfiguration.Ledger<IntercompanyPolicy, LedgerAccount>(b, nameof(IntercompanyPolicy.CreditorId), key);
        foreach (var key in new[] { nameof(IntercompanyPolicy.DueToId), nameof(IntercompanyPolicy.ExpenseId), nameof(IntercompanyPolicy.DebtorBankId) })
            GroupConfiguration.Ledger<IntercompanyPolicy, LedgerAccount>(b, nameof(IntercompanyPolicy.DebtorId), key);
        b.HasIndex(x => new { x.GroupId, x.CreditorId, x.DebtorId, x.EffectiveOn }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_PairPolicy_Companies", """ "CreditorId" <> "DebtorId" """));
    }
}

public sealed class IntercompanyPairConfiguration : IEntityTypeConfiguration<IntercompanyPair>
{
    public void Configure(EntityTypeBuilder<IntercompanyPair> b)
    {
        GroupConfiguration.Evidence(b, "IntercompanyPairs");
        GroupConfiguration.Company(b, nameof(IntercompanyPair.CreditorId));
        GroupConfiguration.Company(b, nameof(IntercompanyPair.DebtorId));
        GroupConfiguration.Group<IntercompanyPair, IntercompanyPolicy>(b, nameof(IntercompanyPair.PolicyId));
        GroupConfiguration.Group<IntercompanyPair, IntercompanyPair>(b, nameof(IntercompanyPair.BasisId));
        GroupConfiguration.Ledger<IntercompanyPair, JournalEntry>(b, nameof(IntercompanyPair.CreditorId), nameof(IntercompanyPair.CreditorJournalId));
        GroupConfiguration.Ledger<IntercompanyPair, JournalEntry>(b, nameof(IntercompanyPair.DebtorId), nameof(IntercompanyPair.DebtorJournalId));
        b.Property(x => x.PlanJson).HasColumnType("jsonb");
        b.Property(x => x.Reference).HasMaxLength(100);
        b.HasIndex(x => new { x.GroupId, x.Reference }).IsUnique();
        b.HasIndex(x => new { x.GroupId, x.BasisId, x.Kind });
        b.HasIndex(x => x.CreditorJournalId).IsUnique();
        b.HasIndex(x => x.DebtorJournalId).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_Pair_Amounts",
            """ "Amount" > 0 AND "State" BETWEEN 0 AND 4 AND "Kind" BETWEEN 0 AND 4 AND (("State" = 3 AND "CreditorJournalId" IS NOT NULL AND "DebtorJournalId" IS NOT NULL) OR ("State" <> 3 AND "CreditorJournalId" IS NULL AND "DebtorJournalId" IS NULL)) """));
        b.ToTable(t => t.HasCheckConstraint("CK_Pair_Interest",
            """ ("Kind" = 2 AND "InterestFrom" IS NOT NULL AND "InterestFrom" <= "Date" AND "InterestAnnualPercent" IS NOT NULL AND "InterestAnnualPercent" > 0 AND "InterestAnnualPercent" <= 100) OR ("Kind" <> 2 AND "InterestFrom" IS NULL AND "InterestAnnualPercent" IS NULL) """));
    }
}

public sealed class ReciprocalReviewConfiguration : IEntityTypeConfiguration<ReciprocalReview>
{
    public void Configure(EntityTypeBuilder<ReciprocalReview> b)
    {
        GroupConfiguration.Evidence(b, "ReciprocalReviews");
        GroupConfiguration.Group<ReciprocalReview, IntercompanyPolicy>(b, nameof(ReciprocalReview.PolicyId));
        b.Property(x => x.SnapshotJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.GroupId, x.PolicyId, x.Cutoff });
    }
}

public sealed class TranslationRateConfiguration : IEntityTypeConfiguration<GroupTranslationRate>
{
    public void Configure(EntityTypeBuilder<GroupTranslationRate> b)
    {
        GroupConfiguration.Evidence(b, "GroupTranslationRates");
        GroupConfiguration.Company(b, nameof(GroupTranslationRate.MemberId));
        GroupConfiguration.Ledger<GroupTranslationRate, LedgerAccount>(b, nameof(GroupTranslationRate.MemberId), nameof(GroupTranslationRate.EquityAccountId));
        b.Property(x => x.Rate).HasPrecision(24, 12);
        b.HasIndex(x => new { x.GroupId, x.MemberId, x.EquityAccountId, x.RateDate, x.EffectiveOn }).IsUnique().AreNullsDistinct(false);
        b.ToTable(t => t.HasCheckConstraint("CK_GroupRate_Positive", """ "Rate" > 0 """));
    }
}

public sealed class ConsolidationRunConfiguration : IEntityTypeConfiguration<ConsolidationRun>
{
    public void Configure(EntityTypeBuilder<ConsolidationRun> b)
    {
        GroupConfiguration.Evidence(b, "ConsolidationRuns");
        GroupConfiguration.Group<ConsolidationRun, ConsolidationRun>(b, nameof(ConsolidationRun.SupersedesId));
        GroupConfiguration.Group<ConsolidationRun, GroupAccount>(b, nameof(ConsolidationRun.TranslationReserveId));
        b.Property(x => x.SourceHash).HasMaxLength(64);
        b.Property(x => x.SnapshotJson).HasColumnType("jsonb");
        b.Property(x => x.AdjustmentsJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.GroupId, x.From, x.Cutoff, x.Version }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_GroupRun_State", """ "From" <= "Cutoff" AND "Version" > 0 AND "State" BETWEEN 0 AND 2 """));
    }
}
