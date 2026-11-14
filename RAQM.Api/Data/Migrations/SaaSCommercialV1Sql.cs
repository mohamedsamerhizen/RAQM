namespace RAQM.Api.Data.Migrations;

internal static class SaaSCommercialV1Sql
{
    private static string Scope(bool enabled)
    {
        const string start = "CREATE FUNCTION accounting.scope_guard_v1()";
        const string end = "CREATE FUNCTION accounting.account_guard_v1()";
        var source = AccountingCoreV1Sql.Up;
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        var original = source[from..to].Replace(start, "CREATE OR REPLACE FUNCTION accounting.scope_guard_v1()", StringComparison.Ordinal);
        const string marker = """  PERFORM 1 FROM organizations."Companies" """;
        if (!original.Contains(marker, StringComparison.Ordinal)) throw new InvalidOperationException("The existing company lock guard was not found.");
        return enabled ? original.Replace(marker, """
  IF TG_OP='DELETE' THEN PERFORM saas.assert_write_v1(OLD."TenantId"); ELSE PERFORM saas.assert_write_v1(NEW."TenantId"); END IF;
""" + "\n" + marker, StringComparison.Ordinal) : original;
    }

    public static string Up => Definitions + "\n" + Scope(true);
    public static string Down => Scope(false) + "\n" + Drops;

