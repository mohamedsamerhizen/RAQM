using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RAQM.Api.Data.Migrations;

[DbContext(typeof(RaqmDbContext))]
[Migration("20261113110000_GovernedJournalCorrections")]
public sealed class GovernedJournalCorrections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE FUNCTION accounting.correction_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE original accounting."Journals"%ROWTYPE; policy_id uuid; policy_mode integer;
        BEGIN
          IF NEW."State"<>1 OR NEW."ReversesJournalId" IS NULL THEN RETURN NULL; END IF;
          SELECT * INTO STRICT original FROM accounting."Journals"
            WHERE "Id"=NEW."ReversesJournalId" AND "CompanyId"=NEW."CompanyId";
          IF NEW."PostingDate"<original."PostingDate" THEN
            RAISE EXCEPTION 'A reversal cannot precede its original journal' USING ERRCODE='23514';
          END IF;
          IF EXISTS (SELECT 1 FROM analytics."CostAllocationExecutions" e WHERE e."CompanyId"=NEW."CompanyId"
            AND original."Id" IN (e."JournalId",e."CorrectionJournalId")) THEN
            RAISE EXCEPTION 'Correct financial allocations through the allocation workflow' USING ERRCODE='23514';
          END IF;
          IF original."SourceModule"<>'ManualJournal' THEN RETURN NULL; END IF;
          SELECT p."Id", p."Mode" INTO policy_id, policy_mode FROM governance."Policies" p
            WHERE p."CompanyId"=NEW."CompanyId" AND p."Duty"=2
              AND p."ApprovedAt"<=transaction_timestamp() AND p."EffectiveAt"<=transaction_timestamp()
            ORDER BY p."EffectiveAt" DESC,p."Version" DESC LIMIT 1;
          IF policy_mode=0 AND NOT EXISTS (
            SELECT 1 FROM governance."Events" e JOIN governance."Authorizations" a
              ON a."Id"=e."AuthorizationId" AND a."CompanyId"=e."CompanyId"
            WHERE e."CompanyId"=NEW."CompanyId" AND e."Duty"=2 AND e."Type"='GovernanceDecision'
              AND e."Outcome"=1 AND e."NewState"='Authorized' AND e."PolicyId"=policy_id
              AND e."ActorId"=NEW."PostedBy" AND e."JournalId"=NEW."Id" AND e."RelatedId"=original."Id"
              AND a."PolicyId"=policy_id AND a."Duty"=2 AND a."SourceId"=original."Id"
              AND a."State"=3 AND a."UsedBy"=NEW."PostedBy"
              AND a."ContextJson"->>'PostingDate'=to_char(NEW."PostingDate",'YYYY-MM-DD')
              AND a."ContextJson"->>'Reason'=NEW."PostingReason"
              AND a."ContextJson"->>'IdempotencyKey'=NEW."IdempotencyKey") THEN
            RAISE EXCEPTION 'Active governance requires a matching reversal authorization' USING ERRCODE='23514';
          END IF;
          RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER z_correction_guard AFTER INSERT OR UPDATE ON accounting."Journals"
          DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION accounting.correction_guard_v1();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER z_correction_guard ON accounting."Journals";
        DROP FUNCTION accounting.correction_guard_v1();
        """);
}
