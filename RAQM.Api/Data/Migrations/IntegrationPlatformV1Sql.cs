namespace RAQM.Api.Data.Migrations;

internal static class IntegrationPlatformV1Sql
{
    public const string Up = """
CREATE FUNCTION integration.capture_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE row_data jsonb; kind integer; states integer[]; event_id uuid; happened timestamptz; source_version uuid;
        cause uuid; trace text; event_name text; payload text;
BEGIN
 states:=string_to_array(TG_ARGV[0],',')::integer[];
 IF NOT NEW."State"=ANY(states) THEN RETURN NEW; END IF;
 IF TG_OP='UPDATE' THEN
   IF OLD."State"=ANY(states) THEN RETURN NEW; END IF;
 END IF;
 row_data:=to_jsonb(NEW);
 kind:=(row_data->>'Kind')::integer;
 IF TG_NARGS>2 AND kind IS DISTINCT FROM TG_ARGV[2]::integer THEN RETURN NEW; END IF;
 event_id:=gen_random_uuid();
 happened:=clock_timestamp();
 event_name:=TG_ARGV[1];
 source_version:=CASE WHEN jsonb_typeof(row_data->'Version')='string' THEN (row_data->>'Version')::uuid ELSE NEW."Id" END;
 IF event_name='payment.settled' AND (row_data->>'IsRefund')::boolean THEN RETURN NEW; END IF;
 cause:=coalesce((row_data->>'JournalId')::uuid,(row_data->>'SourceDocumentId')::uuid);
 trace:=coalesce(current_setting('raqm.trace_id',true),'');
 payload:=jsonb_build_object('id',event_id,'type',event_name,'version',1,'tenantId',NEW."TenantId",
    'organizationId',NEW."OrganizationId",'companyId',NEW."CompanyId",'sourceId',NEW."Id",'sourceVersion',source_version,
    'occurredAt',happened,'correlationId',trace,'causationId',cause)::text;
 INSERT INTO integration."Events" ("Id","TenantId","OrganizationId","CompanyId","SourceId","SourceVersion","CausationId",
    "Type","Version","OccurredAt","Correlation","Payload")
 VALUES(event_id,NEW."TenantId",NEW."OrganizationId",NEW."CompanyId",NEW."Id",source_version,cause,event_name,1,happened,trace,payload);
 INSERT INTO integration."Deliveries" ("Id","TenantId","OrganizationId","CompanyId","EventId","SubscriptionId","State","Attempts","Generation","DueAt")
 SELECT gen_random_uuid(),NEW."TenantId",NEW."OrganizationId",NEW."CompanyId",event_id,s."Id",0,0,0,happened
 FROM integration."Subscriptions" s JOIN integration."Applications" a ON a."Id"=s."ApplicationId" AND a."CompanyId"=s."CompanyId"
 WHERE s."CompanyId"=NEW."CompanyId" AND s."Active" AND s."ApprovedBy" IS NOT NULL AND event_name=ANY(s."Events")
   AND a."State"=1 AND 'webhooks.receive'=ANY(a."Scopes");
 RETURN NEW;
END $$;
CREATE TRIGGER integration_journal AFTER INSERT OR UPDATE ON accounting."Journals" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('1','journal.posted');
CREATE TRIGGER integration_invoice AFTER INSERT OR UPDATE ON trading."Documents" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('2','invoice.posted','0');
CREATE TRIGGER integration_bill AFTER INSERT OR UPDATE ON trading."Documents" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('2','supplier-bill.posted','1');
CREATE TRIGGER integration_payment AFTER INSERT OR UPDATE ON trading."Payments" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('1','payment.settled');
CREATE TRIGGER integration_reconciliation AFTER INSERT OR UPDATE ON banking."Sessions" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('2','bank-reconciliation.finalized');
CREATE TRIGGER integration_receipt AFTER INSERT OR UPDATE ON inventory."Operations" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('2','inventory-receipt.posted','0');
CREATE TRIGGER integration_issue AFTER INSERT OR UPDATE ON inventory."Operations" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('2','inventory-issue.posted','2');
CREATE TRIGGER integration_asset AFTER INSERT OR UPDATE ON fixed_assets."AssetOperations" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('2','asset-capitalized','0');
CREATE TRIGGER integration_depreciation AFTER INSERT OR UPDATE ON fixed_assets."AssetOperations" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('2','depreciation.posted','3');
CREATE TRIGGER integration_period AFTER INSERT OR UPDATE ON accounting."Periods" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('1,2','period.closed');
CREATE TRIGGER integration_revaluation AFTER INSERT OR UPDATE ON foreign_exchange."FxOperations" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('2','fx-revaluation.posted','7');
CREATE TRIGGER integration_consolidation AFTER INSERT OR UPDATE ON groups."ConsolidationRuns" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('2','consolidation.published');
CREATE TRIGGER integration_tax AFTER INSERT OR UPDATE ON tax."Returns" FOR EACH ROW EXECUTE FUNCTION integration.capture_v1('1','tax-return.approved');
CREATE TRIGGER integration_events_immutable BEFORE UPDATE OR DELETE ON integration."Events" FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1();
CREATE TRIGGER integration_attempts_immutable BEFORE UPDATE OR DELETE ON integration."Attempts" FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1();
CREATE TRIGGER integration_events_no_truncate BEFORE TRUNCATE ON integration."Events" FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1();
CREATE TRIGGER integration_attempts_no_truncate BEFORE TRUNCATE ON integration."Attempts" FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1();
CREATE FUNCTION integration.inbox_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."State"=0 AND NEW."ApprovedBy" IS NULL AND NEW."Result" IS NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF to_jsonb(OLD)-ARRAY['State','ApprovedBy','Result']=to_jsonb(NEW)-ARRAY['State','ApprovedBy','Result'] AND
     ((OLD."State"=0 AND NEW."State"=1 AND NEW."ApprovedBy" IS NOT NULL AND NEW."Result" IS NULL) OR
      (OLD."State"=1 AND NEW."State"=2 AND NEW."ApprovedBy"=OLD."ApprovedBy" AND NEW."Result" IS NOT NULL) OR
      (OLD."State" IN(0,1) AND NEW."State"=3 AND NEW."ApprovedBy" IS NOT DISTINCT FROM OLD."ApprovedBy" AND NEW."Result" IS NULL))
     THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Inbound identity and processed result must be retained' USING ERRCODE='23514';
END $$;
CREATE TRIGGER integration_inbox BEFORE INSERT OR UPDATE OR DELETE ON integration."Inbox" FOR EACH ROW EXECUTE FUNCTION integration.inbox_guard_v1();
CREATE TRIGGER integration_inbox_no_truncate BEFORE TRUNCATE ON integration."Inbox" FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1();
CREATE TRIGGER integration_master_claim_immutable BEFORE UPDATE OR DELETE ON integration."ImportedMasters" FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1();
CREATE TRIGGER integration_master_claim_no_truncate BEFORE TRUNCATE ON integration."ImportedMasters" FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1();
""";

    public const string Down = """
DROP FUNCTION integration.capture_v1() CASCADE;
DROP FUNCTION integration.inbox_guard_v1() CASCADE;
""";
}
