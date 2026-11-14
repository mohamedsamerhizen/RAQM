namespace RAQM.Api.Data.Migrations;

// Historical PostgreSQL guards for FinancialCloseV1.
internal static class FinancialCloseV1Sql
{
    public const string Evidence = """
CREATE FUNCTION financial_close.definition_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE did uuid; definition financial_close."ReportDefinitions"%ROWTYPE;
BEGIN
 IF TG_TABLE_NAME='ReportDefinitions' THEN did:=NEW."Id"; ELSE did:=NEW."DefinitionId"; END IF;
 SELECT * INTO definition FROM financial_close."ReportDefinitions" WHERE "Id"=did;
 IF (SELECT count(*) FROM financial_close."ReportAccountMappings" WHERE "DefinitionId"=did)<>definition."MappingCount"
 OR (definition."RetainedEarningsId" IS NOT NULL AND NOT EXISTS(
     SELECT 1 FROM financial_close."ReportAccountMappings" m JOIN accounting."Accounts" a ON a."Id"=m."AccountId"
     WHERE m."DefinitionId"=did AND m."AccountId"=definition."RetainedEarningsId" AND m."Group"=5
       AND a."Classification"=2 AND a."IsPosting" AND NOT a."IsContra")) THEN
   RAISE EXCEPTION 'Report definition mappings are incomplete or inconsistent' USING ERRCODE='23514';
 END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION financial_close.mapping_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM accounting."Accounts" a WHERE a."CompanyId"=NEW."CompanyId" AND a."Id"=NEW."AccountId"
   AND a."IsPosting" AND a."Classification"=CASE WHEN NEW."Group"<2 THEN 0 WHEN NEW."Group"<4 THEN 1
     WHEN NEW."Group"<8 THEN 2 WHEN NEW."Group" IN(8,11) THEN 3 ELSE 4 END) THEN
   RAISE EXCEPTION 'Statement mapping requires a compatible posting account' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION financial_close.schedule_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Recognition schedules are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."State"<>0 OR NEW."ApprovedBy" IS NOT NULL) THEN
   RAISE EXCEPTION 'New recognition schedules must be drafts' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (
   (to_jsonb(NEW)-ARRAY['State','Version','ApprovedBy']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['State','Version','ApprovedBy'])
   OR NOT ((OLD."State"=0 AND NEW."State" IN(1,2)) OR (OLD."State"=1 AND NEW."State"=2))
   OR (NEW."State"=1 AND NEW."ApprovedBy" IS NULL)
   OR (NEW."State"=2 AND NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy")) THEN
   RAISE EXCEPTION 'Recognition schedule terms are immutable and transitions are controlled' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION financial_close.occurrence_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE schedule financial_close."RecognitionSchedules"%ROWTYPE;
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Recognition occurrences are retained' USING ERRCODE='23514'; END IF;
 SELECT * INTO schedule FROM financial_close."RecognitionSchedules" WHERE "Id"=NEW."ScheduleId";
 IF TG_OP='INSERT' AND (schedule."State" IS DISTINCT FROM 0 OR NEW."JournalId" IS NOT NULL OR
   NEW."ReversalJournalId" IS NOT NULL OR NEW."ReversalHash" IS NOT NULL OR
   NEW."Amount"<>trading.round_even_v1(NEW."Amount",trading.units_v1(schedule."Currency"))) THEN
   RAISE EXCEPTION 'New occurrences require a draft schedule and valid minor units' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (
   (to_jsonb(NEW)-ARRAY['JournalId','ReversalJournalId','ReversalHash']) IS DISTINCT FROM
   (to_jsonb(OLD)-ARRAY['JournalId','ReversalJournalId','ReversalHash']) OR OLD."ReversalJournalId" IS NOT NULL
   OR (OLD."JournalId" IS NOT NULL AND NEW."JournalId" IS DISTINCT FROM OLD."JournalId")
   OR (OLD."JournalId" IS NULL AND schedule."State"<>1)
   OR (OLD."ReversalHash" IS NOT NULL AND NEW."ReversalHash" IS DISTINCT FROM OLD."ReversalHash")) THEN
   RAISE EXCEPTION 'Recognition occurrence can only attach an original and one reversal' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION financial_close.schedule_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE sid uuid; expected numeric; n bigint; total numeric; last_number integer;
BEGIN
 IF TG_TABLE_NAME='RecognitionSchedules' THEN sid:=NEW."Id"; ELSE sid:=NEW."ScheduleId"; END IF;
 SELECT "Total" INTO expected FROM financial_close."RecognitionSchedules" WHERE "Id"=sid;
 SELECT count(*),coalesce(sum("Amount"),0),max("Number") INTO n,total,last_number
 FROM financial_close."RecognitionOccurrences" WHERE "ScheduleId"=sid;
 IF n NOT BETWEEN 1 AND 120 OR last_number<>n OR total<>expected THEN
   RAISE EXCEPTION 'Recognition schedule occurrences must conserve the approved total' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION financial_close.close_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Close sessions are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND NEW."State"<>0 THEN RAISE EXCEPTION 'New close sessions require preparation' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (NEW."PeriodId"<>OLD."PeriodId" OR NEW."CreatedBy"<>OLD."CreatedBy" OR NEW."CreatedAt"<>OLD."CreatedAt"
   OR NOT ((OLD."State"=0 AND NEW."State" IN(0,1,3)) OR (OLD."State"=1 AND NEW."State" IN(0,2,3)))) THEN
   RAISE EXCEPTION 'Invalid close workflow transition' USING ERRCODE='23514'; END IF;
 IF NEW."State" IN(1,2) AND (
   NOT EXISTS(SELECT 1 FROM financial_close."PeriodCloseTasks" WHERE "SessionId"=NEW."Id" AND "Code"='GL_REVIEW'
     AND "Required" AND "CompletedAt" IS NOT NULL) OR
   EXISTS(SELECT 1 FROM financial_close."PeriodCloseTasks" WHERE "SessionId"=NEW."Id" AND "Required" AND "CompletedAt" IS NULL) OR
   EXISTS(SELECT 1 FROM financial_close."CloseExceptions" WHERE "SessionId"=NEW."Id" AND "Blocking" AND "ResolvedAt" IS NULL)) THEN
   RAISE EXCEPTION 'Required close evidence is incomplete' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION financial_close.child_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Close evidence is retained' USING ERRCODE='23514'; END IF;
 IF NOT EXISTS(SELECT 1 FROM financial_close."PeriodCloseSessions" WHERE "Id"=NEW."SessionId" AND "State"=0) THEN
   RAISE EXCEPTION 'Return the close session to preparation before changing evidence' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' THEN
   IF TG_TABLE_NAME='PeriodCloseTasks' THEN
     IF OLD."CompletedAt" IS NOT NULL OR NEW."CompletedAt" IS NULL OR NEW."CompletedBy" IS NULL OR NEW."Evidence" IS NULL OR
        (to_jsonb(NEW)-ARRAY['CompletedAt','CompletedBy','Evidence']) IS DISTINCT FROM
        (to_jsonb(OLD)-ARRAY['CompletedAt','CompletedBy','Evidence']) THEN
       RAISE EXCEPTION 'Task evidence may be recorded once' USING ERRCODE='23514'; END IF;
   ELSE
     IF OLD."ResolvedAt" IS NOT NULL OR NEW."ResolvedAt" IS NULL OR NEW."ResolvedBy" IS NULL OR NEW."Resolution" IS NULL OR
        (to_jsonb(NEW)-ARRAY['ResolvedAt','ResolvedBy','Resolution']) IS DISTINCT FROM
        (to_jsonb(OLD)-ARRAY['ResolvedAt','ResolvedBy','Resolution']) THEN
       RAISE EXCEPTION 'Exception resolution may be recorded once' USING ERRCODE='23514'; END IF;
   END IF;
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION financial_close.completed_period_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW."State"=2 AND NOT EXISTS(SELECT 1 FROM accounting."Periods" WHERE "Id"=NEW."PeriodId" AND "State"=1) THEN
   RAISE EXCEPTION 'Completion and the Closed accounting state must commit together' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION financial_close.period_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW."State"<>OLD."State" AND EXISTS(SELECT 1 FROM financial_close."PeriodCloseSessions"
   WHERE "CompanyId"=NEW."CompanyId" AND "PeriodId"=NEW."Id" AND "State" IN(0,1)) THEN
   RAISE EXCEPTION 'An active close session owns this period transition' USING ERRCODE='23514'; END IF;
 IF NEW."State"=0 AND OLD."State"<>0 AND EXISTS(SELECT 1 FROM financial_close."YearEndRecords" c
   JOIN accounting."FiscalYears" y ON y."Id"=c."FiscalYearId"
   WHERE c."CompanyId"=NEW."CompanyId" AND c."Finalized" AND y."EndDate">=NEW."EndDate") THEN
   RAISE EXCEPTION 'A finalized fiscal year prevents reopening this period' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;
""";

