using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RAQM.Api.Data.Migrations;

[DbContext(typeof(RaqmDbContext))]
[Migration("20261114150000_StockedSaleCancellation")]
public sealed class StockedSaleCancellation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("CancelUnissuedStock", "DocumentLines", "boolean",
            schema: "trading", nullable: false, defaultValue: false);

        // Keep the original journal guard's source, control-account and reversal checks.
        // The credit-specific branch is replaced by the per-line deferred guard below.
        var historical = InventoryProcurementV1Sql.Up;
        var start = historical.IndexOf("CREATE FUNCTION inventory.journal_contract_v1()", StringComparison.Ordinal);
        var credit = historical.IndexOf(" IF NEW.\"SourceModule\"='AR.Credit' AND EXISTS(", start, StringComparison.Ordinal);
        migrationBuilder.Sql("CREATE OR REPLACE " + historical[(start + "CREATE ".Length)..credit] + " RETURN NULL;\nEND $$;");
        migrationBuilder.Sql("""
            CREATE FUNCTION inventory.cancellation_line_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."CancelUnissuedStock" AND NOT EXISTS (
                SELECT 1 FROM trading."Documents" credit JOIN trading."DocumentLines" sale
                  ON sale."CompanyId"=credit."CompanyId" AND sale."DocumentId"=credit."OriginalDocumentId"
                WHERE credit."CompanyId"=NEW."CompanyId" AND credit."Id"=NEW."DocumentId" AND credit."Kind"=2
                  AND sale."Id"=NEW."OriginalLineId" AND sale."StockItemId" IS NOT NULL
              ) THEN
                RAISE EXCEPTION 'Cancellation requires the original stocked sale line' USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER b_cancellation_line BEFORE INSERT OR UPDATE ON trading."DocumentLines"
              FOR EACH ROW EXECUTE FUNCTION inventory.cancellation_line_guard_v1();

            CREATE FUNCTION inventory.cancellation_approval_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF OLD."State"=0 AND NEW."State"=1 AND NEW."Kind"=2
                AND EXISTS (SELECT 1 FROM trading."DocumentLines" l WHERE l."CompanyId"=NEW."CompanyId"
                  AND l."DocumentId"=NEW."Id" AND l."CancelUnissuedStock")
                AND (NEW."ApprovedBy" IS NULL OR NEW."ApprovedBy"=OLD."CreatedBy" OR NEW."ApprovedBy"=OLD."UpdatedBy"
                  OR NOT EXISTS (SELECT 1 FROM organizations."Members" m WHERE m."TenantId"=NEW."TenantId"
                    AND m."OrganizationId"=NEW."OrganizationId" AND m."UserId"=NEW."ApprovedBy" AND m."Role"='Owner'))
              THEN
                RAISE EXCEPTION 'Unissued cancellation requires independent Owner approval' USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER b_cancellation_approval BEFORE UPDATE ON trading."Documents"
              FOR EACH ROW EXECUTE FUNCTION inventory.cancellation_approval_guard_v1();

            CREATE FUNCTION inventory.assert_sales_quantity_v1(company uuid, sale_id uuid) RETURNS void LANGUAGE plpgsql AS $$
            DECLARE sale trading."DocumentLines"%ROWTYPE; issued numeric; cancelled numeric;
            BEGIN
              SELECT * INTO STRICT sale FROM trading."DocumentLines"
                WHERE "CompanyId"=company AND "Id"=sale_id FOR UPDATE;
              IF sale."StockItemId" IS NULL THEN RETURN; END IF;
              SELECT coalesce(sum((o."PayloadJson"->>'Quantity')::numeric),0) INTO issued
                FROM inventory."Operations" o WHERE o."CompanyId"=company AND o."TradeLineId"=sale_id AND o."Kind"=2 AND o."State"=2;
              SELECT coalesce(sum(l."Quantity"),0) INTO cancelled
                FROM trading."DocumentLines" l JOIN trading."Documents" d ON d."CompanyId"=l."CompanyId" AND d."Id"=l."DocumentId"
                WHERE l."CompanyId"=company AND l."OriginalLineId"=sale_id AND l."CancelUnissuedStock" AND d."Kind"=2 AND d."State"=2;
              IF issued+cancelled*sale."StockSalesFactor">sale."Quantity"*sale."StockSalesFactor" THEN
                RAISE EXCEPTION 'Issued and cancelled stock exceeds the sale quantity' USING ERRCODE='23514';
              END IF;
            END $$;

            CREATE OR REPLACE FUNCTION inventory.stock_credit_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE row record;
            BEGIN
              IF NEW."State"<>1 OR NEW."SourceModule"<>'AR.Credit' THEN RETURN NULL; END IF;
              FOR row IN
                SELECT l."Id" AS credit_line, l."CancelUnissuedStock" AS cancellation,
                  sale."Id" AS sale_line, sale."StockItemId" AS stocked
                FROM trading."DocumentLines" l JOIN trading."DocumentLines" sale
                  ON sale."CompanyId"=l."CompanyId" AND sale."Id"=l."OriginalLineId"
                WHERE l."CompanyId"=NEW."CompanyId" AND l."DocumentId"=NEW."SourceDocumentId"
              LOOP
                IF row.cancellation THEN
                  IF row.stocked IS NULL OR NOT EXISTS (
                    SELECT 1 FROM trading."Documents" d JOIN organizations."Members" m
                      ON m."TenantId"=d."TenantId" AND m."OrganizationId"=d."OrganizationId" AND m."UserId"=d."ApprovedBy"
                    WHERE d."CompanyId"=NEW."CompanyId" AND d."Id"=NEW."SourceDocumentId" AND d."Kind"=2
                      AND d."State"=2 AND d."JournalId"=NEW."Id" AND d."PostingDate"=NEW."PostingDate"
                      AND d."ApprovedBy"<>d."CreatedBy" AND m."Role"='Owner'
                  ) OR EXISTS (
                    SELECT 1 FROM inventory."Operations" o WHERE o."CompanyId"=NEW."CompanyId"
                      AND o."TradeLineId"=row.credit_line AND o."Kind"=10 AND o."State"=2
                  ) THEN
                    RAISE EXCEPTION 'Unissued cancellation requires an independently approved stocked credit without return movements' USING ERRCODE='23514';
                  END IF;
                  PERFORM inventory.assert_sales_quantity_v1(NEW."CompanyId", row.sale_line);
                ELSIF (row.stocked IS NOT NULL OR EXISTS(
                    SELECT 1 FROM inventory."Operations" o WHERE o."CompanyId"=NEW."CompanyId"
                      AND o."TradeLineId"=row.sale_line AND o."Kind"=2 AND o."State"=2
                  )) AND NOT EXISTS (
                    SELECT 1 FROM inventory."Operations" o WHERE o."CompanyId"=NEW."CompanyId"
                      AND o."TradeLineId"=row.credit_line AND o."TradeDocumentId"=NEW."SourceDocumentId"
                      AND o."Kind"=10 AND o."State"=2 AND o."Date"=NEW."PostingDate" AND o."PostedBy"=NEW."PostedBy"
                  ) THEN
                    RAISE EXCEPTION 'Stocked sales credit requires finalized return evidence' USING ERRCODE='23514';
                END IF;
              END LOOP;
              RETURN NULL;
            END $$;

            CREATE FUNCTION inventory.sales_operation_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."State"=2 AND NEW."Kind"=2 THEN
                PERFORM inventory.assert_sales_quantity_v1(NEW."CompanyId", NEW."TradeLineId");
              ELSIF NEW."State"=2 AND NEW."Kind"=10 AND EXISTS (
                SELECT 1 FROM trading."DocumentLines" l WHERE l."CompanyId"=NEW."CompanyId"
                  AND l."Id"=NEW."TradeLineId" AND l."CancelUnissuedStock"
              ) THEN
                RAISE EXCEPTION 'An unissued cancellation cannot be posted as a stock return' USING ERRCODE='23514';
              END IF;
              RETURN NULL;
            END $$;
            CREATE CONSTRAINT TRIGGER z_sales_operation AFTER INSERT OR UPDATE ON inventory."Operations"
              DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inventory.sales_operation_guard_v1();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Stocked sale cancellation retains financial evidence. Restore a validated backup or migrate forward.");
}
