using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BudgetPlanningV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "planning");

            migrationBuilder.CreateTable(
                name: "BudgetCommitmentEvents",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcurementLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    StockOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    EventKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetCommitmentEvents", x => x.Id);
                    table.UniqueConstraint("AK_BudgetCommitmentEvents_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_BudgetCommitment_Source", "\"Amount\"<>0 AND ((\"Kind\" IN (0,1) AND \"ProcurementLineId\" IS NOT NULL AND \"AssetOperationId\" IS NULL)\n  OR (\"Kind\"=2 AND \"ProcurementLineId\" IS NULL AND \"AssetOperationId\" IS NOT NULL AND \"StockOperationId\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_BudgetCommitmentEvents_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetCommitmentEvents_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetCommitmentEvents_AssetOperations_CompanyId_AssetOpera~",
                        columns: x => new { x.CompanyId, x.AssetOperationId },
                        principalSchema: "fixed_assets",
                        principalTable: "AssetOperations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetCommitmentEvents_Companies_TenantId_OrganizationId_Co~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetCommitmentEvents_Operations_CompanyId_StockOperationId",
                        columns: x => new { x.CompanyId, x.StockOperationId },
                        principalSchema: "inventory",
                        principalTable: "Operations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetCommitmentEvents_Periods_CompanyId_PeriodId",
                        columns: x => new { x.CompanyId, x.PeriodId },
                        principalSchema: "accounting",
                        principalTable: "Periods",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetCommitmentEvents_ProcurementLines_CompanyId_Procureme~",
                        columns: x => new { x.CompanyId, x.ProcurementLineId },
                        principalSchema: "inventory",
                        principalTable: "ProcurementLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BudgetOverrideGrants",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Intent = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    MaxExcess = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Expires = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetOverrideGrants", x => x.Id);
                    table.UniqueConstraint("AK_BudgetOverrideGrants_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_BudgetOverride_Amount", " \"MaxExcess\">0 ");
                    table.ForeignKey(
                        name: "FK_BudgetOverrideGrants_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetOverrideGrants_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetOverrideGrants_Companies_TenantId_OrganizationId_Comp~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BudgetPolicies",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    WarningPercent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetPolicies", x => x.Id);
                    table.UniqueConstraint("AK_BudgetPolicies_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_BudgetPolicy_Mode", "\"Kind\" BETWEEN 1 AND 3 AND \"Mode\" BETWEEN 0 AND 2 AND \"WarningPercent\">0 AND \"WarningPercent\"<=100");
                    table.ForeignKey(
                        name: "FK_BudgetPolicies_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetPolicies_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetPolicies_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialBudgets",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ApprovalLimit = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialBudgets", x => x.Id);
                    table.UniqueConstraint("AK_FinancialBudgets_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Budget_Kind", "\"Kind\" BETWEEN 0 AND 3 AND \"ApprovalLimit\">0");
                    table.ForeignKey(
                        name: "FK_FinancialBudgets_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialBudgets_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialBudgets_FiscalYears_CompanyId_FiscalYearId",
                        columns: x => new { x.CompanyId, x.FiscalYearId },
                        principalSchema: "accounting",
                        principalTable: "FiscalYears",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ForecastVersions",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    BasisId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScenarioBaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    AsOf = table.Column<DateOnly>(type: "date", nullable: false),
                    Horizon = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AssumptionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastVersions", x => x.Id);
                    table.UniqueConstraint("AK_ForecastVersions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Forecast_State", "\"Number\">0 AND \"Horizon\" IN (3,12) AND \"State\" BETWEEN 0 AND 2\n  AND (\"State\"<>1 OR (\"ApprovedBy\" IS NOT NULL AND \"CreatedBy\"<>\"ApprovedBy\"))");
                    table.ForeignKey(
                        name: "FK_ForecastVersions_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ForecastVersions_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ForecastVersions_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ForecastVersions_ForecastVersions_CompanyId_BasisId",
                        columns: x => new { x.CompanyId, x.BasisId },
                        principalSchema: "planning",
                        principalTable: "ForecastVersions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ForecastVersions_ForecastVersions_CompanyId_ScenarioBaseId",
                        columns: x => new { x.CompanyId, x.ScenarioBaseId },
                        principalSchema: "planning",
                        principalTable: "ForecastVersions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BudgetVersions",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetId = table.Column<Guid>(type: "uuid", nullable: false),
                    BasisVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActiveAtPreparationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ActivatedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Evidence = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetVersions", x => x.Id);
                    table.UniqueConstraint("AK_BudgetVersions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_BudgetVersion_State", "\"Number\">0 AND \"State\" BETWEEN 0 AND 6 AND ((\"State\" IN (4,5))=(\"EffectiveDate\" IS NOT NULL))\n  AND (\"ActivatedOn\" IS NULL)=(\"EffectiveDate\" IS NULL)\n  AND (\"State\" NOT IN (3,4,5) OR (\"ApprovedBy\" IS NOT NULL AND \"ApprovedBy\"<>\"CreatedBy\"))");
                    table.ForeignKey(
                        name: "FK_BudgetVersions_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetVersions_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetVersions_AspNetUsers_ReviewerId",
                        column: x => x.ReviewerId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetVersions_BudgetVersions_CompanyId_ActiveAtPreparation~",
                        columns: x => new { x.CompanyId, x.ActiveAtPreparationId },
                        principalSchema: "planning",
                        principalTable: "BudgetVersions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetVersions_BudgetVersions_CompanyId_BasisVersionId",
                        columns: x => new { x.CompanyId, x.BasisVersionId },
                        principalSchema: "planning",
                        principalTable: "BudgetVersions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetVersions_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetVersions_FinancialBudgets_CompanyId_BudgetId",
                        columns: x => new { x.CompanyId, x.BudgetId },
                        principalSchema: "planning",
                        principalTable: "FinancialBudgets",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BudgetAllocations",
                schema: "planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetAllocations", x => x.Id);
                    table.UniqueConstraint("AK_BudgetAllocations_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_BudgetAllocation_Amount", " \"Amount\">=0 ");
                    table.ForeignKey(
                        name: "FK_BudgetAllocations_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetAllocations_BudgetVersions_CompanyId_BudgetVersionId",
                        columns: x => new { x.CompanyId, x.BudgetVersionId },
                        principalSchema: "planning",
                        principalTable: "BudgetVersions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetAllocations_Companies_TenantId_OrganizationId_Company~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetAllocations_Periods_CompanyId_PeriodId",
                        columns: x => new { x.CompanyId, x.PeriodId },
                        principalSchema: "accounting",
                        principalTable: "Periods",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetAllocations_CompanyId_AccountId",
                schema: "planning",
                table: "BudgetAllocations",
                columns: new[] { "CompanyId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetAllocations_CompanyId_BudgetVersionId_AccountId_Perio~",
                schema: "planning",
                table: "BudgetAllocations",
                columns: new[] { "CompanyId", "BudgetVersionId", "AccountId", "PeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetAllocations_CompanyId_PeriodId",
                schema: "planning",
                table: "BudgetAllocations",
                columns: new[] { "CompanyId", "PeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetAllocations_TenantId_OrganizationId_CompanyId",
                schema: "planning",
                table: "BudgetAllocations",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetCommitmentEvents_ActorId",
                schema: "planning",
                table: "BudgetCommitmentEvents",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetCommitmentEvents_CompanyId_AccountId_PeriodId_Date",
                schema: "planning",
                table: "BudgetCommitmentEvents",
                columns: new[] { "CompanyId", "AccountId", "PeriodId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetCommitmentEvents_CompanyId_AssetOperationId",
                schema: "planning",
                table: "BudgetCommitmentEvents",
                columns: new[] { "CompanyId", "AssetOperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetCommitmentEvents_CompanyId_EventKey",
                schema: "planning",
                table: "BudgetCommitmentEvents",
                columns: new[] { "CompanyId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetCommitmentEvents_CompanyId_PeriodId",
                schema: "planning",
                table: "BudgetCommitmentEvents",
                columns: new[] { "CompanyId", "PeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetCommitmentEvents_CompanyId_ProcurementLineId",
                schema: "planning",
                table: "BudgetCommitmentEvents",
                columns: new[] { "CompanyId", "ProcurementLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetCommitmentEvents_CompanyId_StockOperationId",
                schema: "planning",
                table: "BudgetCommitmentEvents",
                columns: new[] { "CompanyId", "StockOperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetCommitmentEvents_TenantId_OrganizationId_CompanyId",
                schema: "planning",
                table: "BudgetCommitmentEvents",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetOverrideGrants_CompanyId_AccountId",
                schema: "planning",
                table: "BudgetOverrideGrants",
                columns: new[] { "CompanyId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetOverrideGrants_CompanyId_Intent_AccountId",
                schema: "planning",
                table: "BudgetOverrideGrants",
                columns: new[] { "CompanyId", "Intent", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetOverrideGrants_CreatedBy",
                schema: "planning",
                table: "BudgetOverrideGrants",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetOverrideGrants_TenantId_OrganizationId_CompanyId",
                schema: "planning",
                table: "BudgetOverrideGrants",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetPolicies_CompanyId_AccountId_EffectiveDate",
                schema: "planning",
                table: "BudgetPolicies",
                columns: new[] { "CompanyId", "AccountId", "EffectiveDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetPolicies_CreatedBy",
                schema: "planning",
                table: "BudgetPolicies",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetPolicies_TenantId_OrganizationId_CompanyId",
                schema: "planning",
                table: "BudgetPolicies",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetVersions_ApprovedBy",
                schema: "planning",
                table: "BudgetVersions",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetVersions_CompanyId_ActiveAtPreparationId",
                schema: "planning",
                table: "BudgetVersions",
                columns: new[] { "CompanyId", "ActiveAtPreparationId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetVersions_CompanyId_BasisVersionId",
                schema: "planning",
                table: "BudgetVersions",
                columns: new[] { "CompanyId", "BasisVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetVersions_CompanyId_BudgetId",
                schema: "planning",
                table: "BudgetVersions",
                columns: new[] { "CompanyId", "BudgetId" },
                unique: true,
                filter: "\"State\"=4");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetVersions_CompanyId_BudgetId_Key",
                schema: "planning",
                table: "BudgetVersions",
                columns: new[] { "CompanyId", "BudgetId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetVersions_CompanyId_BudgetId_Number",
                schema: "planning",
                table: "BudgetVersions",
                columns: new[] { "CompanyId", "BudgetId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetVersions_CreatedBy",
                schema: "planning",
                table: "BudgetVersions",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetVersions_ReviewerId",
                schema: "planning",
                table: "BudgetVersions",
                column: "ReviewerId");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetVersions_TenantId_OrganizationId_CompanyId",
                schema: "planning",
                table: "BudgetVersions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialBudgets_CompanyId_Code",
                schema: "planning",
                table: "FinancialBudgets",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialBudgets_CompanyId_FiscalYearId_Kind",
                schema: "planning",
                table: "FinancialBudgets",
                columns: new[] { "CompanyId", "FiscalYearId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialBudgets_OwnerId",
                schema: "planning",
                table: "FinancialBudgets",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialBudgets_TenantId_OrganizationId_CompanyId",
                schema: "planning",
                table: "FinancialBudgets",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastVersions_ApprovedBy",
                schema: "planning",
                table: "ForecastVersions",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ForecastVersions_CompanyId_BasisId",
                schema: "planning",
                table: "ForecastVersions",
                columns: new[] { "CompanyId", "BasisId" });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastVersions_CompanyId_Code_Number",
                schema: "planning",
                table: "ForecastVersions",
                columns: new[] { "CompanyId", "Code", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForecastVersions_CompanyId_RequestKey",
                schema: "planning",
                table: "ForecastVersions",
                columns: new[] { "CompanyId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForecastVersions_CompanyId_ScenarioBaseId",
                schema: "planning",
                table: "ForecastVersions",
                columns: new[] { "CompanyId", "ScenarioBaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastVersions_CreatedBy",
                schema: "planning",
                table: "ForecastVersions",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ForecastVersions_TenantId_OrganizationId_CompanyId",
                schema: "planning",
                table: "ForecastVersions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });
            migrationBuilder.Sql(BudgetPlanningV1Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BudgetPlanningV1Sql.Down);
            migrationBuilder.DropTable(
                name: "BudgetAllocations",
                schema: "planning");

            migrationBuilder.DropTable(
                name: "BudgetCommitmentEvents",
                schema: "planning");

            migrationBuilder.DropTable(
                name: "BudgetOverrideGrants",
                schema: "planning");

            migrationBuilder.DropTable(
                name: "BudgetPolicies",
                schema: "planning");

            migrationBuilder.DropTable(
                name: "ForecastVersions",
                schema: "planning");

            migrationBuilder.DropTable(
                name: "BudgetVersions",
                schema: "planning");

            migrationBuilder.DropTable(
                name: "FinancialBudgets",
                schema: "planning");
        }
    }
}