    public const string Posting = """
CREATE FUNCTION financial_close.assert_lines_v1(jid uuid, expected jsonb) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
 IF EXISTS(
   WITH e AS (SELECT n, (item->>'AccountId')::uuid account, (item->>'Debit')::numeric debit,
       (item->>'Credit')::numeric credit, item->>'Description' description
     FROM jsonb_array_elements(expected) WITH ORDINALITY AS payload(item,n)),
   a AS (SELECT "Number" n, "AccountId" account, "Debit" debit, "Credit" credit, "Description" description
     FROM accounting."JournalLines" WHERE "JournalId"=jid)
   SELECT 1 FROM e FULL JOIN a USING(n)
   WHERE (e.account,e.debit,e.credit,e.description) IS DISTINCT FROM (a.account,a.debit,a.credit,a.description)
 ) THEN RAISE EXCEPTION 'Journal lines differ from approved financial evidence' USING ERRCODE='23514'; END IF;
END $$;

CREATE FUNCTION financial_close.assert_occurrence_v1(oid uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE occurrence financial_close."RecognitionOccurrences"%ROWTYPE; schedule financial_close."RecognitionSchedules"%ROWTYPE; expected jsonb;
BEGIN
 SELECT * INTO occurrence FROM financial_close."RecognitionOccurrences" WHERE "Id"=oid;
 IF NOT FOUND THEN RAISE EXCEPTION 'Recognition occurrence missing' USING ERRCODE='23514'; END IF;
 IF occurrence."JournalId" IS NULL THEN
   IF occurrence."ReversalHash" IS NOT NULL THEN
     RAISE EXCEPTION 'Recognition reversal requires its original journal' USING ERRCODE='23514'; END IF;
   RETURN;
 END IF;
 SELECT * INTO schedule FROM financial_close."RecognitionSchedules" WHERE "Id"=occurrence."ScheduleId";
 IF schedule."ApprovedBy" IS NULL OR NOT EXISTS(SELECT 1 FROM accounting."Journals" j
   WHERE j."Id"=occurrence."JournalId" AND j."CompanyId"=occurrence."CompanyId" AND j."State"=1
     AND j."SourceModule"='FinancialClose.Recognition' AND j."SourceDocumentId"=oid AND j."PostingDate"=occurrence."Date") THEN
   RAISE EXCEPTION 'Recognition source and its posted journal must commit together' USING ERRCODE='23514'; END IF;
 IF schedule."Kind"=4 THEN expected:=schedule."TemplateJson";
 ELSE
   expected:=jsonb_build_array(
     jsonb_build_object('AccountId',schedule."DebitAccountId",'Debit',occurrence."Amount",'Credit',0,'Description',schedule."Name"),
     jsonb_build_object('AccountId',schedule."CreditAccountId",'Debit',0,'Credit',occurrence."Amount",'Description',schedule."Name"));
 END IF;
 PERFORM financial_close.assert_lines_v1(occurrence."JournalId",expected);
 IF (occurrence."ReversalHash" IS NOT NULL)<>(occurrence."ReversalJournalId" IS NOT NULL) OR
   (occurrence."ReversalJournalId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM accounting."Journals" j
     WHERE j."Id"=occurrence."ReversalJournalId" AND j."State"=1 AND j."ReversesJournalId"=occurrence."JournalId"
       AND j."CompanyId"=occurrence."CompanyId" AND j."PostingDate">=occurrence."Date")) THEN
   RAISE EXCEPTION 'Recognition reversal must commit with its occurrence link' USING ERRCODE='23514'; END IF;
END $$;

CREATE FUNCTION financial_close.year_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Year-end evidence is retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."Finalized" OR NEW."JournalId" IS NOT NULL) THEN
   RAISE EXCEPTION 'Stage year-end before posting' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (OLD."Finalized" OR
   (to_jsonb(NEW)-ARRAY['Finalized','JournalId','ClosedAt']) IS DISTINCT FROM
   (to_jsonb(OLD)-ARRAY['Finalized','JournalId','ClosedAt'])) THEN
   RAISE EXCEPTION 'Finalized year-end evidence is immutable' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION financial_close.assert_year_v1(yid uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE record_row financial_close."YearEndRecords"%ROWTYPE; fiscal accounting."FiscalYears"%ROWTYPE; profit numeric; retained numeric;
BEGIN
 SELECT * INTO record_row FROM financial_close."YearEndRecords" WHERE "Id"=yid;
 SELECT * INTO fiscal FROM accounting."FiscalYears" WHERE "Id"=record_row."FiscalYearId";
 IF NOT record_row."Finalized" OR NOT EXISTS(SELECT 1 FROM financial_close."ReportDefinitions" d
      WHERE d."Id"=record_row."DefinitionId" AND d."RetainedEarningsId"=record_row."RetainedEarningsId")
    OR EXISTS(SELECT 1 FROM accounting."Periods" p WHERE p."CompanyId"=record_row."CompanyId"
      AND p."EndDate"<fiscal."EndDate" AND p."State"=0)
    OR NOT EXISTS(SELECT 1 FROM financial_close."PeriodCloseSessions" s
   JOIN accounting."Periods" p ON p."Id"=s."PeriodId"
   WHERE s."Id"=record_row."SessionId" AND s."State"=2 AND p."FiscalYearId"=fiscal."Id"
      AND p."EndDate"=fiscal."EndDate" AND p."State"=1) THEN
   RAISE EXCEPTION 'Year end must commit with a completed final period' USING ERRCODE='23514'; END IF;
 IF record_row."JournalId" IS NOT NULL THEN
   IF NOT EXISTS(SELECT 1 FROM accounting."Journals" WHERE "Id"=record_row."JournalId" AND "State"=1
     AND "CompanyId"=record_row."CompanyId" AND "SourceModule"='FinancialClose.YearEnd' AND "SourceDocumentId"=yid
     AND "PostingDate"=fiscal."EndDate" AND "PostedBy"=record_row."ClosedBy") OR
     EXISTS(SELECT 1 FROM accounting."JournalLines" l JOIN accounting."Accounts" a ON a."Id"=l."AccountId"
       WHERE l."JournalId"=record_row."JournalId" AND l."AccountId"<>record_row."RetainedEarningsId" AND a."Classification" NOT IN(3,4)) THEN
     RAISE EXCEPTION 'Year end requires its exact typed journal' USING ERRCODE='23514'; END IF;
   PERFORM financial_close.assert_lines_v1(record_row."JournalId",record_row."ClosingLinesJson");
 ELSIF jsonb_array_length(record_row."ClosingLinesJson")<>0 THEN
   RAISE EXCEPTION 'A nonempty closing requires a posted journal' USING ERRCODE='23514'; END IF;
 SELECT coalesce(sum(l."Credit"-l."Debit"),0) INTO retained FROM accounting."JournalLines" l
 WHERE l."JournalId"=record_row."JournalId" AND l."AccountId"=record_row."RetainedEarningsId";
 SELECT coalesce(sum(l."Credit"-l."Debit"),0) INTO profit FROM accounting."JournalLines" l
 JOIN accounting."Journals" j ON j."Id"=l."JournalId" JOIN accounting."Accounts" a ON a."Id"=l."AccountId"
 WHERE l."CompanyId"=record_row."CompanyId" AND j."State"=1 AND a."Classification" IN(3,4)
   AND j."PostingDate" BETWEEN fiscal."StartDate" AND fiscal."EndDate" AND j."Id" IS DISTINCT FROM record_row."JournalId";
 IF retained<>record_row."Profit" OR profit<>record_row."Profit" OR EXISTS(
   SELECT 1 FROM accounting."JournalLines" l JOIN accounting."Journals" j ON j."Id"=l."JournalId"
   JOIN accounting."Accounts" a ON a."Id"=l."AccountId" WHERE l."CompanyId"=record_row."CompanyId"
      AND j."State"=1 AND a."Classification" IN(3,4) AND j."PostingDate"<=fiscal."EndDate"
   GROUP BY l."AccountId" HAVING sum(l."Debit"-l."Credit")<>0) THEN
   RAISE EXCEPTION 'Year end must clear nominal balances and transfer their result exactly' USING ERRCODE='23514'; END IF;
END $$;

CREATE FUNCTION financial_close.posting_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE oid uuid;
BEGIN
 IF TG_TABLE_NAME='RecognitionOccurrences' THEN PERFORM financial_close.assert_occurrence_v1(NEW."Id"); RETURN NULL; END IF;
 IF TG_TABLE_NAME='YearEndRecords' THEN PERFORM financial_close.assert_year_v1(NEW."Id"); RETURN NULL; END IF;
 IF NEW."State"<>1 THEN RETURN NULL; END IF;
 IF NEW."SourceModule"='FinancialClose.Recognition' THEN
   IF NOT EXISTS(SELECT 1 FROM financial_close."RecognitionOccurrences" WHERE "Id"=NEW."SourceDocumentId" AND "JournalId"=NEW."Id") THEN
     RAISE EXCEPTION 'Recognition journal requires its source link' USING ERRCODE='23514'; END IF;
   PERFORM financial_close.assert_occurrence_v1(NEW."SourceDocumentId");
 ELSIF NEW."SourceModule"='FinancialClose.YearEnd' THEN
   IF NOT EXISTS(SELECT 1 FROM financial_close."YearEndRecords" WHERE "Id"=NEW."SourceDocumentId" AND "JournalId"=NEW."Id") THEN
     RAISE EXCEPTION 'Closing journal requires its year-end record' USING ERRCODE='23514'; END IF;
   PERFORM financial_close.assert_year_v1(NEW."SourceDocumentId");
 ELSIF NEW."SourceModule" LIKE 'FinancialClose.%' THEN RAISE EXCEPTION 'Unknown financial close source' USING ERRCODE='23514';
 END IF;
 IF NEW."ReversesJournalId" IS NOT NULL THEN
   IF EXISTS(SELECT 1 FROM financial_close."YearEndRecords" WHERE "JournalId"=NEW."ReversesJournalId") THEN
     RAISE EXCEPTION 'Year-end journals are retained' USING ERRCODE='23514'; END IF;
   SELECT "Id" INTO oid FROM financial_close."RecognitionOccurrences" WHERE "JournalId"=NEW."ReversesJournalId";
   IF FOUND AND NOT EXISTS(SELECT 1 FROM financial_close."RecognitionOccurrences" WHERE "Id"=oid AND "ReversalJournalId"=NEW."Id") THEN
     RAISE EXCEPTION 'Use the recognition reversal workflow' USING ERRCODE='23514'; END IF;
   IF EXISTS(SELECT 1 FROM financial_close."RecognitionSchedules" s
     JOIN accounting."JournalLines" l ON l."Id"=s."BasisLineId"
     WHERE l."JournalId"=NEW."ReversesJournalId" AND s."CompanyId"=NEW."CompanyId" AND
       (s."State"<>2 OR EXISTS(SELECT 1 FROM financial_close."RecognitionOccurrences" o
         WHERE o."ScheduleId"=s."Id" AND o."JournalId" IS NOT NULL AND o."ReversalJournalId" IS NULL))) THEN
     RAISE EXCEPTION 'Deferral basis remains linked to active or unreversed recognition' USING ERRCODE='23514'; END IF;
 END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION financial_close.journal_cutoff_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW."State"=1 AND EXISTS(SELECT 1 FROM financial_close."YearEndRecords" c JOIN accounting."FiscalYears" y
   ON y."Id"=c."FiscalYearId" WHERE c."CompanyId"=NEW."CompanyId" AND c."Finalized" AND y."EndDate">=NEW."PostingDate") THEN
   RAISE EXCEPTION 'Finalized year cutoff prevents posting' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

DO $$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['ReportDefinitions','ReportAccountMappings','RecognitionSchedules','RecognitionOccurrences','PeriodCloseSessions','PeriodCloseTasks','CloseExceptions','YearEndRecords'] LOOP
   EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON financial_close.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
   EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON financial_close.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['ReportDefinitions','ReportAccountMappings'] LOOP
   EXECUTE format('CREATE TRIGGER b_immutable BEFORE UPDATE OR DELETE ON financial_close.%I FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
   EXECUTE format('CREATE CONSTRAINT TRIGGER z_definition AFTER INSERT ON financial_close.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION financial_close.definition_contract_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['RecognitionSchedules','RecognitionOccurrences'] LOOP
   EXECUTE format('CREATE CONSTRAINT TRIGGER z_schedule AFTER INSERT ON financial_close.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION financial_close.schedule_contract_v1()',t);
 END LOOP;
END $$;
CREATE TRIGGER b_mapping BEFORE INSERT ON financial_close."ReportAccountMappings" FOR EACH ROW EXECUTE FUNCTION financial_close.mapping_guard_v1();
CREATE TRIGGER b_schedule BEFORE INSERT OR UPDATE OR DELETE ON financial_close."RecognitionSchedules" FOR EACH ROW EXECUTE FUNCTION financial_close.schedule_guard_v1();
CREATE TRIGGER b_occurrence BEFORE INSERT OR UPDATE OR DELETE ON financial_close."RecognitionOccurrences" FOR EACH ROW EXECUTE FUNCTION financial_close.occurrence_guard_v1();
CREATE TRIGGER b_close BEFORE INSERT OR UPDATE OR DELETE ON financial_close."PeriodCloseSessions" FOR EACH ROW EXECUTE FUNCTION financial_close.close_guard_v1();
CREATE TRIGGER b_task BEFORE INSERT OR UPDATE OR DELETE ON financial_close."PeriodCloseTasks" FOR EACH ROW EXECUTE FUNCTION financial_close.child_guard_v1();
CREATE TRIGGER b_exception BEFORE INSERT OR UPDATE OR DELETE ON financial_close."CloseExceptions" FOR EACH ROW EXECUTE FUNCTION financial_close.child_guard_v1();
CREATE TRIGGER b_year BEFORE INSERT OR UPDATE OR DELETE ON financial_close."YearEndRecords" FOR EACH ROW EXECUTE FUNCTION financial_close.year_guard_v1();
CREATE TRIGGER y_close_period BEFORE UPDATE ON accounting."Periods" FOR EACH ROW EXECUTE FUNCTION financial_close.period_guard_v1();
CREATE TRIGGER y_year_cutoff BEFORE INSERT OR UPDATE ON accounting."Journals" FOR EACH ROW EXECUTE FUNCTION financial_close.journal_cutoff_v1();
CREATE CONSTRAINT TRIGGER z_close_period AFTER UPDATE ON financial_close."PeriodCloseSessions" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION financial_close.completed_period_v1();
CREATE CONSTRAINT TRIGGER z_occurrence_journal AFTER INSERT OR UPDATE ON financial_close."RecognitionOccurrences" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION financial_close.posting_contract_v1();
CREATE CONSTRAINT TRIGGER z_year_journal AFTER INSERT OR UPDATE ON financial_close."YearEndRecords" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION financial_close.posting_contract_v1();
CREATE CONSTRAINT TRIGGER z_financial_close_journal AFTER INSERT OR UPDATE ON accounting."Journals" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION financial_close.posting_contract_v1();
""";

    public const string Down = """
DROP FUNCTION financial_close.posting_contract_v1() CASCADE;
DROP FUNCTION financial_close.journal_cutoff_v1() CASCADE;
DROP FUNCTION financial_close.assert_year_v1(uuid);
DROP FUNCTION financial_close.year_guard_v1() CASCADE;
DROP FUNCTION financial_close.assert_occurrence_v1(uuid);
DROP FUNCTION financial_close.assert_lines_v1(uuid,jsonb);
DROP FUNCTION financial_close.period_guard_v1() CASCADE;
DROP FUNCTION financial_close.completed_period_v1() CASCADE;
DROP FUNCTION financial_close.child_guard_v1() CASCADE;
DROP FUNCTION financial_close.close_guard_v1() CASCADE;
DROP FUNCTION financial_close.schedule_contract_v1() CASCADE;
DROP FUNCTION financial_close.occurrence_guard_v1() CASCADE;
DROP FUNCTION financial_close.schedule_guard_v1() CASCADE;
DROP FUNCTION financial_close.mapping_guard_v1() CASCADE;
DROP FUNCTION financial_close.definition_contract_v1() CASCADE;
""";
}
