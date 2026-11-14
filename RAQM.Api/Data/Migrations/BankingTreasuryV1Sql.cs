namespace RAQM.Api.Data.Migrations;

// Historical PostgreSQL DDL for the BankingTreasuryV1 migration.
internal static class BankingTreasuryV1Sql
{
    public const string Up = """
ALTER TABLE banking."Statements" ADD CONSTRAINT "EX_Statement_Window" EXCLUDE USING gist
("CompanyId" WITH =, "BankAccountId" WITH =, daterange("Start","End",'[]') WITH &&);

CREATE FUNCTION banking.account_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Treasury accounts must be retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' AND (to_jsonb(NEW)-ARRAY['Name','Active','CustodianId','Version']) IS DISTINCT FROM
    (to_jsonb(OLD)-ARRAY['Name','Active','CustodianId','Version']) THEN
   RAISE EXCEPTION 'Treasury account identity and GL mapping are immutable' USING ERRCODE='23514'; END IF;
 IF NOT EXISTS(SELECT 1 FROM organizations."Companies" WHERE "Id"=NEW."CompanyId" AND "BaseCurrency"=NEW."Currency")
   OR NOT EXISTS(SELECT 1 FROM accounting."Accounts" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."LedgerAccountId"
      AND "Classification"=0 AND "IsPosting" AND NOT "IsContra")
   OR (NEW."Kind"=0 AND NEW."Active" AND NOT EXISTS(SELECT 1 FROM banking."Banks" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."BankProfileId" AND "Active")) THEN
   RAISE EXCEPTION 'Invalid treasury currency, asset mapping or bank profile' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION banking.mapping_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_TABLE_SCHEMA='accounting' THEN
   IF (NEW."Classification",NEW."IsPosting",NEW."IsContra") IS DISTINCT FROM (OLD."Classification",OLD."IsPosting",OLD."IsContra")
     AND EXISTS(SELECT 1 FROM banking."TreasuryAccounts" WHERE "CompanyId"=OLD."CompanyId" AND "LedgerAccountId"=OLD."Id") THEN
     RAISE EXCEPTION 'Treasury ledger account semantics are immutable' USING ERRCODE='23514'; END IF;
 ELSE
   IF NEW."BaseCurrency"<>OLD."BaseCurrency" AND EXISTS(SELECT 1 FROM banking."TreasuryAccounts" WHERE "CompanyId"=OLD."Id") THEN
     RAISE EXCEPTION 'Treasury setup freezes company currency' USING ERRCODE='23514'; END IF;
 END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION banking.payment_attribution_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE account banking."TreasuryAccounts"%ROWTYPE;
BEGIN
 IF TG_OP='UPDATE' THEN
   IF NEW."BankAccountId" IS DISTINCT FROM OLD."BankAccountId" THEN
     RAISE EXCEPTION 'Payment attribution cannot change' USING ERRCODE='23514'; END IF;
   RETURN NEW;
 END IF;
 SELECT * INTO account FROM banking."TreasuryAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "LedgerAccountId"=NEW."CashAccountId";
 IF (FOUND AND (NOT account."Active" OR account."Kind"=2)) OR NEW."BankAccountId" IS DISTINCT FROM
   (CASE WHEN account."Kind"=0 THEN account."Id" ELSE NULL::uuid END) THEN
   RAISE EXCEPTION 'Settlement account attribution is invalid' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION banking.statement_line_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE statement banking."Statements"%ROWTYPE; units int;
BEGIN
 SELECT * INTO statement FROM banking."Statements" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."StatementId";
 SELECT trading.units_v1("Currency") INTO units FROM banking."TreasuryAccounts" WHERE "Id"=NEW."BankAccountId";
 IF NEW."Sequence" NOT BETWEEN 1 AND statement."LineCount" OR NEW."BookingDate" NOT BETWEEN statement."Start" AND statement."End"
   OR NEW."Amount"<>trading.round_even_v1(NEW."Amount",units) THEN
   RAISE EXCEPTION 'Statement line sequence, date or precision is invalid' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION banking.statement_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE n bigint; delta numeric; units int; previous banking."Statements"%ROWTYPE;
BEGIN
 SELECT trading.units_v1("Currency") INTO units FROM banking."TreasuryAccounts" WHERE "Id"=NEW."BankAccountId" AND "Kind"=0;
 SELECT count(*),coalesce(sum("Amount"),0) INTO n,delta FROM banking."StatementLines" WHERE "StatementId"=NEW."Id";
 IF units IS NULL OR n<>NEW."LineCount" OR NEW."Opening"+delta<>NEW."Closing"
   OR NEW."Opening"<>trading.round_even_v1(NEW."Opening",units) OR NEW."Closing"<>trading.round_even_v1(NEW."Closing",units) THEN
   RAISE EXCEPTION 'Statement arithmetic or account mismatch' USING ERRCODE='23514'; END IF;
 SELECT * INTO previous FROM banking."Statements" WHERE "BankAccountId"=NEW."BankAccountId" AND "Id"<>NEW."Id" ORDER BY "End" DESC LIMIT 1;
 IF FOUND AND (previous."End"+1<>NEW."Start" OR previous."Closing"<>NEW."Opening") THEN
   RAISE EXCEPTION 'Statement continuity mismatch' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION banking.match_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE session banking."Sessions"%ROWTYPE; parent banking."Matches"%ROWTYPE;
 statement banking."Statements"%ROWTYPE; book accounting."JournalLines"%ROWTYPE; journal accounting."Journals"%ROWTYPE;
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Matching evidence must be retained' USING ERRCODE='23514'; END IF;
 IF TG_TABLE_NAME='Matches' THEN
   SELECT * INTO session FROM banking."Sessions" WHERE "Id"=NEW."SessionId";
   IF TG_OP='UPDATE' AND (OLD."ReleasedAt" IS NOT NULL OR NEW."ReleasedAt" IS NULL OR NEW."ReleasedBy" IS NULL
     OR nullif(trim(NEW."ReleaseReason"),'') IS NULL OR
     (to_jsonb(NEW)-ARRAY['ReleasedAt','ReleasedBy','ReleaseReason']) IS DISTINCT FROM
     (to_jsonb(OLD)-ARRAY['ReleasedAt','ReleasedBy','ReleaseReason'])) THEN
     RAISE EXCEPTION 'Match can only be released once' USING ERRCODE='23514'; END IF;
   IF NOT EXISTS(SELECT 1 FROM banking."StatementLines" WHERE "Id"=NEW."StatementLineId" AND "StatementId"=session."StatementId"
     AND "Amount"=NEW."Amount") THEN RAISE EXCEPTION 'Match statement amount or scope mismatch' USING ERRCODE='23514'; END IF;
 ELSE
   SELECT * INTO parent FROM banking."Matches" WHERE "Id"=NEW."MatchId";
   SELECT * INTO session FROM banking."Sessions" WHERE "Id"=parent."SessionId";
   SELECT * INTO statement FROM banking."Statements" WHERE "Id"=session."StatementId";
   SELECT * INTO book FROM accounting."JournalLines" WHERE "Id"=NEW."JournalLineId";
   SELECT * INTO journal FROM accounting."Journals" WHERE "Id"=book."JournalId";
   IF TG_OP='UPDATE' AND (OLD."Released" OR NOT NEW."Released" OR
     (to_jsonb(NEW)-'Released') IS DISTINCT FROM (to_jsonb(OLD)-'Released')) THEN
     RAISE EXCEPTION 'Match item is immutable except release' USING ERRCODE='23514'; END IF;
   IF NEW."Amount"<>book."Debit"-book."Credit" OR sign(NEW."Amount")<>sign(parent."Amount")
     OR journal."State"<>1 OR journal."PostingDate">statement."End"
     OR journal."PostingDate"<(SELECT min("Start") FROM banking."Statements" WHERE "BankAccountId"=session."BankAccountId")
     OR NOT EXISTS(SELECT 1 FROM banking."TreasuryAccounts" WHERE "Id"=session."BankAccountId" AND "LedgerAccountId"=book."AccountId") THEN
     RAISE EXCEPTION 'Match requires an eligible posted book line on the same bank and cutoff' USING ERRCODE='23514'; END IF;
 END IF;
 IF session."State" IS DISTINCT FROM 0 THEN
   RAISE EXCEPTION 'Matching session is locked' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION banking.match_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE mid uuid; parent banking."Matches"%ROWTYPE; n bigint; total numeric;
BEGIN
 IF TG_TABLE_NAME='Matches' THEN mid:=NEW."Id"; ELSE mid:=NEW."MatchId"; END IF;
 SELECT * INTO parent FROM banking."Matches" WHERE "Id"=mid;
 SELECT count(*),coalesce(sum("Amount"),0) INTO n,total FROM banking."MatchItems" WHERE "MatchId"=mid;
 IF n NOT BETWEEN 1 AND 50 OR total<>parent."Amount" OR EXISTS(SELECT 1 FROM banking."MatchItems"
    WHERE "MatchId"=mid AND "Released"<>(parent."ReleasedAt" IS NOT NULL)) THEN
   RAISE EXCEPTION 'Match item amounts and release flags must conserve the whole movement' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

CREATE FUNCTION banking.session_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE statement banking."Statements"%ROWTYPE; book numeric; deposits numeric; withdrawals numeric;
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Reconciliation history must be retained' USING ERRCODE='23514'; END IF;
 SELECT * INTO statement FROM banking."Statements" WHERE "Id"=NEW."StatementId";
 IF TG_OP='INSERT' THEN
   IF NEW."State"<>0 OR EXISTS(SELECT 1 FROM banking."Statements" older WHERE older."BankAccountId"=NEW."BankAccountId"
     AND older."End"<statement."Start" AND NOT EXISTS(SELECT 1 FROM banking."Sessions" s WHERE s."StatementId"=older."Id" AND s."State"=2)) THEN
     RAISE EXCEPTION 'Reconcile statements in order' USING ERRCODE='23514'; END IF;
   RETURN NEW;
 END IF;
 IF (NEW."StatementId",NEW."BankAccountId") IS DISTINCT FROM (OLD."StatementId",OLD."BankAccountId") OR NOT
    ((OLD."State"=0 AND NEW."State" IN(0,1)) OR (OLD."State"=1 AND NEW."State" IN(0,2)) OR (OLD."State"=2 AND NEW."State"=0)) THEN
   RAISE EXCEPTION 'Invalid reconciliation state transition' USING ERRCODE='23514'; END IF;
 IF OLD."State"=2 AND EXISTS(SELECT 1 FROM banking."Sessions" s JOIN banking."Statements" st ON st."Id"=s."StatementId"
     WHERE s."BankAccountId"=NEW."BankAccountId" AND st."End">statement."End") THEN
   RAISE EXCEPTION 'A later reconciliation depends on this session' USING ERRCODE='23514'; END IF;
 IF NEW."State"=2 THEN
   WITH entries AS (
     SELECT l."Id",j."PostingDate",l."Debit"-l."Credit" amount FROM accounting."JournalLines" l
     JOIN accounting."Journals" j ON j."Id"=l."JournalId"
     JOIN banking."TreasuryAccounts" a ON a."CompanyId"=l."CompanyId" AND a."LedgerAccountId"=l."AccountId"
     WHERE a."Id"=NEW."BankAccountId" AND j."State"=1 AND j."PostingDate"<=statement."End"
   ), available AS (
     SELECT e.amount FROM entries e WHERE e."PostingDate">=(SELECT min("Start") FROM banking."Statements" WHERE "BankAccountId"=NEW."BankAccountId")
     AND NOT EXISTS(SELECT 1 FROM banking."MatchItems" i JOIN banking."Matches" m ON m."Id"=i."MatchId"
       JOIN banking."Sessions" s ON s."Id"=m."SessionId" JOIN banking."Statements" st ON st."Id"=s."StatementId"
       WHERE i."JournalLineId"=e."Id" AND NOT i."Released" AND st."End"<=statement."End")
   )
   SELECT (SELECT coalesce(sum(amount),0) FROM entries),coalesce(sum(greatest(amount,0)),0),
      coalesce(sum(greatest(-amount,0)),0) INTO book,deposits,withdrawals FROM available;
   IF (book,deposits,withdrawals) IS DISTINCT FROM (NEW."FinalizedBook",NEW."FinalizedDeposits",NEW."FinalizedWithdrawals")
     OR statement."Closing"+deposits-withdrawals<>book OR EXISTS(SELECT 1 FROM banking."StatementLines" l
     WHERE l."StatementId"=statement."Id" AND NOT EXISTS(SELECT 1 FROM banking."Matches" m
       WHERE m."StatementLineId"=l."Id" AND m."SessionId"=NEW."Id" AND m."ReleasedAt" IS NULL)) THEN
     RAISE EXCEPTION 'Finalization must explain the complete bank cutoff' USING ERRCODE='23514'; END IF;
 END IF;
 RETURN NEW;
END $$;
""";