    private const string Definitions = """
CREATE FUNCTION saas.can_operate_v1(t uuid) RETURNS boolean LANGUAGE sql VOLATILE AS $$
 SELECT NOT EXISTS(SELECT 1 FROM saas."Accounts" a LEFT JOIN saas."Subscriptions" s ON s."TenantId"=a."TenantId"
   WHERE a."TenantId"=t AND (a."State"<>0 OR a."SuspendAt"<=clock_timestamp() OR s."Id" IS NULL
     OR s."Cancelled" OR s."AccessUntil"<=clock_timestamp()
     OR (s."CancelAtEnd" AND s."End"<=(clock_timestamp() AT TIME ZONE 'UTC')::date)))
$$;

CREATE FUNCTION saas.assert_write_v1(t uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
 PERFORM 1 FROM organizations."Tenants" WHERE "Id"=t FOR SHARE;
 IF NOT saas.can_operate_v1(t) THEN
   RAISE EXCEPTION 'Commercial account is restricted' USING ERRCODE='P1401';
 END IF;
END $$;

CREATE FUNCTION saas.resource_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE p saas."Plans"%ROWTYPE; used bigint; capacity integer; required text;
BEGIN
 IF TG_OP='DELETE' THEN
   PERFORM saas.assert_write_v1(OLD."TenantId");
   RETURN OLD;
 END IF;
 PERFORM saas.assert_write_v1(NEW."TenantId");
 IF TG_OP<>'INSERT' THEN RETURN NEW; END IF;
 SELECT pl.* INTO p FROM saas."Subscriptions" s JOIN saas."Plans" pl ON pl."Id"=s."PlanId" WHERE s."TenantId"=NEW."TenantId";
 IF NOT FOUND THEN RETURN NEW; END IF;
 required:=TG_ARGV[0];
 IF required<>'' AND NOT required=ANY(p."Features") THEN
   RAISE EXCEPTION 'This plan has no matching feature' USING ERRCODE='P1403';
 END IF;
 IF TG_NARGS<2 THEN RETURN NEW; END IF;
 UPDATE saas."Accounts" SET "QuotaEpoch"="QuotaEpoch"+1 WHERE "TenantId"=NEW."TenantId";
 CASE TG_ARGV[1]
 WHEN 'company' THEN capacity:=p."CompanyLimit"; SELECT count(*) INTO used FROM organizations."Companies" WHERE "TenantId"=NEW."TenantId";
 WHEN 'seat' THEN
   IF EXISTS(SELECT 1 FROM organizations."Members" WHERE "TenantId"=NEW."TenantId" AND "UserId"=NEW."UserId") THEN RETURN NEW; END IF;
   capacity:=p."SeatLimit"; SELECT count(DISTINCT "UserId") INTO used FROM organizations."Members" WHERE "TenantId"=NEW."TenantId";
 WHEN 'warehouse' THEN capacity:=p."WarehouseLimit"; SELECT count(*) INTO used FROM inventory."Warehouses" WHERE "TenantId"=NEW."TenantId";
 WHEN 'integration' THEN capacity:=p."IntegrationLimit"; SELECT count(*) INTO used FROM integration."Applications" WHERE "TenantId"=NEW."TenantId" AND "State"<>3;
 WHEN 'webhook' THEN capacity:=p."WebhookLimit"; SELECT count(*) INTO used FROM integration."Subscriptions" WHERE "TenantId"=NEW."TenantId";
 ELSE RAISE EXCEPTION 'Unrecognized quota guard';
 END CASE;
 IF used>=capacity THEN RAISE EXCEPTION 'Commercial quota exhausted' USING ERRCODE='P1402'; END IF;
 RETURN NEW;
END $$;

CREATE TRIGGER saas_company BEFORE INSERT OR UPDATE OR DELETE ON organizations."Companies" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('','company');
CREATE TRIGGER saas_member BEFORE INSERT OR UPDATE OR DELETE ON organizations."Members" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('','seat');
CREATE TRIGGER saas_warehouse BEFORE INSERT ON inventory."Warehouses" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('inventory','warehouse');
CREATE TRIGGER saas_app BEFORE INSERT ON integration."Applications" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('integrations','integration');
CREATE TRIGGER saas_webhook BEFORE INSERT ON integration."Subscriptions" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('webhooks','webhook');
CREATE TRIGGER saas_schedule BEFORE INSERT ON integration."Schedules" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('exports');
CREATE TRIGGER saas_asset BEFORE INSERT ON fixed_assets."AssetOperations" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('fixed-assets');
CREATE TRIGGER saas_stock BEFORE INSERT ON inventory."Operations" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('inventory');
CREATE TRIGGER saas_budget BEFORE INSERT ON planning."BudgetVersions" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('budgeting');
CREATE TRIGGER saas_fx BEFORE INSERT ON foreign_exchange."FxOperations" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('foreign-exchange');
CREATE TRIGGER saas_dimension BEFORE INSERT ON analytics."DimensionDefinitions" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('dimensions');
CREATE TRIGGER saas_consolidation BEFORE INSERT ON groups."ConsolidationRuns" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('consolidation');
CREATE TRIGGER saas_tax BEFORE INSERT ON tax."Registrations" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('tax');
CREATE TRIGGER saas_governance BEFORE INSERT ON governance."Policies" FOR EACH ROW EXECUTE FUNCTION saas.resource_guard_v1('governance');

CREATE FUNCTION saas.plan_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."PublishedBy" IS NULL AND NEW."PublishedAt" IS NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF OLD."PublishedAt" IS NULL AND NEW."PublishedAt" IS NOT NULL AND NEW."CreatedBy"<>NEW."PublishedBy"
     AND to_jsonb(OLD)-ARRAY['PublishedAt','PublishedBy']=to_jsonb(NEW)-ARRAY['PublishedAt','PublishedBy'] THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Commercial plan requires independent publication and immutable evidence' USING ERRCODE='23514';
END $$;
CREATE TRIGGER saas_plan BEFORE INSERT OR UPDATE OR DELETE ON saas."Plans" FOR EACH ROW EXECUTE FUNCTION saas.plan_guard_v1();

CREATE FUNCTION saas.movement_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE capacity numeric; collected numeric;
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."VerifiedBy" IS NULL AND NEW."VerifiedAt" IS NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF OLD."VerifiedAt" IS NULL AND NEW."VerifiedAt" IS NOT NULL AND NEW."PreparedBy"<>NEW."VerifiedBy"
     AND to_jsonb(OLD)-ARRAY['VerifiedAt','VerifiedBy']=to_jsonb(NEW)-ARRAY['VerifiedAt','VerifiedBy'] THEN
     PERFORM 1 FROM organizations."Tenants" WHERE "Id"=NEW."TenantId" FOR UPDATE;
     IF NEW."Kind"=1 THEN
       SELECT m."Amount" INTO capacity FROM saas."Movements" m WHERE m."Id"=NEW."BasisId" AND m."InvoiceId"=NEW."InvoiceId"
         AND m."TenantId"=NEW."TenantId" AND m."Kind"=0 AND m."VerifiedAt" IS NOT NULL;
       SELECT coalesce(sum(m."Amount"),0) INTO collected FROM saas."Movements" m WHERE m."BasisId"=NEW."BasisId"
         AND m."TenantId"=NEW."TenantId" AND m."VerifiedAt" IS NOT NULL;
     ELSE
       SELECT i."Total" INTO capacity FROM saas."Invoices" i WHERE i."Id"=NEW."InvoiceId" AND i."TenantId"=NEW."TenantId";
       SELECT coalesce(sum(CASE WHEN m."Kind"=1 THEN -m."Amount" ELSE m."Amount" END),0) INTO collected
         FROM saas."Movements" m WHERE m."InvoiceId"=NEW."InvoiceId" AND m."TenantId"=NEW."TenantId" AND m."VerifiedAt" IS NOT NULL;
     END IF;
     IF capacity IS NOT NULL AND NEW."Amount"<=capacity-collected THEN RETURN NEW; END IF;
   END IF;
 END IF;
 RAISE EXCEPTION 'Commercial movements require independent verified and conserved evidence' USING ERRCODE='23514';
END $$;
CREATE TRIGGER saas_movement BEFORE INSERT OR UPDATE OR DELETE ON saas."Movements" FOR EACH ROW EXECUTE FUNCTION saas.movement_guard_v1();

DO $immutable$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['Invoices','Events','Provisioning'] LOOP
   EXECUTE format('CREATE TRIGGER saas_immutable BEFORE UPDATE OR DELETE ON saas.%I FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['Invoices','Events','Provisioning','Movements','Plans'] LOOP
   EXECUTE format('CREATE TRIGGER saas_no_truncate BEFORE TRUNCATE ON saas.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
END $immutable$;
""";

    private const string Drops = """
DROP FUNCTION saas.movement_guard_v1() CASCADE;
DROP FUNCTION saas.plan_guard_v1() CASCADE;
DROP FUNCTION saas.resource_guard_v1() CASCADE;
DROP FUNCTION saas.assert_write_v1(uuid) CASCADE;
DROP FUNCTION saas.can_operate_v1(uuid) CASCADE;
""";
}
