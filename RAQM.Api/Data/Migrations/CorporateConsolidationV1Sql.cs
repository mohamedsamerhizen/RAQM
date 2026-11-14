namespace RAQM.Api.Data.Migrations;

internal static class CorporateConsolidationV1Sql
{
    public const string Up = """
CREATE FUNCTION groups.approval_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."ApprovedBy" IS NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF OLD."ApprovedBy" IS NULL AND NEW."ApprovedBy" IS NOT NULL AND NEW."ApprovedBy"<>NEW."PreparedBy"
     AND to_jsonb(NEW)-'ApprovedBy'=to_jsonb(OLD)-'ApprovedBy' THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Group configuration requires a draft and independent approval' USING ERRCODE='23514';
END $$;

CREATE FUNCTION groups.ownership_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE cursor_id uuid; seen uuid[]; next_id uuid; next_kind integer; at_date date;
BEGIN
 IF NEW."InvesteeId"=NEW."CompanyId" OR NOT EXISTS(SELECT 1 FROM groups."CorporateGroups" g WHERE g."Id"=NEW."GroupId"
   AND g."StartsOn"<=NEW."EffectiveOn" AND (g."RetiredOn" IS NULL OR g."RetiredOn">NEW."EffectiveOn"))
 OR EXISTS(SELECT 1 FROM groups."GroupOwnerships" x
   WHERE x."GroupId"=NEW."GroupId" AND x."InvesteeId"=NEW."InvesteeId" AND x."EffectiveOn">=NEW."EffectiveOn") THEN
   RAISE EXCEPTION 'Ownership requires a prospective nonparent investee' USING ERRCODE='23514';
 END IF;
 FOR at_date IN SELECT NEW."EffectiveOn" UNION SELECT x."EffectiveOn" FROM groups."GroupOwnerships" x
     WHERE x."GroupId"=NEW."GroupId" AND x."EffectiveOn">NEW."EffectiveOn"
 LOOP
   FOR cursor_id IN SELECT DISTINCT x."InvesteeId" FROM groups."GroupOwnerships" x
      WHERE x."GroupId"=NEW."GroupId" AND x."EffectiveOn"<=at_date UNION SELECT NEW."InvesteeId"
   LOOP
     seen:=ARRAY[cursor_id];
     IF cursor_id=NEW."InvesteeId" THEN
       next_id:=NEW."InvestorId"; next_kind:=NEW."Kind";
     ELSE
       SELECT x."InvestorId",x."Kind" INTO next_id,next_kind FROM groups."GroupOwnerships" x
        WHERE x."GroupId"=NEW."GroupId" AND x."InvesteeId"=cursor_id AND x."EffectiveOn"<=at_date
        ORDER BY x."EffectiveOn" DESC LIMIT 1;
     END IF;
     IF next_kind=4 THEN CONTINUE; END IF;
     WHILE next_id IS DISTINCT FROM NEW."CompanyId" LOOP
       IF next_id IS NULL OR next_id=ANY(seen) THEN
         RAISE EXCEPTION 'Cyclic or disconnected group ownership' USING ERRCODE='23514';
       END IF;
       seen:=array_append(seen,next_id);
       IF next_id=NEW."InvesteeId" THEN
         next_id:=NEW."InvestorId"; next_kind:=NEW."Kind";
       ELSE
         SELECT x."InvestorId",x."Kind" INTO next_id,next_kind FROM groups."GroupOwnerships" x
          WHERE x."GroupId"=NEW."GroupId" AND x."InvesteeId"=next_id AND x."EffectiveOn"<=at_date
          ORDER BY x."EffectiveOn" DESC LIMIT 1;
       END IF;
       IF next_kind IS DISTINCT FROM 0 THEN
         RAISE EXCEPTION 'Group paths require controlled intermediate companies' USING ERRCODE='23514';
       END IF;
     END LOOP;
   END LOOP;
 END LOOP;
 RETURN NEW;
END $$;

CREATE FUNCTION groups.register_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='UPDATE' AND to_jsonb(NEW)-ARRAY['RetiredOn','ReportReaders']=to_jsonb(OLD)-ARRAY['RetiredOn','ReportReaders']
   AND (NEW."RetiredOn" IS NOT DISTINCT FROM OLD."RetiredOn" OR (OLD."RetiredOn" IS NULL AND NEW."RetiredOn">CURRENT_DATE))
   THEN RETURN NEW; END IF;
 RAISE EXCEPTION 'Corporate group identity and retirement history are retained' USING ERRCODE='23514';
END $$;

CREATE FUNCTION groups.pair_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE p groups."IntercompanyPolicies"%ROWTYPE; b groups."IntercompanyPairs"%ROWTYPE;
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Reciprocal evidence is retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' THEN
   SELECT * INTO p FROM groups."IntercompanyPolicies" x WHERE x."Id"=NEW."PolicyId" AND x."GroupId"=NEW."GroupId"
     AND x."CreditorId"=NEW."CreditorId" AND x."DebtorId"=NEW."DebtorId" AND x."EffectiveOn"<=NEW."Date" AND x."ApprovedBy" IS NOT NULL;
   IF NOT FOUND OR NEW."State"<>0 OR NEW."ApprovedBy" IS NOT NULL OR NEW."PlanJson" IS NOT NULL THEN
     RAISE EXCEPTION 'Create a reciprocal draft under an approved policy' USING ERRCODE='23514';
   END IF;
   IF NEW."BasisId" IS NOT NULL THEN
     SELECT * INTO b FROM groups."IntercompanyPairs" x WHERE x."Id"=NEW."BasisId" AND x."GroupId"=NEW."GroupId"
       AND x."CreditorId"=NEW."CreditorId" AND x."DebtorId"=NEW."DebtorId" AND x."Date"<=NEW."Date" AND x."State"=3;
     IF NOT FOUND OR NEW."Kind" IN(0,1) OR (NEW."Kind"=2 AND b."Kind"<>1) OR (NEW."Kind"=3 AND b."Kind" NOT IN(0,1,2))
       OR (NEW."Kind"=4 AND (b."Kind"=4 OR NEW."Amount"<>b."Amount")) THEN
       RAISE EXCEPTION 'Reciprocal draft requires an eligible posted basis' USING ERRCODE='23514';
     END IF;
   ELSIF NEW."Kind" IN(2,3,4) THEN
     RAISE EXCEPTION 'The movement requires reciprocal basis evidence' USING ERRCODE='23514';
   END IF;
   RETURN NEW;
 END IF;
 IF to_jsonb(NEW)-ARRAY['State','ApprovedBy','PlanJson','CreditorJournalId','DebtorJournalId'] <>
    to_jsonb(OLD)-ARRAY['State','ApprovedBy','PlanJson','CreditorJournalId','DebtorJournalId'] THEN
   RAISE EXCEPTION 'Reciprocal financial evidence is immutable' USING ERRCODE='23514';
 END IF;
 IF (OLD."State"=0 AND NEW."State"=1 AND NEW."ApprovedBy"<>NEW."CreatedBy" AND NEW."PlanJson" IS NULL)
 OR (OLD."State"=1 AND NEW."State"=2 AND OLD."ApprovedBy"=NEW."ApprovedBy" AND NEW."PlanJson" IS NOT NULL)
 OR (OLD."State"=2 AND NEW."State"=3 AND OLD."ApprovedBy"=NEW."ApprovedBy" AND OLD."PlanJson"=NEW."PlanJson")
 OR (OLD."State" IN(0,1) AND NEW."State"=4 AND OLD."ApprovedBy" IS NOT DISTINCT FROM NEW."ApprovedBy" AND NEW."PlanJson" IS NULL) THEN
   RETURN NEW;
 END IF;
 RAISE EXCEPTION 'Invalid reciprocal posting state' USING ERRCODE='23514';
END $$;

CREATE FUNCTION groups.pair_journal_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE p groups."IntercompanyPairs"%ROWTYPE; actual jsonb; expected jsonb; original uuid; paired boolean;
BEGIN
 IF NEW."State"<>1 OR (TG_OP='UPDATE' AND OLD."State"=1) THEN RETURN NEW; END IF;
 paired:=NEW."SourceModule"='Intercompany.Pair' OR EXISTS(SELECT 1 FROM accounting."Journals" j
     WHERE j."Id"=NEW."ReversesJournalId" AND j."CompanyId"=NEW."CompanyId" AND j."SourceModule"='Intercompany.Pair');
 IF NOT paired THEN
   IF EXISTS(SELECT 1 FROM accounting."JournalLines" l JOIN groups."IntercompanyPolicies" c ON
         (c."CreditorId"=l."CompanyId" AND c."DueFromId"=l."AccountId" OR c."DebtorId"=l."CompanyId" AND c."DueToId"=l."AccountId")
       WHERE l."JournalId"=NEW."Id" AND c."ApprovedBy" IS NOT NULL) THEN
     RAISE EXCEPTION 'Reciprocal control requires a coordinated source' USING ERRCODE='23514';
   END IF;
   RETURN NEW;
 END IF;
 SELECT * INTO p FROM groups."IntercompanyPairs" x
   WHERE x."State"=2 AND x."Date"=NEW."PostingDate" AND x."Currency"=NEW."Currency"
     AND x."TenantId"=NEW."TenantId" AND x."OrganizationId"=NEW."OrganizationId"
     AND (x."CreditorId"=NEW."CompanyId" OR x."DebtorId"=NEW."CompanyId")
     AND 'Intercompany:'||replace(x."Id"::text,'-','')=NEW."IdempotencyKey";
 IF NOT FOUND OR p."PlanJson"->>'Actor' IS DISTINCT FROM NEW."PostedBy"::text THEN
   RAISE EXCEPTION 'Journal must match a staged reciprocal operation' USING ERRCODE='23514';
 END IF;
 IF p."CreditorId"=NEW."CompanyId" THEN
   expected:=p."PlanJson"->'Creditor'; original:=(p."PlanJson"->>'CreditorReversal')::uuid;
 ELSE
   expected:=p."PlanJson"->'Debtor'; original:=(p."PlanJson"->>'DebtorReversal')::uuid;
 END IF;
 SELECT jsonb_agg(jsonb_build_object('AccountId',l."AccountId",'Debit',l."Debit",'Credit',l."Credit",'Description',l."Description") ORDER BY l."Number")
   INTO actual FROM accounting."JournalLines" l WHERE l."JournalId"=NEW."Id" AND l."CompanyId"=NEW."CompanyId";
 IF actual IS DISTINCT FROM expected OR NEW."ReversesJournalId" IS DISTINCT FROM original
    OR NEW."SourceDocumentId" IS DISTINCT FROM coalesce(original,p."Id")
    OR (p."Kind"=4) IS DISTINCT FROM (original IS NOT NULL)
 OR NEW."SourceModule" IS DISTINCT FROM (CASE WHEN original IS NULL THEN 'Intercompany.Pair' ELSE 'Reversal' END) THEN
   RAISE EXCEPTION 'Reciprocal journal differs from its approved staged plan' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION groups.pair_commit_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE p groups."IntercompanyPairs"%ROWTYPE; b groups."IntercompanyPairs"%ROWTYPE; c groups."IntercompanyPolicies"%ROWTYPE;
        j accounting."Journals"%ROWTYPE; company uuid; journal uuid; reversal uuid; sign_amount numeric; control uuid; offset_id uuid;
BEGIN
 IF TG_TABLE_SCHEMA='accounting' THEN
   IF NEW."State"<>1 OR (NEW."SourceModule"<>'Intercompany.Pair' AND NOT EXISTS(SELECT 1 FROM accounting."Journals" original
       WHERE original."Id"=NEW."ReversesJournalId" AND original."SourceModule"='Intercompany.Pair')) THEN RETURN NEW; END IF;
   SELECT * INTO p FROM groups."IntercompanyPairs" x WHERE NEW."Id" IN(x."CreditorJournalId",x."DebtorJournalId");
   IF NOT FOUND THEN RAISE EXCEPTION 'Reciprocal journal must belong to a committed pair' USING ERRCODE='23514'; END IF;
 ELSE
   SELECT * INTO p FROM groups."IntercompanyPairs" x WHERE x."Id"=NEW."Id";
   IF p."State"=2 THEN RAISE EXCEPTION 'Reciprocal transaction must finish atomically' USING ERRCODE='23514'; END IF;
   IF p."State"<>3 THEN RETURN NEW; END IF;
 END IF;
 SELECT * INTO c FROM groups."IntercompanyPolicies" x WHERE x."Id"=p."PolicyId";
 IF p."Kind"=4 THEN SELECT * INTO b FROM groups."IntercompanyPairs" x WHERE x."Id"=p."BasisId"; END IF;
 FOREACH company IN ARRAY ARRAY[p."CreditorId",p."DebtorId"] LOOP
   IF company=p."CreditorId" THEN
     journal:=p."CreditorJournalId"; reversal:=b."CreditorJournalId"; control:=c."DueFromId"; sign_amount:=p."Amount";
     offset_id:=CASE WHEN p."Kind" IN(1,3) THEN c."CreditorBankId" ELSE c."RevenueId" END;
   ELSE
     journal:=p."DebtorJournalId"; reversal:=b."DebtorJournalId"; control:=c."DueToId"; sign_amount:=-p."Amount";
     offset_id:=CASE WHEN p."Kind" IN(1,3) THEN c."DebtorBankId" ELSE c."ExpenseId" END;
   END IF;
   IF p."Kind"=3 OR p."Kind"=4 AND b."Kind"<>3 THEN sign_amount:=-sign_amount; END IF;
   SELECT * INTO j FROM accounting."Journals" x WHERE x."Id"=journal AND x."CompanyId"=company AND x."State"=1;
   IF NOT FOUND OR j."ReversesJournalId" IS DISTINCT FROM reversal OR j."PostingDate"<>p."Date" OR j."Currency"<>p."Currency"
     OR j."SourceDocumentId" IS DISTINCT FROM coalesce(reversal,p."Id")
     OR j."IdempotencyKey" IS DISTINCT FROM ('Intercompany:'||replace(p."Id"::text,'-',''))
     OR (SELECT sum(l."Debit"-l."Credit") FROM accounting."JournalLines" l WHERE l."JournalId"=journal AND l."AccountId"=control)
         IS DISTINCT FROM sign_amount THEN
     RAISE EXCEPTION 'Paired reciprocal journals do not match their retained source' USING ERRCODE='23514';
   END IF;
   IF p."Kind"<>4 AND ((SELECT count(*) FROM accounting."JournalLines" l WHERE l."JournalId"=journal)<>2
      OR NOT EXISTS(SELECT 1 FROM accounting."JournalLines" l WHERE l."JournalId"=journal AND l."AccountId"=offset_id
          AND l."Debit"-l."Credit"=-sign_amount)) THEN
     RAISE EXCEPTION 'Reciprocal journal requires the configured counterpart account and exact offset' USING ERRCODE='23514';
   END IF;
 END LOOP;
 RETURN NEW;
END $$;

CREATE FUNCTION groups.run_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."State"=0 AND NEW."ApprovedBy" IS NULL AND NEW."PublishedBy" IS NULL AND NEW."PublishedAt" IS NULL
     AND length(NEW."SourceHash")=64 AND jsonb_typeof(NEW."SnapshotJson"->'Contributions')='array'
     AND jsonb_typeof(NEW."SnapshotJson"->'Adjustments')='array'
     AND (SELECT coalesce(sum((l->>'Translated')::numeric),0) FROM jsonb_array_elements(NEW."SnapshotJson"->'Contributions') l) +
         (SELECT coalesce(sum((l->>'Signed')::numeric),0) FROM jsonb_array_elements(NEW."SnapshotJson"->'Adjustments') l)=0
     AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(NEW."SnapshotJson"->'Adjustments') l
       WHERE l->>'Layer'<>'Translation' GROUP BY l->>'Layer',l->>'Reference' HAVING sum((l->>'Signed')::numeric)<>0)
     THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF to_jsonb(NEW)-ARRAY['State','ApprovedBy','PublishedBy','PublishedAt'] =
        to_jsonb(OLD)-ARRAY['State','ApprovedBy','PublishedBy','PublishedAt']
     AND ((OLD."State"=0 AND NEW."State"=1 AND NEW."ApprovedBy"<>NEW."PreparedBy"
       AND NEW."PublishedBy" IS NULL AND NEW."PublishedAt" IS NULL)
     OR (OLD."State"=1 AND NEW."State"=2 AND OLD."ApprovedBy"=NEW."ApprovedBy"
       AND NEW."PublishedBy"=NEW."ApprovedBy" AND NEW."PublishedAt" IS NOT NULL)) THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Consolidation evidence is immutable and requires independent approval' USING ERRCODE='23514';
END $$;

DO $triggers$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['CorporateGroups','GroupOwnerships','GroupAccounts','GroupMappings','IntercompanyPolicies',
   'IntercompanyPairs','ReciprocalReviews','GroupTranslationRates','ConsolidationRuns'] LOOP
   EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON groups.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
   EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON groups.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['GroupOwnerships','GroupAccounts','ReciprocalReviews'] LOOP
   EXECUTE format('CREATE TRIGGER b_immutable BEFORE UPDATE OR DELETE ON groups.%I FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['GroupMappings','IntercompanyPolicies','GroupTranslationRates'] LOOP
   EXECUTE format('CREATE TRIGGER b_approval BEFORE INSERT OR UPDATE OR DELETE ON groups.%I FOR EACH ROW EXECUTE FUNCTION groups.approval_guard_v1()',t);
 END LOOP;
END $triggers$;
CREATE TRIGGER b_group_ownership BEFORE INSERT ON groups."GroupOwnerships" FOR EACH ROW EXECUTE FUNCTION groups.ownership_guard_v1();
CREATE TRIGGER b_group_register BEFORE UPDATE OR DELETE ON groups."CorporateGroups" FOR EACH ROW EXECUTE FUNCTION groups.register_guard_v1();
CREATE TRIGGER b_group_pair BEFORE INSERT OR UPDATE OR DELETE ON groups."IntercompanyPairs" FOR EACH ROW EXECUTE FUNCTION groups.pair_guard_v1();
CREATE TRIGGER b_group_run BEFORE INSERT OR UPDATE OR DELETE ON groups."ConsolidationRuns" FOR EACH ROW EXECUTE FUNCTION groups.run_guard_v1();
CREATE TRIGGER group_pair_journal BEFORE INSERT OR UPDATE ON accounting."Journals" FOR EACH ROW EXECUTE FUNCTION groups.pair_journal_guard_v1();
CREATE CONSTRAINT TRIGGER z_group_pair_commit AFTER INSERT OR UPDATE ON groups."IntercompanyPairs"
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION groups.pair_commit_v1();
CREATE CONSTRAINT TRIGGER z_group_journal_commit AFTER INSERT OR UPDATE ON accounting."Journals"
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION groups.pair_commit_v1();
""";

    public const string Down = """
DROP FUNCTION groups.pair_commit_v1() CASCADE;
DROP FUNCTION groups.pair_journal_guard_v1() CASCADE;
DROP FUNCTION groups.pair_guard_v1() CASCADE;
DROP FUNCTION groups.ownership_guard_v1() CASCADE;
DROP FUNCTION groups.register_guard_v1() CASCADE;
DROP FUNCTION groups.approval_guard_v1() CASCADE;
DROP FUNCTION groups.run_guard_v1() CASCADE;
""";
}
