namespace RAQM.Api.Data.Migrations;

internal static class FinancialDimensionsV1Sql
{
    public const string Up = """
CREATE FUNCTION analytics.value_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Dimension values are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND ((to_jsonb(NEW)-'InactiveOn') IS DISTINCT FROM (to_jsonb(OLD)-'InactiveOn')
    OR OLD."InactiveOn" IS NOT NULL OR NEW."InactiveOn" IS NULL OR EXISTS(SELECT 1 FROM analytics."FinancialProjects" p
      WHERE p."CompanyId"=NEW."CompanyId" AND p."ValueId"=NEW."Id")) THEN
   RAISE EXCEPTION 'Dimension values only permit initial deactivation' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."InactiveOn" IS NOT NULL OR (NEW."ParentId" IS NOT NULL AND NOT EXISTS(
   SELECT 1 FROM analytics."DimensionValues" p WHERE p."CompanyId"=NEW."CompanyId" AND p."Id"=NEW."ParentId"
   AND p."DefinitionId"=NEW."DefinitionId" AND p."EffectiveDate"<=NEW."EffectiveDate"
   AND (p."InactiveOn" IS NULL OR p."InactiveOn">NEW."EffectiveDate")))) THEN
   RAISE EXCEPTION 'Dimension parent must already be effective in the same definition' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION analytics.rule_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF jsonb_typeof(NEW."AllowedJson") IS DISTINCT FROM 'array'
 OR EXISTS(SELECT 1 FROM analytics."DimensionRules" r WHERE r."CompanyId"=NEW."CompanyId" AND r."DefinitionId"=NEW."DefinitionId"
   AND r."AccountId" IS NOT DISTINCT FROM NEW."AccountId" AND r."EffectiveDate">=NEW."EffectiveDate")
 OR (NEW."DefaultValueId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM analytics."DimensionValues" v WHERE
   v."CompanyId"=NEW."CompanyId" AND v."DefinitionId"=NEW."DefinitionId" AND v."Id"=NEW."DefaultValueId"
   AND v."EffectiveDate"<=NEW."EffectiveDate" AND (v."InactiveOn" IS NULL OR v."InactiveOn">NEW."EffectiveDate")))
 OR EXISTS(SELECT 1 FROM jsonb_array_elements_text(NEW."AllowedJson") AS e(id) WHERE NOT EXISTS(
   SELECT 1 FROM analytics."DimensionValues" v WHERE v."CompanyId"=NEW."CompanyId" AND v."DefinitionId"=NEW."DefinitionId" AND v."Id"=e.id::uuid)) THEN
   RAISE EXCEPTION 'Invalid dimensional policy dates or value scope' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION analytics.binding_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF jsonb_typeof(NEW."ValuesJson") IS DISTINCT FROM 'array' OR EXISTS(
   SELECT 1 FROM jsonb_array_elements(NEW."ValuesJson") ln CROSS JOIN LATERAL jsonb_array_elements(ln->'Values') pick
   LEFT JOIN analytics."DimensionDefinitions" d ON d."CompanyId"=NEW."CompanyId" AND d."Id"=(pick->>'DefinitionId')::uuid
   LEFT JOIN analytics."DimensionValues" v ON v."CompanyId"=NEW."CompanyId" AND v."DefinitionId"=d."Id" AND v."Id"=(pick->>'ValueId')::uuid
   WHERE d."Id" IS NULL OR ((pick->>'ValueId') IS NOT NULL AND v."Id" IS NULL)) THEN
   RAISE EXCEPTION 'Binding values must belong to company definitions' USING ERRCODE='23514'; END IF;
 IF (NEW."Source"=0 AND NOT EXISTS(SELECT 1 FROM accounting."Journals" j WHERE j."CompanyId"=NEW."CompanyId" AND
      j."Id"=NEW."SourceId" AND j."Version"=NEW."SourceVersion" AND j."State"=0 AND j."SourceModule"='ManualJournal'))
 OR (NEW."Source"=1 AND NOT EXISTS(SELECT 1 FROM trading."Documents" j WHERE j."CompanyId"=NEW."CompanyId" AND
      j."Id"=NEW."SourceId" AND j."Version"=NEW."SourceVersion" AND j."State"=1 AND j."Kind" IN(0,1,2,3)))
 OR (NEW."Source"=2 AND NOT EXISTS(SELECT 1 FROM inventory."ProcurementLines" l JOIN inventory."ProcurementDocuments" j ON j."Id"=l."DocumentId"
      WHERE l."CompanyId"=NEW."CompanyId" AND j."CompanyId"=NEW."CompanyId" AND l."Id"=NEW."SourceId" AND j."Version"=NEW."SourceVersion" AND j."State"=0))
 OR (NEW."Source"=3 AND NOT EXISTS(SELECT 1 FROM inventory."Operations" j WHERE j."CompanyId"=NEW."CompanyId" AND
      j."Id"=NEW."SourceId" AND j."Version"=NEW."SourceVersion" AND j."State"=1))
 OR (NEW."Source"=4 AND NOT EXISTS(SELECT 1 FROM fixed_assets."AssetOperations" j WHERE j."CompanyId"=NEW."CompanyId" AND
      j."Id"=NEW."SourceId" AND j."Version"=NEW."SourceVersion" AND j."State"=1))
 OR (NEW."Source"=5 AND NOT EXISTS(SELECT 1 FROM financial_close."RecognitionOccurrences" o
      JOIN financial_close."RecognitionSchedules" s ON s."Id"=o."ScheduleId" WHERE o."CompanyId"=NEW."CompanyId" AND
      s."CompanyId"=NEW."CompanyId" AND o."Id"=NEW."SourceId" AND o."JournalId" IS NULL AND s."State"=1 AND s."Version"=NEW."SourceVersion"))
 OR (NEW."Source"=6 AND NOT EXISTS(SELECT 1 FROM foreign_exchange."FxOperations" j WHERE j."CompanyId"=NEW."CompanyId" AND
      j."Id"=NEW."SourceId" AND j."Version"=NEW."SourceVersion" AND j."State"=1 AND j."Kind" IN(0,1,4))) THEN
   RAISE EXCEPTION 'Binding requires an eligible current source version' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION analytics.tag_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM accounting."JournalLines" l JOIN accounting."Journals" j ON j."Id"=l."JournalId"
   WHERE l."Id"=NEW."JournalLineId" AND l."CompanyId"=NEW."CompanyId" AND j."CompanyId"=NEW."CompanyId" AND j."State"=0)
 OR (NEW."ValueId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM analytics."DimensionValues" v WHERE v."CompanyId"=NEW."CompanyId"
   AND v."Id"=NEW."ValueId" AND v."DefinitionId"=NEW."DefinitionId"))
 OR (NEW."RuleId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM analytics."DimensionRules" r WHERE r."CompanyId"=NEW."CompanyId"
   AND r."Id"=NEW."RuleId" AND r."DefinitionId"=NEW."DefinitionId")) THEN
   RAISE EXCEPTION 'Dimension evidence can only be staged on a company draft line' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION analytics.journal_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE basis uuid;
BEGIN
 IF NEW."State"<>1 OR OLD."State"=1 OR NEW."SourceModule"='FinancialClose.YearEnd' THEN RETURN NEW; END IF;
 IF NEW."ReversesJournalId" IS NOT NULL AND EXISTS(SELECT 1 FROM analytics."CostAllocationExecutions" a
   JOIN accounting."JournalLines" l ON l."Id"=a."SourceLineId" WHERE a."CompanyId"=NEW."CompanyId" AND a."State"=2
     AND (l."JournalId"=NEW."ReversesJournalId" OR a."JournalId"=NEW."ReversesJournalId")) THEN
   RAISE EXCEPTION 'Correct the active cost allocation before journal reversal' USING ERRCODE='23514'; END IF;
 basis:=NEW."ReversesJournalId";
 IF NEW."SourceModule"='Dimensions.Correction' THEN
   SELECT "JournalId" INTO basis FROM analytics."CostAllocationExecutions" WHERE "CompanyId"=NEW."CompanyId"
     AND "Id"=NEW."SourceDocumentId" AND "State"=2;
   IF basis IS NULL THEN RAISE EXCEPTION 'Allocation correction requires an executed basis' USING ERRCODE='23514'; END IF;
 END IF;
 IF basis IS NOT NULL THEN
   IF EXISTS(WITH original AS (
     SELECT l."Number",d."DefinitionId",d."ValueId",d."RuleId",d."BindingId" FROM accounting."JournalLines" l
     JOIN analytics."PostedDimensions" d ON d."JournalLineId"=l."Id" WHERE l."JournalId"=basis),
     actual AS (SELECT l."Number",d."DefinitionId",d."ValueId",d."RuleId",d."BindingId" FROM accounting."JournalLines" l
     JOIN analytics."PostedDimensions" d ON d."JournalLineId"=l."Id" WHERE l."JournalId"=NEW."Id")
     SELECT 1 FROM original o FULL JOIN actual a ON (o."Number",o."DefinitionId")=(a."Number",a."DefinitionId")
     WHERE (o."Number",o."DefinitionId",o."ValueId",o."RuleId",o."BindingId") IS DISTINCT FROM
       (a."Number",a."DefinitionId",a."ValueId",a."RuleId",a."BindingId")) THEN
       RAISE EXCEPTION 'Reversal must retain historical dimensions' USING ERRCODE='23514'; END IF;
   RETURN NEW;
 END IF;
 IF EXISTS(SELECT 1 FROM accounting."JournalLines" l CROSS JOIN analytics."DimensionDefinitions" def
   LEFT JOIN LATERAL (SELECT r.* FROM analytics."DimensionRules" r WHERE r."CompanyId"=NEW."CompanyId" AND r."DefinitionId"=def."Id"
      AND (r."AccountId" IS NULL OR r."AccountId"=l."AccountId") AND r."EffectiveDate"<=NEW."PostingDate"
      ORDER BY (r."AccountId" IS NOT NULL) DESC,r."EffectiveDate" DESC LIMIT 1) rule ON TRUE
   LEFT JOIN analytics."PostedDimensions" d ON d."JournalLineId"=l."Id" AND d."DefinitionId"=def."Id"
   LEFT JOIN analytics."DimensionValues" v ON v."Id"=d."ValueId"
   WHERE l."JournalId"=NEW."Id" AND def."CompanyId"=NEW."CompanyId" AND
     ((rule."Mode"=1 AND d."ValueId" IS NULL) OR (rule."Mode"=2 AND d."ValueId" IS NOT NULL) OR
      (rule."Mode"=3 AND d."ValueId" IS DISTINCT FROM rule."DefaultValueId") OR
      (d."Id" IS NOT NULL AND d."RuleId" IS DISTINCT FROM rule."Id") OR
      (d."ValueId" IS NOT NULL AND (v."EffectiveDate">NEW."PostingDate" OR v."InactiveOn"<=NEW."PostingDate" OR
        (jsonb_array_length(rule."AllowedJson")>0 AND NOT (rule."AllowedJson" ? d."ValueId"::text)))))) THEN
   RAISE EXCEPTION 'Posted dimensions violate current account or effective value policy' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION analytics.project_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE previous analytics."FinancialProjectEvents"%ROWTYPE; owner_id uuid; starts date;
BEGIN
 SELECT "OwnerId","Start" INTO owner_id,starts FROM analytics."FinancialProjects" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."ProjectId";
 SELECT * INTO previous FROM analytics."FinancialProjectEvents" WHERE "CompanyId"=NEW."CompanyId" AND "ProjectId"=NEW."ProjectId"
   ORDER BY "Number" DESC LIMIT 1;
 IF (NOT FOUND AND (NEW."Number"<>1 OR NEW."State"<>0))
 OR (previous."Id" IS NOT NULL AND (NEW."Number"<>previous."Number"+1 OR NEW."Date"<previous."Date" OR NOT (
   (previous."State"=0 AND NEW."State"=1 AND NEW."ActorId"<>owner_id) OR
   (previous."State"=1 AND NEW."State"=2) OR
   (previous."State"=2 AND NEW."State" IN(3,4)) OR (previous."State"=3 AND NEW."State" IN(2,4)) OR
   (previous."State"=4 AND NEW."State" IN(2,5)))))
 OR (NEW."State"=2 AND NEW."Date"<starts) THEN
   RAISE EXCEPTION 'Invalid project event transition' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION analytics.slice_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE authority numeric;
BEGIN
 SELECT a."Amount" INTO authority FROM planning."BudgetAllocations" a JOIN planning."BudgetVersions" v ON v."Id"=a."BudgetVersionId"
   WHERE a."Id"=NEW."BudgetAllocationId" AND a."CompanyId"=NEW."CompanyId" AND v."CompanyId"=NEW."CompanyId" AND v."State"=0;
 IF NOT FOUND OR NEW."Amount"+coalesce((SELECT sum("Amount") FROM analytics."DimensionBudgetSlices" WHERE "CompanyId"=NEW."CompanyId"
   AND "BudgetAllocationId"=NEW."BudgetAllocationId"),0)>authority THEN
   RAISE EXCEPTION 'Dimensional budget slice exceeds draft parent authority' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION analytics.allocation_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE source accounting."JournalLines"%ROWTYPE;
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Allocation evidence is retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."State"<>0 OR NEW."JournalId" IS NOT NULL OR NEW."CorrectionJournalId" IS NOT NULL OR
   NEW."PostedBy" IS NOT NULL OR NEW."ApprovedBy" IS NOT NULL) THEN
   RAISE EXCEPTION 'Create a draft allocation first' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (
   (to_jsonb(NEW)-ARRAY['State','Version','ApprovedBy','PostedBy','JournalId','CorrectionJournalId','CorrectedOn']) IS DISTINCT FROM
   (to_jsonb(OLD)-ARRAY['State','Version','ApprovedBy','PostedBy','JournalId','CorrectionJournalId','CorrectedOn'])
   OR NOT ((OLD."State"=0 AND NEW."State" IN(1,3)) OR (OLD."State"=1 AND NEW."State" IN(1,2,3)) OR (OLD."State"=2 AND NEW."State"=4))
   OR (OLD."ApprovedBy" IS NOT NULL AND NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy")
   OR (OLD."State"=2 AND (NEW."JournalId",NEW."PostedBy") IS DISTINCT FROM (OLD."JournalId",OLD."PostedBy"))) THEN
   RAISE EXCEPTION 'Invalid allocation lifecycle or mutation of approved evidence' USING ERRCODE='23514'; END IF;
 IF NEW."State"=2 THEN
   SELECT l.* INTO source FROM accounting."JournalLines" l JOIN accounting."Journals" j ON j."Id"=l."JournalId"
     JOIN accounting."Accounts" a ON a."Id"=l."AccountId" WHERE l."CompanyId"=NEW."CompanyId" AND l."Id"=NEW."SourceLineId"
       AND a."Classification"=4 AND j."State"=1 AND j."PostingDate"<=NEW."Date" AND l."Debit">0
       AND NOT EXISTS(SELECT 1 FROM accounting."Journals" rev WHERE rev."CompanyId"=NEW."CompanyId" AND rev."State"=1 AND rev."ReversesJournalId"=j."Id");
   IF NOT FOUND OR NEW."PostedBy" IS NULL OR (NEW."PlanJson"->>'Amount')::numeric IS DISTINCT FROM source."Debit" OR
     EXISTS(SELECT 1 FROM jsonb_array_elements(NEW."PlanJson"->'Lines') p WHERE (p->>'AccountId')::uuid IS DISTINCT FROM source."AccountId")
     OR EXISTS(SELECT 1 FROM analytics."CostAllocationExecutions" old WHERE old."CompanyId"=NEW."CompanyId" AND old."SourceLineId"=NEW."SourceLineId"
       AND old."State"=4 AND old."CorrectedOn">NEW."Date")
     OR NOT EXISTS(SELECT 1 FROM jsonb_array_elements(NEW."PlanJson"->'Lines') p
       HAVING sum((p->>'Debit')::numeric)=source."Debit" AND sum((p->>'Credit')::numeric)=source."Debit") THEN
     RAISE EXCEPTION 'Allocation must conserve the retained source expense' USING ERRCODE='23514'; END IF;
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION analytics.allocation_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE a analytics."CostAllocationExecutions"%ROWTYPE; reversed jsonb;
BEGIN
 IF TG_TABLE_SCHEMA='accounting' THEN
   IF NEW."State"<>1 OR NEW."SourceModule" NOT IN ('Dimensions.Allocation','Dimensions.Correction') THEN RETURN NULL; END IF;
   IF NOT EXISTS(SELECT 1 FROM analytics."CostAllocationExecutions" e WHERE e."CompanyId"=NEW."CompanyId" AND e."Id"=NEW."SourceDocumentId"
     AND ((NEW."SourceModule"='Dimensions.Allocation' AND e."JournalId"=NEW."Id" AND e."State" IN(2,4)) OR
          (NEW."SourceModule"='Dimensions.Correction' AND e."CorrectionJournalId"=NEW."Id" AND e."State"=4))) THEN
      RAISE EXCEPTION 'Financial allocation requires completed source evidence' USING ERRCODE='23514'; END IF;
   RETURN NULL;
 END IF;
 SELECT * INTO STRICT a FROM analytics."CostAllocationExecutions" WHERE "Id"=NEW."Id";
 IF a."State" NOT IN(2,4) THEN
   IF a."PostedBy" IS NOT NULL OR a."JournalId" IS NOT NULL OR a."CorrectionJournalId" IS NOT NULL THEN
     RAISE EXCEPTION 'Staged allocation must complete before commit' USING ERRCODE='23514'; END IF;
   RETURN NULL;
 END IF;
 IF a."Mode"=0 THEN
   IF a."JournalId" IS NOT NULL OR a."CorrectionJournalId" IS NOT NULL THEN
     RAISE EXCEPTION 'Analytical allocations cannot post journals' USING ERRCODE='23514'; END IF;
   RETURN NULL;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM accounting."Journals" j WHERE j."CompanyId"=a."CompanyId" AND j."Id"=a."JournalId"
   AND j."State"=1 AND j."SourceDocumentId"=a."Id" AND j."SourceModule"='Dimensions.Allocation' AND j."PostingDate"=a."Date"
   AND j."PostedBy"=a."PostedBy") THEN RAISE EXCEPTION 'Financial allocation requires its approved journal' USING ERRCODE='23514'; END IF;
 PERFORM financial_close.assert_lines_v1(a."JournalId",a."PlanJson"->'Lines');
 IF a."State"=4 THEN
   IF NOT EXISTS(SELECT 1 FROM accounting."Journals" j WHERE j."CompanyId"=a."CompanyId" AND j."Id"=a."CorrectionJournalId"
     AND j."State"=1 AND j."SourceDocumentId"=a."Id" AND j."SourceModule"='Dimensions.Correction' AND j."PostingDate"=a."CorrectedOn") THEN
     RAISE EXCEPTION 'Financial allocation correction requires its reversal journal' USING ERRCODE='23514'; END IF;
   SELECT jsonb_agg(p || jsonb_build_object('Debit',p->'Credit','Credit',p->'Debit') ORDER BY n)
     INTO reversed FROM jsonb_array_elements(a."PlanJson"->'Lines') WITH ORDINALITY t(p,n);
   PERFORM financial_close.assert_lines_v1(a."CorrectionJournalId",reversed);
 END IF;
 RETURN NULL;
END $$;

DO $$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['DimensionDefinitions','DimensionValues','DimensionRules','DimensionBindings','PostedDimensions',
   'FinancialProjects','FinancialProjectEvents','DimensionBudgetSlices','ProjectFinancialForecasts','CostAllocationExecutions'] LOOP
   EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON analytics.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
   EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON analytics.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['DimensionDefinitions','DimensionRules','DimensionBindings','PostedDimensions',
   'FinancialProjects','FinancialProjectEvents','DimensionBudgetSlices','ProjectFinancialForecasts'] LOOP
   EXECUTE format('CREATE TRIGGER b_immutable BEFORE UPDATE OR DELETE ON analytics.%I FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
END $$;
CREATE TRIGGER b_dimension_value BEFORE INSERT OR UPDATE OR DELETE ON analytics."DimensionValues" FOR EACH ROW EXECUTE FUNCTION analytics.value_guard_v1();
CREATE TRIGGER b_dimension_rule BEFORE INSERT ON analytics."DimensionRules" FOR EACH ROW EXECUTE FUNCTION analytics.rule_guard_v1();
CREATE TRIGGER b_dimension_binding BEFORE INSERT ON analytics."DimensionBindings" FOR EACH ROW EXECUTE FUNCTION analytics.binding_guard_v1();
CREATE TRIGGER b_posted_dimension BEFORE INSERT ON analytics."PostedDimensions" FOR EACH ROW EXECUTE FUNCTION analytics.tag_guard_v1();
CREATE TRIGGER b_dimension_project_event BEFORE INSERT ON analytics."FinancialProjectEvents" FOR EACH ROW EXECUTE FUNCTION analytics.project_guard_v1();
CREATE TRIGGER b_dimension_budget_slice BEFORE INSERT ON analytics."DimensionBudgetSlices" FOR EACH ROW EXECUTE FUNCTION analytics.slice_guard_v1();
CREATE TRIGGER b_dimension_allocation BEFORE INSERT OR UPDATE OR DELETE ON analytics."CostAllocationExecutions" FOR EACH ROW EXECUTE FUNCTION analytics.allocation_guard_v1();
CREATE TRIGGER dimension_journal_policy BEFORE UPDATE ON accounting."Journals" FOR EACH ROW EXECUTE FUNCTION analytics.journal_guard_v1();
CREATE CONSTRAINT TRIGGER z_dimension_allocation AFTER INSERT OR UPDATE ON analytics."CostAllocationExecutions" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION analytics.allocation_contract_v1();
CREATE CONSTRAINT TRIGGER z_dimension_allocation_journal AFTER INSERT OR UPDATE ON accounting."Journals" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION analytics.allocation_contract_v1();
""";

    public const string Down = """
DROP FUNCTION analytics.allocation_contract_v1() CASCADE;
DROP FUNCTION analytics.allocation_guard_v1() CASCADE;
DROP FUNCTION analytics.slice_guard_v1() CASCADE;
DROP FUNCTION analytics.project_guard_v1() CASCADE;
DROP FUNCTION analytics.journal_guard_v1() CASCADE;
DROP FUNCTION analytics.tag_guard_v1() CASCADE;
DROP FUNCTION analytics.binding_guard_v1() CASCADE;
DROP FUNCTION analytics.rule_guard_v1() CASCADE;
DROP FUNCTION analytics.value_guard_v1() CASCADE;
""";
}
