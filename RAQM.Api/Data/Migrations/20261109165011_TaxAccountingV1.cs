using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaxAccountingV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tax");

            migrationBuilder.CreateTable(
                name: "Registrations",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Region = table.Column<string>(type: "text", nullable: true),
                    Authority = table.Column<string>(type: "text", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: false),
                    Residence = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PreparedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registrations", x => x.Id);
                    table.UniqueConstraint("AK_Registrations_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Registrations_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommercialEvidence",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercialEvidence", x => x.Id);
                    table.UniqueConstraint("AK_CommercialEvidence_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_CommercialEvidence_Companies_TenantId_OrganizationId_Compan~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommercialEvidence_Documents_CompanyId_DocumentId",
                        columns: x => new { x.CompanyId, x.DocumentId },
                        principalSchema: "trading",
                        principalTable: "Documents",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommercialEvidence_Registrations_CompanyId_RegistrationId",
                        columns: x => new { x.CompanyId, x.RegistrationId },
                        principalSchema: "tax",
                        principalTable: "Registrations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Controls",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Controls", x => x.Id);
                    table.UniqueConstraint("AK_Controls_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Tax_ControlRole", " \"Role\" BETWEEN 0 AND 10 ");
                    table.ForeignKey(
                        name: "FK_Controls_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Controls_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Controls_Registrations_CompanyId_RegistrationId",
                        columns: x => new { x.CompanyId, x.RegistrationId },
                        principalSchema: "tax",
                        principalTable: "Registrations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Periods",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    OpenedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Periods1", x => x.Id);
                    table.UniqueConstraint("AK_Periods_CompanyId_Id1", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Tax_Period", " \"From\"<=\"To\" ");
                    table.ForeignKey(
                        name: "FK_Periods_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Periods_Registrations_CompanyId_RegistrationId",
                        columns: x => new { x.CompanyId, x.RegistrationId },
                        principalSchema: "tax",
                        principalTable: "Registrations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegulatoryDocuments",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Schema = table.Column<string>(type: "text", nullable: false),
                    ContentJson = table.Column<string>(type: "jsonb", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegulatoryDocuments", x => x.Id);
                    table.UniqueConstraint("AK_RegulatoryDocuments_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Tax_StructuredVersion", " \"Version\">0 ");
                    table.ForeignKey(
                        name: "FK_RegulatoryDocuments_Companies_TenantId_OrganizationId_Compa~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegulatoryDocuments_Documents_CompanyId_DocumentId",
                        columns: x => new { x.CompanyId, x.DocumentId },
                        principalSchema: "trading",
                        principalTable: "Documents",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegulatoryDocuments_Documents_CompanyId_OriginalDocumentId",
                        columns: x => new { x.CompanyId, x.OriginalDocumentId },
                        principalSchema: "trading",
                        principalTable: "Documents",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RegulatoryDocuments_Registrations_CompanyId_RegistrationId",
                        columns: x => new { x.CompanyId, x.RegistrationId },
                        principalSchema: "tax",
                        principalTable: "Registrations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Rules",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Group = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ItemClass = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Treatment = table.Column<int>(type: "integer", nullable: false),
                    RateId = table.Column<Guid>(type: "uuid", nullable: true),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    PreparedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rules", x => x.Id);
                    table.UniqueConstraint("AK_Rules_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Tax_Rule", " \"EffectiveOn\"<=\"EndsOn\" AND \"Order\" BETWEEN 1 AND 20 AND \"Treatment\" BETWEEN 0 AND 3 AND ((\"Treatment\" IN (0,1)) = (\"RateId\" IS NOT NULL)) ");
                    table.ForeignKey(
                        name: "FK_Rules_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Rules_Parties_CompanyId_PartyId",
                        columns: x => new { x.CompanyId, x.PartyId },
                        principalSchema: "trading",
                        principalTable: "Parties",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Rules_Registrations_CompanyId_RegistrationId",
                        columns: x => new { x.CompanyId, x.RegistrationId },
                        principalSchema: "tax",
                        principalTable: "Registrations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Rules_TaxRates_CompanyId_RateId",
                        columns: x => new { x.CompanyId, x.RateId },
                        principalSchema: "trading",
                        principalTable: "TaxRates",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Operations",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodId = table.Column<Guid>(type: "uuid", nullable: true),
                    BillId = table.Column<Guid>(type: "uuid", nullable: true),
                    BasisId = table.Column<Guid>(type: "uuid", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    PostingActor = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    InputsJson = table.Column<string>(type: "jsonb", nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: true),
                    PreparedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operations1", x => x.Id);
                    table.UniqueConstraint("AK_Operations_CompanyId_Id1", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Tax_Operation", " \"Amount\">=0 AND \"Kind\" BETWEEN 0 AND 9 AND \"State\" BETWEEN 0 AND 4 AND ((\"State\"=3) = (\"JournalId\" IS NOT NULL)) ");
                    table.ForeignKey(
                        name: "FK_Operations_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_Documents_CompanyId_BillId",
                        columns: x => new { x.CompanyId, x.BillId },
                        principalSchema: "trading",
                        principalTable: "Documents",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_Operations_CompanyId_BasisId",
                        columns: x => new { x.CompanyId, x.BasisId },
                        principalSchema: "tax",
                        principalTable: "Operations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_Payments_CompanyId_PaymentId",
                        columns: x => new { x.CompanyId, x.PaymentId },
                        principalSchema: "trading",
                        principalTable: "Payments",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_Periods_CompanyId_PeriodId",
                        columns: x => new { x.CompanyId, x.PeriodId },
                        principalSchema: "tax",
                        principalTable: "Periods",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_Registrations_CompanyId_RegistrationId",
                        columns: x => new { x.CompanyId, x.RegistrationId },
                        principalSchema: "tax",
                        principalTable: "Registrations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Returns",
                schema: "tax",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupersedesId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    PreparedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Returns", x => x.Id);
                    table.UniqueConstraint("AK_Returns_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Tax_Return", " \"Version\">0 AND \"State\" IN(0,1) ");
                    table.ForeignKey(
                        name: "FK_Returns_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Returns_Periods_CompanyId_PeriodId",
                        columns: x => new { x.CompanyId, x.PeriodId },
                        principalSchema: "tax",
                        principalTable: "Periods",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Returns_Registrations_CompanyId_RegistrationId",
                        columns: x => new { x.CompanyId, x.RegistrationId },
                        principalSchema: "tax",
                        principalTable: "Registrations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Returns_Returns_CompanyId_SupersedesId",
                        columns: x => new { x.CompanyId, x.SupersedesId },
                        principalSchema: "tax",
                        principalTable: "Returns",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommercialEvidence_CompanyId_DocumentId",
                schema: "tax",
                table: "CommercialEvidence",
                columns: new[] { "CompanyId", "DocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommercialEvidence_CompanyId_RegistrationId",
                schema: "tax",
                table: "CommercialEvidence",
                columns: new[] { "CompanyId", "RegistrationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CommercialEvidence_TenantId_OrganizationId_CompanyId",
                schema: "tax",
                table: "CommercialEvidence",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Controls_CompanyId_AccountId",
                schema: "tax",
                table: "Controls",
                columns: new[] { "CompanyId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Controls_CompanyId_RegistrationId_Role",
                schema: "tax",
                table: "Controls",
                columns: new[] { "CompanyId", "RegistrationId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Controls_TenantId_OrganizationId_CompanyId",
                schema: "tax",
                table: "Controls",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_BasisId",
                schema: "tax",
                table: "Operations",
                column: "BasisId",
                unique: true,
                filter: " \"State\"=3 AND \"Kind\"=9 ");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_BasisId",
                schema: "tax",
                table: "Operations",
                columns: new[] { "CompanyId", "BasisId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_BillId",
                schema: "tax",
                table: "Operations",
                columns: new[] { "CompanyId", "BillId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_JournalId1",
                schema: "tax",
                table: "Operations",
                columns: new[] { "CompanyId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_PaymentId",
                schema: "tax",
                table: "Operations",
                columns: new[] { "CompanyId", "PaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_PeriodId",
                schema: "tax",
                table: "Operations",
                columns: new[] { "CompanyId", "PeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_RegistrationId_Date_State",
                schema: "tax",
                table: "Operations",
                columns: new[] { "CompanyId", "RegistrationId", "Date", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_RegistrationId_Reference",
                schema: "tax",
                table: "Operations",
                columns: new[] { "CompanyId", "RegistrationId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Operations_JournalId",
                schema: "tax",
                table: "Operations",
                column: "JournalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Operations_PaymentId",
                schema: "tax",
                table: "Operations",
                column: "PaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Operations_TenantId_OrganizationId_CompanyId1",
                schema: "tax",
                table: "Operations",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Periods_CompanyId_RegistrationId_From",
                schema: "tax",
                table: "Periods",
                columns: new[] { "CompanyId", "RegistrationId", "From" });

            migrationBuilder.CreateIndex(
                name: "IX_Periods_TenantId_OrganizationId_CompanyId1",
                schema: "tax",
                table: "Periods",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_CompanyId_Code",
                schema: "tax",
                table: "Registrations",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_TenantId_OrganizationId_CompanyId",
                schema: "tax",
                table: "Registrations",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegulatoryDocuments_CompanyId_DocumentId_Version",
                schema: "tax",
                table: "RegulatoryDocuments",
                columns: new[] { "CompanyId", "DocumentId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegulatoryDocuments_CompanyId_OriginalDocumentId",
                schema: "tax",
                table: "RegulatoryDocuments",
                columns: new[] { "CompanyId", "OriginalDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegulatoryDocuments_CompanyId_RegistrationId",
                schema: "tax",
                table: "RegulatoryDocuments",
                columns: new[] { "CompanyId", "RegistrationId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegulatoryDocuments_TenantId_OrganizationId_CompanyId",
                schema: "tax",
                table: "RegulatoryDocuments",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Returns_CompanyId_PeriodId_Version",
                schema: "tax",
                table: "Returns",
                columns: new[] { "CompanyId", "PeriodId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Returns_CompanyId_RegistrationId",
                schema: "tax",
                table: "Returns",
                columns: new[] { "CompanyId", "RegistrationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Returns_CompanyId_SupersedesId",
                schema: "tax",
                table: "Returns",
                columns: new[] { "CompanyId", "SupersedesId" });

            migrationBuilder.CreateIndex(
                name: "IX_Returns_TenantId_OrganizationId_CompanyId",
                schema: "tax",
                table: "Returns",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_CompanyId_PartyId",
                schema: "tax",
                table: "Rules",
                columns: new[] { "CompanyId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_CompanyId_RateId",
                schema: "tax",
                table: "Rules",
                columns: new[] { "CompanyId", "RateId" });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_CompanyId_RegistrationId_Group_Side_EffectiveOn",
                schema: "tax",
                table: "Rules",
                columns: new[] { "CompanyId", "RegistrationId", "Group", "Side", "EffectiveOn" });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_TenantId_OrganizationId_CompanyId",
                schema: "tax",
                table: "Rules",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });
            migrationBuilder.Sql(TaxAccountingV1Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TaxAccountingV1Sql.Down);
            migrationBuilder.DropForeignKey(
                name: "FK_Operations_Companies_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "Operations");

            migrationBuilder.DropForeignKey(
                name: "FK_Operations_Journals_CompanyId_JournalId",
                schema: "inventory",
                table: "Operations");

            migrationBuilder.DropForeignKey(
                name: "FK_Periods_Companies_TenantId_OrganizationId_CompanyId",
                schema: "accounting",
                table: "Periods");

            migrationBuilder.DropTable(
                name: "CommercialEvidence",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "Controls",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "Operations",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "RegulatoryDocuments",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "Returns",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "Rules",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "Periods",
                schema: "tax");

            migrationBuilder.DropTable(
                name: "Registrations",
                schema: "tax");
        }
    }
}
