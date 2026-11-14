namespace RAQM.Api.Data.Migrations;

// Versioned additive DDL. Preserve this historical definition after deployment.
internal static class TradingSubledgerV1Sql
{
    public const string Up="""
ALTER TABLE trading."TaxRates" ADD CONSTRAINT "EX_TaxRate_Window" EXCLUDE USING gist
("CompanyId" WITH =,"TaxCodeId" WITH =,daterange("EffectiveFrom","EffectiveTo",'[]') WITH &&);

CREATE FUNCTION trading.round_even_v1(n numeric, units integer) RETURNS numeric LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE scale numeric:=power(10::numeric,units); v numeric; whole numeric; fraction numeric;
BEGIN
 v:=abs(n)*scale;whole:=floor(v);fraction:=v-whole;
 IF fraction>0.5 OR (fraction=0.5 AND mod(whole,2)=1) THEN whole:=whole+1; END IF;
 RETURN sign(n)*whole*CASE units WHEN 0 THEN 1 WHEN 2 THEN 0.01 WHEN 3 THEN 0.001 END;
END $$;
CREATE FUNCTION trading.units_v1(code text) RETURNS integer LANGUAGE sql IMMUTABLE AS $$
 SELECT CASE WHEN code IN('IQD','KWD','BHD','JOD','OMR','TND') THEN 3 WHEN code IN('JPY','KRW') THEN 0
 WHEN code IN('USD','EUR','GBP','AED','SAR','CAD','AUD','CHF','CNY') THEN 2 ELSE -1 END
$$;

CREATE FUNCTION trading.mapping_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE a accounting."Accounts"%ROWTYPE; c uuid; role integer;
BEGIN
 IF TG_OP='UPDATE' THEN RAISE EXCEPTION 'Mapping rows are replaced explicitly, not mutated' USING ERRCODE='23514'; END IF;
 IF TG_OP='DELETE' THEN c:=OLD."CompanyId";role:=OLD."Role"; ELSE c:=NEW."CompanyId";role:=NEW."Role"; END IF;
 IF (TG_OP='DELETE' OR role<>4) AND (EXISTS(SELECT 1 FROM trading."Parties" WHERE "CompanyId"=c) OR
   EXISTS(SELECT 1 FROM trading."TaxCodes" WHERE "CompanyId"=c)) THEN
   RAISE EXCEPTION 'Trading configuration is frozen by business references' USING ERRCODE='23514';
 END IF;
 IF TG_OP='DELETE' THEN RETURN OLD; END IF;
 SELECT * INTO a FROM accounting."Accounts" WHERE "Id"=NEW."AccountId" AND "CompanyId"=c;
 IF NOT FOUND OR NOT a."IsActive" OR NOT a."IsPosting" OR a."IsContra" OR a."Classification"<>
  (CASE WHEN role IN(0,4,6,8) THEN 0 WHEN role IN(1,5,7) THEN 1 WHEN role=2 THEN 3 WHEN role=3 THEN 4 ELSE 2 END) THEN
  RAISE EXCEPTION 'Invalid trading account role/classification' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;
CREATE FUNCTION trading.mapped_account_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF (NEW."Classification",NEW."IsContra",NEW."IsPosting",NEW."NormalBalance") IS DISTINCT FROM
    (OLD."Classification",OLD."IsContra",OLD."IsPosting",OLD."NormalBalance") AND
    EXISTS(SELECT 1 FROM trading."AccountMappings" WHERE "CompanyId"=OLD."CompanyId" AND "AccountId"=OLD."Id") THEN
   RAISE EXCEPTION 'Mapped financial account semantics are immutable' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;
CREATE FUNCTION trading.company_currency_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW."BaseCurrency"<>OLD."BaseCurrency" AND (EXISTS(SELECT 1 FROM trading."Parties" WHERE "CompanyId"=OLD."Id") OR
   EXISTS(SELECT 1 FROM trading."AccountMappings" WHERE "CompanyId"=OLD."Id")) THEN
   RAISE EXCEPTION 'Trading setup freezes company currency' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;
CREATE FUNCTION trading.party_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE currency text;
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Counterparty references are retained; deactivate instead' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (NEW."Kind",NEW."Currency",NEW."ControlAccountId") IS DISTINCT FROM
  (OLD."Kind",OLD."Currency",OLD."ControlAccountId") THEN
  RAISE EXCEPTION 'Counterparty financial identity is immutable' USING ERRCODE='23514';
 END IF;
 SELECT "BaseCurrency" INTO currency FROM organizations."Companies" WHERE "Id"=NEW."CompanyId";
 IF NEW."Currency"<>currency OR NOT EXISTS(SELECT 1 FROM trading."AccountMappings" m WHERE m."CompanyId"=NEW."CompanyId"
   AND m."AccountId"=NEW."ControlAccountId" AND m."Role"=CASE WHEN NEW."Kind"=0 THEN 0 ELSE 1 END) OR
   (NEW."CreditLimit" IS NOT NULL AND NEW."CreditLimit"<>trading.round_even_v1(NEW."CreditLimit",trading.units_v1(currency))) THEN
   RAISE EXCEPTION 'Counterparty currency/control/limit invalid' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;
CREATE FUNCTION trading.tax_code_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Tax codes are retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (NEW."Code",NEW."InputAccountId",NEW."OutputAccountId") IS DISTINCT FROM
   (OLD."Code",OLD."InputAccountId",OLD."OutputAccountId") THEN
   RAISE EXCEPTION 'Tax identity/account mappings are immutable' USING ERRCODE='23514';
 END IF;
 IF NOT EXISTS(SELECT 1 FROM trading."AccountMappings" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=NEW."InputAccountId" AND "Role"=6)
 OR NOT EXISTS(SELECT 1 FROM trading."AccountMappings" WHERE "CompanyId"=NEW."CompanyId" AND "AccountId"=NEW."OutputAccountId" AND "Role"=5) THEN
   RAISE EXCEPTION 'Tax accounts do not match configured controls' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION trading.document_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE n bigint; net numeric; tax numeric; p trading."Parties"%ROWTYPE;
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Documents are cancelled, not deleted' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND NEW."State"<>0 THEN RAISE EXCEPTION 'Create a draft document' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' THEN
   IF OLD."State" IN(2,3) THEN RAISE EXCEPTION 'Posted/cancelled documents are immutable' USING ERRCODE='23514'; END IF;
   IF (NEW."PartyId",NEW."Kind",NEW."OriginalDocumentId") IS DISTINCT FROM (OLD."PartyId",OLD."Kind",OLD."OriginalDocumentId") THEN
     RAISE EXCEPTION 'Document financial identity cannot change' USING ERRCODE='23514'; END IF;
   IF NOT ((OLD."State"=0 AND NEW."State" IN(0,1,3)) OR (OLD."State"=1 AND NEW."State" IN(0,2,3))) THEN
     RAISE EXCEPTION 'Invalid document state transition' USING ERRCODE='23514'; END IF;
   IF OLD."State"=1 AND (to_jsonb(NEW)-ARRAY['State','Version','UpdatedBy','UpdatedAt','ApprovedBy','ApprovedAt','JournalId','PostingDate','Number','Sequence','PostKey','PostRequestHash','PostedBy','PostedAt'])
     IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['State','Version','UpdatedBy','UpdatedAt','ApprovedBy','ApprovedAt','JournalId','PostingDate','Number','Sequence','PostKey','PostRequestHash','PostedBy','PostedAt']) THEN
     RAISE EXCEPTION 'Approved financial payload cannot change' USING ERRCODE='23514';
   END IF;
 END IF;
 IF NEW."State"<>2 AND (NEW."JournalId" IS NOT NULL OR NEW."PostingDate" IS NOT NULL OR NEW."PostKey" IS NOT NULL) THEN
   RAISE EXCEPTION 'Unposted document cannot have a posting link' USING ERRCODE='23514'; END IF;
 SELECT * INTO p FROM trading."Parties" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."PartyId";
 IF NOT FOUND OR p."Kind"<>(CASE WHEN NEW."Kind" IN(0,2,4) THEN 0 ELSE 1 END) OR NEW."ControlAccountId"<>p."ControlAccountId"
   OR NEW."Currency"<>p."Currency" OR NEW."MinorUnits"<>trading.units_v1(NEW."Currency") THEN
   RAISE EXCEPTION 'Document party/control/currency mismatch' USING ERRCODE='23514'; END IF;
 IF NEW."State" IN(1,2) THEN
   SELECT count(*),coalesce(sum("NetAmount"),0),coalesce(sum("TaxAmount"),0) INTO n,net,tax FROM trading."DocumentLines" WHERE "DocumentId"=NEW."Id";
   IF n<1 OR n>300 OR net<>NEW."NetTotal" OR tax<>NEW."TaxTotal" OR NEW."Total"<>net+tax THEN
     RAISE EXCEPTION 'Document totals do not match line snapshots' USING ERRCODE='23514'; END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE FUNCTION trading.document_line_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE d trading."Documents"%ROWTYPE; o trading."DocumentLines"%ROWTYPE; original trading."Documents"%ROWTYPE;
 r trading."TaxRates"%ROWTYPE; tc trading."TaxCodes"%ROWTYPE; gross numeric; discount numeric; net numeric; tax numeric; units integer; doc uuid;
BEGIN
 IF TG_OP='DELETE' THEN doc:=OLD."DocumentId"; ELSE doc:=NEW."DocumentId"; END IF;
 SELECT * INTO d FROM trading."Documents" WHERE "Id"=doc FOR UPDATE;
 IF NOT FOUND OR d."State"<>0 THEN RAISE EXCEPTION 'Only draft document lines can change' USING ERRCODE='23514'; END IF;
 IF TG_OP='DELETE' THEN RETURN OLD; END IF;
 IF TG_OP='UPDATE' AND NEW."DocumentId"<>OLD."DocumentId" THEN RAISE EXCEPTION 'Line identity cannot move' USING ERRCODE='23514'; END IF;
 units:=d."MinorUnits";
 IF d."Kind" IN(2,3) THEN
   SELECT * INTO original FROM trading."Documents" WHERE "Id"=d."OriginalDocumentId" AND "CompanyId"=d."CompanyId" AND "State"=2;
   IF NOT FOUND OR original."PartyId"<>d."PartyId" OR original."Kind" IN(2,3) THEN
     RAISE EXCEPTION 'Invalid credit original' USING ERRCODE='23514'; END IF;
   SELECT * INTO o FROM trading."DocumentLines" WHERE "Id"=NEW."OriginalLineId" AND "DocumentId"=original."Id";
   IF NOT FOUND OR (NEW."AccountId",NEW."UnitPrice",NEW."DiscountPercent",NEW."TaxRateId",NEW."TaxAccountId",NEW."TaxPercent",NEW."TaxInclusive")
      IS DISTINCT FROM (o."AccountId",o."UnitPrice",o."DiscountPercent",o."TaxRateId",o."TaxAccountId",o."TaxPercent",o."TaxInclusive") THEN
     RAISE EXCEPTION 'Credit must retain original line policy/accounts' USING ERRCODE='23514'; END IF;
 ELSE
   IF NEW."OriginalLineId" IS NOT NULL THEN RAISE EXCEPTION 'Unexpected original line' USING ERRCODE='23514'; END IF;
   IF d."Kind" IN(4,5) THEN
     IF NEW."TaxRateId" IS NOT NULL OR NEW."DiscountPercent"<>0 OR NOT EXISTS(SELECT 1 FROM trading."AccountMappings"
       WHERE "CompanyId"=d."CompanyId" AND "AccountId"=NEW."AccountId" AND "Role"=9) THEN
       RAISE EXCEPTION 'Opening document requires equity without tax or discount' USING ERRCODE='23514'; END IF;
   ELSIF NOT EXISTS(SELECT 1 FROM accounting."Accounts" WHERE "CompanyId"=d."CompanyId" AND "Id"=NEW."AccountId"
       AND "IsActive" AND "IsPosting" AND NOT "IsContra" AND ((d."Kind"=0 AND "Classification"=3) OR (d."Kind"=1 AND "Classification" IN(0,4))))
       OR EXISTS(SELECT 1 FROM trading."AccountMappings" WHERE "CompanyId"=d."CompanyId" AND "AccountId"=NEW."AccountId" AND "Role" NOT IN(2,3)) THEN
     RAISE EXCEPTION 'Document line requires an eligible ordinary account' USING ERRCODE='23514';
   END IF;
   IF NEW."TaxRateId" IS NOT NULL THEN
     SELECT * INTO r FROM trading."TaxRates" WHERE "Id"=NEW."TaxRateId" AND "CompanyId"=d."CompanyId";
     IF NOT FOUND OR d."DocumentDate"<r."EffectiveFrom" OR d."DocumentDate">r."EffectiveTo" OR NEW."TaxPercent"<>r."RatePercent" THEN
       RAISE EXCEPTION 'Tax version not effective' USING ERRCODE='23514'; END IF;
     SELECT * INTO tc FROM trading."TaxCodes" WHERE "Id"=r."TaxCodeId";
     IF NOT tc."IsActive" OR NEW."TaxAccountId" IS DISTINCT FROM (CASE WHEN d."Kind" IN(0,4) THEN tc."OutputAccountId" ELSE tc."InputAccountId" END) THEN
       RAISE EXCEPTION 'Tax account/code mismatch' USING ERRCODE='23514'; END IF;
   ELSIF NEW."TaxPercent"<>0 OR NEW."TaxAccountId" IS NOT NULL OR NEW."TaxInclusive" THEN
     RAISE EXCEPTION 'Tax version required' USING ERRCODE='23514';
   END IF;
   gross:=trading.round_even_v1(NEW."Quantity"*NEW."UnitPrice",units);
   discount:=trading.round_even_v1(gross*NEW."DiscountPercent"*0.01,units);
   net:=CASE WHEN NEW."TaxInclusive" THEN trading.round_even_v1((gross-discount)::numeric(36,16)/(1+NEW."TaxPercent"*0.01),units) ELSE gross-discount END;
   tax:=CASE WHEN NEW."TaxInclusive" THEN gross-discount-net ELSE trading.round_even_v1(net*NEW."TaxPercent"*0.01,units) END;
   IF (NEW."DiscountAmount",NEW."NetAmount",NEW."TaxAmount") IS DISTINCT FROM (discount,net,tax) THEN
     RAISE EXCEPTION 'Tax/discount snapshot arithmetic mismatch' USING ERRCODE='23514'; END IF;
 END IF;
 IF NEW."Quantity">1000000000 OR NEW."DiscountAmount"<0 OR NEW."NetAmount"<>trading.round_even_v1(NEW."NetAmount",units)
   OR NEW."TaxAmount"<>trading.round_even_v1(NEW."TaxAmount",units) THEN
   RAISE EXCEPTION 'Invalid quantity/currency scale' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;
""";
    public const string Contracts="""
CREATE FUNCTION trading.settlement_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE units integer; effective date;
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Settlement evidence cannot be deleted' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' AND NEW."State"<>0 THEN RAISE EXCEPTION 'Prepare settlement before posting' USING ERRCODE='23514'; END IF;
 IF TG_OP='INSERT' THEN
   SELECT trading.units_v1("BaseCurrency") INTO units FROM organizations."Companies" WHERE "Id"=NEW."CompanyId";
   IF NEW."ControlAmount"<>trading.round_even_v1(NEW."ControlAmount",units) OR
      NEW."AdvanceAmount"<>trading.round_even_v1(NEW."AdvanceAmount",units) THEN
     RAISE EXCEPTION 'Settlement requires currency minor-unit precision' USING ERRCODE='23514'; END IF;
   IF TG_TABLE_NAME='Allocations' THEN effective:=NEW."EffectiveDate"; ELSE effective:=NEW."PostingDate"; END IF;
   IF NOT EXISTS(SELECT 1 FROM accounting."Periods" WHERE "CompanyId"=NEW."CompanyId" AND "State"=0 AND effective BETWEEN "StartDate" AND "EndDate") THEN
     RAISE EXCEPTION 'Settlement requires an open period' USING ERRCODE='23514'; END IF;
 END IF;
 IF TG_OP='UPDATE' THEN
   IF TG_TABLE_NAME='Allocations' THEN
     IF OLD."State"<>0 OR NEW."State"<>1 OR
       (to_jsonb(NEW)-ARRAY['State','JournalId']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['State','JournalId']) THEN
       RAISE EXCEPTION 'Allocations are append-only' USING ERRCODE='23514'; END IF;
   ELSE
     IF OLD."State"=2 OR NEW."State"<OLD."State" OR
       (to_jsonb(NEW)-ARRAY['State','JournalId','Sequence','Number','PostedAt','ReversalJournalId','ReversalDate','ReversalKey','ReversalReason','ReversedBy','ReversedAt'])
       IS DISTINCT FROM
       (to_jsonb(OLD)-ARRAY['State','JournalId','Sequence','Number','PostedAt','ReversalJournalId','ReversalDate','ReversalKey','ReversalReason','ReversedBy','ReversedAt']) THEN
       RAISE EXCEPTION 'Payment payload is immutable' USING ERRCODE='23514'; END IF;
     IF OLD."State"=1 AND (NEW."JournalId",NEW."Sequence",NEW."Number",NEW."PostedAt") IS DISTINCT FROM
       (OLD."JournalId",OLD."Sequence",OLD."Number",OLD."PostedAt") THEN
       RAISE EXCEPTION 'Original payment journal is immutable' USING ERRCODE='23514'; END IF;
   END IF;
 END IF;
 RETURN NEW;
END $$;

-- Every source has an independently derived account/amount contract. Descriptions are not accounting values.
CREATE FUNCTION trading.expected_lines_v1(jid uuid) RETURNS TABLE(account uuid,debit numeric,credit numeric)
LANGUAGE sql STABLE AS $$
 WITH j AS (SELECT * FROM accounting."Journals" WHERE "Id"=jid),
 d AS (SELECT d.*,d."Kind" IN(0,3,4) AS cd FROM trading."Documents" d,j WHERE d."Id"=j."SourceDocumentId"
   AND j."SourceModule"=CASE d."Kind" WHEN 0 THEN 'AR.Invoice' WHEN 1 THEN 'AP.Bill' WHEN 2 THEN 'AR.Credit'
   WHEN 3 THEN 'AP.Credit' WHEN 4 THEN 'AR.Opening' ELSE 'AP.Opening' END),
 p AS (SELECT p.*,((p."PartyKind"=0)<>p."IsRefund") AS cd FROM trading."Payments" p,j
   WHERE p."Id"=j."SourceDocumentId" AND j."SourceModule"='Settlement.Payment'),
 a AS (SELECT a.* FROM trading."Allocations" a,j WHERE a."Id"=j."SourceDocumentId" AND j."SourceModule"='Settlement.Allocation')
 SELECT d."ControlAccountId",CASE WHEN cd THEN "Total" ELSE 0 END,CASE WHEN cd THEN 0 ELSE "Total" END FROM d
 UNION ALL SELECT l."AccountId",CASE WHEN d.cd THEN 0 ELSE l."NetAmount" END,CASE WHEN d.cd THEN l."NetAmount" ELSE 0 END
   FROM d JOIN trading."DocumentLines" l ON l."DocumentId"=d."Id" WHERE l."NetAmount">0
 UNION ALL SELECT l."TaxAccountId",CASE WHEN d.cd THEN 0 ELSE l."TaxAmount" END,CASE WHEN d.cd THEN l."TaxAmount" ELSE 0 END
   FROM d JOIN trading."DocumentLines" l ON l."DocumentId"=d."Id" WHERE l."TaxAmount">0
 UNION ALL SELECT "CashAccountId",CASE WHEN cd THEN "Amount" ELSE 0 END,CASE WHEN cd THEN 0 ELSE "Amount" END FROM p
 UNION ALL SELECT "ControlAccountId",CASE WHEN cd THEN 0 ELSE "ControlAmount" END,CASE WHEN cd THEN "ControlAmount" ELSE 0 END FROM p WHERE "ControlAmount">0
 UNION ALL SELECT "AdvanceAccountId",CASE WHEN cd THEN 0 ELSE "AdvanceAmount" END,CASE WHEN cd THEN "AdvanceAmount" ELSE 0 END FROM p WHERE "AdvanceAmount">0
 UNION ALL SELECT "AdvanceAccountId",CASE WHEN "PartyKind"=0 THEN "AdvanceAmount" ELSE 0 END,CASE WHEN "PartyKind"=0 THEN 0 ELSE "AdvanceAmount" END FROM a WHERE "AdvanceAmount">0
 UNION ALL SELECT "ControlAccountId",CASE WHEN "PartyKind"=0 THEN 0 ELSE "AdvanceAmount" END,CASE WHEN "PartyKind"=0 THEN "AdvanceAmount" ELSE 0 END FROM a WHERE "AdvanceAmount">0
$$;

CREATE FUNCTION trading.assert_journal_v1(jid uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE j accounting."Journals"%ROWTYPE; original accounting."Journals"%ROWTYPE; reserved boolean; protected boolean;
BEGIN
 SELECT * INTO j FROM accounting."Journals" WHERE "Id"=jid AND "State"=1;
 IF NOT FOUND THEN RETURN; END IF;
 reserved:=j."SourceModule" IN('AR.Invoice','AP.Bill','AR.Credit','AP.Credit','AR.Opening','AP.Opening','Settlement.Payment','Settlement.Allocation');
 IF reserved THEN
   IF NOT (EXISTS(SELECT 1 FROM trading."Documents" d WHERE d."CompanyId"=j."CompanyId" AND d."Id"=j."SourceDocumentId"
       AND d."State"=2 AND d."JournalId"=j."Id" AND d."PostingDate"=j."PostingDate" AND d."Currency"=j."Currency")
     OR EXISTS(SELECT 1 FROM trading."Payments" p WHERE p."CompanyId"=j."CompanyId" AND p."Id"=j."SourceDocumentId"
       AND p."State" IN(1,2) AND p."JournalId"=j."Id" AND p."PostingDate"=j."PostingDate" AND p."Currency"=j."Currency")
     OR EXISTS(SELECT 1 FROM trading."Allocations" a WHERE a."CompanyId"=j."CompanyId" AND a."Id"=j."SourceDocumentId"
       AND a."State"=1 AND a."ReversesAllocationId" IS NULL AND a."JournalId"=j."Id" AND a."EffectiveDate"=j."PostingDate")) THEN
     RAISE EXCEPTION 'Posted journal requires finalized coordinated source' USING ERRCODE='23514'; END IF;
   IF EXISTS(
     WITH e AS (SELECT account,sum(debit) d,sum(credit) c FROM trading.expected_lines_v1(jid) GROUP BY account),
     actual AS (SELECT "AccountId" account,sum("Debit") d,sum("Credit") c FROM accounting."JournalLines" WHERE "JournalId"=jid GROUP BY "AccountId")
     SELECT 1 FROM e FULL JOIN actual USING(account) WHERE coalesce(e.d,0)<>coalesce(actual.d,0) OR coalesce(e.c,0)<>coalesce(actual.c,0)
   ) THEN RAISE EXCEPTION 'Journal differs from source financial contract' USING ERRCODE='23514'; END IF;
   RETURN;
 END IF;
 SELECT EXISTS(SELECT 1 FROM accounting."JournalLines" l JOIN trading."AccountMappings" m ON m."CompanyId"=l."CompanyId" AND m."AccountId"=l."AccountId"
   WHERE l."JournalId"=jid AND m."Role" IN(0,1,5,6,7,8)) INTO protected;
 IF j."ReversesJournalId" IS NOT NULL THEN
   SELECT * INTO original FROM accounting."Journals" WHERE "Id"=j."ReversesJournalId";
   IF EXISTS(SELECT 1 FROM trading."Documents" WHERE "JournalId"=original."Id") THEN
     RAISE EXCEPTION 'Correct trading documents with credit notes' USING ERRCODE='23514'; END IF;
   IF original."SourceModule"='Settlement.Payment' AND EXISTS(SELECT 1 FROM trading."Payments" WHERE "JournalId"=original."Id") THEN
     IF NOT EXISTS(SELECT 1 FROM trading."Payments" WHERE "JournalId"=original."Id" AND "State"=2 AND "ReversalJournalId"=jid AND "ReversalDate"=j."PostingDate") THEN
       RAISE EXCEPTION 'Payment reversal requires coordinated source' USING ERRCODE='23514'; END IF;
     RETURN;
   END IF;
   IF original."SourceModule"='Settlement.Allocation' AND EXISTS(SELECT 1 FROM trading."Allocations" WHERE "JournalId"=original."Id") THEN
     IF NOT EXISTS(SELECT 1 FROM trading."Allocations" a JOIN trading."Allocations" o ON o."Id"=a."ReversesAllocationId"
       WHERE o."JournalId"=original."Id" AND a."State"=1 AND a."JournalId"=jid AND a."EffectiveDate"=j."PostingDate") THEN
       RAISE EXCEPTION 'Allocation reversal requires coordinated source' USING ERRCODE='23514'; END IF;
     RETURN;
   END IF;
   IF protected AND NOT EXISTS(SELECT 1 FROM accounting."JournalLines" l JOIN trading."AccountMappings" m ON m."CompanyId"=l."CompanyId" AND m."AccountId"=l."AccountId"
     WHERE l."JournalId"=jid AND m."Role" IN(0,1,5,6,7,8) AND original."PostedAt">=m."ActivatedAt") THEN RETURN; END IF;
 END IF;
 IF protected THEN RAISE EXCEPTION 'Protected accounts require subledger sources' USING ERRCODE='23514'; END IF;
END $$;

CREATE FUNCTION trading.assert_company_v1(cid uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE d trading."Documents"%ROWTYPE; p trading."Payments"%ROWTYPE; a trading."Allocations"%ROWTYPE;
 o trading."Allocations"%ROWTYPE; target trading."Documents"%ROWTYPE; sourceparty uuid; sourcecontrol uuid; sourcekind integer;
 sourcecurrency text; sourcedate date; sourceadvance uuid; initialcontrol numeric; initialadvance numeric; usedcontrol numeric; usedadvance numeric; atdate date;
BEGIN
 FOR d IN SELECT * FROM trading."Documents" WHERE "CompanyId"=cid AND "State"=2 LOOP
   IF NOT EXISTS(SELECT 1 FROM accounting."Journals" WHERE "Id"=d."JournalId" AND "CompanyId"=cid AND "State"=1
     AND "SourceDocumentId"=d."Id" AND "SourceModule"=CASE d."Kind" WHEN 0 THEN 'AR.Invoice' WHEN 1 THEN 'AP.Bill'
     WHEN 2 THEN 'AR.Credit' WHEN 3 THEN 'AP.Credit' WHEN 4 THEN 'AR.Opening' ELSE 'AP.Opening' END) THEN
     RAISE EXCEPTION 'Posted document requires matching journal' USING ERRCODE='23514'; END IF;
   IF d."OriginalDocumentId" IS NOT NULL AND EXISTS(SELECT 1 FROM trading."Documents"
       WHERE "Id"=d."OriginalDocumentId" AND "PostingDate">d."PostingDate") THEN
     RAISE EXCEPTION 'Credit posting precedes original posting' USING ERRCODE='23514'; END IF;
 END LOOP;
 IF EXISTS(SELECT 1 FROM trading."DocumentLines" l JOIN trading."Documents" d ON d."Id"=l."DocumentId"
   JOIN trading."DocumentLines" original ON original."Id"=l."OriginalLineId"
   WHERE d."CompanyId"=cid AND d."State"=2 GROUP BY original."Id",original."Quantity",original."NetAmount",original."TaxAmount",original."DiscountAmount"
   HAVING sum(l."Quantity")>original."Quantity" OR sum(l."NetAmount")>original."NetAmount" OR sum(l."TaxAmount")>original."TaxAmount"
     OR sum(l."DiscountAmount")>original."DiscountAmount" OR (sum(l."Quantity")=original."Quantity" AND
       (sum(l."NetAmount"),sum(l."TaxAmount"),sum(l."DiscountAmount")) IS DISTINCT FROM (original."NetAmount",original."TaxAmount",original."DiscountAmount"))) THEN
   RAISE EXCEPTION 'Cumulative credits exceed original snapshots or lose final residual' USING ERRCODE='23514'; END IF;
 FOR p IN SELECT * FROM trading."Payments" WHERE "CompanyId"=cid LOOP
   IF p."State"=0 OR (p."State"<>2 AND p."ReversalKey" IS NOT NULL) OR NOT EXISTS(SELECT 1 FROM accounting."Journals"
     WHERE "Id"=p."JournalId" AND "CompanyId"=cid AND "State"=1 AND "SourceModule"='Settlement.Payment' AND "SourceDocumentId"=p."Id") THEN
     RAISE EXCEPTION 'Prepared/staged payment cannot commit without final journal state' USING ERRCODE='23514'; END IF;
   IF NOT EXISTS(SELECT 1 FROM trading."Parties" WHERE "Id"=p."PartyId" AND "CompanyId"=cid AND "Kind"=p."PartyKind"
     AND "Currency"=p."Currency" AND "ControlAccountId"=p."ControlAccountId") OR NOT EXISTS(SELECT 1 FROM trading."AccountMappings"
     WHERE "CompanyId"=cid AND "AccountId"=p."CashAccountId" AND "Role"=4) OR NOT EXISTS(SELECT 1 FROM trading."AccountMappings"
     WHERE "CompanyId"=cid AND "AccountId"=p."AdvanceAccountId" AND "Role"=CASE p."PartyKind" WHEN 0 THEN 7 ELSE 8 END) THEN
     RAISE EXCEPTION 'Payment party/account mappings mismatch' USING ERRCODE='23514'; END IF;
   IF p."State"=2 AND (p."ReversedAt" IS NULL OR p."ReversalDate"<p."PostingDate" OR NOT EXISTS(SELECT 1 FROM accounting."Journals"
     WHERE "Id"=p."ReversalJournalId" AND "CompanyId"=cid AND "State"=1 AND "ReversesJournalId"=p."JournalId" AND "PostingDate"=p."ReversalDate")) THEN
     RAISE EXCEPTION 'Invalid payment reversal journal/date' USING ERRCODE='23514'; END IF;
   IF p."IsRefund" THEN
     IF p."RefundPaymentId" IS NOT NULL THEN
       SELECT "PartyId","PartyKind","ControlAccountId","Currency","PostingDate" INTO sourceparty,sourcekind,sourcecontrol,sourcecurrency,sourcedate
       FROM trading."Payments" WHERE "Id"=p."RefundPaymentId" AND NOT "IsRefund";
     ELSE
       SELECT "PartyId",CASE WHEN "Kind"=2 THEN 0 ELSE 1 END,"ControlAccountId","Currency","PostingDate" INTO sourceparty,sourcekind,sourcecontrol,sourcecurrency,sourcedate
       FROM trading."Documents" WHERE "Id"=p."RefundCreditId" AND "State"=2 AND "Kind" IN(2,3);
     END IF;
     IF NOT FOUND OR (p."PartyId",p."PartyKind",p."ControlAccountId",p."Currency") IS DISTINCT FROM
       (sourceparty,sourcekind,sourcecontrol,sourcecurrency) OR p."PostingDate"<sourcedate THEN
       RAISE EXCEPTION 'Refund funding scope/date mismatch' USING ERRCODE='23514'; END IF;
   END IF;
 END LOOP;
 FOR a IN SELECT * FROM trading."Allocations" WHERE "CompanyId"=cid LOOP
   SELECT * INTO target FROM trading."Documents" WHERE "Id"=a."TargetDocumentId" AND "State"=2 AND "Kind" NOT IN(2,3);
   IF a."State"<>1 OR NOT FOUND OR (a."PartyId",a."PartyKind",a."ControlAccountId") IS DISTINCT FROM
     (target."PartyId",CASE WHEN target."Kind" IN(0,4) THEN 0 ELSE 1 END,target."ControlAccountId") OR a."EffectiveDate"<target."PostingDate" THEN
     RAISE EXCEPTION 'Allocation target scope/state/date invalid' USING ERRCODE='23514'; END IF;
   IF a."PaymentId" IS NOT NULL THEN
     SELECT "PartyId","PartyKind","ControlAccountId","Currency","PostingDate","AdvanceAccountId" INTO sourceparty,sourcekind,sourcecontrol,sourcecurrency,sourcedate,sourceadvance
     FROM trading."Payments" WHERE "Id"=a."PaymentId" AND NOT "IsRefund";
   ELSE
     SELECT "PartyId",CASE WHEN "Kind"=2 THEN 0 ELSE 1 END,"ControlAccountId","Currency","PostingDate",NULL::uuid INTO sourceparty,sourcekind,sourcecontrol,sourcecurrency,sourcedate,sourceadvance
     FROM trading."Documents" WHERE "Id"=a."CreditDocumentId" AND "State"=2 AND "Kind" IN(2,3);
   END IF;
   IF NOT FOUND OR (a."PartyId",a."PartyKind",a."ControlAccountId",target."Currency") IS DISTINCT FROM
     (sourceparty,sourcekind,sourcecontrol,sourcecurrency) OR a."EffectiveDate"<sourcedate OR
     (a."AdvanceAmount">0 AND a."AdvanceAccountId" IS DISTINCT FROM sourceadvance) THEN
     RAISE EXCEPTION 'Allocation funding scope/date invalid' USING ERRCODE='23514'; END IF;
   IF a."ReversesAllocationId" IS NOT NULL THEN
     SELECT * INTO o FROM trading."Allocations" WHERE "Id"=a."ReversesAllocationId" AND "ReversesAllocationId" IS NULL AND "State"=1;
     IF NOT FOUND OR a."EffectiveDate"<o."EffectiveDate" OR
       (a."PartyId",a."FundingKind",a."PaymentId",a."CreditDocumentId",a."TargetDocumentId",a."Amount",a."ControlAmount",a."AdvanceAmount",a."ControlAccountId",a."AdvanceAccountId") IS DISTINCT FROM
       (o."PartyId",o."FundingKind",o."PaymentId",o."CreditDocumentId",o."TargetDocumentId",o."Amount",o."ControlAmount",o."AdvanceAmount",o."ControlAccountId",o."AdvanceAccountId") THEN
       RAISE EXCEPTION 'Deallocation must negate original allocation exactly' USING ERRCODE='23514'; END IF;
   END IF;
   IF (a."AdvanceAmount"=0 AND a."JournalId" IS NOT NULL) OR (a."AdvanceAmount">0 AND NOT EXISTS(SELECT 1 FROM accounting."Journals" j
     WHERE j."Id"=a."JournalId" AND j."CompanyId"=cid AND j."State"=1 AND j."PostingDate"=a."EffectiveDate" AND
       ((a."ReversesAllocationId" IS NULL AND j."SourceModule"='Settlement.Allocation' AND j."SourceDocumentId"=a."Id")
       OR (a."ReversesAllocationId" IS NOT NULL AND j."ReversesJournalId"=o."JournalId")))) THEN
     RAISE EXCEPTION 'Advance allocation journal contract missing' USING ERRCODE='23514'; END IF;
 END LOOP;
 -- Check every event cutoff, including reversals: a current-only check could conceal historic double consumption.
 FOR atdate IN SELECT "PostingDate" FROM trading."Documents" WHERE "CompanyId"=cid AND "State"=2
   UNION SELECT "PostingDate" FROM trading."Payments" WHERE "CompanyId"=cid
   UNION SELECT "ReversalDate" FROM trading."Payments" WHERE "CompanyId"=cid AND "ReversalDate" IS NOT NULL
   UNION SELECT "EffectiveDate" FROM trading."Allocations" WHERE "CompanyId"=cid LOOP
   FOR d IN SELECT * FROM trading."Documents" WHERE "CompanyId"=cid AND "State"=2 AND "Kind" NOT IN(2,3) AND "PostingDate"<=atdate LOOP
     SELECT coalesce(sum(CASE WHEN "ReversesAllocationId" IS NULL THEN "Amount" ELSE -"Amount" END),0) INTO usedcontrol
       FROM trading."Allocations" WHERE "TargetDocumentId"=d."Id" AND "EffectiveDate"<=atdate;
     IF usedcontrol<0 OR usedcontrol>d."Total" THEN RAISE EXCEPTION 'Target overallocated at cutoff' USING ERRCODE='23514'; END IF;
   END LOOP;
   FOR sourceparty,initialcontrol,initialadvance,sourcekind IN
     SELECT "Id",CASE WHEN "State"=2 AND "ReversalDate"<=atdate THEN 0 ELSE "ControlAmount" END,
       CASE WHEN "State"=2 AND "ReversalDate"<=atdate THEN 0 ELSE "AdvanceAmount" END,0
       FROM trading."Payments" WHERE "CompanyId"=cid AND NOT "IsRefund" AND "PostingDate"<=atdate
     UNION ALL SELECT "Id","Total",0,1 FROM trading."Documents" WHERE "CompanyId"=cid AND "State"=2 AND "Kind" IN(2,3) AND "PostingDate"<=atdate LOOP
     SELECT coalesce(sum(CASE WHEN "ReversesAllocationId" IS NULL THEN "ControlAmount" ELSE -"ControlAmount" END),0),
       coalesce(sum(CASE WHEN "ReversesAllocationId" IS NULL THEN "AdvanceAmount" ELSE -"AdvanceAmount" END),0) INTO usedcontrol,usedadvance
       FROM trading."Allocations" WHERE "EffectiveDate"<=atdate AND
       (CASE WHEN sourcekind=0 THEN "PaymentId"=sourceparty ELSE "CreditDocumentId"=sourceparty END);
     SELECT usedcontrol+coalesce(sum("ControlAmount"),0),usedadvance+coalesce(sum("AdvanceAmount"),0) INTO usedcontrol,usedadvance
       FROM trading."Payments" WHERE "IsRefund" AND "PostingDate"<=atdate AND ("State"<>2 OR "ReversalDate">atdate)
       AND (CASE WHEN sourcekind=0 THEN "RefundPaymentId"=sourceparty ELSE "RefundCreditId"=sourceparty END);
     IF usedcontrol<0 OR usedadvance<0 OR usedcontrol>initialcontrol OR usedadvance>initialadvance THEN
       RAISE EXCEPTION 'Funding overconsumed at cutoff' USING ERRCODE='23514'; END IF;
   END LOOP;
 END LOOP;
END $$;

CREATE FUNCTION trading.deferred_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_TABLE_SCHEMA='accounting' THEN
   PERFORM trading.assert_journal_v1(NEW."Id");
 ELSIF TG_TABLE_NAME='Documents' THEN
   IF NEW."State"=2 THEN PERFORM trading.assert_company_v1(NEW."CompanyId"); END IF;
 ELSE
   PERFORM trading.assert_company_v1(NEW."CompanyId");
 END IF;
 RETURN NULL;
END $$;
DO $$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['Parties','AccountMappings','TaxCodes','TaxRates','Documents','DocumentLines','Payments','Allocations'] LOOP
   EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
   EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON trading.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['Documents','Payments','Allocations'] LOOP
   EXECUTE format('CREATE CONSTRAINT TRIGGER z_source_contract AFTER INSERT OR UPDATE ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.deferred_guard_v1()',t);
 END LOOP;
END $$;
CREATE CONSTRAINT TRIGGER z_trading_contract AFTER INSERT OR UPDATE ON accounting."Journals" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.deferred_guard_v1();
CREATE TRIGGER b_mapping_guard BEFORE INSERT OR UPDATE OR DELETE ON trading."AccountMappings" FOR EACH ROW EXECUTE FUNCTION trading.mapping_guard_v1();
CREATE TRIGGER b_party_guard BEFORE INSERT OR UPDATE OR DELETE ON trading."Parties" FOR EACH ROW EXECUTE FUNCTION trading.party_guard_v1();
CREATE TRIGGER b_tax_code_guard BEFORE INSERT OR UPDATE OR DELETE ON trading."TaxCodes" FOR EACH ROW EXECUTE FUNCTION trading.tax_code_guard_v1();
CREATE TRIGGER b_tax_rate_guard BEFORE UPDATE OR DELETE ON trading."TaxRates" FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1();
CREATE TRIGGER b_document_guard BEFORE INSERT OR UPDATE OR DELETE ON trading."Documents" FOR EACH ROW EXECUTE FUNCTION trading.document_guard_v1();
CREATE TRIGGER b_document_line_guard BEFORE INSERT OR UPDATE OR DELETE ON trading."DocumentLines" FOR EACH ROW EXECUTE FUNCTION trading.document_line_guard_v1();
CREATE TRIGGER b_payment_guard BEFORE INSERT OR UPDATE OR DELETE ON trading."Payments" FOR EACH ROW EXECUTE FUNCTION trading.settlement_guard_v1();
CREATE TRIGGER b_allocation_guard BEFORE INSERT OR UPDATE OR DELETE ON trading."Allocations" FOR EACH ROW EXECUTE FUNCTION trading.settlement_guard_v1();
CREATE TRIGGER trading_account_guard BEFORE UPDATE ON accounting."Accounts" FOR EACH ROW EXECUTE FUNCTION trading.mapped_account_guard_v1();
CREATE TRIGGER trading_currency_guard BEFORE UPDATE ON organizations."Companies" FOR EACH ROW EXECUTE FUNCTION trading.company_currency_guard_v1();
""";
    public const string Down="""
DROP FUNCTION trading.deferred_guard_v1() CASCADE;
DROP FUNCTION trading.assert_company_v1(uuid);
DROP FUNCTION trading.assert_journal_v1(uuid);
DROP FUNCTION trading.expected_lines_v1(uuid);
DROP FUNCTION trading.settlement_guard_v1() CASCADE;
DROP FUNCTION trading.mapping_guard_v1() CASCADE;
DROP FUNCTION trading.party_guard_v1() CASCADE;
DROP FUNCTION trading.tax_code_guard_v1() CASCADE;
DROP FUNCTION trading.document_guard_v1() CASCADE;
DROP FUNCTION trading.document_line_guard_v1() CASCADE;
DROP FUNCTION trading.mapped_account_guard_v1() CASCADE;
DROP FUNCTION trading.company_currency_guard_v1() CASCADE;
DROP FUNCTION trading.units_v1(text);
DROP FUNCTION trading.round_even_v1(numeric,integer);
""";
}
