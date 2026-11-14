using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FinancialDimensionsV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.CreateTable(
                name: "CostAllocationExecutions",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrectionJournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    CorrectedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PostedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputJson = table.Column<string>(type: "jsonb", nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostAllocationExecutions", x => x.Id);
                    table.UniqueConstraint("AK_CostAllocationExecutions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_CostAllocation_State", "\"Mode\" IN (0,1) AND \"State\" BETWEEN 0 AND 4 AND ((\"State\"=4)=(\"CorrectedOn\" IS NOT NULL)) AND\n(\"State\" NOT IN(1,2,4) OR (\"ApprovedBy\" IS NOT NULL AND \"ApprovedBy\"<>\"CreatedBy\"))");
                    table.ForeignKey(
                        name: "FK_CostAllocationExecutions_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CostAllocationExecutions_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CostAllocationExecutions_AspNetUsers_PostedBy",
                        column: x => x.PostedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CostAllocationExecutions_Companies_TenantId_OrganizationId_~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CostAllocationExecutions_JournalLines_CompanyId_SourceLineId",
                        columns: x => new { x.CompanyId, x.SourceLineId },
                        principalSchema: "accounting",
                        principalTable: "JournalLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CostAllocationExecutions_Journals_CompanyId_CorrectionJourn~",
                        columns: x => new { x.CompanyId, x.CorrectionJournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CostAllocationExecutions_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DimensionBindings",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValuesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionBindings", x => x.Id);
                    table.UniqueConstraint("AK_DimensionBindings_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_DimensionBinding_Source", " \"Source\" BETWEEN 0 AND 6 AND \"Revision\">0 ");
                    table.ForeignKey(
                        name: "FK_DimensionBindings_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionBindings_Companies_TenantId_OrganizationId_Company~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DimensionDefinitions",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionDefinitions", x => x.Id);
                    table.UniqueConstraint("AK_DimensionDefinitions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_DimensionDefinition_Kind", " \"Kind\" BETWEEN 0 AND 5 ");
                    table.ForeignKey(
                        name: "FK_DimensionDefinitions_Companies_TenantId_OrganizationId_Comp~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DimensionValues",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    CostCenterId = table.Column<Guid>(type: "uuid", nullable: true),
                    ManagerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Classification = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    InactiveOn = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionValues", x => x.Id);
                    table.UniqueConstraint("AK_DimensionValues_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_DimensionValue_Terms", "(\"InactiveOn\" IS NULL OR \"InactiveOn\">\"EffectiveDate\") AND (\"ParentId\" IS NULL OR \"ParentId\"<>\"Id\")");
                    table.ForeignKey(
                        name: "FK_DimensionValues_AspNetUsers_ManagerId",
                        column: x => x.ManagerId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionValues_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionValues_DimensionDefinitions_CompanyId_DefinitionId",
                        columns: x => new { x.CompanyId, x.DefinitionId },
                        principalSchema: "analytics",
                        principalTable: "DimensionDefinitions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionValues_DimensionValues_CompanyId_BranchId",
                        columns: x => new { x.CompanyId, x.BranchId },
                        principalSchema: "analytics",
                        principalTable: "DimensionValues",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionValues_DimensionValues_CompanyId_CostCenterId",
                        columns: x => new { x.CompanyId, x.CostCenterId },
                        principalSchema: "analytics",
                        principalTable: "DimensionValues",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionValues_DimensionValues_CompanyId_DepartmentId",
                        columns: x => new { x.CompanyId, x.DepartmentId },
                        principalSchema: "analytics",
                        principalTable: "DimensionValues",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionValues_DimensionValues_CompanyId_ParentId",
                        columns: x => new { x.CompanyId, x.ParentId },
                        principalSchema: "analytics",
                        principalTable: "DimensionValues",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DimensionBudgetSlices",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetAllocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    HardLimit = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionBudgetSlices", x => x.Id);
                    table.UniqueConstraint("AK_DimensionBudgetSlices_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_DimensionBudgetSlice_Amount", " \"Amount\">=0 ");
                    table.ForeignKey(
                        name: "FK_DimensionBudgetSlices_BudgetAllocations_CompanyId_BudgetAll~",
                        columns: x => new { x.CompanyId, x.BudgetAllocationId },
                        principalSchema: "planning",
                        principalTable: "BudgetAllocations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionBudgetSlices_Companies_TenantId_OrganizationId_Com~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionBudgetSlices_DimensionValues_CompanyId_ValueId",
                        columns: x => new { x.CompanyId, x.ValueId },
                        principalSchema: "analytics",
                        principalTable: "DimensionValues",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DimensionRules",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefaultValueId = table.Column<Guid>(type: "uuid", nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    AllowedJson = table.Column<string>(type: "jsonb", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionRules", x => x.Id);
                    table.UniqueConstraint("AK_DimensionRules_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_DimensionRule_Terms", "\"Mode\" BETWEEN 0 AND 3 AND (\"Mode\"<>3 OR \"DefaultValueId\" IS NOT NULL) AND (\"Mode\"<>2 OR \"DefaultValueId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_DimensionRules_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionRules_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionRules_DimensionDefinitions_CompanyId_DefinitionId",
                        columns: x => new { x.CompanyId, x.DefinitionId },
                        principalSchema: "analytics",
                        principalTable: "DimensionDefinitions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DimensionRules_DimensionValues_CompanyId_DefaultValueId",
                        columns: x => new { x.CompanyId, x.DefaultValueId },
                        principalSchema: "analytics",
                        principalTable: "DimensionValues",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialProjects",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ValueId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Start = table.Column<DateOnly>(type: "date", nullable: false),
                    End = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Evidence = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialProjects", x => x.Id);
                    table.UniqueConstraint("AK_FinancialProjects_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_FinancialProject_Window", " \"End\">=\"Start\" ");
                    table.ForeignKey(
                        name: "FK_FinancialProjects_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialProjects_Companies_TenantId_OrganizationId_Company~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialProjects_DimensionValues_CompanyId_ValueId",
                        columns: x => new { x.CompanyId, x.ValueId },
                        principalSchema: "analytics",
                        principalTable: "DimensionValues",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialProjects_Parties_CompanyId_CustomerId",
                        columns: x => new { x.CompanyId, x.CustomerId },
                        principalSchema: "trading",
                        principalTable: "Parties",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PostedDimensions",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValueId = table.Column<Guid>(type: "uuid", nullable: true),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: true),
                    BindingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Origin = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostedDimensions", x => x.Id);
                    table.UniqueConstraint("AK_PostedDimensions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_PostedDimension_Origin", "\"Origin\" BETWEEN 0 AND 7 AND ((\"Origin\"=7)=(\"ValueId\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_PostedDimensions_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostedDimensions_DimensionBindings_CompanyId_BindingId",
                        columns: x => new { x.CompanyId, x.BindingId },
                        principalSchema: "analytics",
                        principalTable: "DimensionBindings",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostedDimensions_DimensionDefinitions_CompanyId_DefinitionId",
                        columns: x => new { x.CompanyId, x.DefinitionId },
                        principalSchema: "analytics",
                        principalTable: "DimensionDefinitions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostedDimensions_DimensionRules_CompanyId_RuleId",
                        columns: x => new { x.CompanyId, x.RuleId },
                        principalSchema: "analytics",
                        principalTable: "DimensionRules",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostedDimensions_DimensionValues_CompanyId_ValueId",
                        columns: x => new { x.CompanyId, x.ValueId },
                        principalSchema: "analytics",
                        principalTable: "DimensionValues",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostedDimensions_JournalLines_CompanyId_JournalLineId",
                        columns: x => new { x.CompanyId, x.JournalLineId },
                        principalSchema: "accounting",
                        principalTable: "JournalLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialProjectEvents",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialProjectEvents", x => x.Id);
                    table.UniqueConstraint("AK_FinancialProjectEvents_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ProjectEvent_State", " \"State\" BETWEEN 0 AND 5 AND \"Number\">0 ");
                    table.ForeignKey(
                        name: "FK_FinancialProjectEvents_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialProjectEvents_Companies_TenantId_OrganizationId_Co~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialProjectEvents_FinancialProjects_CompanyId_ProjectId",
                        columns: x => new { x.CompanyId, x.ProjectId },
                        principalSchema: "analytics",
                        principalTable: "FinancialProjects",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectFinancialForecasts",
                schema: "analytics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateOnly>(type: "date", nullable: false),
                    PostedCost = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    PostedRevenue = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    CostToComplete = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    RevenueToComplete = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Evidence = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectFinancialForecasts", x => x.Id);
                    table.UniqueConstraint("AK_ProjectFinancialForecasts_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_ProjectFinancialForecasts_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectFinancialForecasts_Companies_TenantId_OrganizationId~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectFinancialForecasts_FinancialProjects_CompanyId_Proje~",
                        columns: x => new { x.CompanyId, x.ProjectId },
                        principalSchema: "analytics",
                        principalTable: "FinancialProjects",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CostAllocationExecutions_ApprovedBy",
                schema: "analytics",
                table: "CostAllocationExecutions",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_CostAllocationExecutions_CompanyId_CorrectionJournalId",
                schema: "analytics",
                table: "CostAllocationExecutions",
                columns: new[] { "CompanyId", "CorrectionJournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_CostAllocationExecutions_CompanyId_JournalId",
                schema: "analytics",
                table: "CostAllocationExecutions",
                columns: new[] { "CompanyId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_CostAllocationExecutions_CompanyId_Key",
                schema: "analytics",
                table: "CostAllocationExecutions",
                columns: new[] { "CompanyId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostAllocationExecutions_CompanyId_SourceLineId",
                schema: "analytics",
                table: "CostAllocationExecutions",
                columns: new[] { "CompanyId", "SourceLineId" },
                unique: true,
                filter: "\"State\"=2");

            migrationBuilder.CreateIndex(
                name: "IX_CostAllocationExecutions_CreatedBy",
                schema: "analytics",
                table: "CostAllocationExecutions",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_CostAllocationExecutions_PostedBy",
                schema: "analytics",
                table: "CostAllocationExecutions",
                column: "PostedBy");

            migrationBuilder.CreateIndex(
                name: "IX_CostAllocationExecutions_TenantId_OrganizationId_CompanyId",
                schema: "analytics",
                table: "CostAllocationExecutions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionBindings_ActorId",
                schema: "analytics",
                table: "DimensionBindings",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_DimensionBindings_CompanyId_Source_SourceId_SourceVersion_R~",
                schema: "analytics",
                table: "DimensionBindings",
                columns: new[] { "CompanyId", "Source", "SourceId", "SourceVersion", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionBindings_TenantId_OrganizationId_CompanyId",
                schema: "analytics",
                table: "DimensionBindings",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionBudgetSlices_CompanyId_BudgetAllocationId_ValueId",
                schema: "analytics",
                table: "DimensionBudgetSlices",
                columns: new[] { "CompanyId", "BudgetAllocationId", "ValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionBudgetSlices_CompanyId_ValueId",
                schema: "analytics",
                table: "DimensionBudgetSlices",
                columns: new[] { "CompanyId", "ValueId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionBudgetSlices_TenantId_OrganizationId_CompanyId",
                schema: "analytics",
                table: "DimensionBudgetSlices",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionDefinitions_CompanyId_Code",
                schema: "analytics",
                table: "DimensionDefinitions",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionDefinitions_CompanyId_Kind",
                schema: "analytics",
                table: "DimensionDefinitions",
                columns: new[] { "CompanyId", "Kind" },
                unique: true,
                filter: "\"Kind\"<5");

            migrationBuilder.CreateIndex(
                name: "IX_DimensionDefinitions_TenantId_OrganizationId_CompanyId",
                schema: "analytics",
                table: "DimensionDefinitions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRules_CompanyId_AccountId",
                schema: "analytics",
                table: "DimensionRules",
                columns: new[] { "CompanyId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRules_CompanyId_DefaultValueId",
                schema: "analytics",
                table: "DimensionRules",
                columns: new[] { "CompanyId", "DefaultValueId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRules_CompanyId_DefinitionId_AccountId_EffectiveDa~",
                schema: "analytics",
                table: "DimensionRules",
                columns: new[] { "CompanyId", "DefinitionId", "AccountId", "EffectiveDate" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRules_TenantId_OrganizationId_CompanyId",
                schema: "analytics",
                table: "DimensionRules",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValues_CompanyId_BranchId",
                schema: "analytics",
                table: "DimensionValues",
                columns: new[] { "CompanyId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValues_CompanyId_CostCenterId",
                schema: "analytics",
                table: "DimensionValues",
                columns: new[] { "CompanyId", "CostCenterId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValues_CompanyId_DefinitionId_Code",
                schema: "analytics",
                table: "DimensionValues",
                columns: new[] { "CompanyId", "DefinitionId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValues_CompanyId_DepartmentId",
                schema: "analytics",
                table: "DimensionValues",
                columns: new[] { "CompanyId", "DepartmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValues_CompanyId_ParentId",
                schema: "analytics",
                table: "DimensionValues",
                columns: new[] { "CompanyId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValues_ManagerId",
                schema: "analytics",
                table: "DimensionValues",
                column: "ManagerId");

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValues_TenantId_OrganizationId_CompanyId",
                schema: "analytics",
                table: "DimensionValues",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialProjectEvents_ActorId",
                schema: "analytics",
                table: "FinancialProjectEvents",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialProjectEvents_CompanyId_ProjectId_Number",
                schema: "analytics",
                table: "FinancialProjectEvents",
                columns: new[] { "CompanyId", "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialProjectEvents_TenantId_OrganizationId_CompanyId",
                schema: "analytics",
                table: "FinancialProjectEvents",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialProjects_CompanyId_CustomerId",
                schema: "analytics",
                table: "FinancialProjects",
                columns: new[] { "CompanyId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialProjects_CompanyId_ValueId",
                schema: "analytics",
                table: "FinancialProjects",
                columns: new[] { "CompanyId", "ValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialProjects_OwnerId",
                schema: "analytics",
                table: "FinancialProjects",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialProjects_TenantId_OrganizationId_CompanyId",
                schema: "analytics",
                table: "FinancialProjects",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostedDimensions_CompanyId_BindingId",
                schema: "analytics",
                table: "PostedDimensions",
                columns: new[] { "CompanyId", "BindingId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostedDimensions_CompanyId_DefinitionId_ValueId",
                schema: "analytics",
                table: "PostedDimensions",
                columns: new[] { "CompanyId", "DefinitionId", "ValueId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostedDimensions_CompanyId_JournalLineId_DefinitionId",
                schema: "analytics",
                table: "PostedDimensions",
                columns: new[] { "CompanyId", "JournalLineId", "DefinitionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostedDimensions_CompanyId_RuleId",
                schema: "analytics",
                table: "PostedDimensions",
                columns: new[] { "CompanyId", "RuleId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostedDimensions_CompanyId_ValueId",
                schema: "analytics",
                table: "PostedDimensions",
                columns: new[] { "CompanyId", "ValueId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostedDimensions_TenantId_OrganizationId_CompanyId",
                schema: "analytics",
                table: "PostedDimensions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectFinancialForecasts_ApprovedBy",
                schema: "analytics",
                table: "ProjectFinancialForecasts",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectFinancialForecasts_CompanyId_ProjectId_At",
                schema: "analytics",
                table: "ProjectFinancialForecasts",
                columns: new[] { "CompanyId", "ProjectId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectFinancialForecasts_TenantId_OrganizationId_CompanyId",
                schema: "analytics",
                table: "ProjectFinancialForecasts",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });
            migrationBuilder.Sql(FinancialDimensionsV1Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FinancialDimensionsV1Sql.Down);
            migrationBuilder.DropTable(
                name: "CostAllocationExecutions",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "DimensionBudgetSlices",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "FinancialProjectEvents",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "PostedDimensions",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "ProjectFinancialForecasts",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "DimensionBindings",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "DimensionRules",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "FinancialProjects",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "DimensionValues",
                schema: "analytics");

            migrationBuilder.DropTable(
                name: "DimensionDefinitions",
                schema: "analytics");
        }
    }
}
