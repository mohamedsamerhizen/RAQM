using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TradingSubledgerV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "trading");

            migrationBuilder.CreateTable(
                name: "AccountMappings",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMappings", x => x.Id);
                    table.UniqueConstraint("AK_AccountMappings_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_TradingRole", "\"Role\" BETWEEN 0 AND 9");
                    table.ForeignKey(
                        name: "FK_AccountMappings_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountMappings_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountMappings_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Parties",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    BillingAddress = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TaxIdentifier = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    PaymentTermsDays = table.Column<int>(type: "integer", nullable: false),
                    CreditLimit = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ControlAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefaultAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parties", x => x.Id);
                    table.UniqueConstraint("AK_Parties_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Party_Configuration", "\"Kind\" IN(0,1) AND \"PaymentTermsDays\" BETWEEN 0 AND 365 AND (\"CreditLimit\" IS NULL OR \"CreditLimit\">=0)");
                    table.ForeignKey(
                        name: "FK_Parties_Accounts_CompanyId_ControlAccountId",
                        columns: x => new { x.CompanyId, x.ControlAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Parties_Accounts_CompanyId_DefaultAccountId",
                        columns: x => new { x.CompanyId, x.DefaultAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Parties_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Parties_AspNetUsers_UpdatedBy",
                        column: x => x.UpdatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Parties_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxCodes",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OutputAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    InputAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxCodes", x => x.Id);
                    table.UniqueConstraint("AK_TaxCodes_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_TaxCodes_Accounts_CompanyId_InputAccountId",
                        columns: x => new { x.CompanyId, x.InputAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxCodes_Accounts_CompanyId_OutputAccountId",
                        columns: x => new { x.CompanyId, x.OutputAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxCodes_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxCodes_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    OriginalDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ControlAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CurrencyPolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    MinorUnits = table.Column<int>(type: "integer", nullable: false),
                    PartyName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PartyTaxIdentifier = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    BillingAddress = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ScheduledPaymentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    SupplierReferenceKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    NetTotal = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: true),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PostKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PostRequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PostedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.UniqueConstraint("AK_Documents_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_TradeDocument", "\"Kind\" BETWEEN 0 AND 5 AND \"State\" BETWEEN 0 AND 3 AND \"NetTotal\">=0 AND \"TaxTotal\">=0 AND \"Total\">0 AND \"Total\"=\"NetTotal\"+\"TaxTotal\" AND \"DueDate\">=\"DocumentDate\" AND \"CurrencyPolicyVersion\"=1");
                    table.CheckConstraint("CK_TradeOriginal", "(\"Kind\" IN(2,3)) = (\"OriginalDocumentId\" IS NOT NULL)");
                    table.CheckConstraint("CK_TradePosted", "\"State\"<>2 OR (\"JournalId\" IS NOT NULL AND \"PostingDate\" IS NOT NULL AND \"Number\" IS NOT NULL AND \"Sequence\">0 AND \"PostKey\" IS NOT NULL AND \"PostedBy\" IS NOT NULL AND \"PostedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Documents_Accounts_CompanyId_ControlAccountId",
                        columns: x => new { x.CompanyId, x.ControlAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Documents_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Documents_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Documents_AspNetUsers_PostedBy",
                        column: x => x.PostedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Documents_AspNetUsers_UpdatedBy",
                        column: x => x.UpdatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Documents_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Documents_Documents_CompanyId_OriginalDocumentId",
                        columns: x => new { x.CompanyId, x.OriginalDocumentId },
                        principalSchema: "trading",
                        principalTable: "Documents",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Documents_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Documents_Parties_CompanyId_PartyId",
                        columns: x => new { x.CompanyId, x.PartyId },
                        principalSchema: "trading",
                        principalTable: "Parties",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxRates",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxCodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: false),
                    RatePercent = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRates", x => x.Id);
                    table.UniqueConstraint("AK_TaxRates_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_TaxRate", "\"Version\">0 AND \"RatePercent\" BETWEEN 0 AND 100 AND \"EffectiveFrom\"<=\"EffectiveTo\"");
                    table.ForeignKey(
                        name: "FK_TaxRates_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxRates_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaxRates_TaxCodes_CompanyId_TaxCodeId",
                        columns: x => new { x.CompanyId, x.TaxCodeId },
                        principalSchema: "trading",
                        principalTable: "TaxCodes",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyKind = table.Column<int>(type: "integer", nullable: false),
                    IsRefund = table.Column<bool>(type: "boolean", nullable: false),
                    RefundPaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RefundCreditId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CashAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ControlAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdvanceAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    ControlAmount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    AdvanceAmount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: true),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalJournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReversalKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReversedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ReversedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.UniqueConstraint("AK_Payments_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_PaymentAmounts", "\"PartyKind\" IN(0,1) AND \"State\" IN(0,1,2) AND \"Amount\">0 AND \"ControlAmount\">=0 AND \"AdvanceAmount\">=0 AND \"Amount\"=\"ControlAmount\"+\"AdvanceAmount\"");
                    table.CheckConstraint("CK_PaymentPosted", "\"State\"=0 OR (\"JournalId\" IS NOT NULL AND \"PostedAt\" IS NOT NULL AND \"Sequence\">0 AND \"Number\" IS NOT NULL)");
                    table.CheckConstraint("CK_PaymentRefund", "(NOT \"IsRefund\" AND \"RefundPaymentId\" IS NULL AND \"RefundCreditId\" IS NULL) OR (\"IsRefund\" AND ((\"RefundPaymentId\" IS NOT NULL)::int+(\"RefundCreditId\" IS NOT NULL)::int)=1)");
                    table.CheckConstraint("CK_PaymentReversed", "\"State\"<>2 OR (\"ReversalJournalId\" IS NOT NULL AND \"ReversalDate\" IS NOT NULL AND \"ReversalKey\" IS NOT NULL AND \"ReversedBy\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Payments_Accounts_CompanyId_AdvanceAccountId",
                        columns: x => new { x.CompanyId, x.AdvanceAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Accounts_CompanyId_CashAccountId",
                        columns: x => new { x.CompanyId, x.CashAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Accounts_CompanyId_ControlAccountId",
                        columns: x => new { x.CompanyId, x.ControlAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_AspNetUsers_ReversedBy",
                        column: x => x.ReversedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Documents_CompanyId_RefundCreditId",
                        columns: x => new { x.CompanyId, x.RefundCreditId },
                        principalSchema: "trading",
                        principalTable: "Documents",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Journals_CompanyId_ReversalJournalId",
                        columns: x => new { x.CompanyId, x.ReversalJournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Parties_CompanyId_PartyId",
                        columns: x => new { x.CompanyId, x.PartyId },
                        principalSchema: "trading",
                        principalTable: "Parties",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Payments_CompanyId_RefundPaymentId",
                        columns: x => new { x.CompanyId, x.RefundPaymentId },
                        principalSchema: "trading",
                        principalTable: "Payments",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentLines",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    TaxRateId = table.Column<Guid>(type: "uuid", nullable: true),
                    TaxAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    TaxPercent = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    TaxInclusive = table.Column<bool>(type: "boolean", nullable: false),
                    NetAmount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentLines", x => x.Id);
                    table.UniqueConstraint("AK_DocumentLines_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_TradeLine", "\"Number\">0 AND \"Quantity\">0 AND \"UnitPrice\">=0 AND \"DiscountPercent\" BETWEEN 0 AND 100 AND \"TaxPercent\" BETWEEN 0 AND 100 AND \"NetAmount\">=0 AND \"TaxAmount\">=0 AND \"Total\"=\"NetAmount\"+\"TaxAmount\"");
                    table.ForeignKey(
                        name: "FK_DocumentLines_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentLines_Accounts_CompanyId_TaxAccountId",
                        columns: x => new { x.CompanyId, x.TaxAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentLines_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentLines_DocumentLines_CompanyId_OriginalLineId",
                        columns: x => new { x.CompanyId, x.OriginalLineId },
                        principalSchema: "trading",
                        principalTable: "DocumentLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentLines_Documents_CompanyId_DocumentId",
                        columns: x => new { x.CompanyId, x.DocumentId },
                        principalSchema: "trading",
                        principalTable: "Documents",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentLines_TaxRates_CompanyId_TaxRateId",
                        columns: x => new { x.CompanyId, x.TaxRateId },
                        principalSchema: "trading",
                        principalTable: "TaxRates",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Allocations",
                schema: "trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartyKind = table.Column<int>(type: "integer", nullable: false),
                    FundingKind = table.Column<int>(type: "integer", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreditDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReversesAllocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    ControlAmount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    AdvanceAmount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    ControlAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdvanceAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Allocations", x => x.Id);
                    table.UniqueConstraint("AK_Allocations_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Allocation", "\"State\" IN(0,1) AND \"PartyKind\" IN(0,1) AND \"FundingKind\" IN(0,1) AND \"Amount\">0 AND \"ControlAmount\">=0 AND \"AdvanceAmount\">=0 AND \"Amount\"=\"ControlAmount\"+\"AdvanceAmount\" AND ((\"FundingKind\"=0 AND \"PaymentId\" IS NOT NULL AND \"CreditDocumentId\" IS NULL) OR (\"FundingKind\"=1 AND \"CreditDocumentId\" IS NOT NULL AND \"PaymentId\" IS NULL AND \"AdvanceAmount\"=0))");
                    table.ForeignKey(
                        name: "FK_Allocations_Accounts_CompanyId_AdvanceAccountId",
                        columns: x => new { x.CompanyId, x.AdvanceAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Allocations_Accounts_CompanyId_ControlAccountId",
                        columns: x => new { x.CompanyId, x.ControlAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Allocations_Allocations_CompanyId_ReversesAllocationId",
                        columns: x => new { x.CompanyId, x.ReversesAllocationId },
                        principalSchema: "trading",
                        principalTable: "Allocations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Allocations_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Allocations_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Allocations_Documents_CompanyId_CreditDocumentId",
                        columns: x => new { x.CompanyId, x.CreditDocumentId },
                        principalSchema: "trading",
                        principalTable: "Documents",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Allocations_Documents_CompanyId_TargetDocumentId",
                        columns: x => new { x.CompanyId, x.TargetDocumentId },
                        principalSchema: "trading",
                        principalTable: "Documents",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Allocations_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Allocations_Parties_CompanyId_PartyId",
                        columns: x => new { x.CompanyId, x.PartyId },
                        principalSchema: "trading",
                        principalTable: "Parties",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Allocations_Payments_CompanyId_PaymentId",
                        columns: x => new { x.CompanyId, x.PaymentId },
                        principalSchema: "trading",
                        principalTable: "Payments",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMappings_ActorId",
                schema: "trading",
                table: "AccountMappings",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMappings_CompanyId_AccountId",
                schema: "trading",
                table: "AccountMappings",
                columns: new[] { "CompanyId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountMappings_CompanyId_Role",
                schema: "trading",
                table: "AccountMappings",
                columns: new[] { "CompanyId", "Role" },
                unique: true,
                filter: "\"Role\" <> 4");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMappings_TenantId_OrganizationId_CompanyId",
                schema: "trading",
                table: "AccountMappings",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_ActorId",
                schema: "trading",
                table: "Allocations",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_CompanyId_AdvanceAccountId",
                schema: "trading",
                table: "Allocations",
                columns: new[] { "CompanyId", "AdvanceAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_CompanyId_ControlAccountId",
                schema: "trading",
                table: "Allocations",
                columns: new[] { "CompanyId", "ControlAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_CompanyId_CreditDocumentId",
                schema: "trading",
                table: "Allocations",
                columns: new[] { "CompanyId", "CreditDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_CompanyId_JournalId",
                schema: "trading",
                table: "Allocations",
                columns: new[] { "CompanyId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_CompanyId_PartyId_EffectiveDate",
                schema: "trading",
                table: "Allocations",
                columns: new[] { "CompanyId", "PartyId", "EffectiveDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_CompanyId_PaymentId",
                schema: "trading",
                table: "Allocations",
                columns: new[] { "CompanyId", "PaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_CompanyId_RequestKey",
                schema: "trading",
                table: "Allocations",
                columns: new[] { "CompanyId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_CompanyId_ReversesAllocationId",
                schema: "trading",
                table: "Allocations",
                columns: new[] { "CompanyId", "ReversesAllocationId" },
                unique: true,
                filter: "\"ReversesAllocationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_CompanyId_TargetDocumentId",
                schema: "trading",
                table: "Allocations",
                columns: new[] { "CompanyId", "TargetDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Allocations_TenantId_OrganizationId_CompanyId",
                schema: "trading",
                table: "Allocations",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLines_CompanyId_AccountId",
                schema: "trading",
                table: "DocumentLines",
                columns: new[] { "CompanyId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLines_CompanyId_DocumentId",
                schema: "trading",
                table: "DocumentLines",
                columns: new[] { "CompanyId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLines_CompanyId_OriginalLineId",
                schema: "trading",
                table: "DocumentLines",
                columns: new[] { "CompanyId", "OriginalLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLines_CompanyId_TaxAccountId",
                schema: "trading",
                table: "DocumentLines",
                columns: new[] { "CompanyId", "TaxAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLines_CompanyId_TaxRateId",
                schema: "trading",
                table: "DocumentLines",
                columns: new[] { "CompanyId", "TaxRateId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLines_DocumentId_Number",
                schema: "trading",
                table: "DocumentLines",
                columns: new[] { "DocumentId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLines_TenantId_OrganizationId_CompanyId",
                schema: "trading",
                table: "DocumentLines",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_ApprovedBy",
                schema: "trading",
                table: "Documents",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CompanyId_ControlAccountId",
                schema: "trading",
                table: "Documents",
                columns: new[] { "CompanyId", "ControlAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CompanyId_JournalId",
                schema: "trading",
                table: "Documents",
                columns: new[] { "CompanyId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CompanyId_Kind_Sequence",
                schema: "trading",
                table: "Documents",
                columns: new[] { "CompanyId", "Kind", "Sequence" },
                unique: true,
                filter: "\"State\"=2");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CompanyId_OriginalDocumentId",
                schema: "trading",
                table: "Documents",
                columns: new[] { "CompanyId", "OriginalDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CompanyId_PartyId_Kind",
                schema: "trading",
                table: "Documents",
                columns: new[] { "CompanyId", "PartyId", "Kind" },
                unique: true,
                filter: "\"Kind\" IN (4,5) AND \"State\"<>3");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CompanyId_PartyId_State_DueDate",
                schema: "trading",
                table: "Documents",
                columns: new[] { "CompanyId", "PartyId", "State", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CompanyId_PartyId_SupplierReferenceKey",
                schema: "trading",
                table: "Documents",
                columns: new[] { "CompanyId", "PartyId", "SupplierReferenceKey" },
                unique: true,
                filter: "\"Kind\"=1 AND \"State\"<>3");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_CreatedBy",
                schema: "trading",
                table: "Documents",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_PostedBy",
                schema: "trading",
                table: "Documents",
                column: "PostedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_OrganizationId_CompanyId",
                schema: "trading",
                table: "Documents",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_UpdatedBy",
                schema: "trading",
                table: "Documents",
                column: "UpdatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Parties_CompanyId_ControlAccountId",
                schema: "trading",
                table: "Parties",
                columns: new[] { "CompanyId", "ControlAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Parties_CompanyId_DefaultAccountId",
                schema: "trading",
                table: "Parties",
                columns: new[] { "CompanyId", "DefaultAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Parties_CompanyId_Kind_Code",
                schema: "trading",
                table: "Parties",
                columns: new[] { "CompanyId", "Kind", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Parties_CreatedBy",
                schema: "trading",
                table: "Parties",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Parties_TenantId_OrganizationId_CompanyId",
                schema: "trading",
                table: "Parties",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Parties_UpdatedBy",
                schema: "trading",
                table: "Parties",
                column: "UpdatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_AdvanceAccountId",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "AdvanceAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_CashAccountId",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "CashAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_ControlAccountId",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "ControlAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_JournalId",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_PartyId",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_RefundCreditId",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "RefundCreditId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_RefundPaymentId",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "RefundPaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_RequestKey",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_ReversalJournalId",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "ReversalJournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_Sequence",
                schema: "trading",
                table: "Payments",
                columns: new[] { "CompanyId", "Sequence" },
                unique: true,
                filter: "\"State\"<>0");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CreatedBy",
                schema: "trading",
                table: "Payments",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ReversedBy",
                schema: "trading",
                table: "Payments",
                column: "ReversedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TenantId_OrganizationId_CompanyId",
                schema: "trading",
                table: "Payments",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodes_CompanyId_Code",
                schema: "trading",
                table: "TaxCodes",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodes_CompanyId_InputAccountId",
                schema: "trading",
                table: "TaxCodes",
                columns: new[] { "CompanyId", "InputAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodes_CompanyId_OutputAccountId",
                schema: "trading",
                table: "TaxCodes",
                columns: new[] { "CompanyId", "OutputAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodes_CreatedBy",
                schema: "trading",
                table: "TaxCodes",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodes_TenantId_OrganizationId_CompanyId",
                schema: "trading",
                table: "TaxCodes",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxRates_CompanyId_TaxCodeId",
                schema: "trading",
                table: "TaxRates",
                columns: new[] { "CompanyId", "TaxCodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxRates_CreatedBy",
                schema: "trading",
                table: "TaxRates",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRates_TaxCodeId_Version",
                schema: "trading",
                table: "TaxRates",
                columns: new[] { "TaxCodeId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxRates_TenantId_OrganizationId_CompanyId",
                schema: "trading",
                table: "TaxRates",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });
            migrationBuilder.Sql(TradingSubledgerV1Sql.Up);
            migrationBuilder.Sql(TradingSubledgerV1Sql.Contracts);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TradingSubledgerV1Sql.Down);
            migrationBuilder.DropTable(
                name: "AccountMappings",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "Allocations",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "DocumentLines",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "Payments",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "TaxRates",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "Documents",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "TaxCodes",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "Parties",
                schema: "trading");
        }
    }
}
