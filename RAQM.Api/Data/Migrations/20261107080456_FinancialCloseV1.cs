using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FinancialCloseV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "financial_close");

            migrationBuilder.CreateTable(
                name: "PeriodCloseSessions",
                schema: "financial_close",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovalHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApprovalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeriodCloseSessions", x => x.Id);
                    table.UniqueConstraint("AK_PeriodCloseSessions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_CloseSession_State", " \"State\" BETWEEN 0 AND 3 AND (\"State\" NOT IN(1,2) OR (\"ApprovedBy\" IS NOT NULL AND \"ApprovalHash\" IS NOT NULL)) AND (\"State\"<>2 OR (\"ClosedBy\" IS NOT NULL AND \"ClosedAt\" IS NOT NULL AND \"EvidenceJson\" IS NOT NULL)) ");
                    table.ForeignKey(
                        name: "FK_PeriodCloseSessions_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PeriodCloseSessions_AspNetUsers_ClosedBy",
                        column: x => x.ClosedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PeriodCloseSessions_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PeriodCloseSessions_Companies_TenantId_OrganizationId_Compa~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PeriodCloseSessions_Periods_CompanyId_PeriodId",
                        columns: x => new { x.CompanyId, x.PeriodId },
                        principalSchema: "accounting",
                        principalTable: "Periods",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecognitionSchedules",
                schema: "financial_close",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    BasisLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    DebitAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreditAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    TemplateJson = table.Column<string>(type: "jsonb", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    ReverseAfterDays = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionSchedules", x => x.Id);
                    table.UniqueConstraint("AK_RecognitionSchedules_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_RecognitionSchedule_Shape", " \"Total\" > 0 AND \"Kind\" BETWEEN 0 AND 4 AND \"State\" BETWEEN 0 AND 2 AND (\"Kind\" IN (2,3))=(\"BasisLineId\" IS NOT NULL) AND (\"Kind\"=4 OR (\"DebitAccountId\" IS NOT NULL AND \"CreditAccountId\" IS NOT NULL AND \"DebitAccountId\"<>\"CreditAccountId\")) ");
                    table.ForeignKey(
                        name: "FK_RecognitionSchedules_Accounts_CompanyId_CreditAccountId",
                        columns: x => new { x.CompanyId, x.CreditAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionSchedules_Accounts_CompanyId_DebitAccountId",
                        columns: x => new { x.CompanyId, x.DebitAccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionSchedules_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionSchedules_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionSchedules_Companies_TenantId_OrganizationId_Comp~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionSchedules_JournalLines_CompanyId_BasisLineId",
                        columns: x => new { x.CompanyId, x.BasisLineId },
                        principalSchema: "accounting",
                        principalTable: "JournalLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportDefinitions",
                schema: "financial_close",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    MappingCount = table.Column<int>(type: "integer", nullable: false),
                    RetainedEarningsId = table.Column<Guid>(type: "uuid", nullable: true),
                    CashRulesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDefinitions", x => x.Id);
                    table.UniqueConstraint("AK_ReportDefinitions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ReportDefinition_Revision", "\"Revision\" > 0 AND \"MappingCount\" BETWEEN 0 AND 2000");
                    table.ForeignKey(
                        name: "FK_ReportDefinitions_Accounts_CompanyId_RetainedEarningsId",
                        columns: x => new { x.CompanyId, x.RetainedEarningsId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReportDefinitions_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReportDefinitions_Companies_TenantId_OrganizationId_Company~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CloseExceptions",
                schema: "financial_close",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Blocking = table.Column<bool>(type: "boolean", nullable: false),
                    RaisedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ResolvedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RaisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloseExceptions", x => x.Id);
                    table.UniqueConstraint("AK_CloseExceptions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_CloseExceptions_AspNetUsers_RaisedBy",
                        column: x => x.RaisedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CloseExceptions_AspNetUsers_ResolvedBy",
                        column: x => x.ResolvedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CloseExceptions_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CloseExceptions_PeriodCloseSessions_CompanyId_SessionId",
                        columns: x => new { x.CompanyId, x.SessionId },
                        principalSchema: "financial_close",
                        principalTable: "PeriodCloseSessions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PeriodCloseTasks",
                schema: "financial_close",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Evidence = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CompletedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeriodCloseTasks", x => x.Id);
                    table.UniqueConstraint("AK_PeriodCloseTasks_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_PeriodCloseTasks_AspNetUsers_CompletedBy",
                        column: x => x.CompletedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PeriodCloseTasks_AspNetUsers_ReviewerId",
                        column: x => x.ReviewerId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PeriodCloseTasks_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PeriodCloseTasks_PeriodCloseSessions_CompanyId_SessionId",
                        columns: x => new { x.CompanyId, x.SessionId },
                        principalSchema: "financial_close",
                        principalTable: "PeriodCloseSessions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecognitionOccurrences",
                schema: "financial_close",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    ReverseDue = table.Column<DateOnly>(type: "date", nullable: true),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalJournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionOccurrences", x => x.Id);
                    table.UniqueConstraint("AK_RecognitionOccurrences_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_RecognitionOccurrence_Shape", " \"Amount\" > 0 AND \"Number\" > 0 AND (\"ReverseDue\" IS NULL OR \"ReverseDue\">\"Date\") AND (\"ReversalJournalId\" IS NULL OR \"JournalId\" IS NOT NULL) ");
                    table.ForeignKey(
                        name: "FK_RecognitionOccurrences_Companies_TenantId_OrganizationId_Co~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionOccurrences_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionOccurrences_Journals_CompanyId_ReversalJournalId",
                        columns: x => new { x.CompanyId, x.ReversalJournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecognitionOccurrences_RecognitionSchedules_CompanyId_Sched~",
                        columns: x => new { x.CompanyId, x.ScheduleId },
                        principalSchema: "financial_close",
                        principalTable: "RecognitionSchedules",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportAccountMappings",
                schema: "financial_close",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Group = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportAccountMappings", x => x.Id);
                    table.UniqueConstraint("AK_ReportAccountMappings_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_ReportMapping_Group", "\"Group\" BETWEEN 0 AND 12");
                    table.ForeignKey(
                        name: "FK_ReportAccountMappings_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReportAccountMappings_Companies_TenantId_OrganizationId_Com~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReportAccountMappings_ReportDefinitions_CompanyId_Definitio~",
                        columns: x => new { x.CompanyId, x.DefinitionId },
                        principalSchema: "financial_close",
                        principalTable: "ReportDefinitions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "YearEndRecords",
                schema: "financial_close",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RetainedEarningsId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Profit = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    ClosingLinesJson = table.Column<string>(type: "jsonb", nullable: false),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Finalized = table.Column<bool>(type: "boolean", nullable: false),
                    ClosedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YearEndRecords", x => x.Id);
                    table.UniqueConstraint("AK_YearEndRecords_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_YearEndRecords_Accounts_CompanyId_RetainedEarningsId",
                        columns: x => new { x.CompanyId, x.RetainedEarningsId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_YearEndRecords_AspNetUsers_ClosedBy",
                        column: x => x.ClosedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_YearEndRecords_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_YearEndRecords_FiscalYears_CompanyId_FiscalYearId",
                        columns: x => new { x.CompanyId, x.FiscalYearId },
                        principalSchema: "accounting",
                        principalTable: "FiscalYears",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_YearEndRecords_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_YearEndRecords_PeriodCloseSessions_CompanyId_SessionId",
                        columns: x => new { x.CompanyId, x.SessionId },
                        principalSchema: "financial_close",
                        principalTable: "PeriodCloseSessions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_YearEndRecords_ReportDefinitions_CompanyId_DefinitionId",
                        columns: x => new { x.CompanyId, x.DefinitionId },
                        principalSchema: "financial_close",
                        principalTable: "ReportDefinitions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CloseExceptions_CompanyId_SessionId",
                schema: "financial_close",
                table: "CloseExceptions",
                columns: new[] { "CompanyId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CloseExceptions_RaisedBy",
                schema: "financial_close",
                table: "CloseExceptions",
                column: "RaisedBy");

            migrationBuilder.CreateIndex(
                name: "IX_CloseExceptions_ResolvedBy",
                schema: "financial_close",
                table: "CloseExceptions",
                column: "ResolvedBy");

            migrationBuilder.CreateIndex(
                name: "IX_CloseExceptions_TenantId_OrganizationId_CompanyId",
                schema: "financial_close",
                table: "CloseExceptions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCloseSessions_ApprovedBy",
                schema: "financial_close",
                table: "PeriodCloseSessions",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCloseSessions_ClosedBy",
                schema: "financial_close",
                table: "PeriodCloseSessions",
                column: "ClosedBy");

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCloseSessions_CompanyId_PeriodId",
                schema: "financial_close",
                table: "PeriodCloseSessions",
                columns: new[] { "CompanyId", "PeriodId" },
                unique: true,
                filter: " \"State\" IN (0,1) ");

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCloseSessions_CreatedBy",
                schema: "financial_close",
                table: "PeriodCloseSessions",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCloseSessions_TenantId_OrganizationId_CompanyId",
                schema: "financial_close",
                table: "PeriodCloseSessions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCloseTasks_CompanyId_SessionId_Code",
                schema: "financial_close",
                table: "PeriodCloseTasks",
                columns: new[] { "CompanyId", "SessionId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCloseTasks_CompletedBy",
                schema: "financial_close",
                table: "PeriodCloseTasks",
                column: "CompletedBy");

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCloseTasks_ReviewerId",
                schema: "financial_close",
                table: "PeriodCloseTasks",
                column: "ReviewerId");

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCloseTasks_TenantId_OrganizationId_CompanyId",
                schema: "financial_close",
                table: "PeriodCloseTasks",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionOccurrences_CompanyId_Date",
                schema: "financial_close",
                table: "RecognitionOccurrences",
                columns: new[] { "CompanyId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionOccurrences_CompanyId_JournalId",
                schema: "financial_close",
                table: "RecognitionOccurrences",
                columns: new[] { "CompanyId", "JournalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionOccurrences_CompanyId_ReversalJournalId",
                schema: "financial_close",
                table: "RecognitionOccurrences",
                columns: new[] { "CompanyId", "ReversalJournalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionOccurrences_CompanyId_ScheduleId_Number",
                schema: "financial_close",
                table: "RecognitionOccurrences",
                columns: new[] { "CompanyId", "ScheduleId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionOccurrences_TenantId_OrganizationId_CompanyId",
                schema: "financial_close",
                table: "RecognitionOccurrences",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSchedules_ApprovedBy",
                schema: "financial_close",
                table: "RecognitionSchedules",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSchedules_CompanyId_BasisLineId",
                schema: "financial_close",
                table: "RecognitionSchedules",
                columns: new[] { "CompanyId", "BasisLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSchedules_CompanyId_CreditAccountId",
                schema: "financial_close",
                table: "RecognitionSchedules",
                columns: new[] { "CompanyId", "CreditAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSchedules_CompanyId_DebitAccountId",
                schema: "financial_close",
                table: "RecognitionSchedules",
                columns: new[] { "CompanyId", "DebitAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSchedules_CompanyId_Reference",
                schema: "financial_close",
                table: "RecognitionSchedules",
                columns: new[] { "CompanyId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSchedules_CreatedBy",
                schema: "financial_close",
                table: "RecognitionSchedules",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionSchedules_TenantId_OrganizationId_CompanyId",
                schema: "financial_close",
                table: "RecognitionSchedules",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportAccountMappings_CompanyId_AccountId",
                schema: "financial_close",
                table: "ReportAccountMappings",
                columns: new[] { "CompanyId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportAccountMappings_CompanyId_DefinitionId_AccountId",
                schema: "financial_close",
                table: "ReportAccountMappings",
                columns: new[] { "CompanyId", "DefinitionId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportAccountMappings_TenantId_OrganizationId_CompanyId",
                schema: "financial_close",
                table: "ReportAccountMappings",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportDefinitions_CompanyId_RetainedEarningsId",
                schema: "financial_close",
                table: "ReportDefinitions",
                columns: new[] { "CompanyId", "RetainedEarningsId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportDefinitions_CompanyId_Revision",
                schema: "financial_close",
                table: "ReportDefinitions",
                columns: new[] { "CompanyId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportDefinitions_CreatedBy",
                schema: "financial_close",
                table: "ReportDefinitions",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ReportDefinitions_TenantId_OrganizationId_CompanyId",
                schema: "financial_close",
                table: "ReportDefinitions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_YearEndRecords_ClosedBy",
                schema: "financial_close",
                table: "YearEndRecords",
                column: "ClosedBy");

            migrationBuilder.CreateIndex(
                name: "IX_YearEndRecords_CompanyId_DefinitionId",
                schema: "financial_close",
                table: "YearEndRecords",
                columns: new[] { "CompanyId", "DefinitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_YearEndRecords_CompanyId_FiscalYearId",
                schema: "financial_close",
                table: "YearEndRecords",
                columns: new[] { "CompanyId", "FiscalYearId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_YearEndRecords_CompanyId_JournalId",
                schema: "financial_close",
                table: "YearEndRecords",
                columns: new[] { "CompanyId", "JournalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_YearEndRecords_CompanyId_RequestKey",
                schema: "financial_close",
                table: "YearEndRecords",
                columns: new[] { "CompanyId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_YearEndRecords_CompanyId_RetainedEarningsId",
                schema: "financial_close",
                table: "YearEndRecords",
                columns: new[] { "CompanyId", "RetainedEarningsId" });

            migrationBuilder.CreateIndex(
                name: "IX_YearEndRecords_CompanyId_SessionId",
                schema: "financial_close",
                table: "YearEndRecords",
                columns: new[] { "CompanyId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_YearEndRecords_TenantId_OrganizationId_CompanyId",
                schema: "financial_close",
                table: "YearEndRecords",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });
            migrationBuilder.Sql(FinancialCloseV1Sql.Evidence);
            migrationBuilder.Sql(FinancialCloseV1Sql.Posting);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FinancialCloseV1Sql.Down);
            migrationBuilder.DropTable(
                name: "CloseExceptions",
                schema: "financial_close");

            migrationBuilder.DropTable(
                name: "PeriodCloseTasks",
                schema: "financial_close");

            migrationBuilder.DropTable(
                name: "RecognitionOccurrences",
                schema: "financial_close");

            migrationBuilder.DropTable(
                name: "ReportAccountMappings",
                schema: "financial_close");

            migrationBuilder.DropTable(
                name: "YearEndRecords",
                schema: "financial_close");

            migrationBuilder.DropTable(
                name: "RecognitionSchedules",
                schema: "financial_close");

            migrationBuilder.DropTable(
                name: "PeriodCloseSessions",
                schema: "financial_close");

            migrationBuilder.DropTable(
                name: "ReportDefinitions",
                schema: "financial_close");
        }
    }
}
