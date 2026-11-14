using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RAQM.Api.Data.Migrations;

[DbContext(typeof(RaqmDbContext))]
[Migration("20261111110000_IntegrationStabilization")]
public sealed class IntegrationStabilization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        const string ambiguousJoin =
            """
            FROM trading."DocumentLines" l JOIN trading."Documents" d ON d."Id"=l."DocumentId"
               JOIN trading."DocumentLines" original ON original."Id"=l."OriginalLineId"
               WHERE d."CompanyId"=cid AND d."State"=2
            """;
        const string qualifiedJoin =
            """
            FROM trading."DocumentLines" l JOIN trading."Documents" credit ON credit."Id"=l."DocumentId"
               JOIN trading."DocumentLines" original ON original."Id"=l."OriginalLineId"
               WHERE credit."CompanyId"=cid AND credit."State"=2
            """;
        migrationBuilder.Sql(ReplaceRequired(TradingCompanyGuard(), ambiguousJoin, qualifiedJoin));
        migrationBuilder.Sql(ReplaceRequired(GovernanceCommitGuard(), """e."NextState"='Authorized'""",
            """e."NewState"='Authorized'"""));
        migrationBuilder.Sql(ReplaceRequired(AllocationGuard(),
            """
            analytics."CostAllocationExecutions" old WHERE old."CompanyId"=NEW."CompanyId" AND old."SourceLineId"=NEW."SourceLineId"
                   AND old."State"=4 AND old."CorrectedOn">NEW."Date"
            """,
            """
            analytics."CostAllocationExecutions" prior WHERE prior."CompanyId"=NEW."CompanyId" AND prior."SourceLineId"=NEW."SourceLineId"
                   AND prior."State"=4 AND prior."CorrectedOn">NEW."Date"
            """));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(AllocationGuard());
        migrationBuilder.Sql(TradingCompanyGuard());
        migrationBuilder.Sql(GovernanceCommitGuard());
    }

    private static string TradingCompanyGuard() =>
        OriginalFunction(TradingSubledgerV1Sql.Contracts, "trading.assert_company_v1");

    private static string GovernanceCommitGuard() =>
        OriginalFunction(GovernanceControlsV1Sql.Up, "governance.financial_commit_v1");

    private static string AllocationGuard() =>
        OriginalFunction(FinancialDimensionsV1Sql.Up, "analytics.allocation_guard_v1");

    private static string OriginalFunction(string source, string name)
    {
        const string end = "END $$;";
        var start = $"CREATE FUNCTION {name}(";
        var from = source.IndexOf(start, StringComparison.Ordinal);
        if (from < 0)
            throw new InvalidOperationException($"Historical function {name} was not found.");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        if (to < 0)
            throw new InvalidOperationException($"End of historical function {name} was not found.");
        return "CREATE OR REPLACE " + source[(from + "CREATE ".Length)..(to + end.Length)];
    }

    private static string ReplaceRequired(string source, string oldValue, string newValue)
    {
        var index = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0 || source.IndexOf(oldValue, index + oldValue.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("Expected exactly one occurrence of the historical SQL fragment.");
        return source[..index] + newValue + source[(index + oldValue.Length)..];
    }
}
