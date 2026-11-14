using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiCurrencyV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "foreign_exchange");

            migrationBuilder.CreateTable(
                name: "FxAccounts",
                schema: "foreign_exchange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxAccounts", x => x.Id);
                    table.UniqueConstraint("AK_FxAccounts_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_FxAccount_Role", " \"Role\" BETWEEN 0 AND 9 ");
                    table.ForeignKey(
                        name: "FK_FxAccounts_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxAccounts_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FxConfigurations",
                schema: "foreign_exchange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FunctionalCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SpotFallbackDays = table.Column<int>(type: "integer", nullable: false),
                    RequireCloseRevaluation = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxConfigurations", x => x.Id);
                    table.UniqueConstraint("AK_FxConfigurations_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_FxConfiguration_Fallback", " \"SpotFallbackDays\" BETWEEN 0 AND 7 ");
                    table.ForeignKey(
                        name: "FK_FxConfigurations_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FxCurrencies",
                schema: "foreign_exchange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxCurrencies", x => x.Id);
                    table.UniqueConstraint("AK_FxCurrencies_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_FxCurrencies_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FxPositions",
                schema: "foreign_exchange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrencyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxPositions", x => x.Id);
                    table.UniqueConstraint("AK_FxPositions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_FxPosition_Type", "\"Kind\" BETWEEN 0 AND 6 AND ((\"Kind\"=6)=(\"PartyId\" IS NULL)) AND ((\"Kind\"=6)=(\"DueDate\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_FxPositions_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxPositions_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxPositions_FxCurrencies_CompanyId_CurrencyId",
                        columns: x => new { x.CompanyId, x.CurrencyId },
                        principalSchema: "foreign_exchange",
                        principalTable: "FxCurrencies",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxPositions_Parties_CompanyId_PartyId",
                        columns: x => new { x.CompanyId, x.PartyId },
                        principalSchema: "trading",
                        principalTable: "Parties",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FxRates",
                schema: "foreign_exchange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrencyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(20,10)", precision: 20, scale: 10, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ReplacesId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Evidence = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxRates", x => x.Id);
                    table.UniqueConstraint("AK_FxRates_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_FxRate_State", "\"Type\" IN(0,1) AND \"State\" BETWEEN 0 AND 3 AND \"ExchangeRate\">0 AND \"Revision\">0\n  AND (\"State\" IN(0,3) OR (\"ApprovedBy\" IS NOT NULL AND \"CreatedBy\"<>\"ApprovedBy\"))");
                    table.ForeignKey(
                        name: "FK_FxRates_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxRates_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxRates_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxRates_FxCurrencies_CompanyId_CurrencyId",
                        columns: x => new { x.CompanyId, x.CurrencyId },
                        principalSchema: "foreign_exchange",
                        principalTable: "FxCurrencies",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxRates_FxRates_CompanyId_ReplacesId",
                        columns: x => new { x.CompanyId, x.ReplacesId },
                        principalSchema: "foreign_exchange",
                        principalTable: "FxRates",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FxBankStatements",
                schema: "foreign_exchange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cutoff = table.Column<DateOnly>(type: "date", nullable: false),
                    StatementForeign = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    BookForeign = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    BookBase = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Evidence = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxBankStatements", x => x.Id);
                    table.UniqueConstraint("AK_FxBankStatements_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_FxBankStatement_Amounts", "\"StatementForeign\">=0 AND \"BookForeign\">=0 AND \"BookBase\">=0");
                    table.ForeignKey(
                        name: "FK_FxBankStatements_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxBankStatements_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxBankStatements_FxPositions_CompanyId_PositionId",
                        columns: x => new { x.CompanyId, x.PositionId },
                        principalSchema: "foreign_exchange",
                        principalTable: "FxPositions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FxOperations",
                schema: "foreign_exchange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginalId = table.Column<Guid>(type: "uuid", nullable: true),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReferenceKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    RequestKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputJson = table.Column<string>(type: "jsonb", nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PostedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxOperations", x => x.Id);
                    table.UniqueConstraint("AK_FxOperations_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_FxOperation_State", "\"Kind\" BETWEEN 0 AND 8 AND \"State\" BETWEEN 0 AND 3 AND ((\"State\"=2)=(\"Sequence\" IS NOT NULL))\n  AND (\"State\"<>2 OR \"PostedBy\" IS NOT NULL) AND (\"State\" NOT IN (1,2) OR\n    (\"ApprovedBy\" IS NOT NULL AND \"ApprovedBy\"<>\"CreatedBy\" AND \"PlanJson\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_FxOperations_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxOperations_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxOperations_AspNetUsers_PostedBy",
                        column: x => x.PostedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxOperations_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxOperations_FxOperations_CompanyId_OriginalId",
                        columns: x => new { x.CompanyId, x.OriginalId },
                        principalSchema: "foreign_exchange",
                        principalTable: "FxOperations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxOperations_FxPositions_CompanyId_PositionId",
                        columns: x => new { x.CompanyId, x.PositionId },
                        principalSchema: "foreign_exchange",
                        principalTable: "FxPositions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxOperations_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxOperations_Parties_CompanyId_PartyId",
                        columns: x => new { x.CompanyId, x.PartyId },
                        principalSchema: "trading",
                        principalTable: "Parties",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FxEffects",
                schema: "foreign_exchange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RateId = table.Column<Guid>(type: "uuid", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ForeignDelta = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    BaseDelta = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxEffects", x => x.Id);
                    table.UniqueConstraint("AK_FxEffects_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_FxEffect_Value", "\"Sequence\">0 AND (\"BaseDelta\"<>0 OR \"ForeignDelta\"<>0)");
                    table.ForeignKey(
                        name: "FK_FxEffects_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxEffects_FxOperations_CompanyId_OperationId",
                        columns: x => new { x.CompanyId, x.OperationId },
                        principalSchema: "foreign_exchange",
                        principalTable: "FxOperations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxEffects_FxPositions_CompanyId_PositionId",
                        columns: x => new { x.CompanyId, x.PositionId },
                        principalSchema: "foreign_exchange",
                        principalTable: "FxPositions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FxEffects_FxRates_CompanyId_RateId",
                        columns: x => new { x.CompanyId, x.RateId },
                        principalSchema: "foreign_exchange",
                        principalTable: "FxRates",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FxAccounts_CompanyId_AccountId",
                schema: "foreign_exchange",
                table: "FxAccounts",
                columns: new[] { "CompanyId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxAccounts_CompanyId_Role",
                schema: "foreign_exchange",
                table: "FxAccounts",
                columns: new[] { "CompanyId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxAccounts_TenantId_OrganizationId_CompanyId",
                schema: "foreign_exchange",
                table: "FxAccounts",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxBankStatements_ActorId",
                schema: "foreign_exchange",
                table: "FxBankStatements",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_FxBankStatements_CompanyId_PositionId_Cutoff",
                schema: "foreign_exchange",
                table: "FxBankStatements",
                columns: new[] { "CompanyId", "PositionId", "Cutoff" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxBankStatements_TenantId_OrganizationId_CompanyId",
                schema: "foreign_exchange",
                table: "FxBankStatements",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxConfigurations_CompanyId",
                schema: "foreign_exchange",
                table: "FxConfigurations",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxConfigurations_TenantId_OrganizationId_CompanyId",
                schema: "foreign_exchange",
                table: "FxConfigurations",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxCurrencies_CompanyId_Code",
                schema: "foreign_exchange",
                table: "FxCurrencies",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxCurrencies_TenantId_OrganizationId_CompanyId",
                schema: "foreign_exchange",
                table: "FxCurrencies",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxEffects_CompanyId_OperationId_PositionId",
                schema: "foreign_exchange",
                table: "FxEffects",
                columns: new[] { "CompanyId", "OperationId", "PositionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxEffects_CompanyId_PositionId_Date_Sequence",
                schema: "foreign_exchange",
                table: "FxEffects",
                columns: new[] { "CompanyId", "PositionId", "Date", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_FxEffects_CompanyId_RateId",
                schema: "foreign_exchange",
                table: "FxEffects",
                columns: new[] { "CompanyId", "RateId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxEffects_TenantId_OrganizationId_CompanyId",
                schema: "foreign_exchange",
                table: "FxEffects",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxOperations_ApprovedBy",
                schema: "foreign_exchange",
                table: "FxOperations",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_FxOperations_CompanyId_Date",
                schema: "foreign_exchange",
                table: "FxOperations",
                columns: new[] { "CompanyId", "Date" },
                unique: true,
                filter: "\"Kind\"=7 AND \"State\"=2 AND \"OriginalId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FxOperations_CompanyId_JournalId",
                schema: "foreign_exchange",
                table: "FxOperations",
                columns: new[] { "CompanyId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxOperations_CompanyId_OriginalId_Kind",
                schema: "foreign_exchange",
                table: "FxOperations",
                columns: new[] { "CompanyId", "OriginalId", "Kind" },
                unique: true,
                filter: "\"Kind\" IN(4,7,8) AND \"State\"=2");

            migrationBuilder.CreateIndex(
                name: "IX_FxOperations_CompanyId_PartyId_ReferenceKey",
                schema: "foreign_exchange",
                table: "FxOperations",
                columns: new[] { "CompanyId", "PartyId", "ReferenceKey" },
                unique: true,
                filter: "\"Kind\" IN(0,1) AND \"State\"<>3");

            migrationBuilder.CreateIndex(
                name: "IX_FxOperations_CompanyId_PositionId",
                schema: "foreign_exchange",
                table: "FxOperations",
                columns: new[] { "CompanyId", "PositionId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxOperations_CompanyId_RequestKey",
                schema: "foreign_exchange",
                table: "FxOperations",
                columns: new[] { "CompanyId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxOperations_CompanyId_Sequence",
                schema: "foreign_exchange",
                table: "FxOperations",
                columns: new[] { "CompanyId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxOperations_CreatedBy",
                schema: "foreign_exchange",
                table: "FxOperations",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_FxOperations_PostedBy",
                schema: "foreign_exchange",
                table: "FxOperations",
                column: "PostedBy");

            migrationBuilder.CreateIndex(
                name: "IX_FxOperations_TenantId_OrganizationId_CompanyId",
                schema: "foreign_exchange",
                table: "FxOperations",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxPositions_CompanyId_AccountId",
                schema: "foreign_exchange",
                table: "FxPositions",
                columns: new[] { "CompanyId", "AccountId" },
                unique: true,
                filter: "\"Kind\"=6");

            migrationBuilder.CreateIndex(
                name: "IX_FxPositions_CompanyId_Code",
                schema: "foreign_exchange",
                table: "FxPositions",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxPositions_CompanyId_CurrencyId",
                schema: "foreign_exchange",
                table: "FxPositions",
                columns: new[] { "CompanyId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxPositions_CompanyId_PartyId",
                schema: "foreign_exchange",
                table: "FxPositions",
                columns: new[] { "CompanyId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxPositions_TenantId_OrganizationId_CompanyId",
                schema: "foreign_exchange",
                table: "FxPositions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxRates_ApprovedBy",
                schema: "foreign_exchange",
                table: "FxRates",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_FxRates_CompanyId_CurrencyId_Type_Date",
                schema: "foreign_exchange",
                table: "FxRates",
                columns: new[] { "CompanyId", "CurrencyId", "Type", "Date" },
                unique: true,
                filter: "\"State\"=1");

            migrationBuilder.CreateIndex(
                name: "IX_FxRates_CompanyId_CurrencyId_Type_Date_Revision",
                schema: "foreign_exchange",
                table: "FxRates",
                columns: new[] { "CompanyId", "CurrencyId", "Type", "Date", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxRates_CompanyId_ReplacesId",
                schema: "foreign_exchange",
                table: "FxRates",
                columns: new[] { "CompanyId", "ReplacesId" });

            migrationBuilder.CreateIndex(
                name: "IX_FxRates_CreatedBy",
                schema: "foreign_exchange",
                table: "FxRates",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_FxRates_TenantId_OrganizationId_CompanyId",
                schema: "foreign_exchange",
                table: "FxRates",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });
            migrationBuilder.Sql(MultiCurrencyV1Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(MultiCurrencyV1Sql.Down);
            migrationBuilder.DropTable(
                name: "FxAccounts",
                schema: "foreign_exchange");

            migrationBuilder.DropTable(
                name: "FxBankStatements",
                schema: "foreign_exchange");

            migrationBuilder.DropTable(
                name: "FxConfigurations",
                schema: "foreign_exchange");

            migrationBuilder.DropTable(
                name: "FxEffects",
                schema: "foreign_exchange");

            migrationBuilder.DropTable(
                name: "FxOperations",
                schema: "foreign_exchange");

            migrationBuilder.DropTable(
                name: "FxRates",
                schema: "foreign_exchange");

            migrationBuilder.DropTable(
                name: "FxPositions",
                schema: "foreign_exchange");

            migrationBuilder.DropTable(
                name: "FxCurrencies",
                schema: "foreign_exchange");
        }
    }
}
