namespace RAQM.Api.Data.Migrations;

internal static class FixedAssetsV1Sql
{
    public const string Up = """
CREATE FUNCTION fixed_assets.control_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM fixed_assets."FixedAssetRegister" WHERE "CompanyId"=NEW."CompanyId" AND "CategoryId"=NEW."CategoryId")
 OR EXISTS(SELECT 1 FROM trading."AccountMappings" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=NEW."AccountId")
 OR EXISTS(SELECT 1 FROM banking."TreasuryAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "LedgerAccountId"=NEW."AccountId")
 OR NOT EXISTS(SELECT 1 FROM accounting."Accounts" a WHERE a."CompanyId"=NEW."CompanyId" AND a."Id"=NEW."AccountId"
   AND a."Classification"=0 AND a."IsPosting" AND a."IsActive" AND a."IsContra"=(NEW."Role" IN (1,2))) THEN
   RAISE EXCEPTION 'Fixed asset control mapping is ineligible or already in use' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION fixed_assets.category_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF (SELECT count(*) FROM fixed_assets."AssetControlAccounts" a WHERE a."CategoryId"=NEW."Id" AND a."Role" IN(0,1,2))<>3
 OR EXISTS(SELECT 1 FROM accounting."Accounts" a WHERE a."Id" IN (NEW."DepreciationExpenseId",NEW."ImpairmentLossId",NEW."GainId",NEW."LossId")
   AND (NOT a."IsPosting" OR NOT a."IsActive" OR a."IsContra" OR a."Classification"<>CASE WHEN a."Id"=NEW."GainId" THEN 3 ELSE 4 END)) THEN
   RAISE EXCEPTION 'Fixed asset category requires three controls and compatible gain and expense accounts' USING ERRCODE='23514';
 END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION fixed_assets.mapping_protection_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_TABLE_SCHEMA='accounting' THEN
   IF (NEW."Classification",NEW."IsPosting",NEW."IsContra") IS DISTINCT FROM (OLD."Classification",OLD."IsPosting",OLD."IsContra")
   AND (EXISTS(SELECT 1 FROM fixed_assets."AssetControlAccounts" WHERE "CompanyId"=OLD."CompanyId" AND "AccountId"=OLD."Id")
     OR EXISTS(SELECT 1 FROM fixed_assets."AssetCategories" WHERE "CompanyId"=OLD."CompanyId" AND
       OLD."Id" IN ("DepreciationExpenseId","ImpairmentLossId","GainId","LossId"))) THEN
     RAISE EXCEPTION 'Mapped fixed asset accounts cannot change semantics' USING ERRCODE='23514'; END IF;
 ELSIF TG_TABLE_SCHEMA='organizations' THEN
   IF NEW."BaseCurrency"<>OLD."BaseCurrency" AND EXISTS(SELECT 1 FROM fixed_assets."AssetCategories" WHERE "CompanyId"=OLD."Id") THEN
     RAISE EXCEPTION 'Fixed asset setup freezes company currency' USING ERRCODE='23514'; END IF;
 ELSIF TG_TABLE_SCHEMA='banking' THEN
   IF EXISTS(SELECT 1 FROM fixed_assets."AssetControlAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=NEW."LedgerAccountId") THEN
     RAISE EXCEPTION 'Fixed asset control cannot be used for treasury' USING ERRCODE='23514'; END IF;
 ELSE
   IF EXISTS(SELECT 1 FROM fixed_assets."AssetControlAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=NEW."AccountId") THEN
     RAISE EXCEPTION 'Fixed asset control cannot be used for trading setup' USING ERRCODE='23514'; END IF;
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION fixed_assets.register_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Asset register evidence is retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."Status"<>0 OR NOT EXISTS(SELECT 1 FROM organizations."Companies"
   WHERE "Id"=NEW."CompanyId" AND "BaseCurrency"=NEW."Currency")) THEN
   RAISE EXCEPTION 'Register a draft in company currency' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (
   (to_jsonb(NEW)-ARRAY['Status','Version','RecognitionDate','AvailableDate','DisposalDate']) IS DISTINCT FROM
   (to_jsonb(OLD)-ARRAY['Status','Version','RecognitionDate','AvailableDate','DisposalDate'])
   OR OLD."Status">=4 OR NOT (NEW."Status"=OLD."Status" OR
      (OLD."Status"=0 AND NEW."Status"=1) OR (OLD."Status"=1 AND NEW."Status" IN(0,2,3)) OR
      (OLD."Status"=2 AND NEW."Status" IN(3,4,5)) OR (OLD."Status"=3 AND NEW."Status" IN(4,5)))
   OR (OLD."RecognitionDate" IS NOT NULL AND NEW."RecognitionDate" IS DISTINCT FROM OLD."RecognitionDate")
   OR (OLD."AvailableDate" IS NOT NULL AND NEW."AvailableDate" IS DISTINCT FROM OLD."AvailableDate")) THEN
   RAISE EXCEPTION 'Invalid fixed asset lifecycle transition' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION fixed_assets.component_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE d boolean; units integer;
BEGIN
 SELECT c."Depreciable",trading.units_v1(a."Currency") INTO d,units FROM fixed_assets."FixedAssetRegister" a
 JOIN fixed_assets."AssetCategories" c ON c."Id"=a."CategoryId" WHERE a."Id"=NEW."AssetId" AND a."Status"=0;
 IF NOT FOUND OR (NOT d AND (NEW."LifeMonths"<>0 OR NEW."OpeningDepreciation"<>0))
 OR NEW."ProposedCost"<>trading.round_even_v1(NEW."ProposedCost",units)
 OR NEW."InitialResidual"<>trading.round_even_v1(NEW."InitialResidual",units)
 OR NEW."OpeningDepreciation"<>trading.round_even_v1(NEW."OpeningDepreciation",units)
 OR NEW."OpeningImpairment"<>trading.round_even_v1(NEW."OpeningImpairment",units) THEN
   RAISE EXCEPTION 'Component terms require a draft asset and valid currency precision' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION fixed_assets.operation_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Asset operations are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."State"<>0 OR NEW."PlanJson" IS NOT NULL OR NEW."JournalId" IS NOT NULL OR NEW."PostedBy" IS NOT NULL) THEN
   RAISE EXCEPTION 'Create a draft operation before approval' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (
   (to_jsonb(NEW)-ARRAY['State','Version','PlanJson','ApprovedBy','PostedBy','PostedAt','JournalId','Sequence']) IS DISTINCT FROM
   (to_jsonb(OLD)-ARRAY['State','Version','PlanJson','ApprovedBy','PostedBy','PostedAt','JournalId','Sequence'])
   OR NOT ((OLD."State"=0 AND NEW."State" IN(1,3)) OR (OLD."State"=1 AND NEW."State" IN(1,2,3)))
   OR (OLD."State"=1 AND (NEW."ApprovedBy",NEW."PlanJson") IS DISTINCT FROM (OLD."ApprovedBy",OLD."PlanJson"))) THEN
   RAISE EXCEPTION 'Asset operation terms and approved plan are immutable' USING ERRCODE='23514'; END IF;
 IF NEW."State"=2 AND (
   NOT EXISTS(SELECT 1 FROM accounting."Periods" WHERE "CompanyId"=NEW."CompanyId" AND "State"=0 AND NEW."Date" BETWEEN "StartDate" AND "EndDate")
   OR EXISTS(SELECT 1 FROM financial_close."YearEndRecords" r JOIN accounting."FiscalYears" y ON y."Id"=r."FiscalYearId"
     WHERE r."CompanyId"=NEW."CompanyId" AND r."Finalized" AND y."EndDate">=NEW."Date")) THEN
   RAISE EXCEPTION 'Asset posting requires an open accounting date' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION fixed_assets.assert_operation_v1(oid uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE o fixed_assets."AssetOperations"%ROWTYPE; expected jsonb; module text;
BEGIN
 SELECT * INTO o FROM fixed_assets."AssetOperations" WHERE "Id"=oid;
 IF NOT FOUND THEN RAISE EXCEPTION 'Asset operation missing' USING ERRCODE='23514'; END IF;
 IF o."State"<>2 THEN
   IF o."PostedBy" IS NOT NULL OR o."JournalId" IS NOT NULL OR
     EXISTS(SELECT 1 FROM fixed_assets."AssetMovements" WHERE "OperationId"=oid) OR
     EXISTS(SELECT 1 FROM fixed_assets."AssetSourceClaims" WHERE "OperationId"=oid) THEN
     RAISE EXCEPTION 'Staged asset evidence must be finalized before commit' USING ERRCODE='23514'; END IF;
   RETURN;
 END IF;
 expected:=o."PlanJson"->'Lines';
 module:='FixedAssets.'||CASE o."Kind" WHEN 0 THEN 'Acquisition' WHEN 1 THEN 'Opening' WHEN 2 THEN 'AvailableForUse'
   WHEN 3 THEN 'Depreciation' WHEN 4 THEN 'Estimate' WHEN 5 THEN 'Transfer' WHEN 6 THEN 'Improvement'
   WHEN 7 THEN 'Repair' WHEN 8 THEN 'Impairment' WHEN 9 THEN 'Recovery' WHEN 10 THEN 'Sale' ELSE 'WriteOff' END;
 IF (jsonb_array_length(expected)>0)<>(o."JournalId" IS NOT NULL) THEN
   RAISE EXCEPTION 'Asset operation must carry exactly its approved journal effect' USING ERRCODE='23514'; END IF;
 IF o."JournalId" IS NOT NULL THEN
   IF NOT EXISTS(SELECT 1 FROM accounting."Journals" j WHERE j."Id"=o."JournalId" AND j."CompanyId"=o."CompanyId"
     AND j."State"=1 AND j."SourceModule"=module AND j."SourceDocumentId"=oid AND j."PostingDate"=o."Date" AND j."PostedBy"=o."PostedBy") THEN
     RAISE EXCEPTION 'Asset operation journal linkage is invalid' USING ERRCODE='23514'; END IF;
   PERFORM financial_close.assert_lines_v1(o."JournalId",expected);
 END IF;
 IF EXISTS(
   WITH e AS (SELECT (item->>'ComponentId')::uuid component, (item->>'DepreciationPeriodId')::uuid period_id,
     (item->>'Cost')::numeric cost, (item->>'Depreciation')::numeric dep, (item->>'Impairment')::numeric imp,
     (item->>'ShadowDepreciation')::numeric shadow, (item->>'Units')::numeric units,
     (item->>'RemainingOverride')::numeric life, (item->>'ResidualOverride')::numeric residual, (item->>'DepreciationCharge')::numeric charge
     FROM jsonb_array_elements(o."PlanJson"->'Movements') item),
   a AS (SELECT * FROM fixed_assets."AssetMovements" WHERE "OperationId"=oid)
   SELECT 1 FROM e FULL JOIN a ON a."ComponentId"=e.component
   WHERE (e.component,e.period_id,e.cost,e.dep,e.imp,e.shadow,e.units,e.life,e.residual,e.charge,o."Date",o."Sequence")
     IS DISTINCT FROM (a."ComponentId",a."DepreciationPeriodId",a."Cost",a."Depreciation",a."Impairment",a."ShadowDepreciation",a."Units",
       a."RemainingOverride",a."ResidualOverride",a."DepreciationCharge",a."Date",a."Sequence")) THEN
   RAISE EXCEPTION 'Asset register movements differ from the approved plan' USING ERRCODE='23514'; END IF;
 IF jsonb_array_length(o."PlanJson"->'Sources')<>(SELECT count(*) FROM fixed_assets."AssetSourceClaims" WHERE "OperationId"=oid)
   OR EXISTS(SELECT 1 FROM fixed_assets."AssetSourceClaims" c WHERE c."OperationId"=oid AND
     NOT ((o."PlanJson"->'Sources') ? c."JournalLineId"::text)) THEN
   RAISE EXCEPTION 'Source claims differ from the approved plan' USING ERRCODE='23514'; END IF;
 IF EXISTS(SELECT 1 FROM fixed_assets."AssetMovements" m JOIN fixed_assets."AssetComponents" c ON c."Id"=m."ComponentId"
   WHERE m."OperationId"=oid AND NOT ((o."PlanJson"->'Assets') ? c."AssetId"::text)) THEN
   RAISE EXCEPTION 'Component does not belong to the approved asset population' USING ERRCODE='23514'; END IF;
 IF o."Kind" IN(0,1,2,10,11) AND EXISTS(
   SELECT 1 FROM jsonb_array_elements_text(o."PlanJson"->'Assets') affected(id)
   LEFT JOIN fixed_assets."FixedAssetRegister" a ON a."Id"=affected.id::uuid AND a."CompanyId"=o."CompanyId"
   WHERE a."Id" IS NULL OR (o."Kind" IN(0,1) AND a."RecognitionDate" IS DISTINCT FROM o."Date")
     OR (o."Kind"=2 AND a."AvailableDate" IS DISTINCT FROM o."Date")
     OR (o."Kind" IN(10,11) AND a."DisposalDate" IS DISTINCT FROM o."Date")) THEN
   RAISE EXCEPTION 'Asset lifecycle and operation must commit together' USING ERRCODE='23514'; END IF;
END $$;

CREATE FUNCTION fixed_assets.contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_TABLE_NAME='AssetOperations' THEN PERFORM fixed_assets.assert_operation_v1(NEW."Id"); RETURN NULL; END IF;
 IF NEW."State"<>1 THEN RETURN NULL; END IF;
 IF NEW."SourceModule" LIKE 'FixedAssets.%' THEN
   IF NOT EXISTS(SELECT 1 FROM fixed_assets."AssetOperations" WHERE "Id"=NEW."SourceDocumentId" AND "JournalId"=NEW."Id") THEN
     RAISE EXCEPTION 'Fixed asset journal requires finalized source evidence' USING ERRCODE='23514'; END IF;
   PERFORM fixed_assets.assert_operation_v1(NEW."SourceDocumentId");
 ELSIF NEW."SourceModule"<>'OpeningBalance' AND EXISTS(
   SELECT 1 FROM accounting."JournalLines" l JOIN fixed_assets."AssetControlAccounts" a
      ON a."CompanyId"=l."CompanyId" AND a."AccountId"=l."AccountId" WHERE l."JournalId"=NEW."Id"
      AND (NEW."SourceModule" NOT IN ('AP.Bill','AP.Credit') OR a."Role" NOT IN(0,3))) THEN
   RAISE EXCEPTION 'Asset control account requires an eligible posting source' USING ERRCODE='23514';
 END IF;
 IF NEW."ReversesJournalId" IS NOT NULL AND (
   EXISTS(SELECT 1 FROM fixed_assets."AssetOperations" o WHERE o."CompanyId"=NEW."CompanyId" AND o."State"=2
      AND NEW."ReversesJournalId" IN(o."JournalId",o."OpeningJournalId"))
   OR EXISTS(SELECT 1 FROM fixed_assets."AssetSourceClaims" c JOIN accounting."JournalLines" l ON l."Id"=c."JournalLineId"
      WHERE c."CompanyId"=NEW."CompanyId" AND l."JournalId"=NEW."ReversesJournalId")) THEN
   RAISE EXCEPTION 'Original journal is retained by asset evidence' USING ERRCODE='23514'; END IF;
 IF NEW."SourceModule"='AP.Credit' AND EXISTS(
   SELECT 1 FROM trading."Documents" credit JOIN trading."Documents" bill ON bill."Id"=credit."OriginalDocumentId"
   JOIN accounting."JournalLines" l ON l."JournalId"=bill."JournalId"
   JOIN fixed_assets."AssetSourceClaims" c ON c."JournalLineId"=l."Id"
   WHERE credit."Id"=NEW."SourceDocumentId" AND credit."CompanyId"=NEW."CompanyId") THEN
   RAISE EXCEPTION 'Supplier credit affects claimed fixed asset bill evidence' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION fixed_assets.child_insert_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM fixed_assets."AssetOperations" o WHERE o."CompanyId"=NEW."CompanyId" AND
   o."Id"=NEW."OperationId" AND o."State"=1 AND o."PostedBy" IS NOT NULL) THEN
   RAISE EXCEPTION 'Asset movements and claims require a staged approved operation' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION fixed_assets.register_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW."RecognitionDate" IS DISTINCT FROM OLD."RecognitionDate" AND NOT EXISTS(
   SELECT 1 FROM fixed_assets."AssetOperations" o WHERE o."CompanyId"=NEW."CompanyId" AND o."State"=2 AND o."Kind" IN(0,1)
     AND o."Date"=NEW."RecognitionDate" AND (o."PlanJson"->'Assets') ? NEW."Id"::text) THEN
   RAISE EXCEPTION 'Recognition date requires posted acquisition evidence' USING ERRCODE='23514'; END IF;
 IF NEW."AvailableDate" IS DISTINCT FROM OLD."AvailableDate" AND NOT EXISTS(
   SELECT 1 FROM fixed_assets."AssetOperations" o WHERE o."CompanyId"=NEW."CompanyId" AND o."State"=2
     AND (o."PlanJson"->'Assets') ? NEW."Id"::text AND
     ((o."Kind"=2 AND o."Date"=NEW."AvailableDate") OR (o."Kind"=1 AND (o."PayloadJson"->>'HistoricalAvailableDate')::date=NEW."AvailableDate"))) THEN
   RAISE EXCEPTION 'Availability requires posted service or opening evidence' USING ERRCODE='23514'; END IF;
 IF NEW."DisposalDate" IS DISTINCT FROM OLD."DisposalDate" AND NOT EXISTS(
   SELECT 1 FROM fixed_assets."AssetOperations" o WHERE o."CompanyId"=NEW."CompanyId" AND o."State"=2 AND o."Kind" IN(10,11)
     AND o."Date"=NEW."DisposalDate" AND o."AssetId"=NEW."Id") THEN
   RAISE EXCEPTION 'Disposal date requires posted derecognition evidence' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

DO $$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['AssetCategories','AssetControlAccounts','FixedAssetRegister','AssetComponents','AssetOperations','AssetMovements','AssetSourceClaims'] LOOP
   EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON fixed_assets.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
   EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON fixed_assets.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['AssetCategories','AssetControlAccounts','AssetComponents','AssetMovements','AssetSourceClaims'] LOOP
   EXECUTE format('CREATE TRIGGER b_immutable BEFORE UPDATE OR DELETE ON fixed_assets.%I FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['AssetMovements','AssetSourceClaims'] LOOP
   EXECUTE format('CREATE TRIGGER b_asset_child BEFORE INSERT ON fixed_assets.%I FOR EACH ROW EXECUTE FUNCTION fixed_assets.child_insert_v1()',t);
 END LOOP;
END $$;
CREATE TRIGGER b_asset_controls BEFORE INSERT ON fixed_assets."AssetControlAccounts" FOR EACH ROW EXECUTE FUNCTION fixed_assets.control_guard_v1();
CREATE CONSTRAINT TRIGGER z_category AFTER INSERT ON fixed_assets."AssetCategories" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fixed_assets.category_contract_v1();
CREATE TRIGGER b_asset_register BEFORE INSERT OR UPDATE OR DELETE ON fixed_assets."FixedAssetRegister" FOR EACH ROW EXECUTE FUNCTION fixed_assets.register_guard_v1();
CREATE CONSTRAINT TRIGGER z_asset_register AFTER UPDATE ON fixed_assets."FixedAssetRegister" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fixed_assets.register_contract_v1();
CREATE TRIGGER b_asset_component BEFORE INSERT ON fixed_assets."AssetComponents" FOR EACH ROW EXECUTE FUNCTION fixed_assets.component_guard_v1();
CREATE TRIGGER b_asset_operation BEFORE INSERT OR UPDATE OR DELETE ON fixed_assets."AssetOperations" FOR EACH ROW EXECUTE FUNCTION fixed_assets.operation_guard_v1();
CREATE CONSTRAINT TRIGGER z_operation AFTER INSERT OR UPDATE ON fixed_assets."AssetOperations" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fixed_assets.contract_v1();
CREATE TRIGGER asset_account_semantics BEFORE UPDATE ON accounting."Accounts" FOR EACH ROW EXECUTE FUNCTION fixed_assets.mapping_protection_v1();
CREATE TRIGGER asset_company_currency BEFORE UPDATE ON organizations."Companies" FOR EACH ROW EXECUTE FUNCTION fixed_assets.mapping_protection_v1();
CREATE TRIGGER asset_bank_mapping BEFORE INSERT OR UPDATE ON banking."TreasuryAccounts" FOR EACH ROW EXECUTE FUNCTION fixed_assets.mapping_protection_v1();
CREATE TRIGGER asset_trading_mapping BEFORE INSERT OR UPDATE ON trading."AccountMappings" FOR EACH ROW EXECUTE FUNCTION fixed_assets.mapping_protection_v1();
CREATE CONSTRAINT TRIGGER z_fixed_assets_journal AFTER INSERT OR UPDATE ON accounting."Journals" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fixed_assets.contract_v1();
""";

    public const string Down = """
DROP FUNCTION fixed_assets.contract_v1() CASCADE;
DROP FUNCTION fixed_assets.register_contract_v1() CASCADE;
DROP FUNCTION fixed_assets.child_insert_v1() CASCADE;
DROP FUNCTION fixed_assets.assert_operation_v1(uuid);
DROP FUNCTION fixed_assets.operation_guard_v1() CASCADE;
DROP FUNCTION fixed_assets.component_guard_v1() CASCADE;
DROP FUNCTION fixed_assets.register_guard_v1() CASCADE;
DROP FUNCTION fixed_assets.mapping_protection_v1() CASCADE;
DROP FUNCTION fixed_assets.category_contract_v1() CASCADE;
DROP FUNCTION fixed_assets.control_guard_v1() CASCADE;
""";
}
