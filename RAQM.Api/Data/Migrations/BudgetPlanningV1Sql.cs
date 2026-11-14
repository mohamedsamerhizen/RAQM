namespace RAQM.Api.Data.Migrations;

internal static class BudgetPlanningV1Sql
{
    public const string Up = """
CREATE FUNCTION planning.budget_version_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE planned numeric; fiscal uuid; predecessor uuid;
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Budget versions are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."State"<>0 OR NEW."ReviewerId" IS NOT NULL OR NEW."ApprovedBy" IS NOT NULL) THEN
   RAISE EXCEPTION 'Create a draft budget version' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' THEN
   IF (to_jsonb(NEW)-ARRAY['State','Version','ReviewerId','ApprovedBy','EffectiveDate','ActivatedOn']) IS DISTINCT FROM
      (to_jsonb(OLD)-ARRAY['State','Version','ReviewerId','ApprovedBy','EffectiveDate','ActivatedOn'])
      OR NOT ((OLD."State"=0 AND NEW."State"=1) OR (OLD."State"=1 AND NEW."State" IN (2,6))
       OR (OLD."State"=2 AND NEW."State" IN (3,6)) OR (OLD."State"=3 AND NEW."State" IN (4,6))
       OR (OLD."State"=4 AND NEW."State"=5))
      OR (OLD."ReviewerId" IS NOT NULL AND OLD."ReviewerId" IS DISTINCT FROM NEW."ReviewerId")
      OR (OLD."ApprovedBy" IS NOT NULL AND OLD."ApprovedBy" IS DISTINCT FROM NEW."ApprovedBy")
      OR (OLD."EffectiveDate" IS NOT NULL AND (OLD."EffectiveDate",OLD."ActivatedOn") IS DISTINCT FROM (NEW."EffectiveDate",NEW."ActivatedOn"))
      OR (NEW."State" IN (1,2,3,4,5) AND (NEW."ReviewerId" IS NULL OR NEW."ReviewerId"=NEW."CreatedBy")) THEN
      RAISE EXCEPTION 'Budget version lifecycle or retained terms changed' USING ERRCODE='23514';
   END IF;
   IF NEW."State"=1 AND NOT EXISTS(SELECT 1 FROM planning."BudgetAllocations" WHERE "BudgetVersionId"=NEW."Id") THEN
      RAISE EXCEPTION 'Submit a populated budget' USING ERRCODE='23514'; END IF;
   IF NEW."State"=3 THEN
     SELECT coalesce(sum("Amount"),0) INTO planned FROM planning."BudgetAllocations" WHERE "BudgetVersionId"=NEW."Id";
     IF NOT EXISTS(SELECT 1 FROM planning."FinancialBudgets" WHERE "Id"=NEW."BudgetId" AND "ApprovalLimit">=planned) THEN
       RAISE EXCEPTION 'Budget approval limit exceeded' USING ERRCODE='23514'; END IF;
   END IF;
   IF NEW."State"=4 THEN
     SELECT "FiscalYearId" INTO fiscal FROM planning."FinancialBudgets" WHERE "Id"=NEW."BudgetId";
     SELECT "Id" INTO predecessor FROM planning."BudgetVersions" WHERE "CompanyId"=NEW."CompanyId" AND "BudgetId"=NEW."BudgetId"
       AND "State" IN (4,5) ORDER BY "Number" DESC LIMIT 1;
     IF predecessor IS DISTINCT FROM NEW."ActiveAtPreparationId" OR NOT EXISTS(SELECT 1 FROM accounting."Periods" p
       WHERE p."CompanyId"=NEW."CompanyId" AND p."FiscalYearId"=fiscal AND p."State"=0
         AND NEW."EffectiveDate" BETWEEN p."StartDate" AND p."EndDate") THEN
       RAISE EXCEPTION 'Budget activation requires current basis and an open fiscal period' USING ERRCODE='23514'; END IF;
     IF EXISTS(SELECT 1 FROM accounting."Periods" p
       JOIN (SELECT "PeriodId","AccountId",sum(effect) AS difference FROM (
         SELECT "PeriodId","AccountId","Amount" AS effect FROM planning."BudgetAllocations" WHERE "BudgetVersionId"=NEW."Id"
         UNION ALL SELECT "PeriodId","AccountId",-"Amount" FROM planning."BudgetAllocations" WHERE "BudgetVersionId"=predecessor
       ) a GROUP BY "PeriodId","AccountId") d ON d."PeriodId"=p."Id"
       WHERE p."CompanyId"=NEW."CompanyId" AND p."State"<>0 AND d.difference<>0) THEN
       RAISE EXCEPTION 'Published budget cannot change closed allocations' USING ERRCODE='23514'; END IF;
   END IF;
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION planning.allocation_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM planning."BudgetVersions" v
   JOIN planning."FinancialBudgets" b ON b."Id"=v."BudgetId"
   JOIN accounting."Periods" p ON p."Id"=NEW."PeriodId"
   JOIN accounting."Accounts" a ON a."Id"=NEW."AccountId"
   JOIN organizations."Companies" c ON c."Id"=NEW."CompanyId"
   WHERE v."CompanyId"=NEW."CompanyId" AND v."Id"=NEW."BudgetVersionId" AND v."State"=0
     AND b."CompanyId"=NEW."CompanyId" AND p."CompanyId"=NEW."CompanyId" AND p."FiscalYearId"=b."FiscalYearId"
     AND a."CompanyId"=NEW."CompanyId" AND a."IsPosting" AND a."IsActive"
     AND a."Classification"=CASE b."Kind" WHEN 0 THEN 3 WHEN 1 THEN 4 ELSE 0 END
     AND (b."Kind"<>2 OR EXISTS(SELECT 1 FROM fixed_assets."AssetControlAccounts" s WHERE s."CompanyId"=NEW."CompanyId"
       AND s."AccountId"=NEW."AccountId" AND s."Role"=0))
     AND (b."Kind"<>3 OR EXISTS(SELECT 1 FROM inventory."InventoryAccounts" i WHERE i."CompanyId"=NEW."CompanyId"
       AND i."AccountId"=NEW."AccountId" AND i."Role"=0))
     AND NEW."Amount"=round(NEW."Amount",trading.units_v1(c."BaseCurrency"))) THEN
   RAISE EXCEPTION 'Budget allocation must use an eligible account and period of the draft fiscal year' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION planning.forecast_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Forecast snapshots are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."State"<>0 OR NEW."ApprovedBy" IS NOT NULL OR
   jsonb_typeof(NEW."SnapshotJson"->'Rows') IS DISTINCT FROM 'array' OR
   jsonb_typeof(NEW."SnapshotJson"->'CashBuckets') IS DISTINCT FROM 'array') THEN
   RAISE EXCEPTION 'Create a populated draft forecast snapshot' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (
   (to_jsonb(NEW)-ARRAY['State','Version','ApprovedBy']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['State','Version','ApprovedBy'])
   OR OLD."State"<>0 OR NEW."State" NOT IN (1,2) OR NEW."ApprovedBy" IS NULL OR NEW."ApprovedBy"=NEW."CreatedBy") THEN
   RAISE EXCEPTION 'Forecast snapshot and assumptions are immutable' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION planning.override_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Budget overrides are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND NEW."UsedAt" IS NOT NULL THEN
   RAISE EXCEPTION 'Budget override starts unused' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (OLD."UsedAt" IS NOT NULL OR NEW."UsedAt" IS NULL OR
   (to_jsonb(NEW)-'UsedAt') IS DISTINCT FROM (to_jsonb(OLD)-'UsedAt')) THEN
   RAISE EXCEPTION 'Only initial override consumption is allowed' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION planning.commitment_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE exposure numeric; initial numeric;
BEGIN
 SELECT coalesce(sum("Amount"),0),max("Amount") FILTER (WHERE "EventKey" LIKE 'reserve:%' OR "EventKey" LIKE 'capital-reserve:%')
   INTO exposure,initial FROM planning."BudgetCommitmentEvents"
   WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=NEW."AccountId" AND "PeriodId"=NEW."PeriodId"
     AND "ProcurementLineId" IS NOT DISTINCT FROM NEW."ProcurementLineId"
     AND "AssetOperationId" IS NOT DISTINCT FROM NEW."AssetOperationId";
 IF initial IS NULL OR exposure<0 OR exposure>initial THEN
   RAISE EXCEPTION 'Commitment movements must stay within reserved authority' USING ERRCODE='23514'; END IF;
 IF NEW."ProcurementLineId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM inventory."ProcurementLines" l
   JOIN inventory."ProcurementDocuments" d ON d."Id"=l."DocumentId"
   WHERE l."CompanyId"=NEW."CompanyId" AND d."CompanyId"=NEW."CompanyId" AND l."Id"=NEW."ProcurementLineId"
     AND d."State" IN (1,2,3) AND d."IsOrder"=(NEW."Kind"=1) AND (d."State"<>3 OR exposure=0)) THEN
   RAISE EXCEPTION 'Commitment requires retained procurement approval' USING ERRCODE='23514'; END IF;
 IF NEW."StockOperationId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM inventory."Operations" s WHERE s."CompanyId"=NEW."CompanyId"
   AND s."Id"=NEW."StockOperationId" AND s."State"=2 AND s."OrderLineId"=NEW."ProcurementLineId") THEN
   RAISE EXCEPTION 'Receipt relief must finalize with stock posting' USING ERRCODE='23514'; END IF;
 IF NEW."AssetOperationId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM fixed_assets."AssetOperations" s
   WHERE s."CompanyId"=NEW."CompanyId" AND s."Id"=NEW."AssetOperationId" AND s."State" IN(1,2,3) AND (s."State"=1 OR exposure=0)) THEN
   RAISE EXCEPTION 'Capital commitment requires retained asset approval' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION planning.anchor_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_TABLE_SCHEMA='organizations' THEN
   IF OLD."BaseCurrency"<>NEW."BaseCurrency" AND
     (EXISTS(SELECT 1 FROM planning."FinancialBudgets" WHERE "CompanyId"=OLD."Id") OR
      EXISTS(SELECT 1 FROM planning."ForecastVersions" WHERE "CompanyId"=OLD."Id") OR
      EXISTS(SELECT 1 FROM planning."BudgetPolicies" WHERE "CompanyId"=OLD."Id")) THEN
     RAISE EXCEPTION 'Planning evidence freezes the company base currency' USING ERRCODE='23514'; END IF;
 ELSIF (OLD."Classification",OLD."IsContra",OLD."IsPosting") IS DISTINCT FROM (NEW."Classification",NEW."IsContra",NEW."IsPosting")
   AND (EXISTS(SELECT 1 FROM planning."BudgetAllocations" WHERE "CompanyId"=OLD."CompanyId" AND "AccountId"=OLD."Id") OR
        EXISTS(SELECT 1 FROM planning."BudgetPolicies" WHERE "CompanyId"=OLD."CompanyId" AND "AccountId"=OLD."Id")) THEN
   RAISE EXCEPTION 'Budgeted accounts retain their posting semantics' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

DO $$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['FinancialBudgets','BudgetVersions','BudgetAllocations','BudgetPolicies','BudgetCommitmentEvents',
   'BudgetOverrideGrants','ForecastVersions'] LOOP
   EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON planning.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
   EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON planning.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['FinancialBudgets','BudgetAllocations','BudgetPolicies','BudgetCommitmentEvents'] LOOP
   EXECUTE format('CREATE TRIGGER b_immutable BEFORE UPDATE OR DELETE ON planning.%I FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
END $$;
CREATE TRIGGER b_budget_version BEFORE INSERT OR UPDATE OR DELETE ON planning."BudgetVersions" FOR EACH ROW EXECUTE FUNCTION planning.budget_version_guard_v1();
CREATE TRIGGER b_budget_allocation BEFORE INSERT ON planning."BudgetAllocations" FOR EACH ROW EXECUTE FUNCTION planning.allocation_guard_v1();
CREATE TRIGGER b_budget_override BEFORE INSERT OR UPDATE OR DELETE ON planning."BudgetOverrideGrants" FOR EACH ROW EXECUTE FUNCTION planning.override_guard_v1();
CREATE TRIGGER b_forecast_snapshot BEFORE INSERT OR UPDATE OR DELETE ON planning."ForecastVersions" FOR EACH ROW EXECUTE FUNCTION planning.forecast_guard_v1();
CREATE CONSTRAINT TRIGGER z_budget_commitment AFTER INSERT ON planning."BudgetCommitmentEvents" DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION planning.commitment_contract_v1();
CREATE TRIGGER planning_company_currency BEFORE UPDATE ON organizations."Companies" FOR EACH ROW EXECUTE FUNCTION planning.anchor_guard_v1();
CREATE TRIGGER planning_account_semantics BEFORE UPDATE ON accounting."Accounts" FOR EACH ROW EXECUTE FUNCTION planning.anchor_guard_v1();
""";

    public const string Down = """
DROP FUNCTION planning.anchor_guard_v1() CASCADE;
DROP FUNCTION planning.commitment_contract_v1() CASCADE;
DROP FUNCTION planning.override_guard_v1() CASCADE;
DROP FUNCTION planning.forecast_guard_v1() CASCADE;
DROP FUNCTION planning.allocation_guard_v1() CASCADE;
DROP FUNCTION planning.budget_version_guard_v1() CASCADE;
""";
}
