using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RAQM.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StockedSalesIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StockItemId",
                schema: "trading",
                table: "DocumentLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StockSalesFactor",
                schema: "trading",
                table: "DocumentLines",
                type: "numeric(20,6)",
                precision: 20,
                scale: 6,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLines_CompanyId_StockItemId",
                schema: "trading",
                table: "DocumentLines",
                columns: new[] { "CompanyId", "StockItemId" });

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentLines_Items_CompanyId_StockItemId",
                schema: "trading",
                table: "DocumentLines",
                columns: new[] { "CompanyId", "StockItemId" },
                principalSchema: "inventory",
                principalTable: "Items",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE FUNCTION trading.stock_intent_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE document_kind integer;
                BEGIN
                  SELECT d."Kind" INTO STRICT document_kind FROM trading."Documents" d
                    WHERE d."CompanyId"=NEW."CompanyId" AND d."Id"=NEW."DocumentId";
                  IF (document_kind=0 AND NEW."StockSalesFactor" IS NULL) OR
                     (document_kind<>0 AND (NEW."StockItemId" IS NOT NULL OR NEW."StockSalesFactor" IS NOT NULL)) OR
                     (NEW."StockItemId" IS NULL AND NEW."StockSalesFactor" IS DISTINCT FROM 0 AND document_kind=0) OR
                     (NEW."StockItemId" IS NOT NULL AND NOT EXISTS (
                       SELECT 1 FROM inventory."Items" i WHERE i."CompanyId"=NEW."CompanyId" AND i."Id"=NEW."StockItemId"
                         AND i."Kind"=0 AND i."IsActive" AND i."SalesFactor"=NEW."StockSalesFactor"
                         AND NEW."Quantity"*i."SalesFactor" BETWEEN 0.000001 AND 999999999999
                         AND round(NEW."Quantity"*i."SalesFactor",6)=NEW."Quantity"*i."SalesFactor")) THEN
                    RAISE EXCEPTION 'Invalid invoice stock intent' USING ERRCODE='23514';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER b_stock_intent BEFORE INSERT OR UPDATE ON trading."DocumentLines"
                  FOR EACH ROW EXECUTE FUNCTION trading.stock_intent_guard_v1();

                CREATE FUNCTION inventory.issue_intent_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE sale trading."DocumentLines"%ROWTYPE; invoice trading."Documents"%ROWTYPE;
                  factor numeric; issued numeric;
                BEGIN
                  IF NEW."Kind"<>2 OR NEW."State"<>2 THEN RETURN NULL; END IF;
                  SELECT * INTO STRICT sale FROM trading."DocumentLines"
                    WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."TradeLineId" FOR UPDATE;
                  SELECT * INTO STRICT invoice FROM trading."Documents"
                    WHERE "CompanyId"=NEW."CompanyId" AND "Id"=sale."DocumentId";
                  SELECT i."SalesFactor" INTO STRICT factor FROM inventory."Items" i
                    WHERE i."CompanyId"=NEW."CompanyId" AND i."Id"=NEW."ItemId" AND i."Kind"=0;
                  SELECT coalesce(sum((o."PayloadJson"->>'Quantity')::numeric),0) INTO issued FROM inventory."Operations" o
                    WHERE o."CompanyId"=NEW."CompanyId" AND o."Kind"=2 AND o."State"=2 AND o."TradeLineId"=sale."Id";
                  IF invoice."Id" IS DISTINCT FROM NEW."TradeDocumentId" OR invoice."Kind"<>0 OR invoice."State"<>2
                    OR invoice."PostingDate">NEW."Date" OR sale."StockSalesFactor"=0
                    OR EXISTS(SELECT 1 FROM accounting."AuditEvents" a WHERE a."CompanyId"=NEW."CompanyId"
                      AND a."EntityId"=sale."Id" AND a."Action"='LegacyInvoiceLineConfirmedNonStock')
                    OR (sale."StockItemId" IS NOT NULL AND sale."StockItemId"<>NEW."ItemId")
                    OR issued>sale."Quantity"*coalesce(sale."StockSalesFactor",factor)
                    OR EXISTS(SELECT 1 FROM inventory."Operations" o WHERE o."CompanyId"=NEW."CompanyId"
                      AND o."Kind"=2 AND o."State"=2 AND o."TradeLineId"=sale."Id" AND o."ItemId"<>NEW."ItemId") THEN
                    RAISE EXCEPTION 'Stock issue does not match the posted sales line or exceeds its quantity' USING ERRCODE='23514';
                  END IF;
                  RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER z_issue_intent AFTER INSERT OR UPDATE ON inventory."Operations"
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inventory.issue_intent_guard_v1();

                CREATE FUNCTION inventory.stock_credit_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."State"=1 AND NEW."SourceModule"='AR.Credit' AND EXISTS (
                    SELECT 1 FROM trading."DocumentLines" credit JOIN trading."DocumentLines" sale
                      ON sale."CompanyId"=credit."CompanyId" AND sale."Id"=credit."OriginalLineId"
                    WHERE credit."CompanyId"=NEW."CompanyId" AND credit."DocumentId"=NEW."SourceDocumentId"
                      AND sale."StockItemId" IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM inventory."Operations" o WHERE o."CompanyId"=NEW."CompanyId"
                          AND o."TradeLineId"=credit."Id" AND o."TradeDocumentId"=credit."DocumentId"
                          AND o."Kind"=10 AND o."State"=2 AND o."Date"=NEW."PostingDate"
                          AND o."PostedBy"=NEW."PostedBy")) THEN
                    RAISE EXCEPTION 'Stocked sales credit requires finalized return evidence' USING ERRCODE='23514';
                  END IF;
                  RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER z_stock_credit AFTER INSERT OR UPDATE ON accounting."Journals"
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inventory.stock_credit_guard_v1();
                """);
            migrationBuilder.Sql(AssetContract(scoped: true));
            migrationBuilder.Sql(ReceiptContract(multiWarehouse: true));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AssetContract(scoped: false));
            migrationBuilder.Sql(ReceiptContract(multiWarehouse: false));
            migrationBuilder.Sql("""
                DROP TRIGGER z_stock_credit ON accounting."Journals";
                DROP FUNCTION inventory.stock_credit_guard_v1();
                DROP TRIGGER z_issue_intent ON inventory."Operations";
                DROP FUNCTION inventory.issue_intent_guard_v1();
                DROP TRIGGER b_stock_intent ON trading."DocumentLines";
                DROP FUNCTION trading.stock_intent_guard_v1();
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_DocumentLines_Items_CompanyId_StockItemId",
                schema: "trading",
                table: "DocumentLines");

            migrationBuilder.DropIndex(
                name: "IX_DocumentLines_CompanyId_StockItemId",
                schema: "trading",
                table: "DocumentLines");

            migrationBuilder.DropColumn(
                name: "StockItemId",
                schema: "trading",
                table: "DocumentLines");

            migrationBuilder.DropColumn(
                name: "StockSalesFactor",
                schema: "trading",
                table: "DocumentLines");
        }

        private static string AssetContract(bool scoped)
        {
            const string start = "CREATE FUNCTION fixed_assets.contract_v1()";
            const string next = "CREATE FUNCTION fixed_assets.child_insert_v1()";
            var sql = FixedAssetsV1Sql.Up;
            var from = sql.IndexOf(start, StringComparison.Ordinal);
            var function = sql[from..sql.IndexOf(next, from, StringComparison.Ordinal)]
                .Replace(start, "CREATE OR REPLACE FUNCTION fixed_assets.contract_v1()", StringComparison.Ordinal);
            if (!scoped) return function;
            const string billWide = """WHERE credit."Id"=NEW."SourceDocumentId" AND credit."CompanyId"=NEW."CompanyId") THEN""";
            if (!function.Contains(billWide, StringComparison.Ordinal))
                throw new InvalidOperationException("Historical asset credit guard was not found.");
            return function.Replace(billWide, """
                JOIN trading."DocumentLines" credited ON credited."CompanyId"=credit."CompanyId" AND credited."DocumentId"=credit."Id"
                JOIN trading."DocumentLines" basis ON basis."CompanyId"=bill."CompanyId" AND basis."DocumentId"=bill."Id"
                    AND basis."Id"=credited."OriginalLineId"
                WHERE credit."Id"=NEW."SourceDocumentId" AND credit."CompanyId"=NEW."CompanyId"
                    AND bill."CompanyId"=credit."CompanyId" AND l."CompanyId"=bill."CompanyId" AND c."CompanyId"=bill."CompanyId"
                    AND basis."NetAmount">0 AND l."Number"=2+(
                      SELECT coalesce(sum((prior."NetAmount">0)::int+(prior."TaxAmount">0)::int),0)
                      FROM trading."DocumentLines" prior WHERE prior."CompanyId"=bill."CompanyId"
                        AND prior."DocumentId"=bill."Id" AND prior."Number"<basis."Number")) THEN
                """, StringComparison.Ordinal);
        }

        private static string ReceiptContract(bool multiWarehouse)
        {
            const string start = "CREATE FUNCTION inventory.operation_contract_v1(";
            const string end = "END $$;";
            var source = InventoryProcurementV1Sql.Up;
            var from = source.IndexOf(start, StringComparison.Ordinal);
            if (from < 0) throw new InvalidOperationException("Historical receipt contract was not found.");
            var to = source.IndexOf(end, from, StringComparison.Ordinal);
            var function = "CREATE OR REPLACE " + source[(from + "CREATE ".Length)..(to + end.Length)];
            const string restriction = """ OR r."WarehouseId"<>o."WarehouseId" """;
            if (!function.Contains(restriction.TrimEnd(), StringComparison.Ordinal))
                throw new InvalidOperationException("Historical receipt warehouse restriction was not found.");
            return multiWarehouse ? function.Replace(restriction.TrimEnd(), "", StringComparison.Ordinal) : function;
        }
    }
}
