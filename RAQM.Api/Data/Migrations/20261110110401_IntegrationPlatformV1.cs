using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationPlatformV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integration");

            migrationBuilder.CreateTable(
                name: "Applications",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applications", x => x.Id);
                    table.UniqueConstraint("AK_Applications_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Integration_Application", " \"State\" BETWEEN 0 AND 3 AND (\"State\"=0 OR \"ApprovedBy\" IS NOT NULL) ");
                    table.ForeignKey(
                        name: "FK_Applications_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    CausationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Correlation = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events1", x => x.Id);
                    table.UniqueConstraint("AK_Events_CompanyId_Id1", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Events_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Credentials",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.Id);
                    table.UniqueConstraint("AK_Credentials_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Integration_Digest", " octet_length(\"Digest\")=32 ");
                    table.ForeignKey(
                        name: "FK_Credentials_Applications_CompanyId_ApplicationId",
                        columns: x => new { x.CompanyId, x.ApplicationId },
                        principalSchema: "integration",
                        principalTable: "Applications",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Credentials_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Inbox",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Input = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Result = table.Column<string>(type: "text", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inbox", x => x.Id);
                    table.UniqueConstraint("AK_Inbox_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Inbox_Applications_CompanyId_ApplicationId",
                        columns: x => new { x.CompanyId, x.ApplicationId },
                        principalSchema: "integration",
                        principalTable: "Applications",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Inbox_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Schedules",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalHours = table.Column<int>(type: "integer", nullable: false),
                    LookbackDays = table.Column<int>(type: "integer", nullable: false),
                    NextRun = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedules", x => x.Id);
                    table.UniqueConstraint("AK_Schedules_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Integration_Schedule", " \"IntervalHours\" BETWEEN 1 AND 720 AND \"LookbackDays\" BETWEEN 1 AND 90 ");
                    table.ForeignKey(
                        name: "FK_Schedules_Applications_CompanyId_ApplicationId",
                        columns: x => new { x.CompanyId, x.ApplicationId },
                        principalSchema: "integration",
                        principalTable: "Applications",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Schedules_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Endpoint = table.Column<string>(type: "text", nullable: false),
                    Events = table.Column<string[]>(type: "text[]", nullable: false),
                    ProtectedSecret = table.Column<string>(type: "text", nullable: false),
                    SecretVersion = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.UniqueConstraint("AK_Subscriptions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Subscriptions_Applications_CompanyId_ApplicationId",
                        columns: x => new { x.CompanyId, x.ApplicationId },
                        principalSchema: "integration",
                        principalTable: "Applications",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImportedMasters",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedMasters", x => x.Id);
                    table.UniqueConstraint("AK_ImportedMasters_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_ImportedMasters_Applications_CompanyId_ApplicationId",
                        columns: x => new { x.CompanyId, x.ApplicationId },
                        principalSchema: "integration",
                        principalTable: "Applications",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImportedMasters_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImportedMasters_Inbox_CompanyId_BatchId",
                        columns: x => new { x.CompanyId, x.BatchId },
                        principalSchema: "integration",
                        principalTable: "Inbox",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImportedMasters_Parties_CompanyId_PartyId",
                        columns: x => new { x.CompanyId, x.PartyId },
                        principalSchema: "trading",
                        principalTable: "Parties",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Occurrences",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Csv = table.Column<string>(type: "text", nullable: true),
                    Digest = table.Column<string>(type: "text", nullable: true),
                    Failure = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Occurrences", x => x.Id);
                    table.UniqueConstraint("AK_Occurrences_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Occurrences_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Occurrences_Schedules_CompanyId_ScheduleId",
                        columns: x => new { x.CompanyId, x.ScheduleId },
                        principalSchema: "integration",
                        principalTable: "Schedules",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Deliveries",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.UniqueConstraint("AK_Deliveries_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Integration_DeliveryLease", " \"Attempts\">=0 AND \"Generation\">=0 AND \"State\" BETWEEN 0 AND 5 AND ((\"State\"=1 AND \"LeaseUntil\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL) OR (\"State\"<>1 AND \"LeaseUntil\" IS NULL AND \"LeaseToken\" IS NULL)) ");
                    table.ForeignKey(
                        name: "FK_Deliveries_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Deliveries_Events_CompanyId_EventId",
                        columns: x => new { x.CompanyId, x.EventId },
                        principalSchema: "integration",
                        principalTable: "Events",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Deliveries_Subscriptions_CompanyId_SubscriptionId",
                        columns: x => new { x.CompanyId, x.SubscriptionId },
                        principalSchema: "integration",
                        principalTable: "Subscriptions",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Attempts",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attempts", x => x.Id);
                    table.UniqueConstraint("AK_Attempts_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Attempts_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Attempts_Deliveries_CompanyId_DeliveryId",
                        columns: x => new { x.CompanyId, x.DeliveryId },
                        principalSchema: "integration",
                        principalTable: "Deliveries",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_CompanyId_Name",
                schema: "integration",
                table: "Applications",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Applications_TenantId_OrganizationId_CompanyId",
                schema: "integration",
                table: "Applications",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_CompanyId_DeliveryId",
                schema: "integration",
                table: "Attempts",
                columns: new[] { "CompanyId", "DeliveryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_DeliveryId_Generation_Number",
                schema: "integration",
                table: "Attempts",
                columns: new[] { "DeliveryId", "Generation", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_TenantId_OrganizationId_CompanyId",
                schema: "integration",
                table: "Attempts",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_CompanyId_ApplicationId",
                schema: "integration",
                table: "Credentials",
                columns: new[] { "CompanyId", "ApplicationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_TenantId_OrganizationId_CompanyId",
                schema: "integration",
                table: "Credentials",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_CompanyId_EventId",
                schema: "integration",
                table: "Deliveries",
                columns: new[] { "CompanyId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_CompanyId_SubscriptionId",
                schema: "integration",
                table: "Deliveries",
                columns: new[] { "CompanyId", "SubscriptionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_EventId_SubscriptionId",
                schema: "integration",
                table: "Deliveries",
                columns: new[] { "EventId", "SubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_State_DueAt",
                schema: "integration",
                table: "Deliveries",
                columns: new[] { "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_State_LeaseUntil",
                schema: "integration",
                table: "Deliveries",
                columns: new[] { "State", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_TenantId_OrganizationId_CompanyId",
                schema: "integration",
                table: "Deliveries",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_CompanyId_OccurredAt",
                schema: "integration",
                table: "Events",
                columns: new[] { "CompanyId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_CompanyId_Type_SourceId_SourceVersion_Version",
                schema: "integration",
                table: "Events",
                columns: new[] { "CompanyId", "Type", "SourceId", "SourceVersion", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_TenantId_OrganizationId_CompanyId1",
                schema: "integration",
                table: "Events",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedMasters_ApplicationId_ExternalId",
                schema: "integration",
                table: "ImportedMasters",
                columns: new[] { "ApplicationId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedMasters_CompanyId_ApplicationId",
                schema: "integration",
                table: "ImportedMasters",
                columns: new[] { "CompanyId", "ApplicationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedMasters_CompanyId_BatchId",
                schema: "integration",
                table: "ImportedMasters",
                columns: new[] { "CompanyId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedMasters_CompanyId_PartyId",
                schema: "integration",
                table: "ImportedMasters",
                columns: new[] { "CompanyId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedMasters_TenantId_OrganizationId_CompanyId",
                schema: "integration",
                table: "ImportedMasters",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Inbox_ApplicationId_ExternalId",
                schema: "integration",
                table: "Inbox",
                columns: new[] { "ApplicationId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Inbox_CompanyId_ApplicationId",
                schema: "integration",
                table: "Inbox",
                columns: new[] { "CompanyId", "ApplicationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Inbox_TenantId_OrganizationId_CompanyId",
                schema: "integration",
                table: "Inbox",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Occurrences_CompanyId_ScheduleId",
                schema: "integration",
                table: "Occurrences",
                columns: new[] { "CompanyId", "ScheduleId" });

            migrationBuilder.CreateIndex(
                name: "IX_Occurrences_ExpiresAt",
                schema: "integration",
                table: "Occurrences",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Occurrences_ScheduleId_ScheduledAt",
                schema: "integration",
                table: "Occurrences",
                columns: new[] { "ScheduleId", "ScheduledAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Occurrences_State_DueAt",
                schema: "integration",
                table: "Occurrences",
                columns: new[] { "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Occurrences_TenantId_OrganizationId_CompanyId",
                schema: "integration",
                table: "Occurrences",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_CompanyId_ApplicationId",
                schema: "integration",
                table: "Schedules",
                columns: new[] { "CompanyId", "ApplicationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_Enabled_NextRun",
                schema: "integration",
                table: "Schedules",
                columns: new[] { "Enabled", "NextRun" });

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_TenantId_OrganizationId_CompanyId",
                schema: "integration",
                table: "Schedules",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_CompanyId_Active",
                schema: "integration",
                table: "Subscriptions",
                columns: new[] { "CompanyId", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_CompanyId_ApplicationId",
                schema: "integration",
                table: "Subscriptions",
                columns: new[] { "CompanyId", "ApplicationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_TenantId_OrganizationId_CompanyId",
                schema: "integration",
                table: "Subscriptions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });
            migrationBuilder.Sql(IntegrationPlatformV1Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(IntegrationPlatformV1Sql.Down);
            migrationBuilder.DropForeignKey(
                name: "FK_Events_Companies_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Events");

            migrationBuilder.DropTable(
                name: "Attempts",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "Credentials",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ImportedMasters",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "Occurrences",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "Deliveries",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "Inbox",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "Schedules",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "Events",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "Subscriptions",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "Applications",
                schema: "integration");
        }
    }
}
