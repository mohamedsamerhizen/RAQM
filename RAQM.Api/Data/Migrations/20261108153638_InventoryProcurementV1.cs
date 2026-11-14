using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InventoryProcurementV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "Categories",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.UniqueConstraint("AK_Categories_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Categories_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryAccounts",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryAccounts", x => x.Id);
                    table.UniqueConstraint("AK_InventoryAccounts_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Inventory_Role", " \"Role\" BETWEEN 0 AND 9 ");
                    table.ForeignKey(
                        name: "FK_InventoryAccounts_Accounts_CompanyId_AccountId",
                        columns: x => new { x.CompanyId, x.AccountId },
                        principalSchema: "accounting",
                        principalTable: "Accounts",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryAccounts_Companies_TenantId_OrganizationId_Company~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Policies",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    InvoicePriceTolerance = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    ApprovalLimit = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Policies", x => x.Id);
                    table.UniqueConstraint("AK_Policies_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Inventory_Policy", "\"Method\" IN (0,1) AND \"InvoicePriceTolerance\">=0 AND \"ApprovalLimit\">0");
                    table.ForeignKey(
                        name: "FK_Policies_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProcurementDocuments",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsOrder = table.Column<bool>(type: "boolean", nullable: false),
                    RequisitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReplacesDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RequestKey = table.Column<string>(type: "text", nullable: false),
                    RequestHash = table.Column<string>(type: "text", nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    Number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Department = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpectedDelivery = table.Column<DateOnly>(type: "date", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcurementDocuments", x => x.Id);
                    table.UniqueConstraint("AK_ProcurementDocuments_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Inventory_Procurement", "\"State\" BETWEEN 0 AND 4 AND (\"ExpectedDelivery\" IS NULL OR \"ExpectedDelivery\">=\"Date\") AND\n(\"IsOrder\" OR \"ReplacesDocumentId\" IS NULL) AND ((\"IsOrder\" AND \"SupplierId\" IS NOT NULL) OR\n(NOT \"IsOrder\" AND \"SupplierId\" IS NULL AND \"RequisitionId\" IS NULL AND \"State\"<>2))");
                    table.ForeignKey(
                        name: "FK_ProcurementDocuments_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementDocuments_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementDocuments_Companies_TenantId_OrganizationId_Comp~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementDocuments_Parties_CompanyId_SupplierId",
                        columns: x => new { x.CompanyId, x.SupplierId },
                        principalSchema: "trading",
                        principalTable: "Parties",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementDocuments_ProcurementDocuments_CompanyId_Replace~",
                        columns: x => new { x.CompanyId, x.ReplacesDocumentId },
                        principalSchema: "inventory",
                        principalTable: "ProcurementDocuments",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementDocuments_ProcurementDocuments_CompanyId_Requisi~",
                        columns: x => new { x.CompanyId, x.RequisitionId },
                        principalSchema: "inventory",
                        principalTable: "ProcurementDocuments",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Warehouses",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Location = table.Column<string>(type: "text", nullable: false),
                    ManagerId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warehouses", x => x.Id);
                    table.UniqueConstraint("AK_Warehouses_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Warehouses_AspNetUsers_ManagerId",
                        column: x => x.ManagerId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Warehouses_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: true),
                    TaxCodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    BaseUnit = table.Column<string>(type: "text", nullable: false),
                    PurchaseUnit = table.Column<string>(type: "text", nullable: false),
                    SalesUnit = table.Column<string>(type: "text", nullable: false),
                    PurchaseFactor = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    SalesFactor = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    DefaultPurchasePrice = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: true),
                    DefaultSalesPrice = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                    table.UniqueConstraint("AK_Items_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Inventory_Item", "\"Kind\" BETWEEN 0 AND 2 AND \"PurchaseFactor\">0 AND \"SalesFactor\">0 AND \"DefaultPurchasePrice\">=0 AND \"DefaultSalesPrice\">=0");
                    table.ForeignKey(
                        name: "FK_Items_Categories_CompanyId_CategoryId",
                        columns: x => new { x.CompanyId, x.CategoryId },
                        principalSchema: "inventory",
                        principalTable: "Categories",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Items_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Items_Parties_CompanyId_SupplierId",
                        columns: x => new { x.CompanyId, x.SupplierId },
                        principalSchema: "trading",
                        principalTable: "Parties",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Items_TaxCodes_CompanyId_TaxCodeId",
                        columns: x => new { x.CompanyId, x.TaxCodeId },
                        principalSchema: "trading",
                        principalTable: "TaxCodes",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProcurementLines",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxRateId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    BaseQuantity = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    NetAmount = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcurementLines", x => x.Id);
                    table.UniqueConstraint("AK_ProcurementLines_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Inventory_ProcurementLine", "\"Quantity\">0 AND \"BaseQuantity\">0 AND \"UnitPrice\">=0 AND \"DiscountPercent\" BETWEEN 0 AND 100 AND \"NetAmount\">=0 AND \"TaxAmount\">=0");
                    table.ForeignKey(
                        name: "FK_ProcurementLines_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementLines_Items_CompanyId_ItemId",
                        columns: x => new { x.CompanyId, x.ItemId },
                        principalSchema: "inventory",
                        principalTable: "Items",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementLines_ProcurementDocuments_CompanyId_DocumentId",
                        columns: x => new { x.CompanyId, x.DocumentId },
                        principalSchema: "inventory",
                        principalTable: "ProcurementDocuments",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementLines_TaxRates_CompanyId_TaxRateId",
                        columns: x => new { x.CompanyId, x.TaxRateId },
                        principalSchema: "trading",
                        principalTable: "TaxRates",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Operations",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelatedOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrderLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    TradeDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TradeLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceJournalLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestKey = table.Column<string>(type: "text", nullable: false),
                    RequestHash = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: true),
                    BookQuantity = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: true),
                    Accrual = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    SourceQuantity = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PostedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operations", x => x.Id);
                    table.UniqueConstraint("AK_Operations_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Inventory_Operation", "\"Kind\" BETWEEN 0 AND 12 AND \"State\" BETWEEN 0 AND 3 AND\n((\"State\"=2)=(\"Sequence\" IS NOT NULL)) AND\n(\"State\"<>2 OR (\"ApprovedBy\" IS NOT NULL AND \"PostedBy\" IS NOT NULL AND \"PlanJson\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_Operations_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_AspNetUsers_PostedBy",
                        column: x => x.PostedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_DocumentLines_CompanyId_TradeLineId",
                        columns: x => new { x.CompanyId, x.TradeLineId },
                        principalSchema: "trading",
                        principalTable: "DocumentLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_Documents_CompanyId_TradeDocumentId",
                        columns: x => new { x.CompanyId, x.TradeDocumentId },
                        principalSchema: "trading",
                        principalTable: "Documents",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_Items_CompanyId_ItemId",
                        columns: x => new { x.CompanyId, x.ItemId },
                        principalSchema: "inventory",
                        principalTable: "Items",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_JournalLines_CompanyId_SourceJournalLineId",
                        columns: x => new { x.CompanyId, x.SourceJournalLineId },
                        principalSchema: "accounting",
                        principalTable: "JournalLines",
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
                        name: "FK_Operations_Operations_CompanyId_RelatedOperationId",
                        columns: x => new { x.CompanyId, x.RelatedOperationId },
                        principalSchema: "inventory",
                        principalTable: "Operations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_ProcurementLines_CompanyId_OrderLineId",
                        columns: x => new { x.CompanyId, x.OrderLineId },
                        principalSchema: "inventory",
                        principalTable: "ProcurementLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Operations_Warehouses_CompanyId_WarehouseId",
                        columns: x => new { x.CompanyId, x.WarehouseId },
                        principalSchema: "inventory",
                        principalTable: "Warehouses",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProcurementDiscrepancies",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Evidence = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ResolvedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolutionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcurementDiscrepancies", x => x.Id);
                    table.UniqueConstraint("AK_ProcurementDiscrepancies_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Inventory_Discrepancy", "\"Kind\" BETWEEN 0 AND 4 AND (\"ResolutionDate\" IS NULL)=(\"ResolvedBy\" IS NULL)\n  AND (\"ResolutionDate\" IS NULL)=(\"Resolution\" IS NULL) AND (\"ResolutionDate\" IS NULL OR \"ResolutionDate\">=\"Date\")");
                    table.ForeignKey(
                        name: "FK_ProcurementDiscrepancies_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementDiscrepancies_AspNetUsers_ResolvedBy",
                        column: x => x.ResolvedBy,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementDiscrepancies_Companies_TenantId_OrganizationId_~",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementDiscrepancies_Operations_CompanyId_ReceiptId",
                        columns: x => new { x.CompanyId, x.ReceiptId },
                        principalSchema: "inventory",
                        principalTable: "Operations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementDiscrepancies_ProcurementLines_CompanyId_OrderLi~",
                        columns: x => new { x.CompanyId, x.OrderLineId },
                        principalSchema: "inventory",
                        principalTable: "ProcurementLines",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    Gross = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    Allowance = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    Reserved = table.Column<decimal>(type: "numeric(24,6)", precision: 24, scale: 6, nullable: false),
                    LotsJson = table.Column<string>(type: "jsonb", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.Id);
                    table.UniqueConstraint("AK_StockMovements_CompanyId_Id", x => new { x.CompanyId, x.Id });
                    table.CheckConstraint("CK_Inventory_Movement", "\"Number\">0 AND \"Sequence\">0 AND (\"Quantity\"<>0 OR \"Gross\"<>0 OR \"Allowance\"<>0 OR \"Reserved\"<>0)");
                    table.ForeignKey(
                        name: "FK_StockMovements_Companies_TenantId_OrganizationId_CompanyId",
                        columns: x => new { x.TenantId, x.OrganizationId, x.CompanyId },
                        principalSchema: "organizations",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Items_CompanyId_ItemId",
                        columns: x => new { x.CompanyId, x.ItemId },
                        principalSchema: "inventory",
                        principalTable: "Items",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Operations_CompanyId_OperationId",
                        columns: x => new { x.CompanyId, x.OperationId },
                        principalSchema: "inventory",
                        principalTable: "Operations",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Warehouses_CompanyId_WarehouseId",
                        columns: x => new { x.CompanyId, x.WarehouseId },
                        principalSchema: "inventory",
                        principalTable: "Warehouses",
                        principalColumns: new[] { "CompanyId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_CompanyId_Code",
                schema: "inventory",
                table: "Categories",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "Categories",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAccounts_CompanyId_AccountId",
                schema: "inventory",
                table: "InventoryAccounts",
                columns: new[] { "CompanyId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAccounts_CompanyId_Role",
                schema: "inventory",
                table: "InventoryAccounts",
                columns: new[] { "CompanyId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAccounts_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "InventoryAccounts",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_CompanyId_CategoryId",
                schema: "inventory",
                table: "Items",
                columns: new[] { "CompanyId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_CompanyId_Code",
                schema: "inventory",
                table: "Items",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_CompanyId_SupplierId",
                schema: "inventory",
                table: "Items",
                columns: new[] { "CompanyId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_CompanyId_TaxCodeId",
                schema: "inventory",
                table: "Items",
                columns: new[] { "CompanyId", "TaxCodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "Items",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_ApprovedBy",
                schema: "inventory",
                table: "Operations",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_ItemId",
                schema: "inventory",
                table: "Operations",
                columns: new[] { "CompanyId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_JournalId",
                schema: "inventory",
                table: "Operations",
                columns: new[] { "CompanyId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_OrderLineId",
                schema: "inventory",
                table: "Operations",
                columns: new[] { "CompanyId", "OrderLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_RelatedOperationId",
                schema: "inventory",
                table: "Operations",
                columns: new[] { "CompanyId", "RelatedOperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_RequestKey",
                schema: "inventory",
                table: "Operations",
                columns: new[] { "CompanyId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_Sequence",
                schema: "inventory",
                table: "Operations",
                columns: new[] { "CompanyId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_SourceJournalLineId",
                schema: "inventory",
                table: "Operations",
                columns: new[] { "CompanyId", "SourceJournalLineId" },
                unique: true,
                filter: "\"State\" = 2 AND \"SourceJournalLineId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_TradeDocumentId",
                schema: "inventory",
                table: "Operations",
                columns: new[] { "CompanyId", "TradeDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_TradeLineId",
                schema: "inventory",
                table: "Operations",
                columns: new[] { "CompanyId", "TradeLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CompanyId_WarehouseId",
                schema: "inventory",
                table: "Operations",
                columns: new[] { "CompanyId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_CreatedBy",
                schema: "inventory",
                table: "Operations",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_PostedBy",
                schema: "inventory",
                table: "Operations",
                column: "PostedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "Operations",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Policies_CompanyId",
                schema: "inventory",
                table: "Policies",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Policies_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "Policies",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDiscrepancies_CompanyId_Date_ResolutionDate",
                schema: "inventory",
                table: "ProcurementDiscrepancies",
                columns: new[] { "CompanyId", "Date", "ResolutionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDiscrepancies_CompanyId_OrderLineId",
                schema: "inventory",
                table: "ProcurementDiscrepancies",
                columns: new[] { "CompanyId", "OrderLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDiscrepancies_CompanyId_ReceiptId",
                schema: "inventory",
                table: "ProcurementDiscrepancies",
                columns: new[] { "CompanyId", "ReceiptId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDiscrepancies_CreatedBy",
                schema: "inventory",
                table: "ProcurementDiscrepancies",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDiscrepancies_ResolvedBy",
                schema: "inventory",
                table: "ProcurementDiscrepancies",
                column: "ResolvedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDiscrepancies_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "ProcurementDiscrepancies",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDocuments_ApprovedBy",
                schema: "inventory",
                table: "ProcurementDocuments",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDocuments_CompanyId_Number",
                schema: "inventory",
                table: "ProcurementDocuments",
                columns: new[] { "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDocuments_CompanyId_ReplacesDocumentId",
                schema: "inventory",
                table: "ProcurementDocuments",
                columns: new[] { "CompanyId", "ReplacesDocumentId" },
                unique: true,
                filter: "\"State\" NOT IN (3,4) AND \"ReplacesDocumentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDocuments_CompanyId_RequestKey",
                schema: "inventory",
                table: "ProcurementDocuments",
                columns: new[] { "CompanyId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDocuments_CompanyId_RequisitionId",
                schema: "inventory",
                table: "ProcurementDocuments",
                columns: new[] { "CompanyId", "RequisitionId" },
                unique: true,
                filter: "\"State\" NOT IN (3,4) AND \"RequisitionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDocuments_CompanyId_SupplierId",
                schema: "inventory",
                table: "ProcurementDocuments",
                columns: new[] { "CompanyId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDocuments_CreatedBy",
                schema: "inventory",
                table: "ProcurementDocuments",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementDocuments_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "ProcurementDocuments",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementLines_CompanyId_DocumentId_ItemId",
                schema: "inventory",
                table: "ProcurementLines",
                columns: new[] { "CompanyId", "DocumentId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementLines_CompanyId_ItemId",
                schema: "inventory",
                table: "ProcurementLines",
                columns: new[] { "CompanyId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementLines_CompanyId_TaxRateId",
                schema: "inventory",
                table: "ProcurementLines",
                columns: new[] { "CompanyId", "TaxRateId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementLines_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "ProcurementLines",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_CompanyId_ItemId_WarehouseId_Sequence_Number",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "CompanyId", "ItemId", "WarehouseId", "Sequence", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_CompanyId_OperationId_Number",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "CompanyId", "OperationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_CompanyId_WarehouseId",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "CompanyId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_CompanyId_Code",
                schema: "inventory",
                table: "Warehouses",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_ManagerId",
                schema: "inventory",
                table: "Warehouses",
                column: "ManagerId");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_TenantId_OrganizationId_CompanyId",
                schema: "inventory",
                table: "Warehouses",
                columns: new[] { "TenantId", "OrganizationId", "CompanyId" });
            migrationBuilder.Sql(InventoryProcurementV1Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(InventoryProcurementV1Sql.Down);
            migrationBuilder.DropTable(
                name: "InventoryAccounts",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Policies",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "ProcurementDiscrepancies",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockMovements",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Operations",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "ProcurementLines",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Warehouses",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Items",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "ProcurementDocuments",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Categories",
                schema: "inventory");
        }
    }
}
