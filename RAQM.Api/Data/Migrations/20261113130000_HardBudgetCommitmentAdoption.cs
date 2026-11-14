using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RAQM.Api.Data.Migrations;

[DbContext(typeof(RaqmDbContext))]
[Migration("20261113130000_HardBudgetCommitmentAdoption")]
public sealed class HardBudgetCommitmentAdoption : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var function = HistoricalGuard();
        function = Replace(function,
            "max(\"Amount\") FILTER (WHERE \"EventKey\" LIKE 'reserve:%' OR \"EventKey\" LIKE 'capital-reserve:%')",
            "max(\"Amount\") FILTER (WHERE \"EventKey\" LIKE 'reserve:%' OR \"EventKey\" LIKE 'capital-reserve:%' OR \"EventKey\" LIKE 'rollin:%')");
        function = Replace(function,
            " IF initial IS NULL OR exposure<0 OR exposure>initial THEN",
            """
 IF NEW."EventKey" LIKE 'rollin:%' AND (
   NEW."ProcurementLineId" IS NULL OR NEW."Amount"<=0 OR
   NEW."EventKey"<>'rollin:'||replace(NEW."ProcurementLineId"::text,'-','')||':'||replace(NEW."PeriodId"::text,'-','') OR
   NOT (
     EXISTS(SELECT 1 FROM planning."BudgetCommitmentEvents" prior
       WHERE prior."CompanyId"=NEW."CompanyId" AND prior."AccountId"=NEW."AccountId"
         AND prior."ProcurementLineId"=NEW."ProcurementLineId" AND prior."PeriodId"<>NEW."PeriodId"
         AND prior."EventKey"='rollout:'||replace(NEW."ProcurementLineId"::text,'-','')||':'||replace(NEW."PeriodId"::text,'-','')
         AND prior."Amount"=-NEW."Amount")
     OR (NEW."Kind"=0 AND EXISTS(
       SELECT 1 FROM inventory."ProcurementLines" source
       JOIN inventory."ProcurementDocuments" req ON req."Id"=source."DocumentId" AND req."CompanyId"=source."CompanyId"
       JOIN inventory."ProcurementDocuments" ord ON ord."RequisitionId"=req."Id" AND ord."CompanyId"=req."CompanyId"
       JOIN inventory."ProcurementLines" ordered ON ordered."DocumentId"=ord."Id" AND ordered."CompanyId"=ord."CompanyId"
       JOIN planning."BudgetCommitmentEvents" moved ON moved."ProcurementLineId"=ordered."Id" AND moved."CompanyId"=NEW."CompanyId"
       WHERE source."Id"=NEW."ProcurementLineId" AND source."CompanyId"=NEW."CompanyId" AND req."IsOrder"=false
         AND ord."IsOrder"=true AND ord."State" IN (1,2,3) AND NEW."Amount"=source."NetAmount"
         AND moved."AccountId"=NEW."AccountId" AND moved."PeriodId"=NEW."PeriodId"
         AND moved."EventKey"='rollin:'||replace(ordered."Id"::text,'-','')||':'||replace(NEW."PeriodId"::text,'-','')))
   )) THEN
   RAISE EXCEPTION 'Commitment rollover requires retained source transfer' USING ERRCODE='23514'; END IF;
 IF initial IS NULL OR exposure<0 OR exposure>initial THEN
""");
        migrationBuilder.Sql(function);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(HistoricalGuard());

    private static string HistoricalGuard()
    {
        var source = BudgetPlanningV1Sql.Up;
        const string start = "CREATE FUNCTION planning.commitment_contract_v1()";
        var from = source.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) throw new InvalidOperationException("Historical commitment guard was not found.");
        var end = source.IndexOf("END $$;", from, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException("Historical commitment guard ending was not found.");
        return source[from..(end + "END $$;".Length)].Replace(start,
            "CREATE OR REPLACE FUNCTION planning.commitment_contract_v1()", StringComparison.Ordinal);
    }

    private static string Replace(string source, string oldText, string newText) =>
        source.Contains(oldText, StringComparison.Ordinal) ? source.Replace(oldText, newText, StringComparison.Ordinal)
            : throw new InvalidOperationException("Historical commitment guard contract changed.");
}
