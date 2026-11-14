namespace RAQM.Api.Data.Migrations;

internal static class InventoryProcurementV1Sql
{
    public const string Up = """
CREATE FUNCTION inventory.operation_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Stock operations are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."State"<>0 OR NEW."PlanJson" IS NOT NULL OR NEW."ApprovedBy" IS NOT NULL OR
   NEW."PostedBy" IS NOT NULL OR NEW."JournalId" IS NOT NULL OR NEW."Accrual"<>0 OR NEW."SourceQuantity"<>0) THEN
   RAISE EXCEPTION 'Stock operation must start as draft' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (
   (to_jsonb(NEW)-ARRAY['State','Version','PlanJson','ApprovedBy','PostedBy','JournalId','Sequence','Accrual','SourceQuantity']) IS DISTINCT FROM
   (to_jsonb(OLD)-ARRAY['State','Version','PlanJson','ApprovedBy','PostedBy','JournalId','Sequence','Accrual','SourceQuantity'])
   OR NOT ((OLD."State"=0 AND NEW."State" IN(1,3)) OR (OLD."State"=1 AND NEW."State" IN(1,2,3)))
   OR (OLD."State"=1 AND (NEW."ApprovedBy",NEW."PlanJson") IS DISTINCT FROM (OLD."ApprovedBy",OLD."PlanJson"))) THEN
   RAISE EXCEPTION 'Invalid stock operation transition or change to retained evidence' USING ERRCODE='23514'; END IF;
 IF NEW."State" IN (1,2) AND (NEW."ApprovedBy" IS NULL OR NEW."CreatedBy"=NEW."ApprovedBy" OR
   jsonb_typeof(NEW."PlanJson"->'Lines') IS DISTINCT FROM 'array' OR jsonb_typeof(NEW."PlanJson"->'Movements') IS DISTINCT FROM 'array') THEN
   RAISE EXCEPTION 'Stock operation requires independent approval and a plan' USING ERRCODE='23514'; END IF;
 IF NEW."State"=2 AND (
   NOT EXISTS(SELECT 1 FROM accounting."Periods" WHERE "CompanyId"=NEW."CompanyId" AND "State"=0 AND NEW."Date" BETWEEN "StartDate" AND "EndDate")
   OR EXISTS(SELECT 1 FROM inventory."Operations" WHERE "CompanyId"=NEW."CompanyId" AND "State"=2 AND "Date">NEW."Date")
   OR EXISTS(SELECT 1 FROM financial_close."YearEndRecords" r JOIN accounting."FiscalYears" y ON y."Id"=r."FiscalYearId"
     WHERE r."CompanyId"=NEW."CompanyId" AND r."Finalized" AND y."EndDate">=NEW."Date")) THEN
   RAISE EXCEPTION 'Stock operation requires an open chronological accounting date' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION inventory.movement_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM inventory."Operations" o WHERE o."CompanyId"=NEW."CompanyId" AND o."Id"=NEW."OperationId"
   AND o."ItemId"=NEW."ItemId" AND o."Date"=NEW."Date" AND o."State"=1 AND o."PostedBy" IS NOT NULL) THEN
   RAISE EXCEPTION 'Stock movement requires a staged approved operation' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION inventory.operation_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE o inventory."Operations"%ROWTYPE; module text; expected jsonb;
BEGIN
 SELECT * INTO STRICT o FROM inventory."Operations" WHERE "Id"=NEW."Id";
 IF o."State"<>2 THEN
   IF o."PostedBy" IS NOT NULL OR o."JournalId" IS NOT NULL OR EXISTS(SELECT 1 FROM inventory."StockMovements" WHERE "OperationId"=o."Id") THEN
     RAISE EXCEPTION 'Staged stock evidence must finalize before commit' USING ERRCODE='23514';
   END IF;
   RETURN NULL;
 END IF;
 module:='Inventory.'||(ARRAY['Receipt','MatchBill','Issue','Transfer','Reserve','Release','Count','Opening','WriteDown',
   'ReceiptCorrection','CustomerReturn','SupplierReturn','LandedCost'])[o."Kind"+1];
 expected:=o."PlanJson"->'Lines';
 IF (jsonb_array_length(expected)>0)<>(o."JournalId" IS NOT NULL) OR
   o."Accrual" IS DISTINCT FROM (o."PlanJson"->>'Accrual')::numeric OR
   o."SourceQuantity" IS DISTINCT FROM (o."PlanJson"->>'SourceQuantity')::numeric THEN
   RAISE EXCEPTION 'Stock operation must finalize its approved plan' USING ERRCODE='23514'; END IF;
 IF o."JournalId" IS NOT NULL THEN
   IF NOT EXISTS(SELECT 1 FROM accounting."Journals" j WHERE j."Id"=o."JournalId" AND j."CompanyId"=o."CompanyId"
     AND j."State"=1 AND j."SourceModule"=module AND j."SourceDocumentId"=o."Id" AND j."PostingDate"=o."Date" AND j."PostedBy"=o."PostedBy") THEN
     RAISE EXCEPTION 'Inventory journal linkage is invalid' USING ERRCODE='23514'; END IF;
   PERFORM financial_close.assert_lines_v1(o."JournalId",expected);
 END IF;
 IF (o."Kind" IN(1,10) OR (o."Kind"=11 AND o."TradeDocumentId" IS NOT NULL)) AND
   NOT EXISTS(SELECT 1 FROM trading."Documents" d JOIN trading."DocumentLines" l ON l."DocumentId"=d."Id"
     WHERE d."CompanyId"=o."CompanyId" AND l."CompanyId"=o."CompanyId" AND d."Id"=o."TradeDocumentId" AND l."Id"=o."TradeLineId"
       AND d."State"=2 AND d."PostingDate"=o."Date" AND d."PostedBy"=o."PostedBy"
       AND d."PostKey"='inventory:'||replace(o."Id"::text,'-','') AND d."Kind"=CASE o."Kind" WHEN 1 THEN 1 WHEN 10 THEN 2 ELSE 3 END) THEN
   RAISE EXCEPTION 'Matched trade evidence must post atomically with stock' USING ERRCODE='23514'; END IF;
 IF EXISTS(
   WITH expected_movements AS (
     SELECT ordinal, (payload->>'WarehouseId')::uuid warehouse, (payload->>'Quantity')::numeric quantity, (payload->>'Gross')::numeric gross,
       (payload->>'Allowance')::numeric allowance, (payload->>'Reserved')::numeric reserved, payload->'Lots' lots
     FROM jsonb_array_elements(o."PlanJson"->'Movements') WITH ORDINALITY AS m(payload, ordinal)),
   actual AS (SELECT * FROM inventory."StockMovements" WHERE "OperationId"=o."Id")
   SELECT 1 FROM expected_movements e FULL JOIN actual a ON e.ordinal=a."Number"
     WHERE (e.ordinal,e.warehouse,e.quantity,e.gross,e.allowance,e.reserved,e.lots,o."ItemId",o."Date",o."Sequence")
       IS DISTINCT FROM (a."Number"::bigint,a."WarehouseId",a."Quantity",a."Gross",a."Allowance",a."Reserved",a."LotsJson",a."ItemId",a."Date",a."Sequence")
 ) THEN RAISE EXCEPTION 'Stock movements differ from the approved plan' USING ERRCODE='23514'; END IF;
 IF o."Kind"=1 THEN
   IF jsonb_typeof(o."PlanJson"->'Claims') IS DISTINCT FROM 'array' OR jsonb_array_length(o."PlanJson"->'Claims')=0 THEN
     RAISE EXCEPTION 'Invoice matching requires receipt allocations' USING ERRCODE='23514'; END IF;
   IF EXISTS(
     WITH claims AS (SELECT (p->>'ReceiptId')::uuid receipt, (p->>'Quantity')::numeric quantity,
       (p->>'Accrual')::numeric accrual, (p->>'InvoiceNet')::numeric invoice FROM jsonb_array_elements(o."PlanJson"->'Claims') p)
     SELECT 1 FROM claims c LEFT JOIN inventory."Operations" r ON r."Id"=c.receipt AND r."CompanyId"=o."CompanyId"
       LEFT JOIN inventory."Operations" anchor ON anchor."Id"=o."RelatedOperationId" AND anchor."CompanyId"=o."CompanyId"
     WHERE r."Id" IS NULL OR r."Kind"<>0 OR r."State"<>2 OR r."ItemId"<>o."ItemId" OR r."WarehouseId"<>o."WarehouseId"
       OR r."Sequence">=o."Sequence" OR r."OrderLineId" IS DISTINCT FROM anchor."OrderLineId"
       OR c.quantity IS NULL OR c.quantity<=0 OR c.accrual IS NULL OR c.accrual<0 OR c.invoice IS NULL OR c.invoice<0
     UNION ALL
     SELECT 1 FROM claims HAVING count(DISTINCT receipt)<>count(*) OR sum(quantity) IS DISTINCT FROM o."SourceQuantity"
       OR sum(accrual) IS DISTINCT FROM o."Accrual" OR sum(invoice) IS DISTINCT FROM (o."PayloadJson"->>'Amount')::numeric
   ) THEN RAISE EXCEPTION 'Invalid invoice receipt allocation' USING ERRCODE='23514'; END IF;
 END IF;
 IF EXISTS(SELECT 1 FROM inventory."StockMovements" m
   WHERE m."CompanyId"=o."CompanyId" AND m."ItemId"=o."ItemId" AND m."Sequence"<=o."Sequence"
     AND m."WarehouseId" IN (SELECT "WarehouseId" FROM inventory."StockMovements" WHERE "OperationId"=o."Id")
   GROUP BY m."WarehouseId" HAVING sum(m."Reserved")<0 OR sum(m."Quantity")<sum(m."Reserved") OR sum(m."Allowance")<0
     OR sum(m."Gross")<sum(m."Allowance") OR (sum(m."Quantity")=0 AND sum(m."Gross")<>0)) THEN
   RAISE EXCEPTION 'Stock movement violates quantity or carrying-value invariants' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION inventory.journal_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW."State"<>1 THEN RETURN NULL; END IF;
 IF NEW."SourceModule" LIKE 'Inventory.%' THEN
   IF NOT EXISTS(SELECT 1 FROM inventory."Operations" o WHERE o."CompanyId"=NEW."CompanyId" AND o."Id"=NEW."SourceDocumentId"
     AND o."JournalId"=NEW."Id" AND o."State"=2) THEN
     RAISE EXCEPTION 'Inventory journal requires a finalized stock source' USING ERRCODE='23514'; END IF;
 ELSIF EXISTS(SELECT 1 FROM accounting."JournalLines" l JOIN inventory."InventoryAccounts" a
     ON a."CompanyId"=l."CompanyId" AND a."AccountId"=l."AccountId"
     WHERE l."JournalId"=NEW."Id" AND a."Role" BETWEEN 0 AND 3 AND
       NOT ((a."Role"=0 AND NEW."SourceModule"='OpeningBalance') OR
         (a."Role"=2 AND NEW."SourceModule" IN('AP.Bill','AP.Credit') AND EXISTS(
           SELECT 1 FROM inventory."Operations" o WHERE o."CompanyId"=NEW."CompanyId" AND o."TradeDocumentId"=NEW."SourceDocumentId"
             AND o."State"=2 AND o."PostedBy"=NEW."PostedBy" AND o."Date"=NEW."PostingDate"
             AND o."Kind"=CASE WHEN NEW."SourceModule"='AP.Bill' THEN 1 ELSE 11 END)))) THEN
   RAISE EXCEPTION 'Inventory control cannot be posted outside an eligible source' USING ERRCODE='23514';
 END IF;
 IF NEW."ReversesJournalId" IS NOT NULL AND EXISTS(
   SELECT 1 FROM inventory."Operations" o LEFT JOIN accounting."JournalLines" l ON l."Id"=o."SourceJournalLineId"
   WHERE o."CompanyId"=NEW."CompanyId" AND o."State"=2 AND
     (o."JournalId"=NEW."ReversesJournalId" OR l."JournalId"=NEW."ReversesJournalId")) THEN
   RAISE EXCEPTION 'Use an explicit inventory correction for linked journal evidence' USING ERRCODE='23514'; END IF;
 IF NEW."SourceModule"='AR.Credit' AND EXISTS(
   SELECT 1 FROM trading."DocumentLines" credit JOIN inventory."Operations" issue ON issue."TradeLineId"=credit."OriginalLineId"
   WHERE credit."CompanyId"=NEW."CompanyId" AND issue."CompanyId"=NEW."CompanyId" AND credit."DocumentId"=NEW."SourceDocumentId"
     AND issue."Kind"=2 AND issue."State"=2)
   AND NOT EXISTS(SELECT 1 FROM inventory."Operations" o WHERE o."CompanyId"=NEW."CompanyId" AND o."TradeDocumentId"=NEW."SourceDocumentId"
     AND o."Kind"=10 AND o."State"=2 AND o."PostedBy"=NEW."PostedBy" AND o."Date"=NEW."PostingDate") THEN
   RAISE EXCEPTION 'Credit on fulfilled stock requires a finalized return' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION inventory.procurement_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Procurement documents are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."State"<>0 OR NEW."ApprovedBy" IS NOT NULL) THEN
   RAISE EXCEPTION 'Procurement documents must start as draft' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (
   (to_jsonb(NEW)-ARRAY['State','Version','ApprovedBy']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['State','Version','ApprovedBy'])
   OR NOT ((OLD."State"=0 AND NEW."State" IN(1,3,4)) OR (OLD."State"=1 AND NEW."State" IN(2,3))
     OR (OLD."State"=2 AND NEW."State"=3))
   OR (NEW."State" IN (1,2) AND (NEW."ApprovedBy" IS NULL OR NEW."ApprovedBy"=NEW."CreatedBy"))
   OR (OLD."ApprovedBy" IS NOT NULL AND OLD."ApprovedBy" IS DISTINCT FROM NEW."ApprovedBy")) THEN
   RAISE EXCEPTION 'Invalid procurement transition or change to retained terms' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION inventory.procurement_line_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM inventory."ProcurementDocuments" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."DocumentId" AND "State"=0) THEN
   RAISE EXCEPTION 'Procurement lines require a draft document' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION inventory.master_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Inventory master history is retained' USING ERRCODE='23514'; END IF;
 IF (to_jsonb(NEW)-'IsActive') IS DISTINCT FROM (to_jsonb(OLD)-'IsActive') THEN
   RAISE EXCEPTION 'Inventory master terms are retained' USING ERRCODE='23514'; END IF;
 IF NOT NEW."IsActive" AND (EXISTS(SELECT 1 FROM inventory."StockMovements" m
   WHERE m."CompanyId"=OLD."CompanyId" AND CASE WHEN TG_TABLE_NAME='Items' THEN m."ItemId"=OLD."Id" ELSE m."WarehouseId"=OLD."Id" END
   GROUP BY m."CompanyId" HAVING sum(m."Quantity")<>0) OR EXISTS(SELECT 1 FROM inventory."Operations" o
   WHERE o."CompanyId"=OLD."CompanyId" AND o."State" IN(0,1) AND
     CASE WHEN TG_TABLE_NAME='Items' THEN o."ItemId"=OLD."Id" ELSE o."WarehouseId"=OLD."Id" END)) THEN
   RAISE EXCEPTION 'Clear stock and pending operations before deactivation' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION inventory.discrepancy_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Procurement discrepancies are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND (NEW."ResolutionDate" IS NOT NULL OR NOT EXISTS(
   SELECT 1 FROM inventory."ProcurementLines" l JOIN inventory."ProcurementDocuments" d ON d."Id"=l."DocumentId"
   WHERE l."CompanyId"=NEW."CompanyId" AND d."CompanyId"=NEW."CompanyId" AND l."Id"=NEW."OrderLineId"
     AND d."IsOrder" AND d."State"=2 AND d."Date"<=NEW."Date") OR
   (NEW."ReceiptId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM inventory."Operations" r WHERE r."Id"=NEW."ReceiptId"
     AND r."CompanyId"=NEW."CompanyId" AND r."Kind"=0 AND r."State"=2 AND r."OrderLineId"=NEW."OrderLineId" AND r."Date"<=NEW."Date"))) THEN
   RAISE EXCEPTION 'Discrepancy requires confirmed PO evidence' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (OLD."ResolutionDate" IS NOT NULL OR NEW."ResolutionDate" IS NULL OR
   (to_jsonb(NEW)-ARRAY['ResolutionDate','Resolution','ResolvedBy']) IS DISTINCT FROM
   (to_jsonb(OLD)-ARRAY['ResolutionDate','Resolution','ResolvedBy'])) THEN
   RAISE EXCEPTION 'Only the first discrepancy resolution is allowed' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION inventory.mapping_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_TABLE_SCHEMA='inventory' THEN
   IF NOT EXISTS(SELECT 1 FROM accounting."Accounts" a WHERE a."CompanyId"=NEW."CompanyId" AND a."Id"=NEW."AccountId"
     AND a."IsActive" AND a."IsPosting" AND a."IsContra"=(NEW."Role"=3)
     AND a."Classification"=CASE WHEN NEW."Role" IN(0,2,3) THEN 0 WHEN NEW."Role"=1 THEN 1
       WHEN NEW."Role"=9 THEN 2 WHEN NEW."Role"=5 THEN 3 ELSE 4 END)
   OR EXISTS(SELECT 1 FROM trading."AccountMappings" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=NEW."AccountId")
   OR EXISTS(SELECT 1 FROM banking."TreasuryAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "LedgerAccountId"=NEW."AccountId")
   OR EXISTS(SELECT 1 FROM fixed_assets."AssetControlAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=NEW."AccountId") THEN
     RAISE EXCEPTION 'Inventory mapping is ineligible or already in use' USING ERRCODE='23514'; END IF;
 ELSIF TG_TABLE_SCHEMA='organizations' THEN
   IF OLD."BaseCurrency"<>NEW."BaseCurrency" AND EXISTS(SELECT 1 FROM inventory."Policies" WHERE "CompanyId"=OLD."Id") THEN
     RAISE EXCEPTION 'Inventory policy freezes company currency' USING ERRCODE='23514'; END IF;
 ELSIF TG_TABLE_SCHEMA='accounting' THEN
   IF (NEW."Classification",NEW."IsPosting",NEW."IsContra",NEW."IsActive") IS DISTINCT FROM
      (OLD."Classification",OLD."IsPosting",OLD."IsContra",OLD."IsActive")
     AND EXISTS(SELECT 1 FROM inventory."InventoryAccounts" WHERE "CompanyId"=OLD."CompanyId" AND "AccountId"=OLD."Id") THEN
     RAISE EXCEPTION 'Inventory account semantics are protected' USING ERRCODE='23514'; END IF;
 ELSIF TG_TABLE_SCHEMA='banking' THEN
   IF EXISTS(SELECT 1 FROM inventory."InventoryAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=NEW."LedgerAccountId") THEN
     RAISE EXCEPTION 'Inventory accounts cannot be treasury accounts' USING ERRCODE='23514'; END IF;
 ELSE
   IF EXISTS(SELECT 1 FROM inventory."InventoryAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=NEW."AccountId") THEN
     RAISE EXCEPTION 'Inventory accounts cannot be used for trading or fixed-asset controls' USING ERRCODE='23514'; END IF;
 END IF;
 RETURN NEW;
END $$;

DO $$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['Policies','InventoryAccounts','Categories','Items','Warehouses','ProcurementDocuments','ProcurementLines',
   'ProcurementDiscrepancies','Operations','StockMovements'] LOOP
   EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON inventory.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
   EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON inventory.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['Policies','InventoryAccounts','Categories','ProcurementLines','StockMovements'] LOOP
   EXECUTE format('CREATE TRIGGER b_immutable BEFORE UPDATE OR DELETE ON inventory.%I FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
END $$;
CREATE TRIGGER b_inventory_operation BEFORE INSERT OR UPDATE OR DELETE ON inventory."Operations" FOR EACH ROW EXECUTE FUNCTION inventory.operation_guard_v1();
CREATE TRIGGER b_inventory_movement BEFORE INSERT ON inventory."StockMovements" FOR EACH ROW EXECUTE FUNCTION inventory.movement_guard_v1();
CREATE TRIGGER b_inventory_procurement BEFORE INSERT OR UPDATE OR DELETE ON inventory."ProcurementDocuments" FOR EACH ROW EXECUTE FUNCTION inventory.procurement_guard_v1();
CREATE TRIGGER b_inventory_procurement_line BEFORE INSERT ON inventory."ProcurementLines" FOR EACH ROW EXECUTE FUNCTION inventory.procurement_line_guard_v1();
CREATE TRIGGER b_inventory_discrepancy BEFORE INSERT OR UPDATE OR DELETE ON inventory."ProcurementDiscrepancies" FOR EACH ROW EXECUTE FUNCTION inventory.discrepancy_guard_v1();
CREATE TRIGGER b_inventory_item BEFORE UPDATE OR DELETE ON inventory."Items" FOR EACH ROW EXECUTE FUNCTION inventory.master_guard_v1();
CREATE TRIGGER b_inventory_warehouse BEFORE UPDATE OR DELETE ON inventory."Warehouses" FOR EACH ROW EXECUTE FUNCTION inventory.master_guard_v1();
CREATE TRIGGER b_inventory_mapping BEFORE INSERT ON inventory."InventoryAccounts" FOR EACH ROW EXECUTE FUNCTION inventory.mapping_guard_v1();
CREATE TRIGGER inventory_account_semantics BEFORE UPDATE ON accounting."Accounts" FOR EACH ROW EXECUTE FUNCTION inventory.mapping_guard_v1();
CREATE TRIGGER inventory_company_currency BEFORE UPDATE ON organizations."Companies" FOR EACH ROW EXECUTE FUNCTION inventory.mapping_guard_v1();
CREATE TRIGGER inventory_bank_mapping BEFORE INSERT OR UPDATE ON banking."TreasuryAccounts" FOR EACH ROW EXECUTE FUNCTION inventory.mapping_guard_v1();
CREATE TRIGGER inventory_trading_mapping BEFORE INSERT OR UPDATE ON trading."AccountMappings" FOR EACH ROW EXECUTE FUNCTION inventory.mapping_guard_v1();
CREATE TRIGGER inventory_asset_mapping BEFORE INSERT OR UPDATE ON fixed_assets."AssetControlAccounts" FOR EACH ROW EXECUTE FUNCTION inventory.mapping_guard_v1();
CREATE CONSTRAINT TRIGGER z_inventory_operation AFTER INSERT OR UPDATE ON inventory."Operations" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inventory.operation_contract_v1();
CREATE CONSTRAINT TRIGGER z_inventory_journal AFTER INSERT OR UPDATE ON accounting."Journals" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inventory.journal_contract_v1();
""";

    public const string Down = """
DROP FUNCTION inventory.journal_contract_v1() CASCADE;
DROP FUNCTION inventory.operation_contract_v1() CASCADE;
DROP FUNCTION inventory.mapping_guard_v1() CASCADE;
DROP FUNCTION inventory.discrepancy_guard_v1() CASCADE;
DROP FUNCTION inventory.master_guard_v1() CASCADE;
DROP FUNCTION inventory.procurement_line_guard_v1() CASCADE;
DROP FUNCTION inventory.procurement_guard_v1() CASCADE;
DROP FUNCTION inventory.movement_guard_v1() CASCADE;
DROP FUNCTION inventory.operation_guard_v1() CASCADE;
""";
}
