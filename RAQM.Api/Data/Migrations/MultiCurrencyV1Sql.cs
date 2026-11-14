namespace RAQM.Api.Data.Migrations;

internal static class MultiCurrencyV1Sql
{
    public const string Up = """
CREATE FUNCTION foreign_exchange.account_dedicated_v1(cid uuid, aid uuid, classification integer) RETURNS boolean LANGUAGE sql AS $$
 SELECT EXISTS(SELECT 1 FROM accounting."Accounts" a WHERE a."CompanyId"=cid AND a."Id"=aid AND a."Classification"=classification
   AND a."IsPosting" AND a."IsActive" AND NOT a."IsContra")
   AND NOT EXISTS(SELECT 1 FROM foreign_exchange."FxAccounts" WHERE "CompanyId"=cid AND "AccountId"=aid)
   AND NOT EXISTS(SELECT 1 FROM foreign_exchange."FxPositions" WHERE "CompanyId"=cid AND "AccountId"=aid AND "Kind"=6)
   AND NOT EXISTS(SELECT 1 FROM trading."AccountMappings" WHERE "CompanyId"=cid AND "AccountId"=aid)
   AND NOT EXISTS(SELECT 1 FROM trading."Parties" WHERE "CompanyId"=cid AND "DefaultAccountId"=aid)
   AND NOT EXISTS(SELECT 1 FROM banking."TreasuryAccounts" WHERE "CompanyId"=cid AND "LedgerAccountId"=aid)
   AND NOT EXISTS(SELECT 1 FROM inventory."InventoryAccounts" WHERE "CompanyId"=cid AND "AccountId"=aid)
   AND NOT EXISTS(SELECT 1 FROM fixed_assets."AssetControlAccounts" WHERE "CompanyId"=cid AND "AccountId"=aid)
   AND NOT EXISTS(SELECT 1 FROM fixed_assets."AssetCategories" WHERE "CompanyId"=cid AND
     aid IN ("DepreciationExpenseId","ImpairmentLossId","GainId","LossId"))
   AND NOT EXISTS(SELECT 1 FROM accounting."JournalLines" l JOIN accounting."Journals" j ON j."Id"=l."JournalId"
     WHERE l."CompanyId"=cid AND l."AccountId"=aid AND j."State"=1)
$$;

CREATE FUNCTION foreign_exchange.setup_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_TABLE_NAME='FxConfigurations' THEN
   IF NOT EXISTS(SELECT 1 FROM organizations."Companies" WHERE "Id"=NEW."CompanyId" AND "BaseCurrency"=NEW."FunctionalCurrency") THEN
     RAISE EXCEPTION 'FX functional currency differs from company' USING ERRCODE='23514'; END IF;
 ELSIF TG_TABLE_NAME='FxAccounts' THEN
   IF NOT foreign_exchange.account_dedicated_v1(NEW."CompanyId",NEW."AccountId",CASE WHEN NEW."Role" IN (0,3,8) THEN 0
     WHEN NEW."Role" IN(1,2,9) THEN 1 WHEN NEW."Role" IN(4,6) THEN 3 ELSE 4 END) THEN
     RAISE EXCEPTION 'FX mapping requires a dedicated posting account' USING ERRCODE='23514'; END IF;
 ELSIF TG_TABLE_NAME='FxCurrencies' THEN
   IF NOT EXISTS(SELECT 1 FROM foreign_exchange."FxConfigurations" WHERE "CompanyId"=NEW."CompanyId" AND "FunctionalCurrency"<>NEW."Code")
     OR trading.units_v1(NEW."Code")<0 THEN
     RAISE EXCEPTION 'Enable a supported currency distinct from functional currency' USING ERRCODE='23514'; END IF;
 ELSIF NEW."Kind"=6 THEN
   IF NOT foreign_exchange.account_dedicated_v1(NEW."CompanyId",NEW."AccountId",0) THEN
     RAISE EXCEPTION 'Foreign bank requires a dedicated ledger account' USING ERRCODE='23514'; END IF;
 ELSE
   IF NOT EXISTS(SELECT 1 FROM foreign_exchange."FxAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=NEW."AccountId"
     AND "Role"=CASE WHEN NEW."Kind"=4 THEN 0 WHEN NEW."Kind"=5 THEN 1 ELSE NEW."Kind" END)
   OR NOT EXISTS(SELECT 1 FROM trading."Parties" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."PartyId"
     AND "Kind"=CASE WHEN NEW."Kind" IN(0,2,4) THEN 0 ELSE 1 END) THEN
     RAISE EXCEPTION 'Foreign position party or account is inconsistent' USING ERRCODE='23514'; END IF;
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION foreign_exchange.configuration_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF (SELECT count(*) FROM foreign_exchange."FxAccounts" WHERE "CompanyId"=NEW."CompanyId")<>10
 OR NOT EXISTS(SELECT 1 FROM foreign_exchange."FxConfigurations" WHERE "CompanyId"=NEW."CompanyId") THEN
   RAISE EXCEPTION 'FX configuration requires all dedicated account roles' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION foreign_exchange.rate_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'FX rates are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."State"<>0 OR NEW."ApprovedBy" IS NOT NULL OR
   (NEW."ReplacesId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM foreign_exchange."FxRates" r WHERE r."Id"=NEW."ReplacesId"
     AND r."CompanyId"=NEW."CompanyId" AND r."State"=1 AND r."CurrencyId"=NEW."CurrencyId" AND r."Type"=NEW."Type" AND r."Date"=NEW."Date"))) THEN
   RAISE EXCEPTION 'FX rate requires valid draft and supersession evidence' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (
   (to_jsonb(NEW)-ARRAY['State','Version','ApprovedBy']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['State','Version','ApprovedBy'])
   OR NOT ((OLD."State"=0 AND NEW."State" IN(1,3)) OR (OLD."State"=1 AND NEW."State"=2))
   OR (OLD."ApprovedBy" IS NOT NULL AND OLD."ApprovedBy" IS DISTINCT FROM NEW."ApprovedBy")) THEN
   RAISE EXCEPTION 'FX rate terms and prior decisions are immutable' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND NEW."State"=1 AND NEW."ReplacesId" IS NOT NULL AND NOT EXISTS(
   SELECT 1 FROM foreign_exchange."FxRates" r WHERE r."Id"=NEW."ReplacesId" AND r."CompanyId"=NEW."CompanyId"
     AND r."CurrencyId"=NEW."CurrencyId" AND r."Type"=NEW."Type" AND r."Date"=NEW."Date" AND r."State"=2) THEN
   RAISE EXCEPTION 'Supersede the predecessor before approving its replacement' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION foreign_exchange.operation_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'FX operations are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."State"<>0 OR NEW."ApprovedBy" IS NOT NULL OR NEW."PostedBy" IS NOT NULL OR
   NEW."PlanJson" IS NOT NULL OR NEW."JournalId" IS NOT NULL) THEN
   RAISE EXCEPTION 'FX operation must start as draft' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (
   (to_jsonb(NEW)-ARRAY['State','Version','ApprovedBy','PostedBy','PlanJson','JournalId','Sequence']) IS DISTINCT FROM
   (to_jsonb(OLD)-ARRAY['State','Version','ApprovedBy','PostedBy','PlanJson','JournalId','Sequence'])
   OR NOT ((OLD."State"=0 AND NEW."State" IN (1,3)) OR (OLD."State"=1 AND NEW."State" IN (1,2,3)))
   OR (OLD."State"=1 AND (OLD."ApprovedBy",OLD."PlanJson") IS DISTINCT FROM (NEW."ApprovedBy",NEW."PlanJson"))) THEN
   RAISE EXCEPTION 'FX source and approved plan are immutable' USING ERRCODE='23514'; END IF;
 IF NEW."State"=2 AND (
   NOT EXISTS(SELECT 1 FROM accounting."Periods" p WHERE p."CompanyId"=NEW."CompanyId" AND p."State"=0
     AND NEW."Date" BETWEEN p."StartDate" AND p."EndDate" AND (NEW."Kind"<>7 OR p."EndDate"=NEW."Date"))
   OR EXISTS(SELECT 1 FROM financial_close."YearEndRecords" r JOIN accounting."FiscalYears" y ON y."Id"=r."FiscalYearId"
     WHERE r."CompanyId"=NEW."CompanyId" AND r."Finalized" AND y."EndDate">=NEW."Date")
   OR EXISTS(SELECT 1 FROM foreign_exchange."FxOperations" o WHERE o."CompanyId"=NEW."CompanyId" AND o."State"=2 AND
     (o."Date">NEW."Date" OR (o."Date"=NEW."Date" AND o."Kind"=7 AND NEW."Kind" NOT IN(7,8))))) THEN
   RAISE EXCEPTION 'FX posting requires an open chronological accounting date' USING ERRCODE='23514'; END IF;
 IF NEW."State"=2 AND (
   (NEW."Kind"=8 AND NOT EXISTS(SELECT 1 FROM foreign_exchange."FxOperations" b
     WHERE b."CompanyId"=NEW."CompanyId" AND b."Id"=NEW."OriginalId" AND b."Kind"=7 AND b."State"=2 AND b."Date"<=NEW."Date"
       AND NOT EXISTS(SELECT 1 FROM foreign_exchange."FxOperations" later WHERE later."CompanyId"=b."CompanyId"
         AND later."State"=2 AND later."Sequence">b."Sequence"))) OR
   (NEW."Kind"=7 AND NEW."OriginalId" IS NOT NULL AND NOT EXISTS(
     SELECT 1 FROM foreign_exchange."FxOperations" b JOIN foreign_exchange."FxOperations" c ON c."OriginalId"=b."Id"
       WHERE b."CompanyId"=NEW."CompanyId" AND c."CompanyId"=NEW."CompanyId" AND b."Id"=NEW."OriginalId"
         AND b."Kind"=7 AND c."Kind"=8 AND b."State"=2 AND c."State"=2 AND b."Date"=NEW."Date" AND c."Date"=NEW."Date")) OR
   (NEW."Kind"=4 AND NOT EXISTS(SELECT 1 FROM foreign_exchange."FxOperations" b WHERE b."CompanyId"=NEW."CompanyId"
     AND b."Id"=NEW."OriginalId" AND b."Kind" IN(0,1) AND b."State"=2 AND b."Date"<=NEW."Date"))) THEN
   RAISE EXCEPTION 'FX correction or credit source is invalid' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION foreign_exchange.effect_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE position_kind integer; company_currency text; transaction_currency text;
BEGIN
 SELECT p."Kind",cfg."FunctionalCurrency",c."Code" INTO position_kind,company_currency,transaction_currency
 FROM foreign_exchange."FxPositions" p JOIN foreign_exchange."FxCurrencies" c ON c."Id"=p."CurrencyId"
 JOIN foreign_exchange."FxConfigurations" cfg ON cfg."CompanyId"=p."CompanyId"
 WHERE p."CompanyId"=NEW."CompanyId" AND p."Id"=NEW."PositionId";
 IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM foreign_exchange."FxOperations" o WHERE o."CompanyId"=NEW."CompanyId" AND
   o."Id"=NEW."OperationId" AND o."State"=1 AND o."PostedBy" IS NOT NULL AND o."Date"=NEW."Date"
   AND (o."Kind" NOT IN(7,8) OR (NEW."ForeignDelta"=0 AND position_kind NOT IN(2,3)))
   AND (o."Kind"<>7 OR EXISTS(SELECT 1 FROM foreign_exchange."FxRates" r WHERE r."CompanyId"=NEW."CompanyId"
     AND r."Id"=NEW."RateId" AND r."Type"=1 AND r."Date"=NEW."Date" AND r."State"=1)))
 OR NEW."ForeignDelta"<>round(NEW."ForeignDelta",trading.units_v1(transaction_currency))
 OR NEW."BaseDelta"<>round(NEW."BaseDelta",trading.units_v1(company_currency))
 OR (NEW."RateId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM foreign_exchange."FxRates" r JOIN foreign_exchange."FxPositions" p
   ON p."CurrencyId"=r."CurrencyId" WHERE p."Id"=NEW."PositionId" AND p."CompanyId"=NEW."CompanyId"
     AND r."Id"=NEW."RateId" AND r."CompanyId"=NEW."CompanyId" AND r."State" IN(1,2) AND r."Date"<=NEW."Date"))
 OR (position_kind=6 AND NEW."ForeignDelta"<>0 AND EXISTS(SELECT 1 FROM foreign_exchange."FxBankStatements" s WHERE
   s."CompanyId"=NEW."CompanyId" AND s."PositionId"=NEW."PositionId" AND s."Cutoff">=NEW."Date" AND s."StatementForeign"=s."BookForeign")) THEN
   RAISE EXCEPTION 'FX movement requires staged source, correct precision and eligible rate and cutoff' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION foreign_exchange.operation_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE o foreign_exchange."FxOperations"%ROWTYPE; expected jsonb; module text;
BEGIN
 SELECT * INTO STRICT o FROM foreign_exchange."FxOperations" WHERE "Id"=NEW."Id";
 IF o."State"<>2 THEN
   IF o."PostedBy" IS NOT NULL OR o."JournalId" IS NOT NULL OR
      EXISTS(SELECT 1 FROM foreign_exchange."FxEffects" WHERE "OperationId"=o."Id") THEN
     RAISE EXCEPTION 'Staged FX evidence must finalize before commit' USING ERRCODE='23514'; END IF;
   RETURN NULL;
 END IF;
 expected:=o."PlanJson"->'Lines';
 module:='FX.'||(ARRAY['Invoice','Bill','Settlement','Advance','Credit','BankPurchase','BankSale','Revaluation','RevaluationCorrection'])[o."Kind"+1];
 IF jsonb_typeof(expected) IS DISTINCT FROM 'array' OR jsonb_typeof(o."PlanJson"->'Movements') IS DISTINCT FROM 'array' OR
    (jsonb_array_length(expected)>0)<>(o."JournalId" IS NOT NULL) THEN
   RAISE EXCEPTION 'FX posting requires approved journal and movement evidence' USING ERRCODE='23514'; END IF;
 IF o."JournalId" IS NOT NULL THEN
   IF NOT EXISTS(SELECT 1 FROM accounting."Journals" j WHERE j."Id"=o."JournalId" AND j."CompanyId"=o."CompanyId" AND
     j."State"=1 AND j."SourceModule"=module AND j."SourceDocumentId"=o."Id" AND j."PostedBy"=o."PostedBy" AND j."PostingDate"=o."Date") THEN
     RAISE EXCEPTION 'FX journal source linkage is invalid' USING ERRCODE='23514'; END IF;
   PERFORM financial_close.assert_lines_v1(o."JournalId",expected);
 END IF;
 IF EXISTS(
   WITH e AS (SELECT (p->>'PositionId')::uuid pid,(p->>'RateId')::uuid rid,(p->>'ForeignDelta')::numeric f,(p->>'BaseDelta')::numeric b
      FROM jsonb_array_elements(o."PlanJson"->'Movements') p),
   a AS (SELECT * FROM foreign_exchange."FxEffects" WHERE "OperationId"=o."Id")
   SELECT 1 FROM e FULL JOIN a ON a."PositionId"=e.pid
   WHERE (e.pid,e.rid,e.f,e.b,o."Date",o."Sequence") IS DISTINCT FROM
     (a."PositionId",a."RateId",a."ForeignDelta",a."BaseDelta",a."Date",a."Sequence")
 ) THEN RAISE EXCEPTION 'Foreign movements differ from the approved plan' USING ERRCODE='23514'; END IF;
 IF EXISTS(
   SELECT 1 FROM foreign_exchange."FxEffects" e WHERE e."CompanyId"=o."CompanyId" AND e."Sequence"<=o."Sequence"
     AND e."PositionId" IN(SELECT "PositionId" FROM foreign_exchange."FxEffects" WHERE "OperationId"=o."Id")
   GROUP BY e."PositionId" HAVING sum(e."ForeignDelta")<0 OR sum(e."BaseDelta")<0 OR
     (sum(e."ForeignDelta")=0 AND sum(e."BaseDelta")<>0)
 ) THEN RAISE EXCEPTION 'Foreign and carrying balances must remain valid' USING ERRCODE='23514'; END IF;
 IF EXISTS(
   WITH effects AS (SELECT p."AccountId" id,sum(e."BaseDelta"*CASE WHEN p."Kind" IN(1,2,4) THEN -1 ELSE 1 END) net
     FROM foreign_exchange."FxEffects" e JOIN foreign_exchange."FxPositions" p ON p."Id"=e."PositionId"
     WHERE e."OperationId"=o."Id" GROUP BY p."AccountId"),
   lines AS (SELECT l."AccountId" id,sum(l."Debit"-l."Credit") net FROM accounting."JournalLines" l WHERE l."JournalId"=o."JournalId"
      AND (EXISTS(SELECT 1 FROM foreign_exchange."FxAccounts" a WHERE a."CompanyId"=o."CompanyId" AND a."AccountId"=l."AccountId" AND a."Role"<4)
        OR EXISTS(SELECT 1 FROM foreign_exchange."FxPositions" p WHERE p."CompanyId"=o."CompanyId" AND p."Kind"=6 AND p."AccountId"=l."AccountId"))
      GROUP BY l."AccountId")
   SELECT 1 FROM effects FULL JOIN lines USING(id) WHERE coalesce(effects.net,0)<>coalesce(lines.net,0)
 ) THEN RAISE EXCEPTION 'FX control lines must equal position carrying movements' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION foreign_exchange.journal_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW."State"<>1 THEN RETURN NULL; END IF;
 IF NEW."SourceModule" LIKE 'FX.%' THEN
   IF NOT EXISTS(SELECT 1 FROM foreign_exchange."FxOperations" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."SourceDocumentId"
     AND "JournalId"=NEW."Id" AND "State"=2) THEN RAISE EXCEPTION 'FX journal needs finalized source' USING ERRCODE='23514'; END IF;
 ELSIF EXISTS(SELECT 1 FROM accounting."JournalLines" l WHERE l."JournalId"=NEW."Id" AND
   (EXISTS(SELECT 1 FROM foreign_exchange."FxAccounts" a WHERE a."CompanyId"=NEW."CompanyId" AND a."AccountId"=l."AccountId"
      AND (NEW."SourceModule"<>'FinancialClose.YearEnd' OR a."Role" NOT BETWEEN 4 AND 7))
    OR EXISTS(SELECT 1 FROM foreign_exchange."FxPositions" p WHERE p."CompanyId"=NEW."CompanyId" AND p."Kind"=6 AND p."AccountId"=l."AccountId")))
   OR EXISTS(SELECT 1 FROM foreign_exchange."FxOperations" WHERE "CompanyId"=NEW."CompanyId" AND "JournalId"=NEW."ReversesJournalId") THEN
   RAISE EXCEPTION 'Use approved FX sources for FX controls and corrections' USING ERRCODE='23514';
 END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION foreign_exchange.statement_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE units integer; book_foreign numeric; book_base numeric;
BEGIN
 SELECT trading.units_v1(c."Code") INTO units FROM foreign_exchange."FxPositions" p
   JOIN foreign_exchange."FxCurrencies" c ON c."Id"=p."CurrencyId"
   WHERE p."CompanyId"=NEW."CompanyId" AND p."Id"=NEW."PositionId" AND p."Kind"=6;
 SELECT coalesce(sum("ForeignDelta"),0),coalesce(sum("BaseDelta"),0) INTO book_foreign,book_base
 FROM foreign_exchange."FxEffects" WHERE "CompanyId"=NEW."CompanyId" AND "PositionId"=NEW."PositionId" AND "Date"<=NEW."Cutoff";
 IF units IS NULL OR NEW."StatementForeign"<>round(NEW."StatementForeign",units) OR
    (book_foreign,book_base) IS DISTINCT FROM (NEW."BookForeign",NEW."BookBase") THEN
   RAISE EXCEPTION 'Foreign statement must retain the correct bank balance at capture' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION foreign_exchange.anchor_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE ids uuid[];
BEGIN
 IF TG_TABLE_SCHEMA='organizations' THEN
   IF NEW."BaseCurrency"<>OLD."BaseCurrency" AND EXISTS(SELECT 1 FROM foreign_exchange."FxConfigurations" WHERE "CompanyId"=OLD."Id") THEN
     RAISE EXCEPTION 'FX evidence freezes company functional currency' USING ERRCODE='23514'; END IF;
   RETURN NEW;
 ELSIF TG_TABLE_SCHEMA='accounting' THEN
   IF (NEW."Classification",NEW."IsPosting",NEW."IsContra",NEW."IsActive") IS NOT DISTINCT FROM
      (OLD."Classification",OLD."IsPosting",OLD."IsContra",OLD."IsActive") THEN RETURN NEW; END IF;
   IF EXISTS(SELECT 1 FROM foreign_exchange."FxAccounts" WHERE "CompanyId"=OLD."CompanyId" AND "AccountId"=OLD."Id")
   OR EXISTS(SELECT 1 FROM foreign_exchange."FxPositions" WHERE "CompanyId"=OLD."CompanyId" AND "AccountId"=OLD."Id" AND "Kind"=6) THEN
     RAISE EXCEPTION 'FX mapped ledger account semantics are protected' USING ERRCODE='23514'; END IF;
   RETURN NEW;
 ELSIF TG_TABLE_SCHEMA='banking' THEN ids:=ARRAY[NEW."LedgerAccountId"];
 ELSIF TG_TABLE_NAME='AssetCategories' THEN ids:=ARRAY[NEW."DepreciationExpenseId",NEW."ImpairmentLossId",NEW."GainId",NEW."LossId"];
 ELSE ids:=ARRAY[NEW."AccountId"];
 END IF;
 IF EXISTS(SELECT 1 FROM foreign_exchange."FxAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=ANY(ids))
 OR EXISTS(SELECT 1 FROM foreign_exchange."FxPositions" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=ANY(ids) AND "Kind"=6) THEN
   RAISE EXCEPTION 'FX ledger accounts cannot be reused as other module mappings' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

DO $$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['FxConfigurations','FxAccounts','FxCurrencies','FxRates','FxPositions','FxOperations','FxEffects','FxBankStatements'] LOOP
   EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON foreign_exchange.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
   EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON foreign_exchange.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['FxConfigurations','FxAccounts','FxCurrencies','FxPositions','FxEffects','FxBankStatements'] LOOP
   EXECUTE format('CREATE TRIGGER b_immutable BEFORE UPDATE OR DELETE ON foreign_exchange.%I FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['FxConfigurations','FxAccounts','FxCurrencies','FxPositions'] LOOP
   EXECUTE format('CREATE TRIGGER b_fx_setup BEFORE INSERT ON foreign_exchange.%I FOR EACH ROW EXECUTE FUNCTION foreign_exchange.setup_guard_v1()',t);
 END LOOP;
END $$;
CREATE CONSTRAINT TRIGGER z_fx_config AFTER INSERT ON foreign_exchange."FxConfigurations" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION foreign_exchange.configuration_contract_v1();
CREATE CONSTRAINT TRIGGER z_fx_accounts AFTER INSERT ON foreign_exchange."FxAccounts" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION foreign_exchange.configuration_contract_v1();
CREATE TRIGGER b_fx_rate BEFORE INSERT OR UPDATE OR DELETE ON foreign_exchange."FxRates" FOR EACH ROW EXECUTE FUNCTION foreign_exchange.rate_guard_v1();
CREATE TRIGGER b_fx_operation BEFORE INSERT OR UPDATE OR DELETE ON foreign_exchange."FxOperations" FOR EACH ROW EXECUTE FUNCTION foreign_exchange.operation_guard_v1();
CREATE TRIGGER b_fx_effect BEFORE INSERT ON foreign_exchange."FxEffects" FOR EACH ROW EXECUTE FUNCTION foreign_exchange.effect_guard_v1();
CREATE TRIGGER b_fx_statement BEFORE INSERT ON foreign_exchange."FxBankStatements" FOR EACH ROW EXECUTE FUNCTION foreign_exchange.statement_guard_v1();
CREATE CONSTRAINT TRIGGER z_fx_operation AFTER INSERT OR UPDATE ON foreign_exchange."FxOperations" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION foreign_exchange.operation_contract_v1();
CREATE CONSTRAINT TRIGGER z_fx_journal AFTER INSERT OR UPDATE ON accounting."Journals" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION foreign_exchange.journal_contract_v1();
CREATE TRIGGER fx_company_currency BEFORE UPDATE ON organizations."Companies" FOR EACH ROW EXECUTE FUNCTION foreign_exchange.anchor_guard_v1();
CREATE TRIGGER fx_account_semantics BEFORE UPDATE ON accounting."Accounts" FOR EACH ROW EXECUTE FUNCTION foreign_exchange.anchor_guard_v1();
CREATE TRIGGER fx_bank_mapping BEFORE INSERT OR UPDATE ON banking."TreasuryAccounts" FOR EACH ROW EXECUTE FUNCTION foreign_exchange.anchor_guard_v1();
CREATE TRIGGER fx_trading_mapping BEFORE INSERT OR UPDATE ON trading."AccountMappings" FOR EACH ROW EXECUTE FUNCTION foreign_exchange.anchor_guard_v1();
CREATE TRIGGER fx_inventory_mapping BEFORE INSERT OR UPDATE ON inventory."InventoryAccounts" FOR EACH ROW EXECUTE FUNCTION foreign_exchange.anchor_guard_v1();
CREATE TRIGGER fx_asset_mapping BEFORE INSERT OR UPDATE ON fixed_assets."AssetControlAccounts" FOR EACH ROW EXECUTE FUNCTION foreign_exchange.anchor_guard_v1();
CREATE TRIGGER fx_asset_category BEFORE INSERT OR UPDATE ON fixed_assets."AssetCategories" FOR EACH ROW EXECUTE FUNCTION foreign_exchange.anchor_guard_v1();
""";

    public const string Down = """
DROP FUNCTION foreign_exchange.anchor_guard_v1() CASCADE;
DROP FUNCTION foreign_exchange.journal_contract_v1() CASCADE;
DROP FUNCTION foreign_exchange.statement_guard_v1() CASCADE;
DROP FUNCTION foreign_exchange.operation_contract_v1() CASCADE;
DROP FUNCTION foreign_exchange.effect_guard_v1() CASCADE;
DROP FUNCTION foreign_exchange.operation_guard_v1() CASCADE;
DROP FUNCTION foreign_exchange.rate_guard_v1() CASCADE;
DROP FUNCTION foreign_exchange.configuration_contract_v1() CASCADE;
DROP FUNCTION foreign_exchange.setup_guard_v1() CASCADE;
DROP FUNCTION foreign_exchange.account_dedicated_v1(uuid,uuid,integer);
""";
}
