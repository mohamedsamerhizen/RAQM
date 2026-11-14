using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SaaSCommercialV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "saas");

            migrationBuilder.CreateTable(
                name: "Accounts",
                schema: "saas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstCompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillingContact = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SuspendAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LegalHold = table.Column<bool>(type: "boolean", nullable: false),
                    QuotaEpoch = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts1", x => x.Id);
                    table.UniqueConstraint("AK_Accounts_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Commercial_Account", " \"State\" BETWEEN 0 AND 2 ");
                    table.ForeignKey(
                        name: "FK_Accounts_Companies_TenantId_OrganizationId_FirstCompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.FirstCompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Accounts_Organizations_TenantId_OrganizationId",
                        columns: x => new { x.TenantId, x.OrganizationId },
                        principalSchema: "organizations",
                        principalTable: "Organizations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Accounts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "organizations",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Cases",
                schema: "saas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ClosedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedTo = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Resolution = table.Column<string>(type: "text", nullable: true),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cases", x => x.Id);
                    table.UniqueConstraint("AK_Cases_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Cases_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "organizations",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                schema: "saas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events2", x => x.Id);
                    table.UniqueConstraint("AK_Events_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Events_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "organizations",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Exports",
                schema: "saas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    Cutoff = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    Companies = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    Rows = table.Column<int>(type: "integer", nullable: false),
                    Csv = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exports", x => x.Id);
                    table.UniqueConstraint("AK_Exports_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Exports_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "organizations",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                schema: "saas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Product = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Interval = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    ExtraSeatPrice = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    IncludedSeats = table.Column<int>(type: "integer", nullable: false),
                    TrialDays = table.Column<int>(type: "integer", nullable: false),
                    GraceDays = table.Column<int>(type: "integer", nullable: false),
                    CompanyLimit = table.Column<int>(type: "integer", nullable: false),
                    SeatLimit = table.Column<int>(type: "integer", nullable: false),
                    WarehouseLimit = table.Column<int>(type: "integer", nullable: false),
                    IntegrationLimit = table.Column<int>(type: "integer", nullable: false),
                    WebhookLimit = table.Column<int>(type: "integer", nullable: false),
                    Features = table.Column<string[]>(type: "text[]", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    PublishedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                    table.CheckConstraint("CK_Plan_Limits", " \"Version\">0 AND \"Price\">=0 AND \"ExtraSeatPrice\">=0 AND \"Interval\" IN(0,1) AND \"CompanyLimit\">0 AND \"SeatLimit\">0 AND \"IncludedSeats\" BETWEEN 0 AND \"SeatLimit\" AND \"WarehouseLimit\">=0 AND \"IntegrationLimit\">=0 AND \"WebhookLimit\">=0 AND \"TrialDays\" BETWEEN 0 AND 90 AND \"GraceDays\" BETWEEN 1 AND 30 ");
                });

            migrationBuilder.CreateTable(
                name: "Provisioning",
                schema: "saas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Trial = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Provisioning", x => x.Id);
                    table.UniqueConstraint("AK_Provisioning_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Provisioning_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "organizations",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                schema: "saas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    NextPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    Anchor = table.Column<DateOnly>(type: "date", nullable: false),
                    Cycle = table.Column<int>(type: "integer", nullable: false),
                    Start = table.Column<DateOnly>(type: "date", nullable: false),
                    End = table.Column<DateOnly>(type: "date", nullable: false),
                    AccessUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Trial = table.Column<bool>(type: "boolean", nullable: false),
                    TrialExpiredRecorded = table.Column<bool>(type: "boolean", nullable: false),
                    CancelAtEnd = table.Column<bool>(type: "boolean", nullable: false),
                    Cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions1", x => x.Id);
                    table.UniqueConstraint("AK_Subscriptions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Subscription_Window", " \"Start\"<\"End\" AND \"Cycle\">=0 ");
                    table.ForeignKey(
                        name: "FK_Subscriptions_Plans_NextPlanId",
                        column: x => x.NextPlanId,
                        principalSchema: "saas",
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "saas",
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "organizations",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                schema: "saas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Lines = table.Column<string>(type: "jsonb", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                    table.UniqueConstraint("AK_Invoices_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Commercial_Invoice", " \"From\"<\"To\" AND \"Total\">=0 AND \"IssuedAt\"<=\"DueAt\" ");
                    table.ForeignKey(
                        name: "FK_Invoices_Plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "saas",
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoices_Subscriptions_TenantId_SubscriptionId",
                        columns: x => new { x.TenantId, x.SubscriptionId },
                        principalSchema: "saas",
                        principalTable: "Subscriptions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoices_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "organizations",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Movements",
                schema: "saas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BasisId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    PreparedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    VerifiedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Movements1", x => x.Id);
                    table.UniqueConstraint("AK_Movements_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Commercial_Movement", " \"Kind\" BETWEEN 0 AND 2 AND \"Amount\">0 AND (\"Kind\"=1)=(\"BasisId\" IS NOT NULL) AND (\"VerifiedAt\" IS NULL)=(\"VerifiedBy\" IS NULL) ");
                    table.ForeignKey(
                        name: "FK_Movements_Invoices_TenantId_InvoiceId",
                        columns: x => new { x.TenantId, x.InvoiceId },
                        principalSchema: "saas",
                        principalTable: "Invoices",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_Movements_TenantId_BasisId",
                        columns: x => new { x.TenantId, x.BasisId },
                        principalSchema: "saas",
                        principalTable: "Movements",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "organizations",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_TenantId",
                schema: "saas",
                table: "Accounts",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_TenantId_OrganizationId_FirstCompanyId",
                schema: "saas",
                table: "Accounts",
                columns: new[] { "TenantId", "OrganizationId", "FirstCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_TenantId_OpenedAt",
                schema: "saas",
                table: "Cases",
                columns: new[] { "TenantId", "OpenedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_TenantId_At",
                schema: "saas",
                table: "Events",
                columns: new[] { "TenantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Exports_TenantId_ExpiresAt",
                schema: "saas",
                table: "Exports",
                columns: new[] { "TenantId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_PlanId",
                schema: "saas",
                table: "Invoices",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TenantId_RequestKey",
                schema: "saas",
                table: "Invoices",
                columns: new[] { "TenantId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TenantId_SubscriptionId",
                schema: "saas",
                table: "Invoices",
                columns: new[] { "TenantId", "SubscriptionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_TenantId_BasisId",
                schema: "saas",
                table: "Movements",
                columns: new[] { "TenantId", "BasisId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_TenantId_InvoiceId",
                schema: "saas",
                table: "Movements",
                columns: new[] { "TenantId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_TenantId_Reference",
                schema: "saas",
                table: "Movements",
                columns: new[] { "TenantId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Code_Version",
                schema: "saas",
                table: "Plans",
                columns: new[] { "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Provisioning_RequesterId",
                schema: "saas",
                table: "Provisioning",
                column: "RequesterId",
                unique: true,
                filter: " \"Trial\" ");

            migrationBuilder.CreateIndex(
                name: "IX_Provisioning_RequesterId_RequestKey",
                schema: "saas",
                table: "Provisioning",
                columns: new[] { "RequesterId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_End",
                schema: "saas",
                table: "Subscriptions",
                column: "End");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_NextPlanId",
                schema: "saas",
                table: "Subscriptions",
                column: "NextPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_PlanId",
                schema: "saas",
                table: "Subscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_TenantId",
                schema: "saas",
                table: "Subscriptions",
                column: "TenantId",
                unique: true);
            migrationBuilder.Sql(SaaSCommercialV1Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SaaSCommercialV1Sql.Down);
            migrationBuilder.DropTable(
                name: "Accounts",
                schema: "saas");

            migrationBuilder.DropTable(
                name: "Cases",
                schema: "saas");

            migrationBuilder.DropTable(
                name: "Events",
                schema: "saas");

            migrationBuilder.DropTable(
                name: "Exports",
                schema: "saas");

            migrationBuilder.DropTable(
                name: "Movements",
                schema: "saas");

            migrationBuilder.DropTable(
                name: "Provisioning",
                schema: "saas");

            migrationBuilder.DropTable(
                name: "Invoices",
                schema: "saas");

            migrationBuilder.DropTable(
                name: "Subscriptions",
                schema: "saas");

            migrationBuilder.DropTable(
                name: "Plans",
                schema: "saas");
        }
    }
}