    public const string Posting = """
CREATE FUNCTION banking.movement_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE source banking."TreasuryAccounts"%ROWTYPE; target banking."TreasuryAccounts"%ROWTYPE;
 line banking."StatementLines"%ROWTYPE; counted banking."CashCounts"%ROWTYPE; variance numeric; units int;
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Treasury evidence must be retained' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' THEN
   IF OLD."State"=2 OR NEW."State"<OLD."State" OR (OLD."State"=0 AND NEW."State"=2) OR
     (to_jsonb(NEW)-ARRAY['State','JournalId','ReversalJournalId','Version','PostedBy','ReversedBy','ApprovalHash','ReversalKey','ReversalHash']) IS DISTINCT FROM
     (to_jsonb(OLD)-ARRAY['State','JournalId','ReversalJournalId','Version','PostedBy','ReversedBy','ApprovalHash','ReversalKey','ReversalHash']) OR
     (OLD."State"=1 AND (NEW."JournalId",NEW."PostedBy",NEW."ApprovalHash") IS DISTINCT FROM (OLD."JournalId",OLD."PostedBy",OLD."ApprovalHash")) THEN
     RAISE EXCEPTION 'Treasury movement payload is immutable' USING ERRCODE='23514'; END IF;
   RETURN NEW;
 END IF;
 SELECT * INTO source FROM banking."TreasuryAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."AccountId" AND "Active";
 units:=trading.units_v1(source."Currency");
 IF NOT FOUND OR NEW."State"<>0 OR NEW."Amount"<>trading.round_even_v1(NEW."Amount",units) THEN
   RAISE EXCEPTION 'Movement requires a valid active source and minor units' USING ERRCODE='23514'; END IF;
 IF NEW."Kind"=0 THEN
   SELECT * INTO target FROM banking."TreasuryAccounts" WHERE "CompanyId"=NEW."CompanyId" AND "Id"=NEW."DestinationId" AND "Active";
   IF NOT FOUND OR (NEW."DebitLedgerId",NEW."CreditLedgerId") IS DISTINCT FROM (target."LedgerAccountId",source."LedgerAccountId")
     OR NEW."OffsetLedgerId" IS NOT NULL OR NEW."StatementLineId" IS NOT NULL OR NEW."CashCountId" IS NOT NULL THEN
     RAISE EXCEPTION 'Transfer GL mappings do not match treasury accounts' USING ERRCODE='23514'; END IF;
   RETURN NEW;
 END IF;
 IF NEW."DestinationId" IS NOT NULL OR NEW."OffsetLedgerId" IS NULL THEN
   RAISE EXCEPTION 'Invalid offset movement' USING ERRCODE='23514'; END IF;
 IF NEW."Kind" IN(1,2) THEN
   SELECT * INTO line FROM banking."StatementLines" WHERE "Id"=NEW."StatementLineId" AND "BankAccountId"=NEW."AccountId";
   IF NOT FOUND OR NEW."PostingDate"<>line."BookingDate" OR NEW."CashCountId" IS NOT NULL OR
       line."Amount"<>(CASE WHEN NEW."Kind"=1 THEN -NEW."Amount" ELSE NEW."Amount" END) THEN
     RAISE EXCEPTION 'Bank adjustment must match source line, date and direction' USING ERRCODE='23514'; END IF;
   variance:=line."Amount";
 ELSIF NEW."Kind"=4 THEN
   SELECT * INTO counted FROM banking."CashCounts" WHERE "Id"=NEW."CashCountId" AND "AccountId"=NEW."AccountId";
   variance:=counted."CountedAmount"-counted."BookAmount";
   IF NOT FOUND OR source."Kind"<>2 OR NEW."PostingDate"<>counted."CountDate" OR NEW."Amount"<>abs(variance) OR NEW."StatementLineId" IS NOT NULL THEN
     RAISE EXCEPTION 'Count correction does not match the recorded variance' USING ERRCODE='23514'; END IF;
 ELSE
   IF source."Kind"<>(CASE WHEN NEW."Kind"=3 THEN 2 ELSE 1 END) OR NEW."StatementLineId" IS NOT NULL OR NEW."CashCountId" IS NOT NULL THEN
     RAISE EXCEPTION 'Cash movement type does not match its treasury account' USING ERRCODE='23514'; END IF;
   variance:=CASE WHEN NEW."Kind"=5 THEN NEW."Amount" ELSE -NEW."Amount" END;
 END IF;
 IF (NEW."DebitLedgerId",NEW."CreditLedgerId") IS DISTINCT FROM
    (CASE WHEN variance>0 THEN source."LedgerAccountId" ELSE NEW."OffsetLedgerId" END,
     CASE WHEN variance>0 THEN NEW."OffsetLedgerId" ELSE source."LedgerAccountId" END)
   OR NOT EXISTS(SELECT 1 FROM accounting."Accounts" WHERE "Id"=NEW."OffsetLedgerId" AND "CompanyId"=NEW."CompanyId"
      AND "Classification"=CASE WHEN variance>0 THEN 3 ELSE 4 END AND "IsPosting" AND "IsActive" AND NOT "IsContra") THEN
   RAISE EXCEPTION 'Offset account or accounting direction is invalid' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;

CREATE FUNCTION banking.assert_movement_v1(mid uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE m banking."Movements"%ROWTYPE; module text;
BEGIN
 SELECT * INTO m FROM banking."Movements" WHERE "Id"=mid;
 IF NOT FOUND THEN RAISE EXCEPTION 'Treasury source missing' USING ERRCODE='23514'; END IF;
 IF m."State"=0 AND m."Kind"=0 AND m."PostedBy" IS NULL AND m."ApprovalHash" IS NULL THEN RETURN; END IF;
 module:=CASE m."Kind" WHEN 0 THEN 'Treasury.Transfer' WHEN 1 THEN 'Treasury.BankFee' WHEN 2 THEN 'Treasury.BankInterest'
   WHEN 3 THEN 'Treasury.PettyExpense' WHEN 4 THEN 'Treasury.CountCorrection' WHEN 5 THEN 'Treasury.CashReceipt' ELSE 'Treasury.CashPayment' END;
 IF m."State"=0 OR (m."Kind"=0 AND m."ApprovalHash" IS NULL) OR NOT EXISTS(SELECT 1 FROM accounting."Journals" WHERE "Id"=m."JournalId" AND "CompanyId"=m."CompanyId"
   AND "State"=1 AND "PostingDate"=m."PostingDate" AND "SourceModule"=module AND "SourceDocumentId"=m."Id" AND "PostedBy"=m."PostedBy")
   OR (SELECT count(*) FROM accounting."JournalLines" WHERE "JournalId"=m."JournalId")<>2
   OR NOT EXISTS(SELECT 1 FROM accounting."JournalLines" WHERE "JournalId"=m."JournalId" AND "AccountId"=m."DebitLedgerId" AND "Debit"=m."Amount" AND "Credit"=0)
   OR NOT EXISTS(SELECT 1 FROM accounting."JournalLines" WHERE "JournalId"=m."JournalId" AND "AccountId"=m."CreditLedgerId" AND "Credit"=m."Amount" AND "Debit"=0) THEN
   RAISE EXCEPTION 'Treasury source and its exact journal must commit together' USING ERRCODE='23514'; END IF;
 IF (m."State"=1 AND m."ReversalKey" IS NOT NULL) OR (m."State"=2 AND NOT EXISTS(SELECT 1 FROM accounting."Journals"
   WHERE "Id"=m."ReversalJournalId" AND "CompanyId"=m."CompanyId" AND "State"=1 AND "ReversesJournalId"=m."JournalId"
     AND "PostedBy"=m."ReversedBy" AND "PostingDate">=m."PostingDate")) THEN
   RAISE EXCEPTION 'Treasury reversal and source must commit together' USING ERRCODE='23514'; END IF;
END $$;

CREATE FUNCTION banking.posting_contract_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE mid uuid;
BEGIN
 IF TG_TABLE_SCHEMA='banking' THEN
   PERFORM banking.assert_movement_v1(NEW."Id");
   RETURN NULL;
 END IF;
 IF NEW."State"<>1 THEN RETURN NULL; END IF;
 IF NEW."SourceModule" LIKE 'Treasury.%' THEN
   IF NOT EXISTS(SELECT 1 FROM banking."Movements" WHERE "Id"=NEW."SourceDocumentId" AND "JournalId"=NEW."Id") THEN
     RAISE EXCEPTION 'Treasury journal needs a finalized source' USING ERRCODE='23514'; END IF;
   PERFORM banking.assert_movement_v1(NEW."SourceDocumentId");
 END IF;
 SELECT "Id" INTO mid FROM banking."Movements" WHERE "JournalId"=NEW."ReversesJournalId";
 IF FOUND AND NOT EXISTS(SELECT 1 FROM banking."Movements" WHERE "Id"=mid AND "ReversalJournalId"=NEW."Id" AND "State"=2) THEN
   RAISE EXCEPTION 'Treasury reversal needs a finalized source' USING ERRCODE='23514'; END IF;
 IF EXISTS(SELECT 1 FROM accounting."JournalLines" l JOIN banking."TreasuryAccounts" a
   ON a."CompanyId"=l."CompanyId" AND a."LedgerAccountId"=l."AccountId" JOIN banking."Sessions" s ON s."BankAccountId"=a."Id"
   JOIN banking."Statements" b ON b."Id"=s."StatementId"
   WHERE l."JournalId"=NEW."Id" AND s."State"=2 AND b."End">=NEW."PostingDate") THEN
   RAISE EXCEPTION 'Posting would change a finalized reconciliation cutoff' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;

DO $$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['Banks','TreasuryAccounts','Statements','StatementLines','Sessions','Matches','MatchItems','Movements','CashCounts'] LOOP
   EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON banking.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
   EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON banking.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['Statements','StatementLines','CashCounts'] LOOP
   EXECUTE format('CREATE TRIGGER b_immutable BEFORE UPDATE OR DELETE ON banking.%I FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['Matches','MatchItems'] LOOP
   EXECUTE format('CREATE TRIGGER b_match_guard BEFORE INSERT OR UPDATE OR DELETE ON banking.%I FOR EACH ROW EXECUTE FUNCTION banking.match_guard_v1()',t);
   EXECUTE format('CREATE CONSTRAINT TRIGGER z_match_contract AFTER INSERT OR UPDATE ON banking.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION banking.match_contract_v1()',t);
 END LOOP;
END $$;
CREATE TRIGGER b_bank_retain BEFORE DELETE ON banking."Banks" FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1();
CREATE TRIGGER b_treasury_account BEFORE INSERT OR UPDATE OR DELETE ON banking."TreasuryAccounts" FOR EACH ROW EXECUTE FUNCTION banking.account_guard_v1();
CREATE TRIGGER b_statement_line BEFORE INSERT ON banking."StatementLines" FOR EACH ROW EXECUTE FUNCTION banking.statement_line_v1();
CREATE CONSTRAINT TRIGGER z_statement_contract AFTER INSERT ON banking."Statements" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION banking.statement_contract_v1();
CREATE TRIGGER b_session_guard BEFORE INSERT OR UPDATE OR DELETE ON banking."Sessions" FOR EACH ROW EXECUTE FUNCTION banking.session_guard_v1();
CREATE TRIGGER b_movement_guard BEFORE INSERT OR UPDATE OR DELETE ON banking."Movements" FOR EACH ROW EXECUTE FUNCTION banking.movement_guard_v1();
CREATE CONSTRAINT TRIGGER z_movement_contract AFTER INSERT OR UPDATE ON banking."Movements" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION banking.posting_contract_v1();
CREATE CONSTRAINT TRIGGER z_treasury_contract AFTER INSERT OR UPDATE ON accounting."Journals" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION banking.posting_contract_v1();
CREATE TRIGGER treasury_mapping_guard BEFORE UPDATE ON accounting."Accounts" FOR EACH ROW EXECUTE FUNCTION banking.mapping_guard_v1();
CREATE TRIGGER treasury_currency_guard BEFORE UPDATE ON organizations."Companies" FOR EACH ROW EXECUTE FUNCTION banking.mapping_guard_v1();
CREATE TRIGGER treasury_payment_attribution BEFORE INSERT OR UPDATE ON trading."Payments" FOR EACH ROW EXECUTE FUNCTION banking.payment_attribution_v1();
""";

    public const string Down = """
DROP FUNCTION banking.posting_contract_v1() CASCADE;
DROP FUNCTION banking.assert_movement_v1(uuid);
DROP FUNCTION banking.movement_guard_v1() CASCADE;
DROP FUNCTION banking.session_guard_v1() CASCADE;
DROP FUNCTION banking.match_contract_v1() CASCADE;
DROP FUNCTION banking.match_guard_v1() CASCADE;
DROP FUNCTION banking.statement_contract_v1() CASCADE;
DROP FUNCTION banking.statement_line_v1() CASCADE;
DROP FUNCTION banking.payment_attribution_v1() CASCADE;
DROP FUNCTION banking.mapping_guard_v1() CASCADE;
DROP FUNCTION banking.account_guard_v1() CASCADE;
""";
}
