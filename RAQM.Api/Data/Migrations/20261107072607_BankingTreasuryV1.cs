using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BankingTreasuryV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "banking");

            migrationBuilder.AddColumn<Guid>(
                name: "BankAccountId",
                schema: "trading",
                table: "Payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Banks",
                schema: "banking",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Swift = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Banks", x => x.Id);
                    table.UniqueConstraint("AK_Banks_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Banks_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TreasuryAccounts",
                schema: "banking",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    BankProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    LedgerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustodianId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AccountMask = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    IbanMask = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    IdentifierDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IbanDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryAccounts", x => x.Id);
                    table.UniqueConstraint("AK_TreasuryAccounts_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_TreasuryAccount_Kind", "(\"Kind\"=0 AND \"BankProfileId\" IS NOT NULL AND (\"IdentifierDigest\" IS NOT NULL OR \"IbanDigest\" IS NOT NULL) AND \"CustodianId\" IS NULL) OR (\"Kind\" IN(1,2) AND \"BankProfileId\" IS NULL AND \"IdentifierDigest\" IS NULL AND \"IbanDigest\" IS NULL AND (\"Kind\"=2)=(\"CustodianId\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_TreasuryAccounts_Accounts_CompanyId_LedgerAccountId",
                        columns: x => new { x.CompanyId, x.LedgerAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TreasuryAccounts_AspNetUsers_CustodianId",
                        column: x => x.CustodianId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TreasuryAccounts_Banks_CompanyId_BankProfileId",
                        columns: x => new { x.CompanyId, x.BankProfileId },
                        principalSchema: "banking",
                        principalTable: "Banks",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TreasuryAccounts_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CashCounts",
                schema: "banking",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CountDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BookAmount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    CountedAmount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Evidence = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CountedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CountedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashCounts", x => x.Id);
                    table.UniqueConstraint("AK_CashCounts_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_CashCount_Amount", " \"CountedAmount\" >= 0");
                    table.ForeignKey(
                        name: "FK_CashCounts_AspNetUsers_CountedBy",
                        column: x => x.CountedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashCounts_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashCounts_TreasuryAccounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "banking",
                        principalTable: "TreasuryAccounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Statements",
                schema: "banking",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Start = table.Column<DateOnly>(type: "date", nullable: false),
                    End = table.Column<DateOnly>(type: "date", nullable: false),
                    Opening = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Closing = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LineCount = table.Column<int>(type: "integer", nullable: false),
                    ImportedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Statements", x => x.Id);
                    table.UniqueConstraint("AK_Statements_CompanyId_BankAccountId_Id", x => new { x.CompanyId, x.BankAccountId, x.Id });
                    table.UniqueConstraint("AK_Statements_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Statement_Dates", "\"Start\" <= \"End\" AND \"LineCount\" BETWEEN 0 AND 5000");
                    table.ForeignKey(
                        name: "FK_Statements_AspNetUsers_ImportedBy",
                        column: x => x.ImportedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Statements_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Statements_TreasuryAccounts_CompanyId_BankAccountId",
                        columns: x => new { x.CompanyId, x.BankAccountId },
                        principalSchema: "banking",
                        principalTable: "TreasuryAccounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                schema: "banking",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StatementId = table.Column<Guid>(type: "uuid", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    FinalizedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinalizedBook = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    FinalizedDeposits = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    FinalizedWithdrawals = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Banking_Sessions", x => x.Id);
                    table.UniqueConstraint("AK_Sessions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Reconciliation_State", "(\"State\" IN(0,1) AND \"FinalizedAt\" IS NULL) OR (\"State\"=2 AND \"FinalizedAt\" IS NOT NULL AND \"FinalizedBy\" IS NOT NULL AND \"FinalizedBook\" IS NOT NULL AND \"FinalizedDeposits\" IS NOT NULL AND \"FinalizedWithdrawals\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Sessions_AspNetUsers_FinalizedBy",
                        column: x => x.FinalizedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sessions_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sessions_Statements_CompanyId_BankAccountId_StatementId",
                        columns: x => new { x.CompanyId, x.BankAccountId, x.StatementId },
                        principalSchema: "banking",
                        principalTable: "Statements",
                        principalColumns: new[] { "CompanyId", "BankAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StatementLines",
                schema: "banking",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StatementId = table.Column<Guid>(type: "uuid", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Row = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    BookingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ValueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OriginalFields = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    StableExternalId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatementLines", x => x.Id);
                    table.UniqueConstraint("AK_StatementLines_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_StatementLine_Amount", " \"Amount\" <> 0 AND \"Row\" > 1");
                    table.ForeignKey(
                        name: "FK_StatementLines_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StatementLines_Statements_CompanyId_BankAccountId_Statement~",
                        columns: x => new { x.CompanyId, x.BankAccountId, x.StatementId },
                        principalSchema: "banking",
                        principalTable: "Statements",
                        principalColumns: new[] { "CompanyId", "BankAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Matches",
                schema: "banking",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatementLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ConfirmedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Matches", x => x.Id);
                    table.UniqueConstraint("AK_Matches_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Matches_AspNetUsers_ConfirmedBy",
                        column: x => x.ConfirmedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Matches_AspNetUsers_ReleasedBy",
                        column: x => x.ReleasedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Matches_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Matches_Sessions_CompanyId_SessionId",
                        columns: x => new { x.CompanyId, x.SessionId },
                        principalSchema: "banking",
                        principalTable: "Sessions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Matches_StatementLines_CompanyId_StatementLineId",
                        columns: x => new { x.CompanyId, x.StatementLineId },
                        principalSchema: "banking",
                        principalTable: "StatementLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Movements",
                schema: "banking",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: true),
                    OffsetLedgerId = table.Column<Guid>(type: "uuid", nullable: true),
                    StatementLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    CashCountId = table.Column<Guid>(type: "uuid", nullable: true),
                    DebitLedgerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreditLedgerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalJournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReversalHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApprovalHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PostedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Movements", x => x.Id);
                    table.UniqueConstraint("AK_Movements_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_TreasuryMovement_Amount", " \"Amount\" > 0 AND \"DebitLedgerId\" <> \"CreditLedgerId\" AND \"Kind\" BETWEEN 0 AND 6");
                    table.CheckConstraint("CK_TreasuryMovement_State", "(\"State\"=0 AND \"JournalId\" IS NULL AND \"ReversalJournalId\" IS NULL) OR (\"State\"=1 AND \"JournalId\" IS NOT NULL AND \"ReversalJournalId\" IS NULL) OR (\"State\"=2 AND \"JournalId\" IS NOT NULL AND \"ReversalJournalId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Movements_Accounts_CompanyId_CreditLedgerId",
                        columns: x => new { x.CompanyId, x.CreditLedgerId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_Accounts_CompanyId_DebitLedgerId",
                        columns: x => new { x.CompanyId, x.DebitLedgerId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_Accounts_CompanyId_OffsetLedgerId",
                        columns: x => new { x.CompanyId, x.OffsetLedgerId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_AspNetUsers_PostedBy",
                        column: x => x.PostedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_AspNetUsers_ReversedBy",
                        column: x => x.ReversedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_CashCounts_CompanyId_CashCountId",
                        columns: x => new { x.CompanyId, x.CashCountId },
                        principalSchema: "banking",
                        principalTable: "CashCounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_Journals_CompanyId_ReversalJournalId",
                        columns: x => new { x.CompanyId, x.ReversalJournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_StatementLines_CompanyId_StatementLineId",
                        columns: x => new { x.CompanyId, x.StatementLineId },
                        principalSchema: "banking",
                        principalTable: "StatementLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_TreasuryAccounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "banking",
                        principalTable: "TreasuryAccounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_TreasuryAccounts_CompanyId_DestinationId",
                        columns: x => new { x.CompanyId, x.DestinationId },
                        principalSchema: "banking",
                        principalTable: "TreasuryAccounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MatchItems",
                schema: "banking",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Released = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchItems", x => x.Id);
                    table.UniqueConstraint("AK_MatchItems_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_MatchItems_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MatchItems_JournalLines_CompanyId_JournalLineId",
                        columns: x => new { x.CompanyId, x.JournalLineId },
                        principalSchema: "accounting",
                        principalTable: "JournalLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MatchItems_Matches_CompanyId_MatchId",
                        columns: x => new { x.CompanyId, x.MatchId },
                        principalSchema: "banking",
                        principalTable: "Matches",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_BankAccountId",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "BankAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Banks_CompanyId_Code",
                schema: "banking",
                table: "Banks",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Banks_TenantId_OrganizationId_CompanyId",
                schema: "banking",
                table: "Banks",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CashCounts_CompanyId_AccountId_CountDate_CountedAt",
                schema: "banking",
                table: "CashCounts",
                columns: new[] { "CompanyId", "AccountId", "CountDate", "CountedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CashCounts_CountedBy",
                schema: "banking",
                table: "CashCounts",
                column: "CountedBy");

            migrationBuilder.CreateIndex(
                name: "IX_CashCounts_TenantId_OrganizationId_CompanyId",
                schema: "banking",
                table: "CashCounts",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Matches_CompanyId_SessionId",
                schema: "banking",
                table: "Matches",
                columns: new[] { "CompanyId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Matches_CompanyId_StatementLineId",
                schema: "banking",
                table: "Matches",
                columns: new[] { "CompanyId", "StatementLineId" },
                unique: true,
                filter: " \"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Matches_ConfirmedBy",
                schema: "banking",
                table: "Matches",
                column: "ConfirmedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Matches_ReleasedBy",
                schema: "banking",
                table: "Matches",
                column: "ReleasedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Matches_TenantId_OrganizationId_CompanyId",
                schema: "banking",
                table: "Matches",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchItems_CompanyId_JournalLineId",
                schema: "banking",
                table: "MatchItems",
                columns: new[] { "CompanyId", "JournalLineId" },
                unique: true,
                filter: "NOT \"Released\" ");

            migrationBuilder.CreateIndex(
                name: "IX_MatchItems_CompanyId_MatchId",
                schema: "banking",
                table: "MatchItems",
                columns: new[] { "CompanyId", "MatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchItems_TenantId_OrganizationId_CompanyId",
                schema: "banking",
                table: "MatchItems",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CompanyId_AccountId",
                schema: "banking",
                table: "Movements",
                columns: new[] { "CompanyId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CompanyId_CashCountId",
                schema: "banking",
                table: "Movements",
                columns: new[] { "CompanyId", "CashCountId" },
                unique: true,
                filter: " \"State\" <> 2");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CompanyId_CreditLedgerId",
                schema: "banking",
                table: "Movements",
                columns: new[] { "CompanyId", "CreditLedgerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CompanyId_DebitLedgerId",
                schema: "banking",
                table: "Movements",
                columns: new[] { "CompanyId", "DebitLedgerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CompanyId_DestinationId",
                schema: "banking",
                table: "Movements",
                columns: new[] { "CompanyId", "DestinationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CompanyId_JournalId",
                schema: "banking",
                table: "Movements",
                columns: new[] { "CompanyId", "JournalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CompanyId_OffsetLedgerId",
                schema: "banking",
                table: "Movements",
                columns: new[] { "CompanyId", "OffsetLedgerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CompanyId_RequestKey",
                schema: "banking",
                table: "Movements",
                columns: new[] { "CompanyId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CompanyId_ReversalJournalId",
                schema: "banking",
                table: "Movements",
                columns: new[] { "CompanyId", "ReversalJournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CompanyId_StatementLineId",
                schema: "banking",
                table: "Movements",
                columns: new[] { "CompanyId", "StatementLineId" },
                unique: true,
                filter: " \"State\" <> 2");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_CreatedBy",
                schema: "banking",
                table: "Movements",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_PostedBy",
                schema: "banking",
                table: "Movements",
                column: "PostedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_ReversedBy",
                schema: "banking",
                table: "Movements",
                column: "ReversedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_TenantId_OrganizationId_CompanyId",
                schema: "banking",
                table: "Movements",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_CompanyId_BankAccountId",
                schema: "banking",
                table: "Sessions",
                columns: new[] { "CompanyId", "BankAccountId" },
                unique: true,
                filter: " \"State\" <> 2");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_CompanyId_BankAccountId_StatementId",
                schema: "banking",
                table: "Sessions",
                columns: new[] { "CompanyId", "BankAccountId", "StatementId" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_CompanyId_StatementId",
                schema: "banking",
                table: "Sessions",
                columns: new[] { "CompanyId", "StatementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_FinalizedBy",
                schema: "banking",
                table: "Sessions",
                column: "FinalizedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TenantId_OrganizationId_CompanyId",
                schema: "banking",
                table: "Sessions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_CompanyId_BankAccountId_StableExternalId",
                schema: "banking",
                table: "StatementLines",
                columns: new[] { "CompanyId", "BankAccountId", "StableExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_CompanyId_BankAccountId_StatementId",
                schema: "banking",
                table: "StatementLines",
                columns: new[] { "CompanyId", "BankAccountId", "StatementId" });

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_CompanyId_StatementId_Row",
                schema: "banking",
                table: "StatementLines",
                columns: new[] { "CompanyId", "StatementId", "Row" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_CompanyId_StatementId_Sequence",
                schema: "banking",
                table: "StatementLines",
                columns: new[] { "CompanyId", "StatementId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_TenantId_OrganizationId_CompanyId",
                schema: "banking",
                table: "StatementLines",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Statements_CompanyId_BankAccountId_Digest",
                schema: "banking",
                table: "Statements",
                columns: new[] { "CompanyId", "BankAccountId", "Digest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Statements_CompanyId_BankAccountId_ExternalId",
                schema: "banking",
                table: "Statements",
                columns: new[] { "CompanyId", "BankAccountId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Statements_CompanyId_BankAccountId_RequestKey",
                schema: "banking",
                table: "Statements",
                columns: new[] { "CompanyId", "BankAccountId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Statements_ImportedBy",
                schema: "banking",
                table: "Statements",
                column: "ImportedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Statements_TenantId_OrganizationId_CompanyId",
                schema: "banking",
                table: "Statements",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryAccounts_CompanyId_BankProfileId_IdentifierDigest",
                schema: "banking",
                table: "TreasuryAccounts",
                columns: new[] { "CompanyId", "BankProfileId", "IdentifierDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryAccounts_CompanyId_Code",
                schema: "banking",
                table: "TreasuryAccounts",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryAccounts_CompanyId_IbanDigest",
                schema: "banking",
                table: "TreasuryAccounts",
                columns: new[] { "CompanyId", "IbanDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryAccounts_CompanyId_LedgerAccountId",
                schema: "banking",
                table: "TreasuryAccounts",
                columns: new[] { "CompanyId", "LedgerAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryAccounts_CustodianId",
                schema: "banking",
                table: "TreasuryAccounts",
                column: "CustodianId");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryAccounts_TenantId_OrganizationId_CompanyId",
                schema: "banking",
                table: "TreasuryAccounts",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_TreasuryAccounts_CompanyId_BankAccountId",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "BankAccountId" },
                principalSchema: "banking",
                principalTable: "TreasuryAccounts",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql(BankingTreasuryV1Sql.Up);
            migrationBuilder.Sql(BankingTreasuryV1Sql.Posting);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BankingTreasuryV1Sql.Down);
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_TreasuryAccounts_CompanyId_BankAccountId",
                schema: "trading",
                table: "Payments");

            migrationBuilder.DropTable(
                name: "MatchItems",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "Movements",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "Matches",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "CashCounts",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "Sessions",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "StatementLines",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "Statements",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "TreasuryAccounts",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "Banks",
                schema: "banking");

            migrationBuilder.DropIndex(
                name: "IX_Payments_CompanyId_BankAccountId",
                schema: "trading",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "BankAccountId",
                schema: "trading",
                table: "Payments");

        }
    }
}
