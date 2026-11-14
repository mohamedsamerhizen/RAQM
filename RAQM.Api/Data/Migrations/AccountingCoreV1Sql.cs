namespace RAQM.Api.Data.Migrations;

// Immutable custom DDL for AccountingCoreV1. Future changes require a new migration.
internal static class AccountingCoreV1Sql
{
    public const string Up = """
ALTER TABLE accounting."FiscalYears" ADD CONSTRAINT "EX_FiscalYear_Overlap"
EXCLUDE USING gist ("CompanyId" WITH =, daterange("StartDate", "EndDate", '[]') WITH &&);
ALTER TABLE accounting."Periods" ADD CONSTRAINT "EX_Period_Overlap"
EXCLUDE USING gist ("CompanyId" WITH =, daterange("StartDate", "EndDate", '[]') WITH &&);

CREATE FUNCTION accounting.scope_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE c uuid;
BEGIN
  IF TG_OP = 'DELETE' THEN c := OLD."CompanyId"; ELSE c := NEW."CompanyId"; END IF;
  PERFORM 1 FROM organizations."Companies" WHERE "Id" = c FOR UPDATE;
  IF TG_OP = 'UPDATE' AND (NEW."Id",NEW."TenantId",NEW."OrganizationId",NEW."CompanyId") IS DISTINCT FROM
    (OLD."Id",OLD."TenantId",OLD."OrganizationId",OLD."CompanyId") THEN
    RAISE EXCEPTION 'Financial identity and scope are immutable' USING ERRCODE='23514';
  END IF;
  IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
  RETURN NEW;
END $$;

CREATE FUNCTION accounting.account_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE pid uuid; p accounting."Accounts"%ROWTYPE; seen uuid[];
BEGIN
  IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Accounts are retained; deactivate instead' USING ERRCODE='23514'; END IF;
  IF TG_OP = 'UPDATE' AND (NEW."Code",NEW."Classification",NEW."NormalBalance",NEW."IsContra",NEW."IsPosting",NEW."ParentId")
    IS DISTINCT FROM (OLD."Code",OLD."Classification",OLD."NormalBalance",OLD."IsContra",OLD."IsPosting",OLD."ParentId")
    AND EXISTS (WITH RECURSIVE family AS (
      SELECT OLD."Id" AS id UNION SELECT a."Id" FROM accounting."Accounts" a JOIN family f ON a."ParentId"=f.id WHERE a."CompanyId"=OLD."CompanyId")
      SELECT 1 FROM accounting."JournalLines" l JOIN accounting."Journals" j ON j."Id"=l."JournalId"
      WHERE l."CompanyId"=OLD."CompanyId" AND l."AccountId" IN (SELECT id FROM family) AND j."State"=1) THEN
    RAISE EXCEPTION 'Posted account structure is immutable' USING ERRCODE='23514';
  END IF;
  IF EXISTS (SELECT 1 FROM accounting."Accounts" a WHERE a."CompanyId"=NEW."CompanyId" AND a."ParentId"=NEW."Id"
    AND (NEW."IsPosting" OR (NOT NEW."IsActive" AND a."IsActive") OR a."Classification"<>NEW."Classification")) THEN
    RAISE EXCEPTION 'Account settings conflict with children' USING ERRCODE='23514';
  END IF;
  pid:=NEW."ParentId"; seen:=ARRAY[NEW."Id"];
  WHILE pid IS NOT NULL LOOP
    IF pid=ANY(seen) THEN RAISE EXCEPTION 'Account hierarchy cycle' USING ERRCODE='23514'; END IF;
    seen:=array_append(seen,pid);
    SELECT * INTO p FROM accounting."Accounts" WHERE "Id"=pid AND "CompanyId"=NEW."CompanyId";
    IF NOT FOUND OR p."IsPosting" OR NOT p."IsActive" OR p."Classification"<>NEW."Classification" THEN
      RAISE EXCEPTION 'Invalid parent account' USING ERRCODE='23514';
    END IF;
    pid:=p."ParentId";
  END LOOP;
  RETURN NEW;
END $$;

CREATE FUNCTION accounting.year_insert_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM accounting."OpeningBatches" b JOIN accounting."Journals" j ON j."Id"=b."JournalId"
      WHERE b."CompanyId"=NEW."CompanyId" AND j."CompanyId"=NEW."CompanyId" AND j."PostingDate">NEW."StartDate") THEN
    RAISE EXCEPTION 'Earlier fiscal year would invalidate opening-balance anchor' USING ERRCODE='23514';
  END IF;
  RETURN NEW;
END $$;

CREATE FUNCTION accounting.period_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE y accounting."FiscalYears"%ROWTYPE;
BEGIN
  IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Fiscal periods cannot be deleted' USING ERRCODE='23514'; END IF;
  IF TG_OP='UPDATE' AND (OLD."State"=2 OR (NEW."FiscalYearId",NEW."Number",NEW."StartDate",NEW."EndDate")
      IS DISTINCT FROM (OLD."FiscalYearId",OLD."Number",OLD."StartDate",OLD."EndDate")) THEN
    RAISE EXCEPTION 'Period calendar and locked periods are immutable' USING ERRCODE='23514';
  END IF;
  SELECT * INTO y FROM accounting."FiscalYears" WHERE "Id"=NEW."FiscalYearId" AND "CompanyId"=NEW."CompanyId";
  IF NOT FOUND OR NEW."StartDate"<y."StartDate" OR NEW."EndDate">y."EndDate" THEN
    RAISE EXCEPTION 'Period lies outside its fiscal year' USING ERRCODE='23514';
  END IF;
  RETURN NEW;
END $$;

CREATE FUNCTION accounting.line_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE j accounting."Journals"%ROWTYPE; jid uuid; cid uuid;
BEGIN
  IF TG_OP='DELETE' THEN jid:=OLD."JournalId"; cid:=OLD."CompanyId"; ELSE jid:=NEW."JournalId"; cid:=NEW."CompanyId"; END IF;
  SELECT * INTO j FROM accounting."Journals" WHERE "Id"=jid AND "CompanyId"=cid FOR UPDATE;
  IF NOT FOUND OR j."State"=1 THEN RAISE EXCEPTION 'Posted lines cannot be inserted, edited or deleted' USING ERRCODE='23514'; END IF;
  IF TG_OP='UPDATE' AND NEW."JournalId"<>OLD."JournalId" THEN
    RAISE EXCEPTION 'Line journal identity cannot change' USING ERRCODE='23514';
  END IF;
  IF TG_OP='DELETE' THEN RETURN OLD; END IF;
  IF NEW."Debit"<>round(NEW."Debit",j."MinorUnits") OR NEW."Credit"<>round(NEW."Credit",j."MinorUnits") THEN
    RAISE EXCEPTION 'Currency minor-unit scale violation' USING ERRCODE='23514';
  END IF;
  RETURN NEW;
END $$;

CREATE FUNCTION accounting.journal_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE c organizations."Companies"%ROWTYPE; p accounting."Periods"%ROWTYPE;
  original accounting."Journals"%ROWTYPE; n bigint; d numeric; cr numeric; units integer;
BEGIN
  IF TG_OP IN ('UPDATE','DELETE') AND OLD."State"=1 THEN
    RAISE EXCEPTION 'Posted journals are immutable' USING ERRCODE='23514';
  END IF;
  IF TG_OP='DELETE' THEN RETURN OLD; END IF;
  IF TG_OP='INSERT' AND NEW."State"<>0 THEN
    RAISE EXCEPTION 'Insert a draft before transitioning to posted' USING ERRCODE='23514';
  END IF;
  IF NEW."State"<>1 THEN RETURN NEW; END IF;
  SELECT * INTO c FROM organizations."Companies" WHERE "Id"=NEW."CompanyId";
  units:=CASE NEW."Currency" WHEN 'IQD' THEN 3 WHEN 'KWD' THEN 3 WHEN 'BHD' THEN 3 WHEN 'JOD' THEN 3
    WHEN 'OMR' THEN 3 WHEN 'TND' THEN 3 WHEN 'JPY' THEN 0 WHEN 'KRW' THEN 0
    WHEN 'USD' THEN 2 WHEN 'EUR' THEN 2 WHEN 'GBP' THEN 2 WHEN 'AED' THEN 2 WHEN 'SAR' THEN 2
    WHEN 'CAD' THEN 2 WHEN 'AUD' THEN 2 WHEN 'CHF' THEN 2 WHEN 'CNY' THEN 2 ELSE -1 END;
  IF NEW."Currency"<>c."BaseCurrency" OR units<>NEW."MinorUnits" OR units<0 OR NEW."CurrencyPolicyVersion"<>1 THEN
    RAISE EXCEPTION 'Company currency/policy mismatch' USING ERRCODE='23514';
  END IF;
  IF NEW."PostingDate">timezone('UTC',current_timestamp)::date OR NEW."DocumentDate">timezone('UTC',current_timestamp)::date
    OR NEW."DocumentDate"<'1900-01-01'::date THEN
    RAISE EXCEPTION 'Invalid journal date' USING ERRCODE='23514';
  END IF;
  IF NEW."PostingDate"<timezone('UTC',current_timestamp)::date AND
    (length(trim(coalesce(NEW."PostingReason",'')))=0 OR NOT EXISTS (SELECT 1 FROM organizations."Members" m
      WHERE m."OrganizationId"=NEW."OrganizationId" AND m."UserId"=NEW."PostedBy" AND m."Role"='Owner')) THEN
    RAISE EXCEPTION 'Backdating requires an owner and reason' USING ERRCODE='23514';
  END IF;
  SELECT * INTO p FROM accounting."Periods" WHERE "Id"=NEW."PeriodId" AND "CompanyId"=NEW."CompanyId";
  IF NOT FOUND OR p."State"<>0 OR NEW."PostingDate"<p."StartDate" OR NEW."PostingDate">p."EndDate" THEN
    RAISE EXCEPTION 'Posting period is missing, closed or mismatched' USING ERRCODE='23514';
  END IF;
  SELECT count(*),coalesce(sum("Debit"),0),coalesce(sum("Credit"),0) INTO n,d,cr FROM accounting."JournalLines"
    WHERE "CompanyId"=NEW."CompanyId" AND "JournalId"=NEW."Id";
  IF n<2 OR n>1000 OR d<>cr OR d<=0 OR d>9999999999999999.9999 THEN
    RAISE EXCEPTION 'Journal must have valid balanced lines' USING ERRCODE='23514';
  END IF;
  IF EXISTS (SELECT 1 FROM accounting."JournalLines" l JOIN accounting."Accounts" a ON a."Id"=l."AccountId"
    WHERE l."JournalId"=NEW."Id" AND (a."CompanyId"<>NEW."CompanyId" OR NOT a."IsActive" OR NOT a."IsPosting"
      OR l."Debit"<>round(l."Debit",units) OR l."Credit"<>round(l."Credit",units))) THEN
    RAISE EXCEPTION 'Account eligibility or currency scale violation' USING ERRCODE='23514';
  END IF;
  IF NEW."SourceModule"='OpeningBalance' AND (NEW."PostingDate" IS DISTINCT FROM
    (SELECT min("StartDate") FROM accounting."FiscalYears" WHERE "CompanyId"=NEW."CompanyId") OR NOT EXISTS
    (SELECT 1 FROM accounting."OpeningBatches" b WHERE b."CompanyId"=NEW."CompanyId" AND b."Id"=NEW."SourceDocumentId" AND b."JournalId"=NEW."Id")) THEN
    RAISE EXCEPTION 'Invalid opening batch source/date' USING ERRCODE='23514';
  END IF;
  IF NEW."ReversesJournalId" IS NOT NULL THEN
    SELECT * INTO original FROM accounting."Journals" WHERE "Id"=NEW."ReversesJournalId" AND "CompanyId"=NEW."CompanyId";
    IF NOT FOUND OR original."State"<>1 OR original."ReversesJournalId" IS NOT NULL
      OR NEW."SourceModule"<>'Reversal' OR NEW."SourceDocumentId"<>original."Id"
      OR length(trim(coalesce(NEW."PostingReason",'')))=0 THEN
      RAISE EXCEPTION 'Invalid reversal source' USING ERRCODE='23514';
    END IF;
    IF EXISTS (SELECT 1 FROM
      (SELECT * FROM accounting."JournalLines" WHERE "JournalId"=original."Id") o
      FULL JOIN (SELECT * FROM accounting."JournalLines" WHERE "JournalId"=NEW."Id") r ON o."Number"=r."Number"
      WHERE o."AccountId" IS DISTINCT FROM r."AccountId" OR o."Debit" IS DISTINCT FROM r."Credit" OR o."Credit" IS DISTINCT FROM r."Debit") THEN
      RAISE EXCEPTION 'Reversal does not negate original lines' USING ERRCODE='23514';
    END IF;
  ELSIF NEW."SourceModule"='Reversal' THEN
    RAISE EXCEPTION 'Reversal original required' USING ERRCODE='23514';
  END IF;
  RETURN NEW;
END $$;

CREATE FUNCTION accounting.reject_mutation_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Accounting evidence is immutable' USING ERRCODE='23514'; END $$;

CREATE FUNCTION accounting.company_settings_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW."BaseCurrency",NEW."FiscalYearStartMonth",NEW."FiscalYearStartDay") IS DISTINCT FROM
    (OLD."BaseCurrency",OLD."FiscalYearStartMonth",OLD."FiscalYearStartDay") AND
    (EXISTS (SELECT 1 FROM accounting."Journals" WHERE "CompanyId"=OLD."Id" AND "State"=1) OR
      ((NEW."FiscalYearStartMonth",NEW."FiscalYearStartDay") IS DISTINCT FROM (OLD."FiscalYearStartMonth",OLD."FiscalYearStartDay")
       AND EXISTS (SELECT 1 FROM accounting."FiscalYears" WHERE "CompanyId"=OLD."Id"))) THEN
    RAISE EXCEPTION 'Company currency/fiscal settings frozen by accounting activity' USING ERRCODE='23514';
  END IF;
  RETURN NEW;
END $$;

CREATE FUNCTION accounting.opening_guard_v1() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP IN ('UPDATE','DELETE') THEN RAISE EXCEPTION 'Opening batch identity is retained; edit its journal draft instead' USING ERRCODE='23514'; END IF;
  IF NOT EXISTS (SELECT 1 FROM accounting."Journals" j WHERE j."Id"=NEW."JournalId" AND j."CompanyId"=NEW."CompanyId"
    AND j."SourceModule"='OpeningBalance' AND j."SourceDocumentId"=NEW."Id" AND j."State"=0) THEN
    RAISE EXCEPTION 'Opening journal source does not match batch' USING ERRCODE='23514';
  END IF;
  RETURN NEW;
END $$;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['Accounts','FiscalYears','Periods','Journals','JournalLines','OpeningBatches','AuditEvents'] LOOP
    EXECUTE format('CREATE TRIGGER a_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON accounting.%I FOR EACH ROW EXECUTE FUNCTION accounting.scope_guard_v1()',t);
    EXECUTE format('CREATE TRIGGER c_no_truncate BEFORE TRUNCATE ON accounting.%I FOR EACH STATEMENT EXECUTE FUNCTION accounting.reject_mutation_v1()',t);
  END LOOP;
END $$;
CREATE TRIGGER b_account_guard BEFORE INSERT OR UPDATE OR DELETE ON accounting."Accounts" FOR EACH ROW EXECUTE FUNCTION accounting.account_guard_v1();
CREATE TRIGGER b_year_guard BEFORE UPDATE OR DELETE ON accounting."FiscalYears" FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1();
CREATE TRIGGER b_year_insert_guard BEFORE INSERT ON accounting."FiscalYears" FOR EACH ROW EXECUTE FUNCTION accounting.year_insert_guard_v1();
CREATE TRIGGER b_period_guard BEFORE INSERT OR UPDATE OR DELETE ON accounting."Periods" FOR EACH ROW EXECUTE FUNCTION accounting.period_guard_v1();
CREATE TRIGGER b_line_guard BEFORE INSERT OR UPDATE OR DELETE ON accounting."JournalLines" FOR EACH ROW EXECUTE FUNCTION accounting.line_guard_v1();
CREATE TRIGGER b_journal_guard BEFORE INSERT OR UPDATE OR DELETE ON accounting."Journals" FOR EACH ROW EXECUTE FUNCTION accounting.journal_guard_v1();
CREATE TRIGGER b_opening_guard BEFORE INSERT OR UPDATE OR DELETE ON accounting."OpeningBatches" FOR EACH ROW EXECUTE FUNCTION accounting.opening_guard_v1();
CREATE TRIGGER b_audit_guard BEFORE UPDATE OR DELETE ON accounting."AuditEvents" FOR EACH ROW EXECUTE FUNCTION accounting.reject_mutation_v1();
CREATE TRIGGER accounting_settings_guard BEFORE UPDATE ON organizations."Companies" FOR EACH ROW EXECUTE FUNCTION accounting.company_settings_guard_v1();
""";
    public const string Down = """
DROP TRIGGER IF EXISTS accounting_settings_guard ON organizations."Companies";
DROP FUNCTION IF EXISTS accounting.scope_guard_v1() CASCADE;
DROP FUNCTION IF EXISTS accounting.account_guard_v1() CASCADE;
DROP FUNCTION IF EXISTS accounting.period_guard_v1() CASCADE;
DROP FUNCTION IF EXISTS accounting.year_insert_guard_v1() CASCADE;
DROP FUNCTION IF EXISTS accounting.line_guard_v1() CASCADE;
DROP FUNCTION IF EXISTS accounting.journal_guard_v1() CASCADE;
DROP FUNCTION IF EXISTS accounting.reject_mutation_v1() CASCADE;
DROP FUNCTION IF EXISTS accounting.company_settings_guard_v1() CASCADE;
DROP FUNCTION IF EXISTS accounting.opening_guard_v1() CASCADE;
""";
}
