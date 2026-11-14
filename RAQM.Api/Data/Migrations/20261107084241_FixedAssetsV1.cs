using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixedAssetsV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fixed_assets");

            migrationBuilder.CreateTable(
                name: "AssetCategories",
                schema: "fixed_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Depreciable = table.Column<bool>(type: "boolean", nullable: false),
                    MandatoryClose = table.Column<bool>(type: "boolean", nullable: false),
                    Threshold = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    DepreciationExpenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImpairmentLossId = table.Column<Guid>(type: "uuid", nullable: false),
                    GainId = table.Column<Guid>(type: "uuid", nullable: false),
                    LossId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetCategories", x => x.Id);
                    table.UniqueConstraint("AK_AssetCategories_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_AssetCategory_Threshold", "\"Threshold\" >= 0");
                    table.ForeignKey(
                        name: "FK_AssetCategories_Accounts_CompanyId_DepreciationExpenseId",
                        columns: x => new { x.CompanyId, x.DepreciationExpenseId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetCategories_Accounts_CompanyId_GainId",
                        columns: x => new { x.CompanyId, x.GainId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetCategories_Accounts_CompanyId_ImpairmentLossId",
                        columns: x => new { x.CompanyId, x.ImpairmentLossId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetCategories_Accounts_CompanyId_LossId",
                        columns: x => new { x.CompanyId, x.LossId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetCategories_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetCategories_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetControlAccounts",
                schema: "fixed_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetControlAccounts", x => x.Id);
                    table.UniqueConstraint("AK_AssetControlAccounts_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_AssetControl_Role", "\"Role\" BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_AssetControlAccounts_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetControlAccounts_AssetCategories_CompanyId_CategoryId",
                        columns: x => new { x.CompanyId, x.CategoryId },
                        principalSchema: "fixed_assets",
                        principalTable: "AssetCategories",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetControlAccounts_Companies_TenantId_OrganizationId_Comp~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FixedAssetRegister",
                schema: "fixed_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AcquisitionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseEvidence = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AssignmentJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RecognitionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AvailableDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DisposalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedAssetRegister", x => x.Id);
                    table.UniqueConstraint("AK_FixedAssetRegister_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Asset_Lifecycle", " \"Status\" BETWEEN 0 AND 5 AND (\"Status\" >= 2)=(\"RecognitionDate\" IS NOT NULL) AND (\"Status\" >= 4)=(\"DisposalDate\" IS NOT NULL) AND (\"RecognitionDate\" IS NULL OR \"RecognitionDate\">=\"AcquisitionDate\") AND (\"Status\"<>3 OR \"AvailableDate\" IS NOT NULL) ");
                    table.ForeignKey(
                        name: "FK_FixedAssetRegister_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FixedAssetRegister_AssetCategories_CompanyId_CategoryId",
                        columns: x => new { x.CompanyId, x.CategoryId },
                        principalSchema: "fixed_assets",
                        principalTable: "AssetCategories",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FixedAssetRegister_Companies_TenantId_OrganizationId_Compan~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FixedAssetRegister_Parties_CompanyId_SupplierId",
                        columns: x => new { x.CompanyId, x.SupplierId },
                        principalSchema: "trading",
                        principalTable: "Parties",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetComponents",
                schema: "fixed_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ProposedCost = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    InitialResidual = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    LifeMonths = table.Column<int>(type: "integer", nullable: false),
                    OpeningDepreciation = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    OpeningImpairment = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetComponents", x => x.Id);
                    table.UniqueConstraint("AK_AssetComponents_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_AssetComponent_Money", " \"Number\">0 AND \"ProposedCost\">0 AND \"InitialResidual\">=0 AND \"InitialResidual\"<=\"ProposedCost\" AND \"LifeMonths\" BETWEEN 0 AND 1200 AND \"OpeningDepreciation\">=0 AND \"OpeningImpairment\">=0 AND \"OpeningDepreciation\"+\"OpeningImpairment\"<=\"ProposedCost\" ");
                    table.ForeignKey(
                        name: "FK_AssetComponents_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetComponents_FixedAssetRegister_CompanyId_AssetId",
                        columns: x => new { x.CompanyId, x.AssetId },
                        principalSchema: "fixed_assets",
                        principalTable: "FixedAssetRegister",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetOperations",
                schema: "fixed_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    PeriodId = table.Column<Guid>(type: "uuid", nullable: true),
                    OpeningJournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: true),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PostedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetOperations", x => x.Id);
                    table.UniqueConstraint("AK_AssetOperations_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_AssetOperation_State", " \"Kind\" BETWEEN 0 AND 11 AND \"State\" BETWEEN 0 AND 3 AND (\"Kind\"<>3 OR (\"PeriodId\" IS NOT NULL AND \"AssetId\" IS NULL)) AND (\"State\" NOT IN(1,2) OR (\"PlanJson\" IS NOT NULL AND \"ApprovedBy\" IS NOT NULL)) AND (\"State\"<>2 OR (\"Sequence\" IS NOT NULL AND \"PostedBy\" IS NOT NULL AND \"PostedAt\" IS NOT NULL)) ");
                    table.ForeignKey(
                        name: "FK_AssetOperations_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetOperations_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetOperations_AspNetUsers_PostedBy",
                        column: x => x.PostedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetOperations_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetOperations_FixedAssetRegister_CompanyId_AssetId",
                        columns: x => new { x.CompanyId, x.AssetId },
                        principalSchema: "fixed_assets",
                        principalTable: "FixedAssetRegister",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetOperations_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetOperations_Journals_CompanyId_OpeningJournalId",
                        columns: x => new { x.CompanyId, x.OpeningJournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetOperations_Periods_CompanyId_PeriodId",
                        columns: x => new { x.CompanyId, x.PeriodId },
                        principalSchema: "accounting",
                        principalTable: "Periods",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetMovements",
                schema: "fixed_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepreciationPeriodId = table.Column<Guid>(type: "uuid", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Depreciation = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Impairment = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    ShadowDepreciation = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Units = table.Column<decimal>(type: "numeric(16,6)", precision: 16, scale: 6, nullable: false),
                    RemainingOverride = table.Column<decimal>(type: "numeric(16,6)", precision: 16, scale: 6, nullable: true),
                    ResidualOverride = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    DepreciationCharge = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetMovements", x => x.Id);
                    table.UniqueConstraint("AK_AssetMovements_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_AssetMovement_Units", " \"Units\" BETWEEN 0 AND 1 AND \"DepreciationCharge\">=0 AND (\"RemainingOverride\" IS NULL OR \"RemainingOverride\">=0) AND (\"ResidualOverride\" IS NULL OR \"ResidualOverride\">=0) ");
                    table.ForeignKey(
                        name: "FK_AssetMovements_AssetComponents_CompanyId_ComponentId",
                        columns: x => new { x.CompanyId, x.ComponentId },
                        principalSchema: "fixed_assets",
                        principalTable: "AssetComponents",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetMovements_AssetOperations_CompanyId_OperationId",
                        columns: x => new { x.CompanyId, x.OperationId },
                        principalSchema: "fixed_assets",
                        principalTable: "AssetOperations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetMovements_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetMovements_Periods_CompanyId_DepreciationPeriodId",
                        columns: x => new { x.CompanyId, x.DepreciationPeriodId },
                        principalSchema: "accounting",
                        principalTable: "Periods",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetSourceClaims",
                schema: "fixed_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetSourceClaims", x => x.Id);
                    table.UniqueConstraint("AK_AssetSourceClaims_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_AssetSourceClaims_AssetOperations_CompanyId_OperationId",
                        columns: x => new { x.CompanyId, x.OperationId },
                        principalSchema: "fixed_assets",
                        principalTable: "AssetOperations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetSourceClaims_Companies_TenantId_OrganizationId_Company~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetSourceClaims_JournalLines_CompanyId_JournalLineId",
                        columns: x => new { x.CompanyId, x.JournalLineId },
                        principalSchema: "accounting",
                        principalTable: "JournalLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssetCategories_CompanyId_Code",
                schema: "fixed_assets",
                table: "AssetCategories",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetCategories_CompanyId_DepreciationExpenseId",
                schema: "fixed_assets",
                table: "AssetCategories",
                columns: new[] { "CompanyId", "DepreciationExpenseId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetCategories_CompanyId_GainId",
                schema: "fixed_assets",
                table: "AssetCategories",
                columns: new[] { "CompanyId", "GainId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetCategories_CompanyId_ImpairmentLossId",
                schema: "fixed_assets",
                table: "AssetCategories",
                columns: new[] { "CompanyId", "ImpairmentLossId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetCategories_CompanyId_LossId",
                schema: "fixed_assets",
                table: "AssetCategories",
                columns: new[] { "CompanyId", "LossId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetCategories_CreatedBy",
                schema: "fixed_assets",
                table: "AssetCategories",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_AssetCategories_TenantId_OrganizationId_CompanyId",
                schema: "fixed_assets",
                table: "AssetCategories",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetComponents_CompanyId_AssetId_Number",
                schema: "fixed_assets",
                table: "AssetComponents",
                columns: new[] { "CompanyId", "AssetId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetComponents_TenantId_OrganizationId_CompanyId",
                schema: "fixed_assets",
                table: "AssetComponents",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetControlAccounts_CompanyId_AccountId",
                schema: "fixed_assets",
                table: "AssetControlAccounts",
                columns: new[] { "CompanyId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetControlAccounts_CompanyId_CategoryId_Role",
                schema: "fixed_assets",
                table: "AssetControlAccounts",
                columns: new[] { "CompanyId", "CategoryId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetControlAccounts_TenantId_OrganizationId_CompanyId",
                schema: "fixed_assets",
                table: "AssetControlAccounts",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetMovements_CompanyId_ComponentId_DepreciationPeriodId",
                schema: "fixed_assets",
                table: "AssetMovements",
                columns: new[] { "CompanyId", "ComponentId", "DepreciationPeriodId" },
                unique: true,
                filter: " \"DepreciationPeriodId\" IS NOT NULL ");

            migrationBuilder.CreateIndex(
                name: "IX_AssetMovements_CompanyId_DepreciationPeriodId",
                schema: "fixed_assets",
                table: "AssetMovements",
                columns: new[] { "CompanyId", "DepreciationPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetMovements_CompanyId_OperationId_ComponentId",
                schema: "fixed_assets",
                table: "AssetMovements",
                columns: new[] { "CompanyId", "OperationId", "ComponentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetMovements_TenantId_OrganizationId_CompanyId",
                schema: "fixed_assets",
                table: "AssetMovements",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperation_Initial",
                schema: "fixed_assets",
                table: "AssetOperations",
                columns: new[] { "CompanyId", "AssetId" },
                unique: true,
                filter: " \"Kind\"=0 AND \"State\"<>3 ");

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperations_ApprovedBy",
                schema: "fixed_assets",
                table: "AssetOperations",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperations_CompanyId_AssetId",
                schema: "fixed_assets",
                table: "AssetOperations",
                columns: new[] { "CompanyId", "AssetId" },
                unique: true,
                filter: " \"Kind\" IN (10,11) AND \"State\"=2 ");

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperations_CompanyId_AssetId_Date",
                schema: "fixed_assets",
                table: "AssetOperations",
                columns: new[] { "CompanyId", "AssetId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperations_CompanyId_JournalId",
                schema: "fixed_assets",
                table: "AssetOperations",
                columns: new[] { "CompanyId", "JournalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperations_CompanyId_OpeningJournalId",
                schema: "fixed_assets",
                table: "AssetOperations",
                columns: new[] { "CompanyId", "OpeningJournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperations_CompanyId_PeriodId",
                schema: "fixed_assets",
                table: "AssetOperations",
                columns: new[] { "CompanyId", "PeriodId" },
                unique: true,
                filter: " \"Kind\"=3 AND \"State\"<>3 ");

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperations_CompanyId_RequestKey",
                schema: "fixed_assets",
                table: "AssetOperations",
                columns: new[] { "CompanyId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperations_CompanyId_Sequence",
                schema: "fixed_assets",
                table: "AssetOperations",
                columns: new[] { "CompanyId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperations_CreatedBy",
                schema: "fixed_assets",
                table: "AssetOperations",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperations_PostedBy",
                schema: "fixed_assets",
                table: "AssetOperations",
                column: "PostedBy");

            migrationBuilder.CreateIndex(
                name: "IX_AssetOperations_TenantId_OrganizationId_CompanyId",
                schema: "fixed_assets",
                table: "AssetOperations",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetSourceClaims_CompanyId_JournalLineId",
                schema: "fixed_assets",
                table: "AssetSourceClaims",
                columns: new[] { "CompanyId", "JournalLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetSourceClaims_CompanyId_OperationId",
                schema: "fixed_assets",
                table: "AssetSourceClaims",
                columns: new[] { "CompanyId", "OperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetSourceClaims_TenantId_OrganizationId_CompanyId",
                schema: "fixed_assets",
                table: "AssetSourceClaims",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssetRegister_CompanyId_CategoryId_Status",
                schema: "fixed_assets",
                table: "FixedAssetRegister",
                columns: new[] { "CompanyId", "CategoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssetRegister_CompanyId_Code",
                schema: "fixed_assets",
                table: "FixedAssetRegister",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssetRegister_CompanyId_SupplierId",
                schema: "fixed_assets",
                table: "FixedAssetRegister",
                columns: new[] { "CompanyId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssetRegister_CreatedBy",
                schema: "fixed_assets",
                table: "FixedAssetRegister",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssetRegister_TenantId_OrganizationId_CompanyId",
                schema: "fixed_assets",
                table: "FixedAssetRegister",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });
            migrationBuilder.Sql(FixedAssetsV1Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FixedAssetsV1Sql.Down);
            migrationBuilder.DropTable(
                name: "AssetControlAccounts",
                schema: "fixed_assets");

            migrationBuilder.DropTable(
                name: "AssetMovements",
                schema: "fixed_assets");

            migrationBuilder.DropTable(
                name: "AssetSourceClaims",
                schema: "fixed_assets");

            migrationBuilder.DropTable(
                name: "AssetComponents",
                schema: "fixed_assets");

            migrationBuilder.DropTable(
                name: "AssetOperations",
                schema: "fixed_assets");

            migrationBuilder.DropTable(
                name: "FixedAssetRegister",
                schema: "fixed_assets");

            migrationBuilder.DropTable(
                name: "AssetCategories",
                schema: "fixed_assets");
        }
    }
}
