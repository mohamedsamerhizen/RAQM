using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RAQM.Api.Data.Migrations;

[DbContext(typeof(RaqmDbContext))]
[Migration("20261114160000_StockCancellationPrecision")]
public sealed class StockCancellationPrecision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE FUNCTION inventory.assert_cancellation_precision_v1(company uuid, document uuid) RETURNS void LANGUAGE plpgsql AS $$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM trading."DocumentLines" credit
            JOIN trading."DocumentLines" sale ON sale."CompanyId"=credit."CompanyId" AND sale."Id"=credit."OriginalLineId"
            CROSS JOIN LATERAL (SELECT credit."Quantity"*sale."StockSalesFactor" AS units) base
            WHERE credit."CompanyId"=company AND credit."DocumentId"=document AND credit."CancelUnissuedStock"
              AND (base.units IS NULL OR base.units NOT BETWEEN 0.000001 AND 999999999999 OR base.units<>trunc(base.units,6))
          ) THEN
            RAISE EXCEPTION 'Cancellation must convert exactly to a supported base quantity with at most six decimal places'
              USING ERRCODE='23514';
          END IF;
        END $$;

        CREATE FUNCTION inventory.cancellation_precision_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF NEW."State"=1 AND NEW."SourceModule"='AR.Credit' THEN
            PERFORM inventory.assert_cancellation_precision_v1(NEW."CompanyId", NEW."SourceDocumentId");
          END IF;
          RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER z_cancellation_precision AFTER INSERT OR UPDATE ON accounting."Journals"
          DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inventory.cancellation_precision_guard_v1();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Stock cancellation quantity integrity requires a forward migration or validated restore.");
}
