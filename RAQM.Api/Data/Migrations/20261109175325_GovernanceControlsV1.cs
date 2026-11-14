using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GovernanceControlsV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "AK_Policies_CompanyId_Id",
                schema: "inventory",
                table: "Policies");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Policies",
                schema: "inventory",
                table: "Policies");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Controls_CompanyId_Id",
                schema: "tax",
                table: "Controls");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Controls",
                schema: "tax",
                table: "Controls");

            migrationBuilder.EnsureSchema(
                name: "governance");

            migrationBuilder.RenameIndex(
                name: "IX_Policies_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "Policies",
                newName: "IX_Policies_TenantId_OrganizationId_CompanyId1");

            migrationBuilder.RenameIndex(
                name: "IX_Controls_TenantId_OrganizationId_CompanyId",
                schema: "tax",
                table: "Controls",
                newName: "IX_Controls_TenantId_OrganizationId_CompanyId1");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Policies_CompanyId_Id1",
                schema: "inventory",
                table: "Policies",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Policies1",
                schema: "inventory",
                table: "Policies",
                column: "Id");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Controls_CompanyId_Id1",
                schema: "tax",
                table: "Controls",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Controls1",
                schema: "tax",
                table: "Controls",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "Delegations",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Duty = table.Column<int>(type: "integer", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegateId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Delegations", x => x.Id);
                    table.UniqueConstraint("AK_Delegations_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Gov_Delegation", " \"ExpiresAt\">\"StartsAt\" AND \"PrincipalId\"<>\"DelegateId\" ");
                    table.ForeignKey(
                        name: "FK_Delegations_AspNetUsers_DelegateId",
                        column: x => x.DelegateId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Delegations_AspNetUsers_PrincipalId",
                        column: x => x.PrincipalId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Delegations_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Grants",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Duty = table.Column<int>(type: "integer", nullable: false),
                    Permission = table.Column<int>(type: "integer", nullable: false),
                    Emergency = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grants", x => x.Id);
                    table.UniqueConstraint("AK_Grants_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Gov_Grant", " \"ExpiresAt\">\"StartsAt\" AND \"Permission\" BETWEEN 0 AND 2 AND (NOT \"Emergency\" OR \"Permission\"=0) ");
                    table.ForeignKey(
                        name: "FK_Grants_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Grants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Grants_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Policies",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Duty = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    EffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RulesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Policies", x => x.Id);
                    table.UniqueConstraint("AK_Policies_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Gov_Policy", " \"Version\">0 AND \"Mode\" BETWEEN 0 AND 2 ");
                    table.ForeignKey(
                        name: "FK_Policies_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Policies_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Policies_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Policies_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Authorizations",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Duty = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    MakerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UsedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IntentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContextJson = table.Column<string>(type: "jsonb", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Authorizations", x => x.Id);
                    table.UniqueConstraint("AK_Authorizations_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Gov_Auth", " \"Amount\">=0 AND \"State\" BETWEEN 0 AND 3 AND ((\"State\"=3)=(\"UsedBy\" IS NOT NULL)) ");
                    table.ForeignKey(
                        name: "FK_Authorizations_AspNetUsers_MakerId",
                        column: x => x.MakerId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Authorizations_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Authorizations_Policies_CompanyId_PolicyId",
                        columns: x => new { x.CompanyId, x.PolicyId },
                        principalSchema: "governance",
                        principalTable: "Policies",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Duty = table.Column<int>(type: "integer", nullable: true),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedId = table.Column<Guid>(type: "uuid", nullable: true),
                    PriorState = table.Column<string>(type: "text", nullable: true),
                    NewState = table.Column<string>(type: "text", nullable: true),
                    Correlation = table.Column<string>(type: "text", nullable: false),
                    EffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    PreviousHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.UniqueConstraint("AK_Events_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Gov_Event", " \"Sequence\">0 AND length(\"Hash\")=64 AND length(\"PreviousHash\")=64 ");
                    table.ForeignKey(
                        name: "FK_Events_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Events_Authorizations_CompanyId_AuthorizationId",
                        columns: x => new { x.CompanyId, x.AuthorizationId },
                        principalSchema: "governance",
                        principalTable: "Authorizations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Events_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Events_Journals_CompanyId_JournalId",
                        columns: x => new { x.CompanyId, x.JournalId },
                        principalSchema: "accounting",
                        principalTable: "Journals",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Events_Policies_CompanyId_PolicyId",
                        columns: x => new { x.CompanyId, x.PolicyId },
                        principalSchema: "governance",
                        principalTable: "Policies",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Exceptions",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CompensatingControl = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exceptions", x => x.Id);
                    table.UniqueConstraint("AK_Exceptions_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Exceptions_Authorizations_CompanyId_AuthorizationId",
                        columns: x => new { x.CompanyId, x.AuthorizationId },
                        principalSchema: "governance",
                        principalTable: "Authorizations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Exceptions_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Reviews",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reviews", x => x.Id);
                    table.UniqueConstraint("AK_Reviews_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Gov_Review", " \"Level\" BETWEEN 1 AND 5 AND \"Decision\" IN(0,1) ");
                    table.ForeignKey(
                        name: "FK_Reviews_AspNetUsers_PrincipalId",
                        column: x => x.PrincipalId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reviews_AspNetUsers_ReviewerId",
                        column: x => x.ReviewerId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reviews_Authorizations_CompanyId_AuthorizationId",
                        columns: x => new { x.CompanyId, x.AuthorizationId },
                        principalSchema: "governance",
                        principalTable: "Authorizations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reviews_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reviews_Delegations_CompanyId_DelegationId",
                        columns: x => new { x.CompanyId, x.DelegationId },
                        principalSchema: "governance",
                        principalTable: "Delegations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Controls",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Objective = table.Column<string>(type: "text", nullable: false),
                    Process = table.Column<string>(type: "text", nullable: false),
                    Frequency = table.Column<string>(type: "text", nullable: false),
                    EvidenceRequired = table.Column<string>(type: "text", nullable: false),
                    Preventive = table.Column<bool>(type: "boolean", nullable: false),
                    CloseBlocking = table.Column<bool>(type: "boolean", nullable: false),
                    Check = table.Column<int>(type: "integer", nullable: false),
                    RiskId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Controls", x => x.Id);
                    table.UniqueConstraint("AK_Controls_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Controls_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Evaluations",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ControlId = table.Column<Guid>(type: "uuid", nullable: false),
                    EngagementId = table.Column<Guid>(type: "uuid", nullable: true),
                    TesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uuid", nullable: true),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    Cutoff = table.Column<DateOnly>(type: "date", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Conclusion = table.Column<int>(type: "integer", nullable: true),
                    Method = table.Column<string>(type: "text", nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReviewEvidence = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Evaluations", x => x.Id);
                    table.UniqueConstraint("AK_Evaluations_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Gov_Eval", " \"From\"<=\"Cutoff\" AND \"Outcome\" BETWEEN 0 AND 2 AND (\"Conclusion\" IS NULL OR \"Conclusion\" BETWEEN 0 AND 2) ");
                    table.ForeignKey(
                        name: "FK_Evaluations_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Evaluations_Controls_CompanyId_ControlId",
                        columns: x => new { x.CompanyId, x.ControlId },
                        principalSchema: "governance",
                        principalTable: "Controls",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Work",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ControlId = table.Column<Guid>(type: "uuid", nullable: true),
                    RiskId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvaluationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Response = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Work", x => x.Id);
                    table.UniqueConstraint("AK_Work_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Gov_Work", " \"Kind\" BETWEEN 0 AND 3 AND \"State\" BETWEEN 0 AND 4 AND \"Severity\" BETWEEN 1 AND 25 ");
                    table.ForeignKey(
                        name: "FK_Work_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Work_Controls_CompanyId_ControlId",
                        columns: x => new { x.CompanyId, x.ControlId },
                        principalSchema: "governance",
                        principalTable: "Controls",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Work_Evaluations_CompanyId_EvaluationId",
                        columns: x => new { x.CompanyId, x.EvaluationId },
                        principalSchema: "governance",
                        principalTable: "Evaluations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Work_Work_CompanyId_ParentId",
                        columns: x => new { x.CompanyId, x.ParentId },
                        principalSchema: "governance",
                        principalTable: "Work",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Work_Work_CompanyId_RiskId",
                        columns: x => new { x.CompanyId, x.RiskId },
                        principalSchema: "governance",
                        principalTable: "Work",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Evidence",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvaluationId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceType = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Provenance = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LegalHold = table.Column<bool>(type: "boolean", nullable: false),
                    RetainThrough = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Evidence", x => x.Id);
                    table.UniqueConstraint("AK_Evidence_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Evidence_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Evidence_Evaluations_CompanyId_EvaluationId",
                        columns: x => new { x.CompanyId, x.EvaluationId },
                        principalSchema: "governance",
                        principalTable: "Evaluations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Evidence_Work_CompanyId_WorkId",
                        columns: x => new { x.CompanyId, x.WorkId },
                        principalSchema: "governance",
                        principalTable: "Work",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Authorizations_CompanyId_PolicyId_Duty_SourceId_IntentHash",
                schema: "governance",
                table: "Authorizations",
                columns: new[] { "CompanyId", "PolicyId", "Duty", "SourceId", "IntentHash" },
                unique: true,
                filter: " \"State\"=0 ");

            migrationBuilder.CreateIndex(
                name: "IX_Authorizations_MakerId",
                schema: "governance",
                table: "Authorizations",
                column: "MakerId");

            migrationBuilder.CreateIndex(
                name: "IX_Authorizations_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Authorizations",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Controls_CompanyId_Code_Version",
                schema: "governance",
                table: "Controls",
                columns: new[] { "CompanyId", "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Controls_CompanyId_RiskId",
                schema: "governance",
                table: "Controls",
                columns: new[] { "CompanyId", "RiskId" });

            migrationBuilder.CreateIndex(
                name: "IX_Controls_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Controls",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_CompanyId_DelegateId_Duty_ExpiresAt",
                schema: "governance",
                table: "Delegations",
                columns: new[] { "CompanyId", "DelegateId", "Duty", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_DelegateId",
                schema: "governance",
                table: "Delegations",
                column: "DelegateId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_PrincipalId",
                schema: "governance",
                table: "Delegations",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Delegations",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evaluations_CompanyId_ControlId_Cutoff_At",
                schema: "governance",
                table: "Evaluations",
                columns: new[] { "CompanyId", "ControlId", "Cutoff", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Evaluations_CompanyId_EngagementId",
                schema: "governance",
                table: "Evaluations",
                columns: new[] { "CompanyId", "EngagementId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evaluations_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Evaluations",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_ActorId",
                schema: "governance",
                table: "Events",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_CompanyId_AuthorizationId",
                schema: "governance",
                table: "Events",
                columns: new[] { "CompanyId", "AuthorizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_CompanyId_JournalId",
                schema: "governance",
                table: "Events",
                columns: new[] { "CompanyId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_CompanyId_PolicyId",
                schema: "governance",
                table: "Events",
                columns: new[] { "CompanyId", "PolicyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_CompanyId_Sequence",
                schema: "governance",
                table: "Events",
                columns: new[] { "CompanyId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_CompanyId_Type_SubjectId_RecordedAt",
                schema: "governance",
                table: "Events",
                columns: new[] { "CompanyId", "Type", "SubjectId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Events",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_CompanyId_EvaluationId",
                schema: "governance",
                table: "Evidence",
                columns: new[] { "CompanyId", "EvaluationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_CompanyId_WorkId_EvaluationId_RecordedAt",
                schema: "governance",
                table: "Evidence",
                columns: new[] { "CompanyId", "WorkId", "EvaluationId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Evidence",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Exceptions_CompanyId_AuthorizationId",
                schema: "governance",
                table: "Exceptions",
                columns: new[] { "CompanyId", "AuthorizationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Exceptions_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Exceptions",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_ApprovedBy",
                schema: "governance",
                table: "Grants",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_CompanyId_UserId_Duty_Permission_ExpiresAt",
                schema: "governance",
                table: "Grants",
                columns: new[] { "CompanyId", "UserId", "Duty", "Permission", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Grants",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_UserId",
                schema: "governance",
                table: "Grants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Policies_ApprovedBy",
                schema: "governance",
                table: "Policies",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Policies_CompanyId_Duty_EffectiveAt",
                schema: "governance",
                table: "Policies",
                columns: new[] { "CompanyId", "Duty", "EffectiveAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Policies_CompanyId_Duty_Version",
                schema: "governance",
                table: "Policies",
                columns: new[] { "CompanyId", "Duty", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Policies_CreatedBy",
                schema: "governance",
                table: "Policies",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Policies_OwnerId",
                schema: "governance",
                table: "Policies",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Policies_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Policies",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_CompanyId_AuthorizationId_Level",
                schema: "governance",
                table: "Reviews",
                columns: new[] { "CompanyId", "AuthorizationId", "Level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_CompanyId_AuthorizationId_PrincipalId",
                schema: "governance",
                table: "Reviews",
                columns: new[] { "CompanyId", "AuthorizationId", "PrincipalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_CompanyId_AuthorizationId_ReviewerId",
                schema: "governance",
                table: "Reviews",
                columns: new[] { "CompanyId", "AuthorizationId", "ReviewerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_CompanyId_DelegationId",
                schema: "governance",
                table: "Reviews",
                columns: new[] { "CompanyId", "DelegationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_PrincipalId",
                schema: "governance",
                table: "Reviews",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_ReviewerId",
                schema: "governance",
                table: "Reviews",
                column: "ReviewerId");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Reviews",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Work_CompanyId_ControlId",
                schema: "governance",
                table: "Work",
                columns: new[] { "CompanyId", "ControlId" });

            migrationBuilder.CreateIndex(
                name: "IX_Work_CompanyId_EvaluationId",
                schema: "governance",
                table: "Work",
                columns: new[] { "CompanyId", "EvaluationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Work_CompanyId_Kind_State_DueOn",
                schema: "governance",
                table: "Work",
                columns: new[] { "CompanyId", "Kind", "State", "DueOn" });

            migrationBuilder.CreateIndex(
                name: "IX_Work_CompanyId_ParentId",
                schema: "governance",
                table: "Work",
                columns: new[] { "CompanyId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Work_CompanyId_RiskId",
                schema: "governance",
                table: "Work",
                columns: new[] { "CompanyId", "RiskId" });

            migrationBuilder.CreateIndex(
                name: "IX_Work_TenantId_OrganizationId_CompanyId",
                schema: "governance",
                table: "Work",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Controls_Work_CompanyId_RiskId",
                schema: "governance",
                table: "Controls",
                columns: new[] { "CompanyId", "RiskId" },
                principalSchema: "governance",
                principalTable: "Work",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Evaluations_Work_CompanyId_EngagementId",
                schema: "governance",
                table: "Evaluations",
                columns: new[] { "CompanyId", "EngagementId" },
                principalSchema: "governance",
                principalTable: "Work",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql(GovernanceControlsV1Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GovernanceControlsV1Sql.Down);
            migrationBuilder.DropForeignKey(
                name: "FK_Controls_Work_CompanyId_RiskId",
                schema: "governance",
                table: "Controls");

            migrationBuilder.DropForeignKey(
                name: "FK_Evaluations_Work_CompanyId_EngagementId",
                schema: "governance",
                table: "Evaluations");

            migrationBuilder.DropTable(
                name: "Events",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "Evidence",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "Exceptions",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "Grants",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "Reviews",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "Authorizations",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "Delegations",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "Policies",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "Work",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "Evaluations",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "Controls",
                schema: "governance");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Policies_CompanyId_Id1",
                schema: "inventory",
                table: "Policies");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Policies1",
                schema: "inventory",
                table: "Policies");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Controls_CompanyId_Id1",
                schema: "tax",
                table: "Controls");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Controls1",
                schema: "tax",
                table: "Controls");

            migrationBuilder.RenameIndex(
                name: "IX_Policies_TenantId_OrganizationId_CompanyId1",
                schema: "inventory",
                table: "Policies",
                newName: "IX_Policies_TenantId_OrganizationId_CompanyId");

            migrationBuilder.RenameIndex(
                name: "IX_Controls_TenantId_OrganizationId_CompanyId1",
                schema: "tax",
                table: "Controls",
                newName: "IX_Controls_TenantId_OrganizationId_CompanyId");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Policies_CompanyId_Id",
                schema: "inventory",
                table: "Policies",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Policies",
                schema: "inventory",
                table: "Policies",
                column: "Id");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Controls_CompanyId_Id",
                schema: "tax",
                table: "Controls",
                columns: new[] { "CompanyId", "Id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Controls",
                schema: "tax",
                table: "Controls",
                column: "Id");
        }
    }
}
