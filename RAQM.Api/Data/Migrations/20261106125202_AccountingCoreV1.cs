using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountingCoreV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "accounting");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,");

            migrationBuilder.CreateTable(
                name: "Accounts",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Classification = table.Column<int>(type: "integer", nullable: false),
                    NormalBalance = table.Column<int>(type: "integer", nullable: false),
                    IsContra = table.Column<bool>(type: "boolean", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsPosting = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.UniqueConstraint("AK_Accounts_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Account_Class", "\"Classification\" BETWEEN 0 AND 4 AND \"NormalBalance\" IN (0,1)");
                    table.CheckConstraint("CK_Account_Code", "\"Code\" ~ '^[A-Z0-9][A-Z0-9._-]{0,31}$'");
                    table.CheckConstraint("CK_Account_Normal", "(\"NormalBalance\" = 0) = ((\"Classification\" IN (0,4)) <> \"IsContra\")");
                    table.ForeignKey(
                        name: "FK_Accounts_Accounts_CompanyId_ParentId",
                        columns: x => new { x.CompanyId, x.ParentId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Accounts_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Accounts_AspNetUsers_UpdatedBy",
                        column: x => x.UpdatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Accounts_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.UniqueConstraint("AK_AuditEvents_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_AuditEvents_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FiscalYears",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalYears", x => x.Id);
                    table.UniqueConstraint("AK_FiscalYears_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_FiscalYear_Dates", "\"StartDate\" <= \"EndDate\"");
                    table.ForeignKey(
                        name: "FK_FiscalYears_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FiscalYears_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Periods",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Periods", x => x.Id);
                    table.UniqueConstraint("AK_Periods_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Period_Config", "\"StartDate\" <= \"EndDate\" AND \"Number\" BETWEEN 1 AND 12 AND \"State\" BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_Periods_AspNetUsers_UpdatedBy",
                        column: x => x.UpdatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Periods_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Periods_FiscalYears_CompanyId_FiscalYearId",
                        columns: x => new { x.CompanyId, x.FiscalYearId },
                        principalSchema: "accounting",
                        principalTable: "FiscalYears",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Journals",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CurrencyPolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    MinorUnits = table.Column<int>(type: "integer", nullable: false),
                    PeriodId = table.Column<Guid>(type: "uuid", nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: true),
                    Number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SourceModule = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReversesJournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    PostedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PostingReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Journals", x => x.Id);
                    table.UniqueConstraint("AK_Journals_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Journal_Posted", "\"State\" = 0 OR (\"PeriodId\" IS NOT NULL AND \"Sequence\" > 0 AND \"Number\" IS NOT NULL AND \"IdempotencyKey\" IS NOT NULL AND \"PayloadHash\" IS NOT NULL AND \"PostedBy\" IS NOT NULL AND \"PostedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_Journal_Source", "length(trim(\"SourceModule\")) > 0 AND \"SourceDocumentId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_Journal_State", "\"State\" IN (0,1) AND \"CurrencyPolicyVersion\" = 1 AND \"MinorUnits\" BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_Journals_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Journals_AspNetUsers_PostedBy",
                        column: x => x.PostedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Journals_AspNetUsers_UpdatedBy",
                        column: x => x.UpdatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Journals_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Journals_Journals_CompanyId_ReversesJournalId",
                        columns: x => new { x.CompanyId, x.ReversesJournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Journals_Periods_CompanyId_PeriodId",
                        columns: x => new { x.CompanyId, x.PeriodId },
                        principalSchema: "accounting",
                        principalTable: "Periods",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalLines",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Debit = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalLines", x => x.Id);
                    table.UniqueConstraint("AK_JournalLines_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Line_Amounts", "\"Number\" > 0 AND ((\"Debit\" > 0 AND \"Credit\" = 0) OR (\"Credit\" > 0 AND \"Debit\" = 0))");
                    table.ForeignKey(
                        name: "FK_JournalLines_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalLines_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalLines_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpeningBatches",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpeningBatches", x => x.Id);
                    table.UniqueConstraint("AK_OpeningBatches_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_OpeningBatches_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpeningBatches_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpeningBatches_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_CompanyId_Code",
                schema: "accounting",
                table: "Accounts",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_CompanyId_ParentId",
                schema: "accounting",
                table: "Accounts",
                columns: new[] { "CompanyId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_CreatedBy",
                schema: "accounting",
                table: "Accounts",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_TenantId_OrganizationId_CompanyId",
                schema: "accounting",
                table: "Accounts",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_UpdatedBy",
                schema: "accounting",
                table: "Accounts",
                column: "UpdatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorId",
                schema: "accounting",
                table: "AuditEvents",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_CompanyId_EntityId_At",
                schema: "accounting",
                table: "AuditEvents",
                columns: new[] { "CompanyId", "EntityId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TenantId_OrganizationId_CompanyId",
                schema: "accounting",
                table: "AuditEvents",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_CompanyId_StartDate",
                schema: "accounting",
                table: "FiscalYears",
                columns: new[] { "CompanyId", "StartDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_CreatedBy",
                schema: "accounting",
                table: "FiscalYears",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_TenantId_OrganizationId_CompanyId",
                schema: "accounting",
                table: "FiscalYears",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_CompanyId_AccountId_JournalId",
                schema: "accounting",
                table: "JournalLines",
                columns: new[] { "CompanyId", "AccountId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_CompanyId_JournalId",
                schema: "accounting",
                table: "JournalLines",
                columns: new[] { "CompanyId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_JournalId_Number",
                schema: "accounting",
                table: "JournalLines",
                columns: new[] { "JournalId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_TenantId_OrganizationId_CompanyId",
                schema: "accounting",
                table: "JournalLines",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Journals_CompanyId_IdempotencyKey",
                schema: "accounting",
                table: "Journals",
                columns: new[] { "CompanyId", "IdempotencyKey" },
                unique: true,
                filter: "\"State\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Journals_CompanyId_PeriodId",
                schema: "accounting",
                table: "Journals",
                columns: new[] { "CompanyId", "PeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_Journals_CompanyId_PostingDate_Sequence",
                schema: "accounting",
                table: "Journals",
                columns: new[] { "CompanyId", "PostingDate", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Journals_CompanyId_ReversesJournalId",
                schema: "accounting",
                table: "Journals",
                columns: new[] { "CompanyId", "ReversesJournalId" },
                unique: true,
                filter: "\"ReversesJournalId\" IS NOT NULL AND \"State\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Journals_CompanyId_Sequence",
                schema: "accounting",
                table: "Journals",
                columns: new[] { "CompanyId", "Sequence" },
                unique: true,
                filter: "\"State\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Journals_CompanyId_SourceModule_SourceDocumentId",
                schema: "accounting",
                table: "Journals",
                columns: new[] { "CompanyId", "SourceModule", "SourceDocumentId" },
                unique: true,
                filter: "\"State\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Journals_CreatedBy",
                schema: "accounting",
                table: "Journals",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Journals_PostedBy",
                schema: "accounting",
                table: "Journals",
                column: "PostedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Journals_TenantId_OrganizationId_CompanyId",
                schema: "accounting",
                table: "Journals",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Journals_UpdatedBy",
                schema: "accounting",
                table: "Journals",
                column: "UpdatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBatches_CompanyId",
                schema: "accounting",
                table: "OpeningBatches",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBatches_CompanyId_JournalId",
                schema: "accounting",
                table: "OpeningBatches",
                columns: new[] { "CompanyId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBatches_CreatedBy",
                schema: "accounting",
                table: "OpeningBatches",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_OpeningBatches_TenantId_OrganizationId_CompanyId",
                schema: "accounting",
                table: "OpeningBatches",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Periods_CompanyId_FiscalYearId",
                schema: "accounting",
                table: "Periods",
                columns: new[] { "CompanyId", "FiscalYearId" });

            migrationBuilder.CreateIndex(
                name: "IX_Periods_FiscalYearId_Number",
                schema: "accounting",
                table: "Periods",
                columns: new[] { "FiscalYearId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Periods_TenantId_OrganizationId_CompanyId",
                schema: "accounting",
                table: "Periods",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Periods_UpdatedBy",
                schema: "accounting",
                table: "Periods",
                column: "UpdatedBy");
            migrationBuilder.Sql(AccountingCoreV1Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AccountingCoreV1Sql.Down);
            migrationBuilder.DropTable(
                name: "AuditEvents",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "JournalLines",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "OpeningBatches",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "Accounts",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "Journals",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "Periods",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "FiscalYears",
                schema: "accounting");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:btree_gist", ",,");
        }
    }
}

