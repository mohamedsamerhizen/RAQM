namespace RAQM.Api.Data.Migrations;

internal static class TaxAccountingV1Sql
{
    public static string Up => Definitions + "\n" + TradingGuard(true);
    public static string Down => TradingGuard(false) + "\n" + Drops;

    private static string TradingGuard(bool taxEnabled)
    {
        const string start = "CREATE FUNCTION trading.assert_journal_v1(";
        const string next = "CREATE FUNCTION trading.assert_company_v1(";
        var source = TradingSubledgerV1Sql.Contracts;
        var from = source.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) throw new InvalidOperationException("The historical trading journal contract was not found.");
        var to = source.IndexOf(next, from, StringComparison.Ordinal);
        if (to < 0) throw new InvalidOperationException("The end of the historical trading journal contract was not found.");
        var function = source[from..to].Replace(start, "CREATE OR REPLACE FUNCTION trading.assert_journal_v1(", StringComparison.Ordinal);
        const string marker = " SELECT EXISTS(SELECT 1 FROM accounting.\"JournalLines\"";
        if (taxEnabled && !function.Contains(marker, StringComparison.Ordinal))
            throw new InvalidOperationException("The historical trading control guard was not found.");
        return taxEnabled ? function.Replace(marker, """
 IF EXISTS(SELECT 1 FROM tax."Operations" t WHERE t."CompanyId"=j."CompanyId" AND t."JournalId"=j."Id" AND t."State"=3)
   AND NOT EXISTS(SELECT 1 FROM accounting."JournalLines" l JOIN trading."AccountMappings" m
     ON m."CompanyId"=l."CompanyId" AND m."AccountId"=l."AccountId" WHERE l."JournalId"=jid AND m."Role" IN(0,1,7,8))
   THEN RETURN; END IF;

""" + marker, StringComparison.Ordinal) : function;
    }

    private const string Definitions = """
ALTER TABLE tax."Periods" ADD CONSTRAINT "EX_TaxPeriods_Overlap"
  EXCLUDE USING gist ("CompanyId" WITH =, "RegistrationId" WITH =, daterange("From","To",'[]') WITH &&);
ALTER TABLE tax."Rules" ADD CONSTRAINT "EX_TaxRules_ApprovedOverlap"
  EXCLUDE USING gist ("CompanyId" WITH =, "RegistrationId" WITH =, "Group" WITH =, "Side" WITH =, "Order" WITH =,
    (coalesce("ItemClass",'')) WITH =, (coalesce("PartyId",'00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
    daterange("EffectiveOn","EndsOn",'[]') WITH &&) WHERE ("ApprovedBy" IS NOT NULL);

CREATE FUNCTION tax.approval_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."ApprovedBy" IS NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF OLD."ApprovedBy" IS NULL AND NEW."ApprovedBy" IS NOT NULL AND NEW."ApprovedBy"<>NEW."PreparedBy"
      AND to_jsonb(NEW)-'ApprovedBy'=to_jsonb(OLD)-'ApprovedBy' THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Tax configuration requires independently approved immutable evidence' USING ERRCODE='23514';
END $$;

CREATE FUNCTION tax.quote_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE d trading."Documents"%ROWTYPE;
BEGIN
 SELECT * INTO d FROM trading."Documents" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."DocumentId" AND "State"=0 AND "Kind" IN(0,1);
 IF NOT FOUND OR jsonb_typeof(NEW."SnapshotJson"->'Lines') IS DISTINCT FROM 'array'
    OR NEW."SnapshotJson"->>'Currency' IS DISTINCT FROM d."Currency"
    OR (NEW."SnapshotJson"->>'PartyId')::uuid IS DISTINCT FROM d."PartyId"
    OR (NEW."SnapshotJson"->>'Date')::date IS DISTINCT FROM d."DocumentDate"
    OR jsonb_array_length(NEW."SnapshotJson"->'Lines')<>(SELECT count(*) FROM trading."DocumentLines" l WHERE l."DocumentId"=d."Id")
    OR EXISTS(SELECT 1 FROM jsonb_array_elements(NEW."SnapshotJson"->'Lines') q
      LEFT JOIN trading."DocumentLines" l ON l."DocumentId"=d."Id" AND l."Number"=(q->>'Number')::int
      WHERE l."Id" IS NULL OR (q->>'Net')::numeric IS DISTINCT FROM l."NetAmount" OR
        (q->>'Tax')::numeric IS DISTINCT FROM l."TaxAmount" OR (q->>'Total')::numeric IS DISTINCT FROM l."Total" OR
        jsonb_array_length(q->'Components') IS DISTINCT FROM 1 OR
        (q->'Components'->0->>'RateId')::uuid IS DISTINCT FROM l."TaxRateId" OR
        NOT EXISTS(SELECT 1 FROM tax."Rules" r WHERE r."CompanyId"=NEW."CompanyId" AND r."RegistrationId"=NEW."RegistrationId"
          AND r."Id"=(q->'Components'->0->>'RuleId')::uuid AND r."ApprovedBy" IS NOT NULL AND r."Side"=d."Kind"
          AND r."EffectiveOn"<=d."DocumentDate" AND r."EndsOn">=d."DocumentDate"
          AND r."RateId" IS NOT DISTINCT FROM l."TaxRateId")) THEN
   RAISE EXCEPTION 'Tax quote must match its eligible draft and approved rule' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION tax.freeze_quote_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE cid uuid; did uuid;
BEGIN
 IF TG_OP='DELETE' THEN cid:=OLD."CompanyId"; did:=OLD."DocumentId"; ELSE cid:=NEW."CompanyId"; did:=NEW."DocumentId"; END IF;
 IF EXISTS(SELECT 1 FROM tax."CommercialEvidence" e WHERE e."CompanyId"=cid AND e."DocumentId"=did) THEN
   RAISE EXCEPTION 'Tax-priced lines are retained. Cancel and recreate the draft.' USING ERRCODE='23514';
 END IF;
 IF TG_OP='DELETE' THEN RETURN OLD; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION tax.trade_document_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE lifecycle text[]:=ARRAY['State','Version','UpdatedBy','UpdatedAt','ApprovedBy','ApprovedAt','JournalId',
 'PostingDate','Number','Sequence','PostKey','PostRequestHash','PostedBy','PostedAt'];
BEGIN
 IF EXISTS(SELECT 1 FROM tax."CommercialEvidence" e WHERE e."CompanyId"=NEW."CompanyId" AND e."DocumentId"=NEW."Id")
    AND to_jsonb(NEW)-lifecycle IS DISTINCT FROM to_jsonb(OLD)-lifecycle THEN
   RAISE EXCEPTION 'Retained tax quote requires an unchanged commercial payload' USING ERRCODE='23514';
 END IF;
 IF NEW."State"=2 AND NEW."Kind"=3 AND EXISTS(SELECT 1 FROM tax."Operations" o WHERE o."CompanyId"=NEW."CompanyId"
    AND o."BillId"=NEW."OriginalDocumentId" AND o."State"=3 AND o."Kind"=7 AND NOT EXISTS(
      SELECT 1 FROM tax."Operations" r WHERE r."CompanyId"=o."CompanyId" AND r."BasisId"=o."Id" AND r."State"=3
        AND r."Kind"=9 AND r."Date"<=NEW."PostingDate")) THEN
   RAISE EXCEPTION 'Correct the linked self-assessment before posting its supplier credit' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION tax.operation_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."State"=0 AND NEW."ApprovedBy" IS NULL AND NEW."PostingActor" IS NULL AND NEW."PaymentId" IS NULL
       AND NEW."PlanJson" IS NOT NULL THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF to_jsonb(NEW)-ARRAY['State','ApprovedBy','PostingActor','PaymentId','JournalId'] =
        to_jsonb(OLD)-ARRAY['State','ApprovedBy','PostingActor','PaymentId','JournalId']
     AND ((OLD."State"=0 AND NEW."State"=1 AND NEW."ApprovedBy"<>NEW."PreparedBy" AND NEW."PostingActor" IS NULL AND NEW."PaymentId" IS NULL)
       OR (OLD."State"=1 AND NEW."State"=2 AND NEW."ApprovedBy"=OLD."ApprovedBy" AND NEW."PostingActor" IS NOT NULL AND NEW."PaymentId" IS NULL)
       OR (OLD."State"=2 AND NEW."State"=3 AND NEW."ApprovedBy"=OLD."ApprovedBy" AND NEW."PostingActor"=OLD."PostingActor"
         AND (NEW."PaymentId" IS NOT NULL)=(NEW."Kind"=8))
       OR (OLD."State" IN(0,1) AND NEW."State"=4 AND NEW."ApprovedBy" IS NOT DISTINCT FROM OLD."ApprovedBy"
         AND NEW."PostingActor" IS NULL AND NEW."PaymentId" IS NULL)) THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Tax operation state, approval or immutable evidence is invalid' USING ERRCODE='23514';
END $$;

CREATE FUNCTION tax.return_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='INSERT' THEN
   IF NEW."State"=0 AND NEW."ApprovedBy" IS NULL AND NEW."ApprovedAt" IS NULL AND length(NEW."SourceHash")=64 THEN RETURN NEW; END IF;
 ELSIF TG_OP='UPDATE' THEN
   IF OLD."State"=0 AND NEW."State"=1 AND NEW."ApprovedBy"<>NEW."PreparedBy" AND NEW."ApprovedAt" IS NOT NULL
     AND to_jsonb(NEW)-ARRAY['State','ApprovedBy','ApprovedAt']=to_jsonb(OLD)-ARRAY['State','ApprovedBy','ApprovedAt']
     AND NEW."SnapshotJson"->>'Agrees'='true'
     AND jsonb_array_length(NEW."SnapshotJson"->'Pending')=0 AND jsonb_array_length(NEW."SnapshotJson"->'UnprofiledDocuments')=0
     AND jsonb_array_length(NEW."SnapshotJson"->'UnassignedTaxLines')=0 THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Tax return evidence requires an independent reconciled approval and is retained' USING ERRCODE='23514';
END $$;

CREATE FUNCTION tax.check_operation_v1(id uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE op tax."Operations"%ROWTYPE; original tax."Operations"%ROWTYPE; j accounting."Journals"%ROWTYPE;
  p trading."Payments"%ROWTYPE; actual jsonb; reversal uuid;
BEGIN
 SELECT * INTO op FROM tax."Operations" t WHERE t."Id"=id;
 IF NOT FOUND THEN RAISE EXCEPTION 'Tax journal requires an operation' USING ERRCODE='23514'; END IF;
 IF op."State"=2 THEN RAISE EXCEPTION 'A staged tax operation must finish before commit' USING ERRCODE='23514'; END IF;
 IF op."State"<>3 THEN RETURN; END IF;
 SELECT * INTO j FROM accounting."Journals" WHERE "CompanyId"=op."CompanyId" AND "Id"=op."JournalId" AND "State"=1;
 IF NOT FOUND OR j."PostingDate"<>op."Date" OR j."PostedBy" IS DISTINCT FROM op."PostingActor" OR
   j."IdempotencyKey" IS DISTINCT FROM ('Tax:'||replace(op."Id"::text,'-','')) OR j."Reference" IS DISTINCT FROM op."Reference" THEN
   RAISE EXCEPTION 'Posted tax operation must match its posted journal' USING ERRCODE='23514';
 END IF;
 reversal:=(op."PlanJson"->>'ReversesJournalId')::uuid;
 IF op."Kind"=9 THEN
   SELECT * INTO original FROM tax."Operations" o WHERE o."CompanyId"=op."CompanyId" AND o."RegistrationId"=op."RegistrationId"
     AND o."Id"=op."BasisId" AND o."State"=3 AND o."Date"<=op."Date" AND o."Kind"<>9;
   IF NOT FOUND OR original."JournalId" IS DISTINCT FROM reversal OR j."ReversesJournalId" IS DISTINCT FROM reversal
       OR j."SourceDocumentId" IS DISTINCT FROM reversal OR j."SourceModule"<>'Reversal' THEN
     RAISE EXCEPTION 'Tax correction requires the exact linked original journal' USING ERRCODE='23514';
   END IF;
   IF original."PaymentId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading."Payments" w WHERE w."CompanyId"=op."CompanyId"
     AND w."Id"=original."PaymentId" AND w."State"=2 AND w."ReversalDate"=op."Date" AND
     w."ReversalKey"=('tax-ap-reverse-'||replace(op."Id"::text,'-',''))) THEN
     RAISE EXCEPTION 'Withholding correction requires reciprocal supplier payment reversal' USING ERRCODE='23514';
   END IF;
 ELSIF reversal IS NOT NULL OR j."SourceModule"<>'Tax.Operation' OR j."SourceDocumentId"<>op."Id" THEN
   RAISE EXCEPTION 'Invalid coordinated tax journal source' USING ERRCODE='23514';
 END IF;
 SELECT jsonb_agg(jsonb_build_object('AccountId',l."AccountId",'Debit',l."Debit",'Credit',l."Credit",'Description',l."Description") ORDER BY l."Number")
   INTO actual FROM accounting."JournalLines" l WHERE l."JournalId"=j."Id" AND l."CompanyId"=op."CompanyId";
 IF actual IS DISTINCT FROM op."PlanJson"->'Lines' THEN
   RAISE EXCEPTION 'Journal differs from the reviewed tax plan' USING ERRCODE='23514';
 END IF;
 IF op."Kind"=8 THEN
   SELECT * INTO p FROM trading."Payments" w WHERE w."CompanyId"=op."CompanyId" AND w."Id"=op."PaymentId"
     AND w."State" IN(1,2) AND w."PartyKind"=1 AND NOT w."IsRefund" AND w."PostingDate"=op."Date"
     AND w."RequestKey"=('tax-ap-'||replace(op."Id"::text,'-','')) AND w."AdvanceAmount"=0
     AND w."Amount"=(op."PlanJson"->>'TaxableBase')::numeric AND w."CashAccountId"=(op."PlanJson"->'Lines'->0->>'AccountId')::uuid;
   IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM trading."Allocations" a WHERE a."CompanyId"=op."CompanyId" AND a."State"=1 AND
     a."PaymentId"=p."Id" AND a."TargetDocumentId"=op."BillId" AND a."ReversesAllocationId" IS NULL AND a."Amount"=p."Amount") THEN
     RAISE EXCEPTION 'Withholding requires an exact finalized bill payment and allocation' USING ERRCODE='23514';
   END IF;
 END IF;
END $$;

CREATE FUNCTION tax.operation_commit_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 PERFORM tax.check_operation_v1(NEW."Id");
 RETURN NEW;
END $$;

CREATE FUNCTION tax.journal_commit_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE opid uuid; payid uuid; is_reversal boolean;
BEGIN
 IF NEW."State"<>1 THEN RETURN NEW; END IF;
 SELECT t."Id" INTO opid FROM tax."Operations" t WHERE t."CompanyId"=NEW."CompanyId" AND t."JournalId"=NEW."Id";
 IF NEW."SourceModule"='Tax.Operation' OR opid IS NOT NULL OR EXISTS(SELECT 1 FROM tax."Operations" t
   WHERE t."CompanyId"=NEW."CompanyId" AND t."JournalId"=NEW."ReversesJournalId") THEN
   IF opid IS NULL THEN RAISE EXCEPTION 'Tax journal or correction requires coordinated source' USING ERRCODE='23514'; END IF;
   PERFORM tax.check_operation_v1(opid);
   RETURN NEW;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM accounting."JournalLines" l JOIN tax."Controls" c ON c."CompanyId"=l."CompanyId" AND c."AccountId"=l."AccountId"
   WHERE l."JournalId"=NEW."Id" AND NOT (
      c."Role" IN(0,1) AND NEW."SourceModule" IN('AR.Invoice','AP.Bill','AR.Credit','AP.Credit','FX.Invoice','FX.Bill','FX.Credit') OR
      c."Role" IN(2,4,9) AND NEW."SourceModule"='FinancialClose.YearEnd')) THEN RETURN NEW; END IF;
 is_reversal:=NEW."SourceModule"='Reversal';
 IF NEW."SourceModule"='Settlement.Payment' OR is_reversal THEN
   SELECT p."Id" INTO payid FROM trading."Payments" p WHERE p."CompanyId"=NEW."CompanyId" AND
     (p."JournalId"=NEW."Id" OR p."ReversalJournalId"=NEW."Id");
   IF payid IS NOT NULL AND EXISTS(SELECT 1 FROM tax."Operations" t WHERE t."CompanyId"=NEW."CompanyId" AND t."PaymentId"=payid AND t."State"=3
       AND (NOT is_reversal OR EXISTS(SELECT 1 FROM tax."Operations" c WHERE c."CompanyId"=t."CompanyId" AND c."BasisId"=t."Id" AND
         c."Kind"=9 AND c."State"=3 AND c."Date"=NEW."PostingDate"))) THEN RETURN NEW; END IF;
 END IF;
 RAISE EXCEPTION 'Protected tax accounts require matching source evidence' USING ERRCODE='23514';
END $$;

CREATE FUNCTION tax.deallocation_commit_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE source_id uuid; owner_id uuid;
BEGIN
 IF NEW."ReversesAllocationId" IS NULL OR NEW."State"<>1 THEN RETURN NEW; END IF;
 SELECT a."PaymentId" INTO source_id FROM trading."Allocations" a WHERE a."CompanyId"=NEW."CompanyId" AND a."Id"=NEW."ReversesAllocationId";
 SELECT t."Id" INTO owner_id FROM tax."Operations" t WHERE t."CompanyId"=NEW."CompanyId" AND t."PaymentId"=source_id;
 IF owner_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM tax."Operations" t WHERE t."CompanyId"=NEW."CompanyId" AND
    t."BasisId"=owner_id AND t."Kind"=9 AND t."State"=3 AND t."Date"=NEW."EffectiveDate") THEN
   RAISE EXCEPTION 'Withholding allocations require a coordinated correction' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

DO $tax_triggers$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['Registrations','Controls','Rules','CommercialEvidence','Periods','Operations','Returns','RegulatoryDocuments'] LOOP
   EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON tax.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
   EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON tax.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['Controls','CommercialEvidence','Periods','RegulatoryDocuments'] LOOP
   EXECUTE format('CREATE TRIGGER b_immutable BEFORE UPDATE OR DELETE ON tax.%I FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['Registrations','Rules'] LOOP
   EXECUTE format('CREATE TRIGGER b_approval BEFORE INSERT OR UPDATE OR DELETE ON tax.%I FOR EACH ROW EXECUTE FUNCTION tax.approval_guard_v1()',t);
 END LOOP;
END $tax_triggers$;
CREATE TRIGGER b_tax_quote BEFORE INSERT ON tax."CommercialEvidence" FOR EACH ROW EXECUTE FUNCTION tax.quote_guard_v1();
CREATE TRIGGER tax_frozen_quote BEFORE INSERT OR UPDATE OR DELETE ON trading."DocumentLines" FOR EACH ROW EXECUTE FUNCTION tax.freeze_quote_v1();
CREATE TRIGGER tax_frozen_document BEFORE UPDATE ON trading."Documents" FOR EACH ROW EXECUTE FUNCTION tax.trade_document_guard_v1();
CREATE TRIGGER b_tax_operation BEFORE INSERT OR UPDATE OR DELETE ON tax."Operations" FOR EACH ROW EXECUTE FUNCTION tax.operation_guard_v1();
CREATE TRIGGER b_tax_return BEFORE INSERT OR UPDATE OR DELETE ON tax."Returns" FOR EACH ROW EXECUTE FUNCTION tax.return_guard_v1();
CREATE CONSTRAINT TRIGGER z_tax_operation AFTER INSERT OR UPDATE ON tax."Operations" DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION tax.operation_commit_v1();
CREATE CONSTRAINT TRIGGER z_tax_journal AFTER INSERT OR UPDATE ON accounting."Journals" DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION tax.journal_commit_v1();
CREATE CONSTRAINT TRIGGER z_tax_deallocation AFTER INSERT OR UPDATE ON trading."Allocations" DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION tax.deallocation_commit_v1();
""";

    private const string Drops = """
DROP FUNCTION tax.deallocation_commit_v1() CASCADE;
DROP FUNCTION tax.journal_commit_v1() CASCADE;
DROP FUNCTION tax.operation_commit_v1() CASCADE;
DROP FUNCTION tax.check_operation_v1(uuid) CASCADE;
DROP FUNCTION tax.return_guard_v1() CASCADE;
DROP FUNCTION tax.operation_guard_v1() CASCADE;
DROP FUNCTION tax.freeze_quote_v1() CASCADE;
DROP FUNCTION tax.trade_document_guard_v1() CASCADE;
DROP FUNCTION tax.quote_guard_v1() CASCADE;
DROP FUNCTION tax.approval_guard_v1() CASCADE;
""";
}
