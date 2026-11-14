using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CorporateConsolidationV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "groups");

            migrationBuilder.CreateTable(
                name: "CorporateGroups",
                schema: "groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    RetiredOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReportReaders = table.Column<Guid[]>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorporateGroups", x => x.Id);
                    table.UniqueConstraint("AK_CorporateGroups_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Group_Dates", " \"RetiredOn\" IS NULL OR \"RetiredOn\" > \"StartsOn\" ");
                    table.ForeignKey(
                        name: "FK_CorporateGroups_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupAccounts",
                schema: "groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Classification = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Cash = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupAccounts", x => x.Id);
                    table.UniqueConstraint("AK_GroupAccounts_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_GroupAccounts_GroupId_Id", x => new { x.GroupId, x.Id });
                    table.ForeignKey(
                        name: "FK_GroupAccounts_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupAccounts_CorporateGroups_CompanyId_GroupId",
                        columns: x => new { x.CompanyId, x.GroupId },
                        principalSchema: "groups",
                        principalTable: "CorporateGroups",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupAccounts_GroupAccounts_GroupId_ParentId",
                        columns: x => new { x.GroupId, x.ParentId },
                        principalSchema: "groups",
                        principalTable: "GroupAccounts",
                        principalColumns: new[] { "GroupId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupOwnerships",
                schema: "groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvestorId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvesteeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Ownership = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Voting = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupOwnerships", x => x.Id);
                    table.UniqueConstraint("AK_GroupOwnerships_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_GroupOwnerships_GroupId_Id", x => new { x.GroupId, x.Id });
                    table.CheckConstraint("CK_Ownership_Values", " \"InvestorId\" <> \"InvesteeId\" AND \"Ownership\" BETWEEN 0 AND 100 AND \"Voting\" BETWEEN 0 AND 100 AND \"Kind\" BETWEEN 0 AND 4 ");
                    table.ForeignKey(
                        name: "FK_GroupOwnerships_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupOwnerships_Companies_TenantId_OrganizationId_InvesteeId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.InvesteeId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupOwnerships_Companies_TenantId_OrganizationId_InvestorId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.InvestorId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupOwnerships_CorporateGroups_CompanyId_GroupId",
                        columns: x => new { x.CompanyId, x.GroupId },
                        principalSchema: "groups",
                        principalTable: "CorporateGroups",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupTranslationRates",
                schema: "groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    EquityAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    RateDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(24,12)", precision: 24, scale: 12, nullable: false),
                    PreparedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Evidence = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupTranslationRates", x => x.Id);
                    table.UniqueConstraint("AK_GroupTranslationRates_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_GroupTranslationRates_GroupId_Id", x => new { x.GroupId, x.Id });
                    table.CheckConstraint("CK_GroupRate_Positive", " \"Rate\" > 0 ");
                    table.ForeignKey(
                        name: "FK_GroupTranslationRates_Accounts_MemberId_EquityAccountId",
                        columns: x => new { x.MemberId, x.EquityAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTranslationRates_Companies_TenantId_OrganizationId_Com~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTranslationRates_Companies_TenantId_OrganizationId_Mem~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.MemberId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupTranslationRates_CorporateGroups_CompanyId_GroupId",
                        columns: x => new { x.CompanyId, x.GroupId },
                        principalSchema: "groups",
                        principalTable: "CorporateGroups",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntercompanyPolicies",
                schema: "groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreditorId = table.Column<Guid>(type: "uuid", nullable: false),
                    DebtorId = table.Column<Guid>(type: "uuid", nullable: false),
                    DueFromId = table.Column<Guid>(type: "uuid", nullable: false),
                    DueToId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreditorBankId = table.Column<Guid>(type: "uuid", nullable: true),
                    DebtorBankId = table.Column<Guid>(type: "uuid", nullable: true),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PreparedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Evidence = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntercompanyPolicies", x => x.Id);
                    table.UniqueConstraint("AK_IntercompanyPolicies_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_IntercompanyPolicies_GroupId_Id", x => new { x.GroupId, x.Id });
                    table.CheckConstraint("CK_PairPolicy_Companies", " \"CreditorId\" <> \"DebtorId\" ");
                    table.ForeignKey(
                        name: "FK_IntercompanyPolicies_Accounts_CreditorId_CreditorBankId",
                        columns: x => new { x.CreditorId, x.CreditorBankId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPolicies_Accounts_CreditorId_DueFromId",
                        columns: x => new { x.CreditorId, x.DueFromId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPolicies_Accounts_CreditorId_RevenueId",
                        columns: x => new { x.CreditorId, x.RevenueId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPolicies_Accounts_DebtorId_DebtorBankId",
                        columns: x => new { x.DebtorId, x.DebtorBankId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPolicies_Accounts_DebtorId_DueToId",
                        columns: x => new { x.DebtorId, x.DueToId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPolicies_Accounts_DebtorId_ExpenseId",
                        columns: x => new { x.DebtorId, x.ExpenseId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPolicies_Companies_TenantId_OrganizationId_Comp~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPolicies_Companies_TenantId_OrganizationId_Cred~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CreditorId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPolicies_Companies_TenantId_OrganizationId_Debt~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.DebtorId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPolicies_CorporateGroups_CompanyId_GroupId",
                        columns: x => new { x.CompanyId, x.GroupId },
                        principalSchema: "groups",
                        principalTable: "CorporateGroups",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConsolidationRuns",
                schema: "groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    Cutoff = table.Column<DateOnly>(type: "date", nullable: false),
                    SupersedesId = table.Column<Guid>(type: "uuid", nullable: true),
                    TranslationReserveId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    PreparedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    AdjustmentsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsolidationRuns", x => x.Id);
                    table.UniqueConstraint("AK_ConsolidationRuns_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_ConsolidationRuns_GroupId_Id", x => new { x.GroupId, x.Id });
                    table.CheckConstraint("CK_GroupRun_State", " \"From\" <= \"Cutoff\" AND \"Version\" > 0 AND \"State\" BETWEEN 0 AND 2 ");
                    table.ForeignKey(
                        name: "FK_ConsolidationRuns_Companies_TenantId_OrganizationId_Company~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsolidationRuns_ConsolidationRuns_GroupId_SupersedesId",
                        columns: x => new { x.GroupId, x.SupersedesId },
                        principalSchema: "groups",
                        principalTable: "ConsolidationRuns",
                        principalColumns: new[] { "GroupId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsolidationRuns_CorporateGroups_CompanyId_GroupId",
                        columns: x => new { x.CompanyId, x.GroupId },
                        principalSchema: "groups",
                        principalTable: "CorporateGroups",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsolidationRuns_GroupAccounts_GroupId_TranslationReserveId",
                        columns: x => new { x.GroupId, x.TranslationReserveId },
                        principalSchema: "groups",
                        principalTable: "GroupAccounts",
                        principalColumns: new[] { "GroupId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GroupMappings",
                schema: "groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    LedgerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PreparedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Evidence = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMappings", x => x.Id);
                    table.UniqueConstraint("AK_GroupMappings_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_GroupMappings_GroupId_Id", x => new { x.GroupId, x.Id });
                    table.ForeignKey(
                        name: "FK_GroupMappings_Accounts_MemberId_LedgerAccountId",
                        columns: x => new { x.MemberId, x.LedgerAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupMappings_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupMappings_Companies_TenantId_OrganizationId_MemberId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.MemberId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupMappings_CorporateGroups_CompanyId_GroupId",
                        columns: x => new { x.CompanyId, x.GroupId },
                        principalSchema: "groups",
                        principalTable: "CorporateGroups",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GroupMappings_GroupAccounts_GroupId_GroupAccountId",
                        columns: x => new { x.GroupId, x.GroupAccountId },
                        principalSchema: "groups",
                        principalTable: "GroupAccounts",
                        principalColumns: new[] { "GroupId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntercompanyPairs",
                schema: "groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreditorId = table.Column<Guid>(type: "uuid", nullable: false),
                    DebtorId = table.Column<Guid>(type: "uuid", nullable: false),
                    BasisId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreditorJournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    DebtorJournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: true),
                    InterestFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    InterestAnnualPercent = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntercompanyPairs", x => x.Id);
                    table.UniqueConstraint("AK_IntercompanyPairs_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_IntercompanyPairs_GroupId_Id", x => new { x.GroupId, x.Id });
                    table.CheckConstraint("CK_Pair_Amounts", " \"Amount\" > 0 AND \"State\" BETWEEN 0 AND 4 AND \"Kind\" BETWEEN 0 AND 4 AND ((\"State\" = 3 AND \"CreditorJournalId\" IS NOT NULL AND \"DebtorJournalId\" IS NOT NULL) OR (\"State\" <> 3 AND \"CreditorJournalId\" IS NULL AND \"DebtorJournalId\" IS NULL)) ");
                    table.CheckConstraint("CK_Pair_Interest", " (\"Kind\" = 2 AND \"InterestFrom\" IS NOT NULL AND \"InterestFrom\" <= \"Date\" AND \"InterestAnnualPercent\" IS NOT NULL AND \"InterestAnnualPercent\" > 0 AND \"InterestAnnualPercent\" <= 100) OR (\"Kind\" <> 2 AND \"InterestFrom\" IS NULL AND \"InterestAnnualPercent\" IS NULL) ");
                    table.ForeignKey(
                        name: "FK_IntercompanyPairs_Companies_TenantId_OrganizationId_Company~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPairs_Companies_TenantId_OrganizationId_Credito~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CreditorId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPairs_Companies_TenantId_OrganizationId_DebtorId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.DebtorId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPairs_CorporateGroups_CompanyId_GroupId",
                        columns: x => new { x.CompanyId, x.GroupId },
                        principalSchema: "groups",
                        principalTable: "CorporateGroups",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPairs_IntercompanyPairs_GroupId_BasisId",
                        columns: x => new { x.GroupId, x.BasisId },
                        principalSchema: "groups",
                        principalTable: "IntercompanyPairs",
                        principalColumns: new[] { "GroupId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPairs_IntercompanyPolicies_GroupId_PolicyId",
                        columns: x => new { x.GroupId, x.PolicyId },
                        principalSchema: "groups",
                        principalTable: "IntercompanyPolicies",
                        principalColumns: new[] { "GroupId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPairs_Journals_CreditorId_CreditorJournalId",
                        columns: x => new { x.CreditorId, x.CreditorJournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyPairs_Journals_DebtorId_DebtorJournalId",
                        columns: x => new { x.DebtorId, x.DebtorJournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReciprocalReviews",
                schema: "groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cutoff = table.Column<DateOnly>(type: "date", nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReciprocalReviews", x => x.Id);
                    table.UniqueConstraint("AK_ReciprocalReviews_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.UniqueConstraint("AK_ReciprocalReviews_GroupId_Id", x => new { x.GroupId, x.Id });
                    table.ForeignKey(
                        name: "FK_ReciprocalReviews_Companies_TenantId_OrganizationId_Company~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReciprocalReviews_CorporateGroups_CompanyId_GroupId",
                        columns: x => new { x.CompanyId, x.GroupId },
                        principalSchema: "groups",
                        principalTable: "CorporateGroups",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReciprocalReviews_IntercompanyPolicies_GroupId_PolicyId",
                        columns: x => new { x.GroupId, x.PolicyId },
                        principalSchema: "groups",
                        principalTable: "IntercompanyPolicies",
                        principalColumns: new[] { "GroupId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsolidationRuns_CompanyId_GroupId",
                schema: "groups",
                table: "ConsolidationRuns",
                columns: new[] { "CompanyId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsolidationRuns_GroupId_From_Cutoff_Version",
                schema: "groups",
                table: "ConsolidationRuns",
                columns: new[] { "GroupId", "From", "Cutoff", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsolidationRuns_GroupId_SupersedesId",
                schema: "groups",
                table: "ConsolidationRuns",
                columns: new[] { "GroupId", "SupersedesId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsolidationRuns_GroupId_TranslationReserveId",
                schema: "groups",
                table: "ConsolidationRuns",
                columns: new[] { "GroupId", "TranslationReserveId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsolidationRuns_TenantId_OrganizationId_CompanyId",
                schema: "groups",
                table: "ConsolidationRuns",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CorporateGroups_OrganizationId_Code",
                schema: "groups",
                table: "CorporateGroups",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CorporateGroups_TenantId_OrganizationId_CompanyId",
                schema: "groups",
                table: "CorporateGroups",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAccounts_CompanyId_GroupId",
                schema: "groups",
                table: "GroupAccounts",
                columns: new[] { "CompanyId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAccounts_GroupId_Code",
                schema: "groups",
                table: "GroupAccounts",
                columns: new[] { "GroupId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupAccounts_GroupId_ParentId",
                schema: "groups",
                table: "GroupAccounts",
                columns: new[] { "GroupId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAccounts_TenantId_OrganizationId_CompanyId",
                schema: "groups",
                table: "GroupAccounts",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMappings_CompanyId_GroupId",
                schema: "groups",
                table: "GroupMappings",
                columns: new[] { "CompanyId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMappings_GroupId_GroupAccountId",
                schema: "groups",
                table: "GroupMappings",
                columns: new[] { "GroupId", "GroupAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMappings_GroupId_MemberId_LedgerAccountId_EffectiveOn",
                schema: "groups",
                table: "GroupMappings",
                columns: new[] { "GroupId", "MemberId", "LedgerAccountId", "EffectiveOn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMappings_MemberId_LedgerAccountId",
                schema: "groups",
                table: "GroupMappings",
                columns: new[] { "MemberId", "LedgerAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMappings_TenantId_OrganizationId_CompanyId",
                schema: "groups",
                table: "GroupMappings",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMappings_TenantId_OrganizationId_MemberId",
                schema: "groups",
                table: "GroupMappings",
                columns: new[] { "TenantId", "OrganizationId", "MemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupOwnerships_CompanyId_GroupId",
                schema: "groups",
                table: "GroupOwnerships",
                columns: new[] { "CompanyId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupOwnerships_GroupId_InvesteeId_EffectiveOn",
                schema: "groups",
                table: "GroupOwnerships",
                columns: new[] { "GroupId", "InvesteeId", "EffectiveOn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupOwnerships_TenantId_OrganizationId_CompanyId",
                schema: "groups",
                table: "GroupOwnerships",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupOwnerships_TenantId_OrganizationId_InvesteeId",
                schema: "groups",
                table: "GroupOwnerships",
                columns: new[] { "TenantId", "OrganizationId", "InvesteeId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupOwnerships_TenantId_OrganizationId_InvestorId",
                schema: "groups",
                table: "GroupOwnerships",
                columns: new[] { "TenantId", "OrganizationId", "InvestorId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTranslationRates_CompanyId_GroupId",
                schema: "groups",
                table: "GroupTranslationRates",
                columns: new[] { "CompanyId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTranslationRates_GroupId_MemberId_EquityAccountId_Rate~",
                schema: "groups",
                table: "GroupTranslationRates",
                columns: new[] { "GroupId", "MemberId", "EquityAccountId", "RateDate", "EffectiveOn" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_GroupTranslationRates_MemberId_EquityAccountId",
                schema: "groups",
                table: "GroupTranslationRates",
                columns: new[] { "MemberId", "EquityAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTranslationRates_TenantId_OrganizationId_CompanyId",
                schema: "groups",
                table: "GroupTranslationRates",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTranslationRates_TenantId_OrganizationId_MemberId",
                schema: "groups",
                table: "GroupTranslationRates",
                columns: new[] { "TenantId", "OrganizationId", "MemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPairs_CompanyId_GroupId",
                schema: "groups",
                table: "IntercompanyPairs",
                columns: new[] { "CompanyId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPairs_CreditorId_CreditorJournalId",
                schema: "groups",
                table: "IntercompanyPairs",
                columns: new[] { "CreditorId", "CreditorJournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPairs_CreditorJournalId",
                schema: "groups",
                table: "IntercompanyPairs",
                column: "CreditorJournalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPairs_DebtorId_DebtorJournalId",
                schema: "groups",
                table: "IntercompanyPairs",
                columns: new[] { "DebtorId", "DebtorJournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPairs_DebtorJournalId",
                schema: "groups",
                table: "IntercompanyPairs",
                column: "DebtorJournalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPairs_GroupId_BasisId_Kind",
                schema: "groups",
                table: "IntercompanyPairs",
                columns: new[] { "GroupId", "BasisId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPairs_GroupId_PolicyId",
                schema: "groups",
                table: "IntercompanyPairs",
                columns: new[] { "GroupId", "PolicyId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPairs_GroupId_Reference",
                schema: "groups",
                table: "IntercompanyPairs",
                columns: new[] { "GroupId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPairs_TenantId_OrganizationId_CompanyId",
                schema: "groups",
                table: "IntercompanyPairs",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPairs_TenantId_OrganizationId_CreditorId",
                schema: "groups",
                table: "IntercompanyPairs",
                columns: new[] { "TenantId", "OrganizationId", "CreditorId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPairs_TenantId_OrganizationId_DebtorId",
                schema: "groups",
                table: "IntercompanyPairs",
                columns: new[] { "TenantId", "OrganizationId", "DebtorId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPolicies_CompanyId_GroupId",
                schema: "groups",
                table: "IntercompanyPolicies",
                columns: new[] { "CompanyId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPolicies_CreditorId_CreditorBankId",
                schema: "groups",
                table: "IntercompanyPolicies",
                columns: new[] { "CreditorId", "CreditorBankId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPolicies_CreditorId_DueFromId",
                schema: "groups",
                table: "IntercompanyPolicies",
                columns: new[] { "CreditorId", "DueFromId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPolicies_CreditorId_RevenueId",
                schema: "groups",
                table: "IntercompanyPolicies",
                columns: new[] { "CreditorId", "RevenueId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPolicies_DebtorId_DebtorBankId",
                schema: "groups",
                table: "IntercompanyPolicies",
                columns: new[] { "DebtorId", "DebtorBankId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPolicies_DebtorId_DueToId",
                schema: "groups",
                table: "IntercompanyPolicies",
                columns: new[] { "DebtorId", "DueToId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPolicies_DebtorId_ExpenseId",
                schema: "groups",
                table: "IntercompanyPolicies",
                columns: new[] { "DebtorId", "ExpenseId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPolicies_GroupId_CreditorId_DebtorId_EffectiveOn",
                schema: "groups",
                table: "IntercompanyPolicies",
                columns: new[] { "GroupId", "CreditorId", "DebtorId", "EffectiveOn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPolicies_TenantId_OrganizationId_CompanyId",
                schema: "groups",
                table: "IntercompanyPolicies",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPolicies_TenantId_OrganizationId_CreditorId",
                schema: "groups",
                table: "IntercompanyPolicies",
                columns: new[] { "TenantId", "OrganizationId", "CreditorId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyPolicies_TenantId_OrganizationId_DebtorId",
                schema: "groups",
                table: "IntercompanyPolicies",
                columns: new[] { "TenantId", "OrganizationId", "DebtorId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReciprocalReviews_CompanyId_GroupId",
                schema: "groups",
                table: "ReciprocalReviews",
                columns: new[] { "CompanyId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReciprocalReviews_GroupId_PolicyId_Cutoff",
                schema: "groups",
                table: "ReciprocalReviews",
                columns: new[] { "GroupId", "PolicyId", "Cutoff" });

            migrationBuilder.CreateIndex(
                name: "IX_ReciprocalReviews_TenantId_OrganizationId_CompanyId",
                schema: "groups",
                table: "ReciprocalReviews",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.Sql(CorporateConsolidationV1Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CorporateConsolidationV1Sql.Down);
            migrationBuilder.DropTable(
                name: "ConsolidationRuns",
                schema: "groups");

            migrationBuilder.DropTable(
                name: "GroupMappings",
                schema: "groups");

            migrationBuilder.DropTable(
                name: "GroupOwnerships",
                schema: "groups");

            migrationBuilder.DropTable(
                name: "GroupTranslationRates",
                schema: "groups");

            migrationBuilder.DropTable(
                name: "IntercompanyPairs",
                schema: "groups");

            migrationBuilder.DropTable(
                name: "ReciprocalReviews",
                schema: "groups");

            migrationBuilder.DropTable(
                name: "GroupAccounts",
                schema: "groups");

            migrationBuilder.DropTable(
                name: "IntercompanyPolicies",
                schema: "groups");

            migrationBuilder.DropTable(
                name: "CorporateGroups",
                schema: "groups");
        }
    }
}
